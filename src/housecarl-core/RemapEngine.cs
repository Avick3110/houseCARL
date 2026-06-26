using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The shared foundation under the plugin-surgery cluster — compact (renumber a plugin into the ESL range), merge
/// (combine plugins into a new one), and their ride-alongs (COMPACT_MERGE_PLAN_2026-06-26 §3). All four operations
/// reduce to the SAME three primitives this engine exposes; the compact/merge MCP tools (later waves) are thin policy
/// layers over them. Built index-free on Mutagen's forward <c>RemapLinks</c> pass (the reverse-ref index is deferred,
/// for its own value — pre-check 2026-06-26: speed/feature fix, NOT a dependency).
///
/// THE MECHANISM, pinned empirically (remap-wave1-mech, this session) — the plan's §4 "assign new FormIDs in place"
/// was a TRAP and is NOT what we do:
///   • <c>mod.RemapLinks(old→new dict)</c> repoints a record's OUTGOING references ONLY — it does NOT move a record's
///     own identity. (Wave-0 proved this half; the mechanism probe re-confirmed it.)
///   • A record's own <c>FormKey</c> setter IS reachable but is NON-PUBLIC, and setting it leaves the FormKey-keyed
///     group cache STALE (ContainsKey(new)=false, ContainsKey(old)=true) — a silent-corruption trap. REJECTED.
///   • The CORRECT renumber is the PUBLIC <c>record.Duplicate(newFormKey)</c> (Mutagen's blessed deep-copy under a new
///     identity) into a FRESH mod, then <c>RemapLinks(dict)</c> to repoint the internal references. The fresh target
///     starts empty, so renumbering never collides with an as-yet-unmoved record. Group state stays consistent.
///
/// COVERAGE BOUNDARY (Q3, honest — never a silent drop):
///   • <see cref="IdentifyExternalReferencers"/> and <see cref="RepointInPlace"/> handle EVERY record type — they only
///     read/mutate a record's outgoing links (Mutagen's by-construction <see cref="IFormLinkContainerGetter"/> surface),
///     which is nesting-agnostic. Full coverage.
///   • <see cref="RenumberRecordsInto"/> places duplicated records via the FLAT top-level groups
///     (<see cref="WriteEngine.EnumerateFlatGroups"/>). Records that live ONLY in NESTED groups (Cell, the Placed*
///     family, INFO under a topic, navmesh, landscape) have no flat group and are REFUSED LOUD here — the nested
///     duplicate-into placement is the next wave's work, not silently skipped.
///
/// At-rest discipline (Option B / CLAUDE.md §1): every method opens at most ONE plugin mutable at a time
/// (<c>CreateFromBinary</c>, the anti-trap single-plugin lane) and disposes master overlays after the write — the
/// load order is never held parsed.
/// </summary>
public static class RemapEngine
{
    /// <summary>The light-master object-ID window, pinned empirically by EslFormIdProbe against Mutagen 0.53.1:
    /// an object ID &lt; <see cref="EslFloor"/> throws <c>LowerFormKeyRangeDisallowedException</c> (the general lower
    /// floor) and one &gt; <see cref="EslCeiling"/> throws <c>FormIDCompactionOutOfBoundsException</c> (the ESL-specific
    /// ceiling, only when the mod is flagged light). The usable window is therefore 0x800–0xFFF INCLUSIVE = 2048 IDs —
    /// NOT the 4096 the build plan's draft refuse-threshold assumed; that reconciliation lands when the compact tool
    /// ships (Wave 2). Compact assigns into this window; the capacity check in <see cref="BuildSequentialRemap"/> enforces it.</summary>
    public const uint EslFloor = 0x800;
    public const uint EslCeiling = 0xFFF;

    // ======================================================================
    //  1. IDENTIFY-PASS  — the per-operation reverse-walk (plan §2 / §3)
    // ======================================================================

    /// <summary>One external reference into the transform set: a record in a plugin OUTSIDE the set whose outgoing link
    /// points at a FormKey being remapped. After the transform that link resolves wrong / dangles unless the referencer
    /// is repointed too (<see cref="RepointInPlace"/>).</summary>
    public sealed record ExternalRef(string Plugin, FormKey Source, string SourceType, FormKey Target);

    /// <summary>The identify-pass result: every external reference found, the DISTINCT referencing plugins (load order,
    /// the opt-in-rewrite set), how many plugins were scanned, and the per-record fault-isolation accounting (a record
    /// whose link walk threw is counted + sampled, never a silent skip — Q3).</summary>
    public sealed record IdentifyResult(
        IReadOnlyList<ExternalRef> Refs,
        IReadOnlyList<string> ExternalPlugins,
        int PluginsScanned,
        int UnscannableRecords,
        IReadOnlyList<string> UnscannableSamples)
    {
        /// <summary>True when at least one plugin OUTSIDE the transform set references a remapped FormKey — the
        /// signal the default new-plugin path must NOT take silently (plan §2: fail loud + offer opt-in rewrite).</summary>
        public bool HasExternalReferencers => ExternalPlugins.Count > 0;
    }

    /// <summary>
    /// Walk the whole active order and find which plugins OUTSIDE <paramref name="transformSet"/> reference any FormKey
    /// in <paramref name="targets"/> (the keys about to be remapped). This is the per-operation safety enumeration the
    /// plan keeps the reverse-walk for (NOT a held index): ~25 s at 3520-plugin scale (one whole-order link walk).
    ///
    /// The exact inverse of <see cref="ErrorCheck"/>'s loop: there, a link is a finding if it does NOT resolve; here, a
    /// link is a finding if its target is in the remap set. Per-record fault isolation is identical (Q3 — one record
    /// Mutagen can't parse is counted + sampled, never an opaque whole-call abort and never a silent skip). One
    /// <see cref="LoadOrderResolver.Capture"/> pins the whole pass; the resolver streams one plugin at a time (Option B).
    /// </summary>
    public static IdentifyResult IdentifyExternalReferencers(
        LoadOrderResolver resolver, IReadOnlySet<FormKey> targets, IReadOnlySet<string> transformSet)
    {
        var view = resolver.Capture();
        var refs = new List<ExternalRef>();
        var externalPlugins = new List<string>();        // load-order order, distinct
        int scanned = 0, unscannable = 0;
        var unscannableSamples = new List<string>();

        foreach (var plugin in resolver.PluginNames)
        {
            if (transformSet.Contains(plugin)) continue;                 // inside the set → its refs are INTERNAL (RemapLinks handles them)
            if (view.ExcludedPlugins.ContainsKey(plugin)) continue;      // unparseable at build — already surfaced by the resolver
            scanned++;
            bool pluginListed = false;

            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // PER-RECORD fault isolation (twin of cross_plugin_query / ErrorCheck): EnumerateFormLinks lazily
                    // parses subrecord content, so ONE record Mutagen can't parse is counted + sampled, never an opaque
                    // whole-call abort and never a silent skip (Q3).
                    try
                    {
                        if (body is not IFormLinkContainerGetter flc) continue;
                        foreach (var link in flc.EnumerateFormLinks())
                        {
                            var t = link.FormKey;
                            if (t.IsNull || !targets.Contains(t)) continue;
                            refs.Add(new ExternalRef(plugin, fk, RecordNaming.StripOverlay(body.GetType().Name), t));
                            if (!pluginListed) { externalPlugins.Add(plugin); pluginListed = true; }
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 5) unscannableSamples.Add($"{plugin} {fk} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // The plugin enumeration itself faulting (a record that throws on top-level enumeration rather than on a
            // link walk) is counted per-plugin and the pass continues — never an opaque whole-pass abort (Q3).
            catch (Exception ex)
            {
                unscannable++;
                if (unscannableSamples.Count < 5) unscannableSamples.Add($"{plugin} — record enumeration aborted: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new IdentifyResult(refs, externalPlugins, scanned, unscannable, unscannableSamples);
    }

    // ======================================================================
    //  2. BUILD-REMAP-DICT — collision-free new-FormID allocation (plan §3)
    // ======================================================================

    /// <summary>A planned remap: the old→new FormKey map, or a loud Q3 refusal (e.g. the source overflows the target
    /// window) with no map.</summary>
    public sealed record RemapPlan(IReadOnlyDictionary<FormKey, FormKey> Dict, string? Error)
    {
        public bool Success => Error is null;
        public static RemapPlan Fail(string error) => new(new Dictionary<FormKey, FormKey>(), error);
    }

    /// <summary>
    /// Assign each FormKey in <paramref name="sourceKeys"/> a NEW FormKey under <paramref name="targetModKey"/>, object
    /// IDs running sequentially from <paramref name="floor"/> through <paramref name="ceiling"/> INCLUSIVE, in the
    /// given order. Collision-free by construction (sequential, distinct). REFUSES LOUD (Q3) if the source count
    /// exceeds the window capacity — for an ESL compaction that is the real "> 2048 records can't be light-compacted"
    /// limit (floor/ceiling = <see cref="EslFloor"/>/<see cref="EslCeiling"/>); it is NAMED, never a truncation.
    /// Duplicate source keys collapse to one mapping (deterministic — first occurrence wins the next ID).
    /// </summary>
    public static RemapPlan BuildSequentialRemap(
        IReadOnlyList<FormKey> sourceKeys, ModKey targetModKey, uint floor, uint ceiling)
    {
        if (ceiling < floor) return RemapPlan.Fail($"invalid remap window: ceiling 0x{ceiling:X} < floor 0x{floor:X}.");
        var dict = new Dictionary<FormKey, FormKey>();
        uint next = floor;
        long capacity = (long)ceiling - floor + 1;
        foreach (var key in sourceKeys)
        {
            if (dict.ContainsKey(key)) continue;                          // de-dupe: one mapping per source key
            if (dict.Count >= capacity)
                return RemapPlan.Fail(
                    $"cannot remap {sourceKeys.Distinct().Count()} records into the window 0x{floor:X}–0x{ceiling:X} " +
                    $"({capacity} IDs): the source overflows it. For an ESL compaction this is the hard light-master " +
                    "ceiling — the plugin has too many records to fit the light range; it cannot be compacted to light. Named, not truncated (Q3).");
            dict[key] = new FormKey(targetModKey, next);
            next++;
        }
        return new RemapPlan(dict, null);
    }

    // ======================================================================
    //  3a. RENUMBER INTO A FRESH MOD — build P′ / M (plan §4 / §5)
    // ======================================================================

    /// <summary>The result of building a renumbered mod: how many source records were copied in and how many of those
    /// were actually renumbered (in the dict), or a loud Q3 refusal (a nested-group record with no flat placement;
    /// a duplicate/add engine fault) with NOTHING half-built that the caller would ship.</summary>
    public sealed record RenumberResult(bool Success, string? Error, int RecordsCopied, int RecordsRenumbered)
    {
        public static RenumberResult Fail(string error) => new(false, error, 0, 0);
    }

    /// <summary>
    /// Copy <paramref name="sources"/> into the (typically fresh) mod <paramref name="target"/>, each under its new
    /// FormKey from <paramref name="dict"/> (a source NOT in the dict — e.g. an override the compaction leaves at its
    /// master's FormID — is copied at its OWN key), then <c>RemapLinks(dict)</c> over the whole target so every INTERNAL
    /// reference among the copied records resolves to the new keys. This is the shared core of compact (one plugin's
    /// records → P′) and merge (several donors' records → M).
    ///
    /// Uses the PUBLIC <c>record.Duplicate(newKey)</c> (Mutagen's deep-copy under a new identity) — NOT the non-public
    /// FormKey setter (that corrupts the group cache; see the class remark). Placement is via the flat top-level groups;
    /// a record that has no flat group (a nested-only family — Cell, Placed*, INFO, navmesh, landscape) is REFUSED LOUD
    /// (Q3): the nested duplicate-into path is a later wave, and a silent skip would ship a plugin missing records.
    /// </summary>
    public static RenumberResult RenumberRecordsInto(
        SkyrimMod target, IEnumerable<IMajorRecordGetter> sources, IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        int copied = 0, renumbered = 0;
        foreach (var rec in sources)
        {
            bool isRenumber = dict.TryGetValue(rec.FormKey, out var newKey);
            if (!isRenumber) newKey = rec.FormKey;                        // unmapped (e.g. an override) — copy at its own key

            IMajorRecord dup;
            try { dup = rec.Duplicate(newKey); }
            catch (Exception ex)
            {
                return RenumberResult.Fail(
                    $"could not duplicate {RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} under {newKey} " +
                    $"({WriteEngine.Describe(ex)}) — the renumber is abandoned with nothing shippable (Q3).");
            }

            if (!TryAddToFlatGroup(target, dup))
                return RenumberResult.Fail(
                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} lives only in a NESTED group (Cell / placed " +
                    "ref / INFO / navmesh / landscape), which has no flat top-level group to place the duplicate into. The nested " +
                    "duplicate-into placement is a later wave — refusing rather than silently dropping the record (Q3).");

            copied++;
            if (isRenumber) renumbered++;
        }

        // Repoint every internal reference among the copied records to the new keys. Flat AND nested links are remapped
        // (RemapLinks walks all outgoing links); the nested-group limit above is about PLACING records, not repointing.
        target.RemapLinks(dict);
        return new RenumberResult(true, null, copied, renumbered);
    }

    /// <summary>Place an already-constructed record into the target mod's matching flat top-level group via the group's
    /// own <c>Add</c>, reusing the single <see cref="WriteEngine.EnumerateFlatGroups"/> enumeration the create/override
    /// surface derives from (no drift). An abstract group (e.g. <c>SkyrimGroup&lt;Global&gt;</c>) matches its concrete arm
    /// (<c>GlobalFloat</c>) because <c>tMajor.IsInstanceOfType(dup)</c> holds and <c>Add(Global)</c> accepts the subtype.
    /// Returns false when no flat group fits (a nested-only record) — the caller fails loud.</summary>
    static bool TryAddToFlatGroup(SkyrimMod target, IMajorRecord dup)
    {
        foreach (var (prop, tMajor, _) in WriteEngine.EnumerateFlatGroups(target.GetType()))
        {
            if (!tMajor.IsInstanceOfType(dup)) continue;
            var group = prop.GetValue(target)
                ?? throw new InvalidOperationException($"flat group '{prop.Name}' was null on the target mod (engine inconsistency, Q3).");
            var add = group.GetType().GetMethod("Add", new[] { tMajor })
                      ?? group.GetType().GetMethods()
                          .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1
                                               && m.GetParameters()[0].ParameterType.IsInstanceOfType(dup));
            if (add is null)
                throw new InvalidOperationException(
                    $"flat group '{prop.Name}' ({group.GetType().Name}) exposes no Add accepting {dup.GetType().Name} (Q3).");
            add.Invoke(group, new object[] { dup });
            return true;
        }
        return false;
    }

    // ======================================================================
    //  3b. STREAMING APPLIER — repoint an existing plugin's refs IN PLACE (plan §2/§3)
    // ======================================================================

    /// <summary>The result of an in-place repoint: success + the on-disk byte size, or a loud Q3 refusal (target not
    /// active / excluded / not on disk / a declared master absent / a sub-0x800 originating record / a serialize fault)
    /// with the file UNTOUCHED.</summary>
    public sealed record RepointResult(bool Success, string? Error, long Bytes, int LinksConsidered)
    {
        public static RepointResult Fail(string error) => new(false, error, 0, 0);
    }

    /// <summary>
    /// Repoint plugin <paramref name="pluginName"/>'s outgoing references against <paramref name="dict"/> IN PLACE — the
    /// streaming applier for an EXTERNAL referencer (a plugin outside the transform set that the identify-pass found
    /// pointing at a remapped record). This rides the existing in-place write lane (the modder's opt-in: explicit flag
    /// + per-plugin consent + no backup, enforced by the service before this is reached). The default new-plugin path
    /// NEVER calls this — only the explicit external-referencer rewrite does.
    ///
    /// EAGER-loads the SINGLE plugin mutable (<c>CreateFromBinary</c> — never the order; the legacy RAM trap), applies
    /// <c>RemapLinks(dict)</c> (every outgoing link, flat AND nested), resolves the target's OWN declared masters to
    /// overlays, and re-serializes over itself via <see cref="WriteEngine.WriteInPlace"/> (own masters, counter verbatim,
    /// no baseline force-include — the xEdit-parity re-emit, staged + crash-atomically swapped). All-or-nothing: any
    /// refusal or serialize fault leaves the original file byte-intact. A sub-0x800 originating record (a vanilla master)
    /// makes the write throw <c>LowerFormKeyRangeDisallowed</c> — surfaced LOUD here, never a silent partial write (Q3).
    /// </summary>
    public static RepointResult RepointInPlace(
        LoadOrderResolver resolver, string pluginName, IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        if (dict.Count == 0) return RepointResult.Fail("no remap entries supplied — nothing to repoint.");
        var view = resolver.Capture();
        if (!view.ContainsPlugin(pluginName))
            return RepointResult.Fail($"repoint target '{pluginName}' is not an active plugin in the load order.");
        if (view.ExcludedPlugins.TryGetValue(pluginName, out var excluded))
            return RepointResult.Fail(
                $"cannot repoint '{pluginName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (it would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");

        var path = view.PluginPath(pluginName);
        if (path is null || !File.Exists(path))
            return RepointResult.Fail($"repoint target '{pluginName}' not found on disk at {path ?? "<unresolved>"} — the file is untouched.");

        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
        {
            return RepointResult.Fail(
                $"cannot open '{pluginName}' to repoint in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is " +
                "refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED.");
        }

        try { targetMod.RemapLinks(dict); }
        catch (Exception ex)
        {
            return RepointResult.Fail($"RemapLinks failed on '{pluginName}' ({WriteEngine.Describe(ex)}) — the file is untouched.");
        }

        // Resolve the target's OWN declared masters to overlays in load order — the faithful re-serialize set
        // WriteInPlace hands Mutagen (mirrors WritePatchBuilder.ResolveOwnMasters). A declared master ABSENT from the
        // active order is a loud Q3 refusal (a re-serialize couldn't resolve the references into it), file untouched.
        var overlays = new List<IDisposable>();
        try
        {
            var resolved = new List<ISkyrimModGetter>();
            foreach (var mr in targetMod.ModHeader.MasterReferences)
            {
                var mfn = mr.Master.FileName.String;
                var mpath = view.PluginPath(mfn);
                if (mpath is null)
                    return RepointResult.Fail(
                        $"cannot re-serialize '{pluginName}' in place: its declared master '{mfn}' is not active in the load order, " +
                        "so a faithful re-serialize can't resolve the references into it. Enable that master (or fix the masters in xEdit) first. The file is UNTOUCHED.");
                var ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE);
                overlays.Add((IDisposable)ov);
                resolved.Add(ov);
            }

            try { WriteEngine.WriteInPlace(targetMod, resolved, path); }
            catch (Exception ex)
            {
                return RepointResult.Fail(
                    $"writing '{pluginName}' in place failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}" +
                    " — note: a sub-0x800 originating record (e.g. a vanilla master) is rejected by the light-/master-aware floor here, not silently written.");
            }
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        long bytes = 0;
        try { bytes = new FileInfo(path).Length; } catch { }
        return new RepointResult(true, null, bytes, dict.Count);
    }
}
