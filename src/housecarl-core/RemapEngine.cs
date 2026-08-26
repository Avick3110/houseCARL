using System.Collections;
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
    public const uint EslFloor = FormIdRange.EslWindowFloor;      // 0x800 — the single home is FormIdRange (shared with the write-allocation floor)
    public const uint EslCeiling = FormIdRange.EslWindowCeiling;  // 0xFFF

    // ======================================================================
    //  1. IDENTIFY-PASS  — the per-operation reverse-walk (plan §2 / §3)
    // ======================================================================

    /// <summary>One external reference into the transform set: a record in a plugin OUTSIDE the set whose outgoing link
    /// points at a FormKey being remapped. After the transform that link resolves wrong / dangles unless the referencer
    /// is repointed too (<see cref="RepointInPlace"/>).</summary>
    public sealed record ExternalRef(string Plugin, FormKey Source, string SourceType, FormKey Target);

    /// <summary>One external OVERRIDE of a record being remapped: a record in a plugin OUTSIDE the set whose OWN FormKey
    /// is in the remap set — it OVERRIDES a record about to be renumbered (gap #2). After the renumber that override points
    /// at a base FormID that no longer exists → orphaned override + missing master. Unlike <see cref="ExternalRef"/> it
    /// CANNOT be auto-repointed: fixing it means changing the override's OWN identity, not rewriting an outgoing link
    /// (<see cref="RepointInPlace"/>/RemapLinks only do links), so it is surfaced as a WARN, never routed through the
    /// repoint path (Q3 — that would report a false success). <paramref name="Record"/> is the overridden FormKey.</summary>
    public sealed record ExternalOverride(string Plugin, FormKey Record, string RecordType);

    /// <summary>The identify-pass result: every external reference found, the DISTINCT referencing plugins (load order,
    /// the opt-in-rewrite set), the external OVERRIDERS (gap #2 — detect + warn, NOT repointable), how many plugins were
    /// scanned, and the per-record fault-isolation accounting (a record whose link walk threw is counted + sampled, never
    /// a silent skip — Q3).</summary>
    public sealed record IdentifyResult(
        IReadOnlyList<ExternalRef> Refs,
        IReadOnlyList<string> ExternalPlugins,
        int PluginsScanned,
        int UnscannableRecords,
        IReadOnlyList<string> UnscannableSamples,
        IReadOnlyList<ExternalOverride> Overrides,
        IReadOnlyList<string> ExternalOverriders)
    {
        /// <summary>True when at least one plugin OUTSIDE the transform set references a remapped FormKey — the
        /// signal the default new-plugin path must NOT take silently (plan §2: fail loud + offer opt-in rewrite).</summary>
        public bool HasExternalReferencers => ExternalPlugins.Count > 0;

        /// <summary>True when at least one plugin OUTSIDE the transform set OVERRIDES a remapped record (gap #2). Unlike
        /// <see cref="HasExternalReferencers"/> this does NOT gate the operation — an override can't be auto-fixed, so the
        /// posture is warn-and-proceed (xEdit parity), never refuse.</summary>
        public bool HasExternalOverriders => ExternalOverriders.Count > 0;
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
        var overrides = new List<ExternalOverride>();     // gap #2: external plugins that OVERRIDE a remapped record
        var externalOverriders = new List<string>();      // distinct overriding plugins, load-order order
        int scanned = 0, unscannable = 0;
        var unscannableSamples = new List<string>();
        // PluginNames CAN list a filename more than once in a degenerate order; scanning a name twice would
        // double-count + double-list it. A real MO2 VFS yields unique filenames, so this is belt-and-braces — but
        // it keeps the result correct regardless (the listing is, by contract, DISTINCT).
        var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in resolver.PluginNames)
        {
            if (transformSet.Contains(plugin)) continue;                 // inside the set → its refs are INTERNAL (RemapLinks handles them)
            if (view.ExcludedPlugins.ContainsKey(plugin)) continue;      // unparseable at build — already surfaced by the resolver
            if (!scannedNames.Add(plugin)) continue;                     // a duplicate name in the order — scan/list it once (Q3: no double-count)
            scanned++;
            bool pluginListed = false;
            bool pluginListedOverride = false;

            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // PER-RECORD fault isolation (twin of cross_plugin_query / ErrorCheck): EnumerateFormLinks lazily
                    // parses subrecord content, so ONE record Mutagen can't parse is counted + sampled, never an opaque
                    // whole-call abort and never a silent skip (Q3).
                    try
                    {
                        // OVERRIDER (gap #2): this external plugin's record shares a FormKey being remapped → it OVERRIDES
                        // a record about to be renumbered. Detected by IDENTITY (fk), independent of outgoing links, so it
                        // is checked BEFORE the FormLinkContainer guard (an override with no outgoing ref into the set is
                        // still a dependent the old link-only walk missed). It CANNOT be auto-repointed (identity change,
                        // not a link rewrite) → collected for a WARN, never routed through the referencer repoint (Q3).
                        if (targets.Contains(fk))
                        {
                            overrides.Add(new ExternalOverride(plugin, fk, RecordNaming.StripOverlay(body.GetType().Name)));
                            if (!pluginListedOverride) { externalOverriders.Add(plugin); pluginListedOverride = true; }
                        }
                        // REFERENCER: an outgoing link whose target is being remapped (after the transform it dangles).
                        // A DELETED record links to nothing (#279 — the shared rule, see DeletedRecordRule): its
                        // content is not live, so it is not a referencer to repoint, and an engine-authored deleted
                        // body can throw on the walk below and land in the unscannable bucket as an untyped skip (Q3).
                        // Deliberately AFTER the overrider test above: that one is identity-only (the record's own
                        // FormKey, read from the header), and a deleted override of a record about to be renumbered is
                        // still a dependent worth warning about — this guard scopes to the link walk, nothing else.
                        if (DeletedRecordRule.HasNoLiveBody(body)) continue;
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

        return new IdentifyResult(refs, externalPlugins, scanned, unscannable, unscannableSamples, overrides, externalOverriders);
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
    internal static bool TryAddToFlatGroup(SkyrimMod target, IMajorRecord dup)
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
    //  3a-NESTED. WHOLE-MOD STRUCTURAL RENUMBER — build P′ incl. nested records
    //  (Wave 2: the gap RenumberRecordsInto refuses loud on). Walks the source mod's
    //  STRUCTURE (not a flat record stream, which loses parentage): each flat-group
    //  record AND its nested children (a worldspace's exterior cells + placed refs,
    //  a dialog topic's INFOs), plus the interior-cell block tree. Renumber mechanism
    //  pinned by remap-wave2-nested-mech: record.Duplicate(newKey) DEEP-COPIES a
    //  record's nested children (at their OLD keys), so we Duplicate the container
    //  then recursively REPLACE each originating child with its own renumbered
    //  Duplicate; IMajorRecordGetterEnumerable is the by-construction discriminator
    //  for "contains records but isn't one" (the FormKey-less block-tree structs we
    //  recurse THROUGH). RemapLinks at the end repoints every internal reference.
    // ======================================================================

    /// <summary>Per-call accounting for the structural renumber: total records placed (flat + nested) and how many of
    /// those were actually renumbered (their key was in the dict — i.e. originating, vs an override copied at its own key).</summary>
    sealed class RenumberStats { public int Copied; public int Renumbered; }

    /// <summary>
    /// Copy EVERY record of <paramref name="source"/> into the (fresh) mod <paramref name="target"/> under its remapped
    /// FormKey from <paramref name="dict"/> (a record NOT in the dict — an override the compaction leaves at its master's
    /// FormID — is copied at its OWN key), reconstructing the FULL nesting: flat top-level records + their nested children
    /// (worldspace→exterior cells→placed refs, dialog topic→INFOs) AND the interior-cell block tree. Then
    /// <c>RemapLinks(dict)</c> over the whole target so every INTERNAL reference resolves to the new keys. This is the
    /// compact tool's core — the structural superset of the flat <see cref="RenumberRecordsInto"/> (which has no parentage
    /// and so refuses nested-only records); both share the <c>Duplicate(newKey)</c> mechanism and <see cref="TryAddToFlatGroup"/>.
    ///
    /// Coverage (Q3): handles every record type — flat groups via <see cref="WriteEngine.EnumerateFlatGroups"/>, interior
    /// cells via the block tree, and any nested child via the generic <see cref="RenumberDescendants"/> walk (no hand-coded
    /// per-family list). A flat record whose group can't be resolved on the target (an engine inconsistency that should be
    /// impossible by construction) is REFUSED LOUD with nothing half-shippable, never a silent drop.
    /// </summary>
    public static RenumberResult RenumberModInto(
        SkyrimMod target, ISkyrimModGetter source, IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        var stats = new RenumberStats();
        try
        {
            // 1. FLAT top-level groups (weapons … AND worldspaces, dialog topics — each carries its nested children).
            foreach (var (prop, _, _) in WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)))
            {
                var srcProp = source.GetType().GetProperty(prop.Name);
                if (srcProp?.GetValue(source) is not IEnumerable srcGroup) continue;      // group absent on the getter — nothing to copy
                foreach (var item in srcGroup)
                {
                    if (item is not IMajorRecordGetter rec) continue;
                    var dup = RenumberOne(rec, dict, stats);
                    if (!TryAddToFlatGroup(target, dup))
                        return RenumberResult.Fail(
                            $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} is a flat top-level record but no matching " +
                            $"group was found on the target mod to place its renumbered copy (engine inconsistency, Q3) — the renumber is abandoned with nothing shippable.");
                }
            }

            // 2. INTERIOR cells (mod.Cells block tree — the nested-only family that has no flat group). Each cell carries
            //    its own placed refs / navmesh / landscape; re-file the renumbered cell by its NEW FormID digits (the
            //    vanilla interior block convention, mirroring WriteEngine.AddInteriorCell).
            if (source.Cells is { } cellsGroup)
                foreach (var block in cellsGroup.Records)
                    foreach (var sub in block.SubBlocks)
                        foreach (var cell in sub.Cells)
                        {
                            var renCell = (Cell)RenumberOne(cell, dict, stats);
                            FileInteriorCellByNewId(target, renCell);
                        }

            // 3. Repoint every internal reference among the copied records (flat AND nested links) to the new keys.
            //    Inside the try so a RemapLinks throw is the SAME structured Q3 refusal as steps 1–2 — a direct engine
            //    caller (e.g. the guard) gets a FAIL result, not a raw crash (PR #122 review #4).
            target.RemapLinks(dict);
        }
        catch (Exception ex)
        {
            return RenumberResult.Fail(
                $"the structural renumber failed ({WriteEngine.Describe(ex)}) — abandoned with nothing shippable (Q3).");
        }

        return new RenumberResult(true, null, stats.Copied, stats.Renumbered);
    }

    /// <summary>Renumber ONE record: <c>Duplicate(newKey)</c> it under its new-or-same FormKey (Mutagen's deep-copy under
    /// a new identity — its nested children come along at their OLD keys), then recursively renumber those descendants in
    /// place. Counted once per record (itself, before its descendants) into <paramref name="stats"/>. Mechanism pinned by
    /// remap-wave2-nested-mech. <paramref name="reg"/> (merge only, null for compact) registers every placed record —
    /// itself AND each descendant — under its NEW key, so the multi-donor walk can detect cross-donor collisions and
    /// graft a losing donor's un-relisted children into the winner's copy (<see cref="MergeModsInto"/>).</summary>
    static IMajorRecord RenumberOne(IMajorRecordGetter rec, IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats,
        MergePlacement? reg = null)
    {
        bool isRenumber = dict.TryGetValue(rec.FormKey, out var newKey);
        if (!isRenumber) newKey = rec.FormKey;                                            // unmapped (an override) — copy at its own key
        var dup = rec.Duplicate(newKey);
        stats.Copied++;
        if (isRenumber) stats.Renumbered++;
        reg?.Register(newKey, dup);
        // Only records that actually CONTAIN nested records pay the property-walk cost (Any() short-circuits flat records).
        if (dup is IMajorRecordGetterEnumerable e && e.EnumerateMajorRecords().Any())
            RenumberDescendants(dup, dict, stats, reg);
        return dup;
    }

    /// <summary>Walk a container's child records and renumber each in place. A list/property element that IS a record
    /// (<see cref="IMajorRecordGetter"/>) is REPLACED by its renumbered <see cref="RenumberOne"/>; one that merely CONTAINS
    /// records (<see cref="IMajorRecordGetterEnumerable"/> but not a record itself — the FormKey-less block-tree structs
    /// WorldspaceBlock/SubBlock, CellBlock/SubBlock) is recursed THROUGH. Everything else (scalars, FormLinks, value
    /// structs) is skipped — <c>RemapLinks</c> repoints outgoing links separately. By construction: no hand-coded family
    /// list; the discriminator is Mutagen's own enumerable marker (remap-wave2-nested-mech).
    ///
    /// <para>Two load-bearing Mutagen assumptions (PR #122 review #5), both confirmed for the tested shapes by
    /// remap-wave2-nested-mech and stable across the corpus by construction: (1) record-container list/property values are
    /// REFERENCE types, so reflective <c>list[i] = …</c> / <c>prop.SetValue</c> writes the renumbered duplicate back into
    /// the parent (never into a boxed struct copy); (2) the child collections implement the non-generic
    /// <see cref="IList"/> (<c>ExtendedList&lt;T&gt;</c> does), so the <c>val is IList</c> gate reaches them. If Mutagen
    /// ever broke either, the affected records would be SKIPPED (left at old keys) rather than fail loud — which is why the
    /// guard pins every nesting shape on disk.</para></summary>
    static void RenumberDescendants(object container, IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats,
        MergePlacement? reg = null)
    {
        foreach (var prop in container.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(container); } catch { continue; }
            if (val is null) continue;

            if (val is IList list && val is not string)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var el = list[i];
                    if (el is IMajorRecordGetter childRec)
                    {
                        // Merge only (reg != null): a child whose mapped key is ALREADY placed in M — the same record
                        // carried by two donors under DIFFERENT parents (a moved reference: donor B's cell holds an
                        // override of donor A's placed ref) — must NOT be duplicated again at the same FormKey. The
                        // already-placed copy is the load-order winner (winner-first walk); this stale deep-copied
                        // child is REMOVED from its parent and the conflict is REPORTED (Q3, never a silent second
                        // record under one FormID — the engine/xEdit-invalid shape the review's C-1 finding named).
                        var nk = dict.TryGetValue(childRec.FormKey, out var mapped) ? mapped : childRec.FormKey;
                        if (reg is not null && reg.IsPlaced(childRec, nk, out var placedChild))
                        {
                            GraftMissingDescendants(childRec, placedChild, dict, stats, reg);
                            list.RemoveAt(i); i--;
                            continue;
                        }
                        list[i] = RenumberOne(childRec, dict, stats, reg);
                    }
                    else if (el is IMajorRecordGetterEnumerable) RenumberDescendants(el, dict, stats, reg);
                }
            }
            else if (val is IMajorRecordGetter singleRec)
            {
                if (!prop.CanWrite) continue;
                var nk = dict.TryGetValue(singleRec.FormKey, out var mapped) ? mapped : singleRec.FormKey;
                if (reg is not null && reg.IsPlaced(singleRec, nk, out var placedSingle))
                {
                    GraftMissingDescendants(singleRec, placedSingle, dict, stats, reg);
                    prop.SetValue(container, null);                        // the winner's copy lives under ITS parent
                    continue;
                }
                prop.SetValue(container, RenumberOne(singleRec, dict, stats, reg));
            }
            else if (val is IMajorRecordGetterEnumerable nestedContainer)
            {
                RenumberDescendants(nestedContainer, dict, stats, reg);
            }
        }
    }

    /// <summary>File an already-renumbered INTERIOR cell into <paramref name="target"/>'s top-level Cells block tree by
    /// its NEW FormID digits (block = id%10, subblock = (id/10)%10 — the vanilla interior convention, mirroring
    /// <see cref="WriteEngine.AddInteriorCell"/>'s STEP-0-proven math). The renumber re-files by the new id, so a cell
    /// moved into the ESL window lands in the block its new id keys — what the CK expects on re-save.</summary>
    static void FileInteriorCellByNewId(SkyrimMod target, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = target.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    // ======================================================================
    //  3a-MERGE. MULTI-DONOR RENUMBER — build the merged mod M (plan A4)
    //  Merge = a RECORDS operation (the 2026-06-27 scope correction): combine the
    //  donor plugins into ONE new plugin, keep the mods installed. Every donor
    //  ORIGINATING record necessarily changes identity (its ModKey becomes M's,
    //  even when the object ID is kept), so the remap dict covers ALL of them —
    //  collision-only applies to the OBJECT ID (zMerge's default: the first donor
    //  in load order keeps its IDs; later donors renumber only IDs already taken).
    //  Cross-donor conflicts on the SAME FormKey resolve to the LOAD-ORDER WINNER
    //  (the locked resolution) and are REPORTED, never silent; the winner's copy
    //  places first (donors walk in REVERSE load order) and a losing donor's
    //  un-relisted nested children (the patch-of-a-donor DIAL/INFO + cell shapes)
    //  are GRAFTED into the winner's already-placed container — the xEdit cell/
    //  topic merge semantic, without which merging a mod with its patch silently
    //  drops the base mod's lines and placed refs (Q3).
    // ======================================================================

    /// <summary>Per-donor merge-remap accounting: how many originating object IDs the donor KEPT (same 6-hex id under
    /// the merged ModKey) vs had to be RENUMBERED (the id was already claimed by an earlier-in-load-order donor, or sat
    /// below the write floor).</summary>
    public sealed record MergeDonorRemap(string Donor, int Kept, int Renumbered);

    /// <summary>A planned multi-donor remap: the UNION old→new FormKey dict over every donor's originating records
    /// (keys are donor-qualified FormKeys, so donors can never collide in the dict itself), per-donor kept/renumbered
    /// accounting, or a loud Q3 refusal (window overflow) with no map.</summary>
    public sealed record MergeRemapPlan(
        IReadOnlyDictionary<FormKey, FormKey> Dict, IReadOnlyList<MergeDonorRemap> Donors, string? Error)
    {
        public bool Success => Error is null;
        public static MergeRemapPlan Fail(string error) =>
            new(new Dictionary<FormKey, FormKey>(), Array.Empty<MergeDonorRemap>(), error);
    }

    /// <summary>
    /// Build the merge remap: every donor originating FormKey → a FormKey under <paramref name="targetModKey"/>,
    /// KEEPING the object ID wherever it is in-window and unclaimed (donors claim in LOAD ORDER, so the first donor
    /// holding an id keeps it — zMerge's default) and allocating the next free id only for COLLISIONS (an id an
    /// earlier donor claimed) and for ids below <paramref name="floor"/> (the write floor rejects them). REFUSES
    /// LOUD (Q3) when the combined donors overflow the window — named, never a truncation.
    /// </summary>
    public static MergeRemapPlan BuildMergeRemap(
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Keys)> donorsByLoadOrder,
        ModKey targetModKey, uint floor, uint ceiling)
    {
        if (ceiling < floor) return MergeRemapPlan.Fail($"invalid remap window: ceiling 0x{ceiling:X} < floor 0x{floor:X}.");

        // Pass 1 — keepable ids write straight into the dict, donors in load order (first claim wins); the rest queue
        // as collisions. Per-donor kept/renumbered tallies here (renumbered = the donor's total − kept).
        var claimed = new HashSet<uint>();
        var collide = new List<FormKey>();
        var seen = new HashSet<FormKey>();                                 // defensive intra-donor de-dupe (BuildSequentialRemap parity)
        var dict = new Dictionary<FormKey, FormKey>();
        var perDonor = new List<MergeDonorRemap>(donorsByLoadOrder.Count);
        foreach (var (donor, keys) in donorsByLoadOrder)
        {
            int kept = 0, total = 0;
            foreach (var k in keys)
            {
                if (!seen.Add(k)) continue;
                total++;
                if (k.ID >= floor && k.ID <= ceiling && claimed.Add(k.ID)) { dict[k] = new FormKey(targetModKey, k.ID); kept++; }
                else collide.Add(k);
            }
            perDonor.Add(new MergeDonorRemap(donor, kept, total - kept));
        }

        long capacity = (long)ceiling - floor + 1;
        if (seen.Count > capacity)
            return MergeRemapPlan.Fail(
                $"cannot merge {seen.Count} originating records into the window 0x{floor:X}–0x{ceiling:X} ({capacity} IDs): " +
                "the combined donors overflow it. Named, not truncated (Q3).");

        // Pass 2 — each collision takes the next free id. The capacity precheck guarantees one exists for every
        // collision, so the in-loop ceiling guard is a defensive invariant check, not a reachable refusal.
        uint next = floor;
        foreach (var k in collide)
        {
            while (next <= ceiling && claimed.Contains(next)) next++;
            if (next > ceiling)
                return MergeRemapPlan.Fail(
                    $"cannot merge: the free-id scan ran past the window ceiling 0x{ceiling:X} while renumbering collisions " +
                    "(engine invariant violated — the capacity precheck should have refused first). Named, not truncated (Q3).");
            dict[k] = new FormKey(targetModKey, next);
            claimed.Add(next);
        }
        return new MergeRemapPlan(dict, perDonor, null);
    }

    /// <summary>One cross-donor conflict the merge resolved: the same record (by pre-merge FormKey) carried by two
    /// donors — the LOAD-ORDER WINNER's version is in the merged plugin, the loser's body is not (its un-relisted
    /// nested children, if any, were grafted). Reported per losing donor (three donors on one record → two entries).</summary>
    public sealed record MergeConflict(FormKey Key, string RecordType, string WinnerDonor, string LoserDonor);

    /// <summary>The result of the multi-donor renumber: record accounting + every cross-donor conflict resolved
    /// (load-order winner), or a loud Q3 refusal with NOTHING half-built that the caller would ship.</summary>
    public sealed record MergeResult(
        bool Success, string? Error, int RecordsCopied, int RecordsRenumbered, IReadOnlyList<MergeConflict> Conflicts)
    {
        public static MergeResult Fail(string error) => new(false, error, 0, 0, Array.Empty<MergeConflict>());
    }

    /// <summary>Merge-walk placement registry: every record placed in M so far, keyed by its NEW (post-remap) FormKey,
    /// with the donor that placed it — the collision detector and graft target for later (earlier-in-load-order) donors.
    /// Carries the conflict list too, so EVERY site that resolves a collision (the top-level walk, the graft helpers,
    /// and the in-flight <see cref="RenumberDescendants"/> drop below) reports through one channel.</summary>
    sealed class MergePlacement
    {
        public readonly Dictionary<FormKey, IMajorRecord> Objects = new();
        public readonly Dictionary<FormKey, string> PlacedBy = new();
        public readonly List<MergeConflict> Conflicts = new();
        public string CurrentDonor = "";
        public void Register(FormKey key, IMajorRecord obj) { Objects[key] = obj; PlacedBy[key] = CurrentDonor; }

        /// <summary>True when the mapped key is already placed in M — records the cross-donor conflict (winner = the
        /// donor that placed it; the walk runs winner-first) and hands back the placed object for a graft recurse.</summary>
        public bool IsPlaced(IMajorRecordGetter rec, FormKey nk, out IMajorRecord placed)
        {
            if (Objects.TryGetValue(nk, out placed!))
            {
                Conflicts.Add(new MergeConflict(rec.FormKey, RecordNaming.StripOverlay(rec.GetType().Name), PlacedBy[nk], CurrentDonor));
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Copy EVERY record of every donor into the (fresh) mod <paramref name="target"/> under its remapped FormKey from
    /// <paramref name="dict"/>, resolving cross-donor conflicts on the same FormKey to the LOAD-ORDER WINNER. Donors
    /// walk in REVERSE load order so each record's WINNING version places first (via the same structural walk compact
    /// uses — <see cref="RenumberOne"/> and the interior-cell block tree); an earlier donor's copy of an already-placed
    /// record is a reported <see cref="MergeConflict"/>, and its NESTED CHILDREN missing from the winner's copy (a base
    /// mod's INFOs a patch's DIAL override doesn't re-list; its placed refs under an overridden cell) are GRAFTED into
    /// the winner's placed container. Then <c>RemapLinks(dict)</c> over the whole target. All-or-nothing (Q3): any
    /// engine fault abandons the merge with nothing shippable.
    /// </summary>
    public static MergeResult MergeModsInto(
        SkyrimMod target,
        IReadOnlyList<(string Name, ISkyrimModGetter Mod)> donorsByLoadOrder,
        IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        var stats = new RenumberStats();
        var reg = new MergePlacement();
        try
        {
            foreach (var (name, src) in donorsByLoadOrder.Reverse())      // winner-first: the LAST donor in load order places first
            {
                reg.CurrentDonor = name;

                // 1. FLAT top-level groups (each record carries its nested children through RenumberOne's walk).
                foreach (var (prop, _, _) in WriteEngine.EnumerateFlatGroups(typeof(SkyrimMod)))
                {
                    var srcProp = src.GetType().GetProperty(prop.Name);
                    if (srcProp?.GetValue(src) is not IEnumerable srcGroup) continue;
                    foreach (var item in srcGroup)
                    {
                        if (item is not IMajorRecordGetter rec) continue;
                        var nk = dict.TryGetValue(rec.FormKey, out var mapped) ? mapped : rec.FormKey;
                        if (reg.IsPlaced(rec, nk, out var existing))
                        {
                            GraftMissingDescendants(rec, existing, dict, stats, reg);
                        }
                        else
                        {
                            var dup = RenumberOne(rec, dict, stats, reg);
                            if (!TryAddToFlatGroup(target, dup))
                                return MergeResult.Fail(
                                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} (donor '{name}') is a flat top-level record but no " +
                                    "matching group was found on the target mod to place its merged copy (engine inconsistency, Q3) — the merge is abandoned with nothing shippable.");
                        }
                    }
                }

                // 2. INTERIOR cells (the mod-level block tree) — placed cell-by-cell, so a losing donor's cell that the
                //    winner doesn't carry still merges, and a conflicted cell grafts its missing placed refs.
                if (src.Cells is { } cellsGroup)
                    foreach (var block in cellsGroup.Records)
                        foreach (var sub in block.SubBlocks)
                            foreach (var cell in sub.Cells)
                            {
                                var nk = dict.TryGetValue(cell.FormKey, out var mapped) ? mapped : cell.FormKey;
                                if (reg.IsPlaced(cell, nk, out var existing))
                                {
                                    GraftMissingDescendants(cell, existing, dict, stats, reg);
                                }
                                else
                                {
                                    var renCell = (Cell)RenumberOne(cell, dict, stats, reg);
                                    FileInteriorCellByNewId(target, renCell);
                                }
                            }
            }

            // 3. Repoint every internal reference among the merged records (flat AND nested links) to the new keys —
            //    including every cross-donor reference (a donor-B link into donor-A resolves because A's originating
            //    keys are all in the dict). Inside the try: a RemapLinks throw is the same structured Q3 refusal.
            target.RemapLinks(dict);
        }
        catch (Exception ex)
        {
            return MergeResult.Fail(
                $"the multi-donor renumber failed ({WriteEngine.Describe(ex)}) — abandoned with nothing shippable (Q3).");
        }

        return new MergeResult(true, null, stats.Copied, stats.Renumbered, reg.Conflicts);
    }

    /// <summary>Graft a LOSING donor record's nested children into the WINNER's already-placed copy: walk the loser's
    /// getter with the same discriminators as <see cref="RenumberDescendants"/> (records; the FormKey-less worldspace
    /// block structs, paired by block NUMBER); a child whose remapped key is already placed recurses (its own children
    /// may still be missing), an unplaced child is renumbered and APPENDED to the winner's same-named list — the xEdit
    /// cell/topic merge semantic (the winner's receiving list is resolved ONCE per property, not per element). A
    /// structural mismatch (no same-named settable list on the winner) THROWS — caught by <see cref="MergeModsInto"/>
    /// into the loud all-or-nothing refusal (Q3), never a silent child drop.</summary>
    static void GraftMissingDescendants(IMajorRecordGetter loser, IMajorRecord winner,
        IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats, MergePlacement reg)
    {
        foreach (var prop in loser.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(loser); } catch { continue; }
            if (val is null || val is string || val is byte[]) continue;

            if (val is IMajorRecordGetter singleRec)
            {
                GraftSingleton(singleRec, winner, prop.Name, dict, stats, reg);
            }
            else if (val is IEnumerable seq)
            {
                IList? winnerList = null;                                  // resolved lazily, ONCE per property
                IList WinnerList(string what) => winnerList ??=
                    winner.GetType().GetProperty(prop.Name)?.GetValue(winner) as IList
                    ?? throw new InvalidOperationException(
                        $"cannot graft {what}: the winning donor's {RecordNaming.StripOverlay(winner.GetType().Name)} " +
                        $"{winner.FormKey} has no settable record list '{prop.Name}' to receive it (Q3).");
                foreach (var el in seq)
                {
                    if (el is IMajorRecordGetter childRec)
                    {
                        var nk = dict.TryGetValue(childRec.FormKey, out var mapped) ? mapped : childRec.FormKey;
                        if (reg.IsPlaced(childRec, nk, out var placed))
                        {
                            GraftMissingDescendants(childRec, placed, dict, stats, reg);
                            continue;
                        }
                        WinnerList($"{RecordNaming.StripOverlay(childRec.GetType().Name)} {childRec.FormKey}")
                            .Add(RenumberOne(childRec, dict, stats, reg));
                    }
                    else if (el is IMajorRecordGetterEnumerable blockStruct)
                    {
                        GraftBlock(blockStruct, WinnerList($"a nested block of '{prop.Name}'"), prop.Name, dict, stats, reg);
                    }
                    else break;                                            // a non-record element type — not a record list; skip the property
                }
            }
        }
    }

    /// <summary>Graft one singleton-property child (e.g. a cell's Landscape): already placed → recurse; the winner's
    /// slot empty → renumber + set; the winner's slot held by a DIFFERENT record → the winner's structure stands and
    /// the loser's child is REPORTED as a resolved conflict (never silently dropped — Q3).</summary>
    static void GraftSingleton(IMajorRecordGetter child, IMajorRecord winner, string propName,
        IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats, MergePlacement reg)
    {
        var nk = dict.TryGetValue(child.FormKey, out var mapped) ? mapped : child.FormKey;
        if (reg.IsPlaced(child, nk, out var placed))
        {
            GraftMissingDescendants(child, placed, dict, stats, reg);
            return;
        }
        var prop = winner.GetType().GetProperty(propName);
        if (prop is null || !prop.CanWrite)
            throw new InvalidOperationException(
                $"cannot graft {RecordNaming.StripOverlay(child.GetType().Name)} {child.FormKey}: the winning donor's " +
                $"{RecordNaming.StripOverlay(winner.GetType().Name)} {winner.FormKey} has no settable '{propName}' slot to receive it (Q3).");
        if (prop.GetValue(winner) is IMajorRecord occupant)
        {
            // The winner already carries its OWN (different) record in this slot — the winner's structure wins wholesale;
            // the loser's child is a resolved conflict, reported by the occupant's donor.
            reg.Conflicts.Add(new MergeConflict(child.FormKey, RecordNaming.StripOverlay(child.GetType().Name),
                reg.PlacedBy.TryGetValue(occupant.FormKey, out var by) ? by : reg.CurrentDonor, reg.CurrentDonor));
            return;
        }
        prop.SetValue(winner, RenumberOne(child, dict, stats, reg));
    }

    /// <summary>Graft through a FormKey-less worldspace block struct: pair the loser's block with the winner's by block
    /// NUMBER (X/Y for exterior grids), creating the winner-side block when missing, then graft each leaf cell. The
    /// worldspace family is the only record-nested block tree (interior cells' mod-level tree is walked cell-by-cell in
    /// <see cref="MergeModsInto"/>); an unrecognized block shape THROWS — the loud Q3 refusal, never a silent drop.</summary>
    static void GraftBlock(IMajorRecordGetterEnumerable loserBlock, IList winnerBlocks, string propName,
        IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats, MergePlacement reg)
    {
        switch (loserBlock)
        {
            case IWorldspaceBlockGetter wb:
            {
                var mBlock = winnerBlocks.Cast<object>().OfType<WorldspaceBlock>()
                    .FirstOrDefault(b => b.BlockNumberX == wb.BlockNumberX && b.BlockNumberY == wb.BlockNumberY);
                if (mBlock is null)
                {
                    mBlock = new WorldspaceBlock { BlockNumberX = wb.BlockNumberX, BlockNumberY = wb.BlockNumberY, GroupType = wb.GroupType };
                    winnerBlocks.Add(mBlock);
                }
                foreach (var subG in wb.Items) GraftSubBlock(subG, mBlock, dict, stats, reg);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"cannot graft: unrecognized nested block shape '{loserBlock.GetType().Name}' under '{propName}' — " +
                    "refusing rather than silently dropping its records (Q3).");
        }
    }

    /// <summary>Pair one worldspace SUB-block by number (creating it winner-side when missing) and graft its cells.</summary>
    static void GraftSubBlock(IWorldspaceSubBlockGetter loserSub, WorldspaceBlock winnerBlock,
        IReadOnlyDictionary<FormKey, FormKey> dict, RenumberStats stats, MergePlacement reg)
    {
        var mSub = winnerBlock.Items
            .FirstOrDefault(s => s.BlockNumberX == loserSub.BlockNumberX && s.BlockNumberY == loserSub.BlockNumberY);
        if (mSub is null)
        {
            mSub = new WorldspaceSubBlock { BlockNumberX = loserSub.BlockNumberX, BlockNumberY = loserSub.BlockNumberY, GroupType = loserSub.GroupType };
            winnerBlock.Items.Add(mSub);
        }
        foreach (var cell in loserSub.Items)
        {
            var nk = dict.TryGetValue(cell.FormKey, out var mapped) ? mapped : cell.FormKey;
            if (reg.IsPlaced(cell, nk, out var placed))
            {
                GraftMissingDescendants(cell, placed, dict, stats, reg);
            }
            else
            {
                mSub.Items.Add((Cell)RenumberOne(cell, dict, stats, reg));
            }
        }
    }

    // ======================================================================
    //  3b. STREAMING APPLIER — repoint an existing plugin's refs IN PLACE (plan §2/§3)
    // ======================================================================

    /// <summary>The result of an in-place repoint: success + the on-disk byte size, or a loud Q3 refusal (target not
    /// active / excluded / not on disk / a declared master absent / a sub-0x800 originating record / a serialize fault)
    /// with the file UNTOUCHED.</summary>
    /// <summary><paramref name="RemapEntries"/> is the size of the remap dict applied — NOT the count of links actually
    /// rewritten in the file (Mutagen's RemapLinks does not report that); a caller must not read it as "links changed".</summary>
    public sealed record RepointResult(bool Success, string? Error, long Bytes, int RemapEntries)
    {
        public static RepointResult Fail(string error) => new(false, error, 0, 0);
    }

    /// <summary>Which of <paramref name="pluginNames"/> are flagged LOCALIZED — the pre-flight for a repoint, run
    /// BEFORE the compaction writes anything.
    ///
    /// <para><see cref="RepointInPlace"/> is reached only AFTER the compacted plugin is already on disk (the reason the
    /// declared-master refusals below are so careful about it), and the in-place write refuses a localized target
    /// outright (<see cref="WriteEngine.LocalizedTargetUnsupportedException"/> — it cannot re-emit one without
    /// corrupting its text). A refusal discovered at that point would strand a half-completed compaction: the target
    /// renumbered on disk, its referencers still pointing at the old FormIDs. So the caller asks this FIRST and refuses
    /// the whole operation while everything is still untouched.</para>
    ///
    /// <para>Reads the header only, through the lazy overlay — and a plugin whose header cannot be read IS reported,
    /// as a plugin houseCARL could not classify. It used to be reported as not-localized on the ground that the write's
    /// choke point would refuse anyway; that ground no longer holds and never fully did. What it costs when it is
    /// wrong is not merely EARLINESS: by the time the write refuses, the target is already compacted and the caller is
    /// left with a renumbered plugin whose referencer still points at the old FormIDs. That is the outcome this
    /// pre-flight exists to avoid, so it fails CLOSED.</para></summary>
    public static IReadOnlyList<(string Plugin, string Why)> LocalizedAmong(LoadOrderResolver resolver, IEnumerable<string> pluginNames)
    {
        var view = resolver.Capture();
        var hits = new List<(string, string)>();
        foreach (var name in pluginNames)
        {
            var path = view.PluginPath(name);
            if (path is null) continue;
            try
            {
                // The same decision the write itself would make, through the one home for it — so this pre-flight and
                // the write cannot disagree about a referencer. Each hit carries the reason it is a hit, so the
                // compaction's refusal can say WHERE that referencer's text lives rather than only that it is
                // localized: the caller is being refused over a plugin they did not name, and "it is localized" is not
                // something they can act on.
                //
                // A plugin houseCARL could NOT read lands here too, which is the point: the answer is unknown, and a
                // repoint that rewrote it on the strength of a failed read is the fail-open this decision was moved
                // off a fallible re-read to prevent.
                if (LocalizedStrings.RefusalReasonFor(path, name, view.DataDir) is { } why) hits.Add((name, why));
            }
            // Assess handles an unreadable plugin itself (it becomes a hit); this catch is for a fault in the path
            // handling around it, and it stays best-effort so one bad name cannot take the whole pre-flight down.
            catch { }
        }
        return hits;
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
            return RepointResult.Fail($"repoint target '{pluginName}' is not an active plugin in the load order.{view.AbsenceClause(pluginName)}");
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
        // WriteInPlace hands Mutagen (mirrors WritePatchBuilder.ResolveOwnMasters, which opens them the same way). A
        // declared master ABSENT from the active order is a loud Q3 refusal (a re-serialize couldn't resolve the
        // references into it), file untouched. These overlays exist ONLY to resolve FormID/master-table references on
        // re-serialize — they are not read for localized strings — so the bare CreateFromBinaryOverlay is correct here
        // and the resolver's strings-wiring OpenOverlay choke point is deliberately not needed (matches the in-place lane).
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
                // #314 / PR #315 review 2 — the same bare open WritePatchBuilder.ResolveOwnMasters had (this comment
                // block's own sibling), and the same consequence: an unopenable declared master escaped as an
                // unhandled exception. Worse here, because the caller reaches this only AFTER the compacted plugin is
                // already on disk — the throw discards every per-plugin repoint result and skips the facegen/voice/SEQ
                // carry that follows, on a half-completed compaction. Asked before opening, then wrapped.
                if (view.IsUnopenable(mfn))
                    return RepointResult.Fail(
                        $"cannot re-serialize '{pluginName}' in place: its declared master '{mfn}' is ACTIVE but cannot be " +
                        "opened by houseCARL (see load_order_status for the reason), so a faithful re-serialize can't " +
                        "resolve the references into it. Repair or remove that plugin in MO2 and retry. The file is UNTOUCHED.");
                ISkyrimModGetter ov;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE); }
                catch (Exception ex)
                {
                    return RepointResult.Fail(
                        $"cannot re-serialize '{pluginName}' in place: its declared master '{mfn}' could not be opened " +
                        $"({WriteEngine.Describe(ex)}). Repair or remove that plugin in MO2 and retry. The file is UNTOUCHED.");
                }
                overlays.Add((IDisposable)ov);
                resolved.Add(ov);
            }

            try { WriteEngine.WriteInPlace(targetMod, resolved, path, resolver.DataDir); }
            // The localized-target refusal is its own whole sentence, and it happens BEFORE the serialize — so the
            // generic arm below would both misattribute it and append a sub-0x800 note about a cause that isn't this
            // one. The service pre-flight (LocalizedAmong) normally refuses long before a referencer gets here; this is
            // the backstop for the path that reaches it anyway.
            catch (LocalizedTargetUnsupportedException ex) { return RepointResult.Fail(ex.Message); }
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
