using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The shared foundation under compact and merge, the MCP tools being thin policy layers over it; contracts in docs/architecture/write-path.md.</summary>
public static class RemapEngine
{
    /// <summary>The light-master object-ID window as Mutagen enforces it: 0x800–0xFFF INCLUSIVE, 2048 IDs.</summary>
    public const uint EslFloor = FormIdRange.EslWindowFloor;      // 0x800 — the single home is FormIdRange (shared with the write-allocation floor)
    public const uint EslCeiling = FormIdRange.EslWindowCeiling;  // 0xFFF

    // ---- 1. IDENTIFY-PASS — the per-operation reverse-walk ----

    /// <summary>How many identify passes have run, so a test can say a refusal reached the caller without one.</summary>
    internal static int IdentifyPasses;

    /// <summary>One external reference: a record outside the set whose outgoing link points at a FormKey being remapped.</summary>
    public sealed record ExternalRef(string Plugin, FormKey Source, string SourceType, FormKey Target);

    /// <summary>One external OVERRIDE: a record outside the set whose OWN FormKey is in the remap set, so it is a WARN.</summary>
    public sealed record ExternalOverride(string Plugin, FormKey Record, string RecordType);

    /// <summary>One plugin outside the set DECLARING a plugin in it as a master while referencing none of its records.</summary>
    public sealed record MasterDeclarer(string Plugin, IReadOnlyList<string> Declared);

    /// <summary>Why the identify pass could not read a plugin through — two causes with two different remedies.</summary>
    public enum UnscannableCause { Unopenable, EnumerationFault }

    /// <summary>One plugin the identify pass could not read through: its name, its cause, and the reason, recorded unconditionally.</summary>
    public sealed record UnscannablePlugin(string Plugin, UnscannableCause Cause, string Reason);

    /// <summary>The identify-pass result: the references, the referencing and overriding plugins, how many were scanned THROUGH, the fault accounting, the declarers.</summary>
    public sealed record IdentifyResult(
        IReadOnlyList<ExternalRef> Refs,
        IReadOnlyList<string> ExternalPlugins,
        int PluginsScanned,
        int UnscannableRecords,
        IReadOnlyList<string> UnscannableSamples,
        IReadOnlyList<ExternalOverride> Overrides,
        IReadOnlyList<string> ExternalOverriders,
        IReadOnlyList<UnscannablePlugin>? UnscannablePlugins = null,
        IReadOnlyList<MasterDeclarer>? MasterDeclarers = null)
    {
        /// <summary>True when at least one plugin OUTSIDE the transform set references a remapped FormKey.</summary>
        public bool HasExternalReferencers => ExternalPlugins.Count > 0;

    /// <summary>True when at least one plugin OUTSIDE the transform set OVERRIDES a remapped record; it gates nothing.</summary>
        public bool HasExternalOverriders => ExternalOverriders.Count > 0;
    }

    /// <summary>Walk the active order for plugins outside <paramref name="transformSet"/> referencing a <paramref name="targets"/> key, under one capture.</summary>
    /// <param name="readDeclaredMasters">Also read each candidate's HEADER and report the declarer-only dependents.</param>
    public static IdentifyResult IdentifyExternalReferencers(
        LoadOrderResolver resolver, IReadOnlySet<FormKey> targets, IReadOnlySet<string> transformSet,
        bool readDeclaredMasters = false)
    {
        System.Threading.Interlocked.Increment(ref IdentifyPasses);        // the pass's own counter, so a test can say a refusal skipped it
        var view = resolver.Capture();
        var refs = new List<ExternalRef>();
        var externalPlugins = new List<string>();        // load-order order, distinct
        var overrides = new List<ExternalOverride>();     // external plugins that OVERRIDE a remapped record
        var externalOverriders = new List<string>();      // distinct overriding plugins, load-order order
        int scanned = 0, unscannable = 0;
        var unscannableSamples = new List<string>();
        var unscannablePlugins = new List<UnscannablePlugin>();   // plugins whose scan faulted — NOT counted as scanned
        var declarers = new List<MasterDeclarer>();               // plugins declaring a transform-set plugin as a master
        // PluginNames can list a filename twice in a degenerate order, and the listing is by contract DISTINCT.
        var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in resolver.PluginNames)
        {
            if (transformSet.Contains(plugin)) continue;                 // inside the set → its refs are INTERNAL (RemapLinks handles them)
            if (view.ExcludedPlugins.ContainsKey(plugin)) continue;      // unparseable at build — already surfaced by the resolver
            if (!scannedNames.Add(plugin)) continue;                     // a duplicate name in the order — scan and list it once, never double-count
            bool pluginListed = false;
            bool pluginListedOverride = false;
            // The header read faulted: the record walk still runs, but the plugin is named once and never scanned.
            bool headerFaulted = false;

            // The HEADER read, before the records, in its own try: the declarer-only dependent no link walk sees.
            if (readDeclaredMasters)
            {
                List<string> declares;
                try { declares = view.DeclaredMasters(plugin).Where(transformSet.Contains).ToList(); }
                catch (PluginUnreadableException ex)
                {
                    // The file would not open, so the record scan would fail the same way: name it and skip.
                    unscannable++;
                    var inner = ex.InnerException ?? ex;
                    unscannablePlugins.Add(new UnscannablePlugin(plugin, UnscannableCause.Unopenable, $"{inner.GetType().Name}: {inner.Message}"));
                    continue;
                }
                catch (Exception ex)
                {
                    // It opened and the master table would not parse, so the walk FALLS THROUGH to the records.
                    unscannable++;
                    unscannablePlugins.Add(new UnscannablePlugin(plugin, UnscannableCause.EnumerationFault,
                                                                 $"master table unreadable: {ex.GetType().Name}: {ex.Message}"));
                    headerFaulted = true;
                    declares = new List<string>();
                }
                if (declares.Count > 0) declarers.Add(new MasterDeclarer(plugin, declares));
            }

            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // PER-RECORD fault isolation: one record Mutagen cannot parse is counted and sampled.
                    try
                    {
                        // OVERRIDER, by IDENTITY and so before the link test: collected for a warning, never repointed.
                        if (targets.Contains(fk))
                        {
                            overrides.Add(new ExternalOverride(plugin, fk, RecordNaming.StripOverlay(body.GetType().Name)));
                            if (!pluginListedOverride) { externalOverriders.Add(plugin); pluginListedOverride = true; }
                        }
                        // REFERENCER: an outgoing link into the remap set; a DELETED record links to nothing.
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
                        if (unscannableSamples.Count < 5) unscannableSamples.Add($"{plugin} {FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
                if (!headerFaulted) scanned++;                           // counted only once the whole plugin has been read through, header included
            }
            // The file could not be OPENED at all, and the reason is the INNER exception.
            catch (PluginUnreadableException ex)
            {
                // A plugin whose header already faulted is named once, with the first fault's reason.
                if (headerFaulted) continue;
                unscannable++;
                var inner = ex.InnerException ?? ex;
                unscannablePlugins.Add(new UnscannablePlugin(plugin, UnscannableCause.Unopenable, $"{inner.GetType().Name}: {inner.Message}"));
            }
            // The plugin opened and a record threw on TOP-LEVEL enumeration: counted per-plugin, never scanned.
            catch (Exception ex)
            {
                if (headerFaulted) continue;                             // already named, for the fault that came first
                unscannable++;
                unscannablePlugins.Add(new UnscannablePlugin(plugin, UnscannableCause.EnumerationFault,
                                                             $"record enumeration aborted: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        // DECLARER-ONLY: the plugins the record walk did not already find, and not one whose walk FAULTED.
        var alreadyFound = new HashSet<string>(externalPlugins, StringComparer.OrdinalIgnoreCase);
        alreadyFound.UnionWith(externalOverriders);
        alreadyFound.UnionWith(unscannablePlugins.Select(u => u.Plugin));
        var declarerOnly = declarers.Where(d => !alreadyFound.Contains(d.Plugin)).ToList();

        return new IdentifyResult(refs, externalPlugins, scanned, unscannable, unscannableSamples, overrides, externalOverriders,
                                  unscannablePlugins, declarerOnly);
    }

    // ---- 2. BUILD-REMAP-DICT — collision-free new-FormID allocation ----

    /// <summary>A planned remap: the old→new FormKey map, or a named refusal with no map.</summary>
    public sealed record RemapPlan(IReadOnlyDictionary<FormKey, FormKey> Dict, string? Error)
    {
        public bool Success => Error is null;
        public static RemapPlan Fail(string error) => new(new Dictionary<FormKey, FormKey>(), error);
    }

    /// <summary>Assign each of <paramref name="sourceKeys"/> a NEW FormKey under <paramref name="targetModKey"/>, ids running sequentially from <paramref name="floor"/> through <paramref name="ceiling"/> INCLUSIVE.</summary>
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

    // ---- 3a. RENUMBER INTO A FRESH MOD ----

    /// <summary>The result of building a renumbered mod: records copied and renumbered, or a named refusal.</summary>
    public sealed record RenumberResult(bool Success, string? Error, int RecordsCopied, int RecordsRenumbered)
    {
        public static RenumberResult Fail(string error) => new(false, error, 0, 0);
    }

    /// <summary>Copy <paramref name="sources"/> into the (typically fresh) mod <paramref name="target"/> under their new
    /// keys — a source not in <paramref name="dict"/> at its OWN key — then <c>RemapLinks</c>; flat groups only.</summary>
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
                    $"could not duplicate {RecordNaming.StripOverlay(rec.GetType().Name)} {FormIdToken.Of(rec.FormKey)} under {newKey} " +
                    $"({WriteEngine.Describe(ex)}) — the renumber is abandoned with nothing shippable (Q3).");
            }

            if (!TryAddToFlatGroup(target, dup))
                return RenumberResult.Fail(
                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {FormIdToken.Of(rec.FormKey)} lives only in a NESTED group (Cell / placed " +
                    "ref / INFO / navmesh / landscape), which has no flat top-level group to place the duplicate into. The nested " +
                    "duplicate-into placement is a later wave — refusing rather than silently dropping the record (Q3).");

            copied++;
            if (isRenumber) renumbered++;
        }

        // Repoint every internal reference, flat AND nested; the nested limit above is about PLACING, not repointing.
        target.RemapLinks(dict);
        return new RenumberResult(true, null, copied, renumbered);
    }

    /// <summary>Place a record into the target's matching flat top-level group; false when no flat group fits.</summary>
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

    // ---- 3a-NESTED. WHOLE-MOD STRUCTURAL RENUMBER — walks the source mod's STRUCTURE, so parentage survives ----

    /// <summary>Per-call accounting for the structural renumber: records placed, and how many were renumbered.</summary>
    sealed class RenumberStats { public int Copied; public int Renumbered; }

    /// <summary>Copy EVERY record of <paramref name="source"/> into the (fresh) mod <paramref name="target"/> under its
    /// remapped key, rebuilding the FULL nesting and the interior-cell block tree, then <c>RemapLinks</c>.</summary>
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
                            $"{RecordNaming.StripOverlay(rec.GetType().Name)} {FormIdToken.Of(rec.FormKey)} is a flat top-level record but no matching " +
                            $"group was found on the target mod to place its renumbered copy (engine inconsistency, Q3) — the renumber is abandoned with nothing shippable.");
                }
            }

            // 2. INTERIOR cells — the nested-only family with no flat group, re-filed by their NEW FormID digits.
            if (source.Cells is { } cellsGroup)
                foreach (var block in cellsGroup.Records)
                    foreach (var sub in block.SubBlocks)
                        foreach (var cell in sub.Cells)
                        {
                            var renCell = (Cell)RenumberOne(cell, dict, stats);
                            FileInteriorCellByNewId(target, renCell);
                        }

            // 3. Repoint every internal reference, inside the try so a throw is the same structured refusal.
            target.RemapLinks(dict);
        }
        catch (Exception ex)
        {
            return RenumberResult.Fail(
                $"the structural renumber failed ({WriteEngine.Describe(ex)}) — abandoned with nothing shippable (Q3).");
        }

        return new RenumberResult(true, null, stats.Copied, stats.Renumbered);
    }

    /// <summary>Renumber ONE record, then its descendants in place; <paramref name="reg"/> (merge only) registers each.</summary>
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

    /// <summary>Walk a container's child records and renumber each in place: a record is REPLACED, a container of
    /// records is recursed THROUGH, everything else skipped; the tests pin every nesting shape, a skip being silent.</summary>
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
                        // Merge only: a child already placed in M wins, so this stale copy is REMOVED and REPORTED.
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

    /// <summary>File a renumbered INTERIOR cell into the target's Cells block tree by its NEW FormID digits.</summary>
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

    // ---- 3a-MERGE. MULTI-DONOR RENUMBER — build the merged mod. Every donor ORIGINATING record changes identity,
    //  so the dict covers all of them and collision handling applies only to the OBJECT ID ----

    /// <summary>Per-donor merge-remap accounting: how many originating object IDs the donor KEPT vs had RENUMBERED.</summary>
    public sealed record MergeDonorRemap(string Donor, int Kept, int Renumbered);

    /// <summary>A planned multi-donor remap: the UNION old→new dict over every donor's originating records, the
    /// per-donor accounting, or a named refusal with no map.</summary>
    public sealed record MergeRemapPlan(
        IReadOnlyDictionary<FormKey, FormKey> Dict, IReadOnlyList<MergeDonorRemap> Donors, string? Error)
    {
        public bool Success => Error is null;
        public static MergeRemapPlan Fail(string error) =>
            new(new Dictionary<FormKey, FormKey>(), Array.Empty<MergeDonorRemap>(), error);
    }

    /// <summary>Build the merge remap: every donor originating FormKey → one under <paramref name="targetModKey"/>,
    /// KEEPING the object id wherever it is in-window and unclaimed, donors claiming in LOAD ORDER.</summary>
    public static MergeRemapPlan BuildMergeRemap(
        IReadOnlyList<(string Donor, IReadOnlyList<FormKey> Keys)> donorsByLoadOrder,
        ModKey targetModKey, uint floor, uint ceiling)
    {
        if (ceiling < floor) return MergeRemapPlan.Fail($"invalid remap window: ceiling 0x{ceiling:X} < floor 0x{floor:X}.");

        // Pass 1 — keepable ids write straight into the dict, donors in load order; the rest queue as collisions.
        var claimed = new HashSet<uint>();
        var collide = new List<FormKey>();
        var seen = new HashSet<FormKey>();                                 // defensive intra-donor de-dupe, as BuildSequentialRemap does
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

        // Pass 2 — each collision takes the next free id; the in-loop ceiling guard is a defensive invariant.
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

    /// <summary>One cross-donor conflict the merge resolved to the LOAD-ORDER WINNER, reported per losing donor.</summary>
    public sealed record MergeConflict(FormKey Key, string RecordType, string WinnerDonor, string LoserDonor);

    /// <summary>The result of the multi-donor renumber: the accounting and every conflict resolved, or a named refusal.</summary>
    public sealed record MergeResult(
        bool Success, string? Error, int RecordsCopied, int RecordsRenumbered, IReadOnlyList<MergeConflict> Conflicts)
    {
        public static MergeResult Fail(string error) => new(false, error, 0, 0, Array.Empty<MergeConflict>());
    }

    /// <summary>Merge-walk placement registry: every record placed in M so far by its NEW FormKey, with the donor
    /// that placed it, and the conflict list, so every site that resolves a collision reports through one channel.</summary>
    sealed class MergePlacement
    {
        public readonly Dictionary<FormKey, IMajorRecord> Objects = new();
        public readonly Dictionary<FormKey, string> PlacedBy = new();
        public readonly List<MergeConflict> Conflicts = new();
        public string CurrentDonor = "";
        public void Register(FormKey key, IMajorRecord obj) { Objects[key] = obj; PlacedBy[key] = CurrentDonor; }

        /// <summary>True when the mapped key is already placed in M — records the conflict and hands back that object.</summary>
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

    /// <summary>Copy EVERY record of every donor into the (fresh) mod <paramref name="target"/> under its remapped
    /// FormKey, resolving cross-donor conflicts to the LOAD-ORDER WINNER — donors walk in REVERSE load order so the
    /// winner places first — grafting a loser's missing nested children in, then <c>RemapLinks(dict)</c>.</summary>
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
                                    $"{RecordNaming.StripOverlay(rec.GetType().Name)} {FormIdToken.Of(rec.FormKey)} (donor '{name}') is a flat top-level record but no " +
                                    "matching group was found on the target mod to place its merged copy (engine inconsistency, Q3) — the merge is abandoned with nothing shippable.");
                        }
                    }
                }

                // 2. INTERIOR cells, placed cell-by-cell, so a losing donor's own cell still merges.
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

            // 3. Repoint every internal reference, cross-donor ones included, inside the try as above.
            target.RemapLinks(dict);
        }
        catch (Exception ex)
        {
            return MergeResult.Fail(
                $"the multi-donor renumber failed ({WriteEngine.Describe(ex)}) — abandoned with nothing shippable (Q3).");
        }

        return new MergeResult(true, null, stats.Copied, stats.Renumbered, reg.Conflicts);
    }

    /// <summary>Graft a LOSING donor record's nested children into the WINNER's already-placed copy, on the same
    /// discriminators as <see cref="RenumberDescendants"/>: an already-placed child recurses, an unplaced one is
    /// renumbered and APPENDED to the winner's same-named list, and a structural mismatch THROWS.</summary>
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
                        $"{FormIdToken.Of(winner.FormKey)} has no settable record list '{prop.Name}' to receive it (Q3).");
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
                        WinnerList($"{RecordNaming.StripOverlay(childRec.GetType().Name)} {FormIdToken.Of(childRec.FormKey)}")
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

    /// <summary>Graft one singleton-property child: already placed → recurse; the slot empty → renumber and set; the
    /// slot held by a DIFFERENT record → the winner's structure stands and the loser's child is REPORTED.</summary>
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
                $"cannot graft {RecordNaming.StripOverlay(child.GetType().Name)} {FormIdToken.Of(child.FormKey)}: the winning donor's " +
                $"{RecordNaming.StripOverlay(winner.GetType().Name)} {FormIdToken.Of(winner.FormKey)} has no settable '{propName}' slot to receive it (Q3).");
        if (prop.GetValue(winner) is IMajorRecord occupant)
        {
            // The winner's own record holds this slot, so its structure wins and the loser's child is a conflict.
            reg.Conflicts.Add(new MergeConflict(child.FormKey, RecordNaming.StripOverlay(child.GetType().Name),
                reg.PlacedBy.TryGetValue(occupant.FormKey, out var by) ? by : reg.CurrentDonor, reg.CurrentDonor));
            return;
        }
        prop.SetValue(winner, RenumberOne(child, dict, stats, reg));
    }

    /// <summary>Graft through a FormKey-less worldspace block struct, pairing blocks by NUMBER and creating the
    /// winner-side one when missing; an unrecognized block shape THROWS rather than dropping records.</summary>
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

    // ---- 3b. STREAMING APPLIER — repoint an existing plugin's refs IN PLACE ----

    /// <summary>The result of an in-place repoint: success plus the on-disk byte size, or a named refusal with the
    /// file UNTOUCHED. <paramref name="RemapEntries"/> is the size of the dict APPLIED, not links rewritten.</summary>
    public sealed record RepointResult(bool Success, string? Error, long Bytes, int RemapEntries)
    {
        public static RepointResult Fail(string error) => new(false, error, 0, 0);
    }

    /// <summary>Which of <paramref name="pluginNames"/> are flagged LOCALIZED — the pre-flight for a repoint, run
    /// BEFORE the compaction writes anything. It fails CLOSED on a referencer it could not open: that sentence, and
    /// the two-class split it forces, are in docs/architecture/write-path.md.</summary>
    /// <returns>One entry per blocked referencer, each carrying the SHAPE it was blocked on.</returns>
    public static IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> LocalizedAmong(
        LoadOrderResolver resolver, IEnumerable<string> pluginNames)
    {
        var view = resolver.Capture();
        var hits = new List<(string, LocalizedShape, string)>();
        foreach (var name in pluginNames)
        {
            var path = view.PluginPath(name);
            if (path is null) continue;
            try
            {
                // The same decision the write itself would make, through the one home for it.
                if (LocalizedStrings.RefusalShapeFor(path, name, view.DataDir) is { } hit) hits.Add((name, hit.Shape, hit.Why));
            }
            // This catch is for a fault in the path handling, best-effort so one bad name cannot end the pre-flight.
            catch { }
        }
        return hits;
    }

    /// <summary>Repoint plugin <paramref name="pluginName"/>'s outgoing references against <paramref name="dict"/> IN
    /// PLACE — the applier for an EXTERNAL referencer, riding the in-place write lane the service has already gated.
    /// It opens the single plugin mutable, remaps, resolves the target's OWN declared masters, and re-serializes over
    /// itself; any refusal or serialize fault leaves the original file byte-intact.</summary>
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
        try { targetMod = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path)); }
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

        // The target's OWN declared masters as overlays in load order, the faithful re-serialize set WriteInPlace
        // hands Mutagen. They resolve FormID and master-table references only, never localized strings.
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
                // Ask before opening: an unopenable declared master would otherwise escape as an unhandled throw.
                if (view.IsUnopenable(mfn))
                    return RepointResult.Fail(
                        $"cannot re-serialize '{pluginName}' in place: its declared master '{mfn}' is ACTIVE but cannot be " +
                        "opened by houseCARL (see load_order_status for the reason), so a faithful re-serialize can't " +
                        "resolve the references into it. Repair or remove that plugin in MO2 and retry. The file is UNTOUCHED.");
                ISkyrimModGetter ov;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(mpath)); }
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
            // The localized-target refusal is its own sentence, and the generic arm below would misattribute it.
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
