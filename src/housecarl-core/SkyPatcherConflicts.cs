using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>The SkyPatcher INI-vs-INI conflict and ITM detector — report-only; the report classes and what counts as a conflict are in docs/architecture/skypatcher-layer.md.</summary>
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

    /// <summary>One conflicting write; <see cref="Conditional"/> means the line carries filters beyond the primary, so whether it applies to the target depends on them.</summary>
    public sealed record SkyPatcherConflictEntry(string File, int Line, string Op, string Value, bool Conditional);

    /// <summary>One file's ITM-class finding for one field — the dead writes only, in line order; the entry count is the physical dead-write count.</summary>
    public sealed record SkyPatcherItm(
        string Subfolder,
        string Field,
        string File,
        IReadOnlyList<SkyPatcherItmEntry> Entries);

    /// <summary>One dead write: its line, op, value, target token(s), and the nearest later same-file line covering each target; <see cref="Conditional"/> is informational, since the overwrite is unconditional.</summary>
    public sealed record SkyPatcherItmEntry(
        int Line, string Op, string Value, string Targets, bool Conditional, IReadOnlyList<int> KillerLines);

    /// <summary>One cross-INI duplicate: two or more files write the same field/target with one distinct value; same entry shape as a conflict, differing only by the value gate.</summary>
    public sealed record SkyPatcherDuplicate(
        string Subfolder,
        string Field,
        string Target,
        IReadOnlyList<SkyPatcherConflictEntry> Entries)
    {
        /// <summary>True when ANY entry's applicability also hangs on non-primary filters.</summary>
        public bool Conditional => Entries.Any(e => e.Conditional);
    }

    /// <summary>All report classes from the one detection pass.</summary>
    public sealed record Report(
        IReadOnlyList<SkyPatcherConflict> Conflicts,
        IReadOnlyList<SkyPatcherItm> Itms,
        IReadOnlyList<SkyPatcherDuplicate> Duplicates);

    /// <summary>All records of the type — the target token a primary-filter-less line writes.</summary>
    const string Broad = "*";

    /// <summary>Detect the same-field set collisions and the intra-file dead lines in one folder's ordered, game-visible union.</summary>
    public static Report Detect(
        SkyPatcherDiscovery.FolderScan folder, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap)
    {
        var conflicts = new List<SkyPatcherConflict>();
        var itms = new List<SkyPatcherItm>();
        var duplicates = new List<SkyPatcherDuplicate>();
        if (folder.Catalog is null) return new Report(conflicts, itms, duplicates);
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

        // ---- one forward pass, grouping by field then by target token; a broad event keeps its own group and also joins every explicit token's ----
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
                Emit(conflicts, duplicates, folder.Subfolder, fieldGroup.Key, token, own.Concat(broad).OrderBy(h => h.seq).ToList());
            Emit(conflicts, duplicates, folder.Subfolder, fieldGroup.Key, Broad, broad);
        }

        // ---- intra-file dead writes: each file's events walked BACKWARD, keeping the nearest later unconditional coverer per token, with broad coverage tracked separately ----
        foreach (var fileFieldGroup in events.GroupBy(e => (e.file, e.field)))
        {
            var evs = fileFieldGroup.ToList();   // already in seq (= line) order by construction
            if (evs.Count < 2) continue;
            var nearestCoverer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int broadCovererLine = -1;
            var dead = new List<SkyPatcherItmEntry>();
            for (int i = evs.Count - 1; i >= 0; i--)
            {
                var e = evs[i];
                var killers = new List<int>();
                bool allCovered = true;
                foreach (var t in e.targets)
                {
                    // The nearest later unconditional line covering t: its own-token coverer or the nearest broad.
                    int k = t == Broad ? broadCovererLine
                        : nearestCoverer.TryGetValue(t, out var own)
                            ? (broadCovererLine < 0 ? own : Math.Min(own, broadCovererLine))
                            : broadCovererLine;
                    if (k < 0) { allCovered = false; break; }
                    if (!killers.Contains(k)) killers.Add(k);
                }
                if (allCovered)
                {
                    killers.Sort();
                    dead.Add(new SkyPatcherItmEntry(e.line, e.op, e.value,
                        e.targets is [Broad] ? $"(all {folder.Subfolder} records)" : string.Join(", ", e.targets),
                        e.conditional, killers));
                }
                if (!e.conditional)   // walking backward, this event is now the nearest coverer for its tokens
                {
                    if (e.targets.Contains(Broad)) broadCovererLine = e.line;
                    else foreach (var t in e.targets) nearestCoverer[t] = e.line;
                }
            }
            if (dead.Count > 0)
            {
                dead.Reverse();   // back to line order
                itms.Add(new SkyPatcherItm(folder.Subfolder, fileFieldGroup.Key.field, fileFieldGroup.Key.file, dead));
            }
        }
        return new Report(conflicts, itms, duplicates);
    }

    static void Emit(List<SkyPatcherConflict> conflicts, List<SkyPatcherDuplicate> duplicates,
        string subfolder, string field, string token,
        IReadOnlyList<(int seq, string file, int line, string op, string value, bool conditional)> hits)
    {
        if (hits.Select(e => e.file).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return;
        var target = token == Broad ? $"(all {subfolder} records)" : token;
        var entries = hits.Select(e => new SkyPatcherConflictEntry(e.file, e.line, e.op, e.value, e.conditional)).ToList();
        // One distinct value across ≥2 files = the cross-INI DUPLICATE class (ITM); ≥2 = a conflict.
        if (hits.Select(e => e.value).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            duplicates.Add(new SkyPatcherDuplicate(subfolder, field, target, entries));
        else
            conflicts.Add(new SkyPatcherConflict(subfolder, field, target, entries));
    }

    /// <summary>The one bare-primary test — a primary-kind filter segment with no connective; shared by this grouping and the layer no-op scan so the two cannot diverge.</summary>
    public static bool IsBarePrimary(SkyPatcherKeyClass cls)
        => cls.Role == SkyPatcherKeyRole.Filter
           && cls.Filter!.Kind == SkyPatcherFilterKind.Primary && (cls.Connective ?? "") == "";

    /// <summary>Collect one patch line's explicit primary targets for the no-op scan; a line with operations but no bare primary is counted into <paramref name="broadLines"/>.</summary>
    public static void CollectExplicitPrimaryTargets(
        SkyPatcherLine parsed, SkyPatcherCatalog catalog, SkyPatcherRecordCatalog recordCatalog,
        ISet<FormKey> forms, ISet<string> editorIds, ref int broadLines)
    {
        if (parsed.Kind != SkyPatcherLineKind.Patch) return;
        bool hasOp = false, hasExplicit = false;
        foreach (var seg in parsed.Segments)
        {
            var cls = catalog.Classify(recordCatalog, seg.Key);
            if (cls.Role == SkyPatcherKeyRole.Operation) hasOp = true;
            else if (IsBarePrimary(cls))
                foreach (var v in seg.Values)
                {
                    hasExplicit = true;
                    if (v.Address is { IsFormId: true } a && SkyPatcherOverlay.TryFormKey(a, out var fk)) forms.Add(fk);
                    else editorIds.Add(v.Raw);
                }
        }
        if (hasOp && !hasExplicit) broadLines++;
    }

    /// <summary>The no-op candidate test: a SET-class op whose before equals after, excluding the deliberate 'none' leave-unchanged value.</summary>
    public static bool IsNoOpWrite(SkyPatcherOverlay.SkyPatcherAppliedOp a, RecordMap? map)
    {
        if (a.Before is null || a.Before != a.After) return false;
        if (a.RawValue.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        var op = map?.Ops.GetValueOrDefault(a.Op);
        return op is not null && !op.IsUnmapped && SetClassSemantics.Contains(op.Semantic);
    }

    /// <summary>The line's target tokens from its primary filter, or BROAD when no bare primary names records; conditional means any other filter is present.</summary>
    static (IReadOnlyList<string> targets, bool conditional) TargetsOf(
        IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters)
    {
        var tokens = new List<string>();
        bool conditional = false;
        foreach (var (seg, cls) in filters)
        {
            if (IsBarePrimary(cls))
                foreach (var v in seg.Values)
                    tokens.Add(v.Address is { IsFormId: true } a && SkyPatcherOverlay.TryFormKey(a, out var fk)
                        ? FormIdToken.Of(fk)
                        : v.Raw.ToLowerInvariant());
            else
                conditional = true;   // Excluded-primary, crosscutting, restrictTo, gates — narrows applicability
        }
        return tokens.Count > 0 ? (tokens, conditional) : (new[] { Broad }, conditional);
    }

    /// <summary>The last-write-wins SET-class semantics — the collision class this detector reports; public because the layer no-op scan uses the same partition.</summary>
    public static readonly IReadOnlySet<SkyPatcherOpSemantic> SetClassSemantics = new HashSet<SkyPatcherOpSemantic>
    {
        SkyPatcherOpSemantic.Set, SkyPatcherOpSemantic.SetFromOwnField, SkyPatcherOpSemantic.ModelPath,
        SkyPatcherOpSemantic.VecComponent, SkyPatcherOpSemantic.ColorChannel, SkyPatcherOpSemantic.FlagBool,
        SkyPatcherOpSemantic.DictSet, SkyPatcherOpSemantic.TeachSpell, SkyPatcherOpSemantic.TeachSkill,
    };

    /// <summary>The accumulating semantics, which are NOT conflicts; the skypatcher-conflicts-guard probe pins that this and <see cref="SetClassSemantics"/> partition every <see cref="SkyPatcherOpSemantic"/> member.</summary>
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

    /// <summary>The field signature a SET-class op writes (null = accumulating or unmapped); flagBool includes the flag, vector and colour ops the component, a dict set the key.</summary>
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
