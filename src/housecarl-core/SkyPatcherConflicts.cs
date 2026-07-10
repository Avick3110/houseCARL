namespace HousecarlCore;

/// <summary>
/// The SkyPatcher INI-vs-INI CONFLICT detector (Wave 2 of the distributor subsystem; plan
/// dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md §3.2.4): group SET-class operations by
/// (target record, target field) across one type folder's ordered, game-visible line union and flag
/// the places two files write the SAME field of the SAME target with DIFFERENT values — the
/// later-sorted file wins (grammar §2 "Conflict resolution"), which is exactly the collision class a
/// modder can't see without reading every INI.
///
/// <para><b>Report-only (locked v1 call, plan §8).</b> The detector names the collision, the entries
/// in apply order, and the winner; WHICH value should win is the judgment half that stays with the
/// agent — a tool that auto-decides merges would be a silent-wrong-answer machine (Q3).</para>
///
/// <para><b>What counts as a conflict.</b> Only SET-class semantics (a literal last-write-wins field
/// write: set / self-copy / vector-component / colour-channel / model path / flagBool / dict-set /
/// Teaches). Add/remove/mult/collection ops ACCUMULATE by design (grammar §2) and are not conflicts;
/// HARD ops have no static value to compare. Targets: the line's PRIMARY filter tokens (FormID
/// normalized through the one <see cref="SkyPatcherOverlay.TryFormKey"/> recognizer; EditorIDs
/// case-folded), or BROAD ("every record of the type") when the line has no bare primary filter —
/// broad overlaps everything. A line whose applicability ALSO hangs on other filters (keywords,
/// restrictTo…, gates) is flagged <see cref="SkyPatcherConflictEntry.Conditional"/>: whether the
/// collision is real then depends on those filters, and the report says so rather than guessing
/// either way.</para>
/// </summary>
public static class SkyPatcherConflicts
{
    /// <summary>One same-field, same-target collision: every SET in apply order (the LAST one wins).</summary>
    public sealed record SkyPatcherConflict(
        string Subfolder,
        string Field,
        string Target,
        IReadOnlyList<SkyPatcherConflictEntry> Entries)
    {
        /// <summary>The write the DLL leaves in place — the last entry in apply order.</summary>
        public SkyPatcherConflictEntry Winner => Entries[^1];
        /// <summary>True when ANY entry's applicability also hangs on non-primary filters.</summary>
        public bool Conditional => Entries.Any(e => e.Conditional);
    }

    /// <summary>One conflicting write. <see cref="Conditional"/> = the line carries filters beyond the
    /// primary, so whether it actually applies to the target depends on them.</summary>
    public sealed record SkyPatcherConflictEntry(string File, int Line, string Op, string Value, bool Conditional);

    /// <summary>All records of the type — the target token a primary-filter-less line writes.</summary>
    const string Broad = "*";

    /// <summary>Detect the same-field set collisions in one folder's ordered, game-visible union.</summary>
    public static IReadOnlyList<SkyPatcherConflict> Detect(
        SkyPatcherDiscovery.FolderScan folder, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap)
    {
        var conflicts = new List<SkyPatcherConflict>();
        if (folder.Catalog is null) return conflicts;
        var maps = fieldMap.ForSubfolder(folder.Subfolder);

        // ---- collect every SET-class event in apply order (seq = the apply-order index) ----
        var events = new List<(int seq, string file, int line, string op, string value, string field, IReadOnlyList<string> targets, bool conditional)>();
        foreach (var ol in SkyPatcherDiscovery.OrderedLines(folder))
        {
            if (ol.Parsed.Kind != SkyPatcherLineKind.Patch) continue;
            var filters = new List<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)>();
            var ops = new List<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)>();
            bool unknown = false;
            foreach (var seg in ol.Parsed.Segments)
            {
                var cls = catalog.Classify(folder.Catalog, seg.Key);
                switch (cls.Role)
                {
                    case SkyPatcherKeyRole.Filter: filters.Add((seg, cls)); break;
                    case SkyPatcherKeyRole.Operation: ops.Add((seg, cls)); break;
                    default: unknown = true; break;
                }
            }
            if (unknown || ops.Count == 0) continue;   // an unresolvable line can't be honestly grouped (the overlay warns on it)

            var (targets, conditional) = TargetsOf(filters);
            foreach (var (seg, cls) in ops)
            {
                if (cls.Operation!.Tractability == SkyPatcherTractability.Hard) continue;
                var field = SetFieldOf(maps, seg.Key);
                if (field is null) continue;   // accumulating / unmapped — not a last-write-wins collision
                events.Add((events.Count, ol.File, ol.LineNumber, seg.Key, (seg.RawValue ?? "").Trim(), field, targets, conditional));
            }
        }

        // ---- group by field, then by target token in ONE forward pass (review fold: the per-token
        //      re-scan was O(tokens × events) — quadratic exactly when most lines target distinct
        //      records, the common shape). Broad events keep their own group (the broad-vs-broad
        //      view) AND append to every explicit token's group (broad collides with everything).
        foreach (var fieldGroup in events.GroupBy(e => e.field, StringComparer.Ordinal))
        {
            var byToken = new Dictionary<string, List<(int seq, string file, int line, string op, string value, bool conditional)>>(StringComparer.OrdinalIgnoreCase);
            var broad = new List<(int seq, string file, int line, string op, string value, bool conditional)>();
            foreach (var e in fieldGroup)
                foreach (var token in e.targets)
                    if (token == Broad) broad.Add((e.seq, e.file, e.line, e.op, e.value, e.conditional));
                    else (byToken.TryGetValue(token, out var l) ? l : byToken[token] = new()).Add((e.seq, e.file, e.line, e.op, e.value, e.conditional));

            foreach (var (token, own) in byToken.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                // Explicit-target group + every broad write of the same field, merged back into apply order.
                Emit(conflicts, folder.Subfolder, fieldGroup.Key, token, own.Concat(broad).OrderBy(h => h.seq).ToList());
            Emit(conflicts, folder.Subfolder, fieldGroup.Key, Broad, broad);
        }
        return conflicts;
    }

    static void Emit(List<SkyPatcherConflict> conflicts, string subfolder, string field, string token,
        IReadOnlyList<(int seq, string file, int line, string op, string value, bool conditional)> hits)
    {
        if (hits.Select(e => e.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return;
        if (hits.Select(e => e.value).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return;
        conflicts.Add(new SkyPatcherConflict(subfolder, field,
            token == Broad ? $"(all {subfolder} records)" : token,
            hits.Select(e => new SkyPatcherConflictEntry(e.file, e.line, e.op, e.value, e.conditional)).ToList()));
    }

    /// <summary>The line's target tokens from its PRIMARY filter (normalized FormKey / case-folded
    /// EditorID), or BROAD when no bare primary names records. Conditional = any other filter present
    /// (whether the line hits the target then depends on record state the detector doesn't evaluate).</summary>
    static (IReadOnlyList<string> targets, bool conditional) TargetsOf(
        IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters)
    {
        var tokens = new List<string>();
        bool conditional = false;
        foreach (var (seg, cls) in filters)
        {
            if (cls.Filter!.Kind == SkyPatcherFilterKind.Primary && (cls.Connective ?? "") == "")
                foreach (var v in seg.Values)
                    tokens.Add(v.Address is { IsFormId: true } a && SkyPatcherOverlay.TryFormKey(a, out var fk)
                        ? fk.ToString()
                        : v.Raw.ToLowerInvariant());
            else
                conditional = true;   // Excluded-primary, crosscutting, restrictTo, gates — narrows applicability
        }
        return tokens.Count > 0 ? (tokens, conditional) : (new[] { Broad }, conditional);
    }

    /// <summary>The last-write-wins SET-class semantics — a later write of the same field/target
    /// REPLACES an earlier one, the collision class this detector reports.</summary>
    internal static readonly IReadOnlySet<SkyPatcherOpSemantic> SetClassSemantics = new HashSet<SkyPatcherOpSemantic>
    {
        SkyPatcherOpSemantic.Set, SkyPatcherOpSemantic.SetFromOwnField, SkyPatcherOpSemantic.ModelPath,
        SkyPatcherOpSemantic.VecComponent, SkyPatcherOpSemantic.ColorChannel, SkyPatcherOpSemantic.FlagBool,
        SkyPatcherOpSemantic.DictSet, SkyPatcherOpSemantic.TeachSpell, SkyPatcherOpSemantic.TeachSkill,
    };

    /// <summary>The accumulating / stateful / collection semantics — order matters but nothing is
    /// silently dropped, so they are NOT conflicts (grammar §2). Together with
    /// <see cref="SetClassSemantics"/> this must cover EVERY <see cref="SkyPatcherOpSemantic"/> member —
    /// the conflicts guard pins that partition, so a future semantic can't silently default to
    /// "accumulating" and make the detector under-report (review finding).</summary>
    internal static readonly IReadOnlySet<SkyPatcherOpSemantic> AccumulatingSemantics = new HashSet<SkyPatcherOpSemantic>
    {
        SkyPatcherOpSemantic.Mult, SkyPatcherOpSemantic.AddNumeric, SkyPatcherOpSemantic.FlagsSet,
        SkyPatcherOpSemantic.FlagsRemove, SkyPatcherOpSemantic.AddForm, SkyPatcherOpSemantic.RemoveForm,
        SkyPatcherOpSemantic.ReplaceForm, SkyPatcherOpSemantic.ClearList, SkyPatcherOpSemantic.AddEntry,
        SkyPatcherOpSemantic.AddEntryOnce, SkyPatcherOpSemantic.RemoveEntry, SkyPatcherOpSemantic.RemoveEntryByCount,
        SkyPatcherOpSemantic.ReplaceEntry, SkyPatcherOpSemantic.MultCount, SkyPatcherOpSemantic.RemoveByKeyword,
        SkyPatcherOpSemantic.DictMult, SkyPatcherOpSemantic.BipedSlotsSet, SkyPatcherOpSemantic.BipedSlotsRemove,
        SkyPatcherOpSemantic.SetEntryCount,
    };

    /// <summary>The field signature a SET-class op writes (null = accumulating / unmapped — not a
    /// last-write-wins collision). FlagBool includes the flag (two different booleans of one flags
    /// leaf don't collide); vector/colour components include the component; dict sets the key.</summary>
    static string? SetFieldOf(IReadOnlyList<RecordMap> maps, string opName)
    {
        foreach (var m in maps)
        {
            var op = m.Ops.GetValueOrDefault(opName);
            if (op is null || op.IsUnmapped) continue;
            if (!SetClassSemantics.Contains(op.Semantic)) return null;
            return op.Semantic switch
            {
                SkyPatcherOpSemantic.VecComponent or SkyPatcherOpSemantic.ColorChannel => $"{op.Path}[{op.Component}]",
                SkyPatcherOpSemantic.FlagBool => $"{op.Path}({op.Flag})",
                SkyPatcherOpSemantic.DictSet => $"{op.Path}[{op.Key}]",
                _ => op.Path,
            };
        }
        return null;
    }
}
