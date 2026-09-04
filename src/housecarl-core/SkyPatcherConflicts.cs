using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>
/// The SkyPatcher INI-vs-INI CONFLICT detector: group SET-class operations by
/// (target record, target field) across one type folder's ordered, game-visible line union and flag
/// the places two files write the SAME field of the SAME target with DIFFERENT values — the
/// later-sorted file wins, which is exactly the collision class a modder can't see without reading
/// every INI.
///
/// <para><b>Report-only.</b> The detector names the collision, the entries in apply order, and the
/// winner; WHICH value should win stays with the agent — a tool that auto-decided merges would be
/// returning silently wrong answers.</para>
///
/// <para><b>What counts as a conflict.</b> Only SET-class semantics (a literal last-write-wins field
/// write: set / self-copy / vector-component / colour-channel / model path / flagBool / dict-set /
/// Teaches). Add/remove/mult/collection ops ACCUMULATE by design and are not conflicts;
/// HARD ops have no static value to compare. Targets: the line's PRIMARY filter tokens (FormID
/// normalized through the one <see cref="SkyPatcherOverlay.TryFormKey"/> recognizer; EditorIDs
/// case-folded), or BROAD ("every record of the type") when the line has no bare primary filter —
/// broad overlaps everything. A line whose applicability ALSO hangs on other filters (keywords,
/// restrictTo…, gates) is flagged <see cref="SkyPatcherConflictEntry.Conditional"/>: whether the
/// collision is real then depends on those filters, and the report says so rather than guessing
/// either way.</para>
///
/// <para><b>Intra-file dead writes (ITM-class).</b>
/// The same pass also reports the SINGLE-file cousin: an earlier SET whose EVERY target a later line
/// of the same file unconditionally re-covers. The dead write is dead weight REGARDLESS of value —
/// same-value twice is the purest form (a write that changes nothing), xEdit's ITM smell at the INI
/// layer — so unlike cross-file conflicts there is no different-values gate. The kill rule is strict
/// so a DEAD verdict is never hedged; a looser per-token rule would call a still-live line
/// removable. ALL of the write's targets must be re-covered — a
/// multi-target line partially overwritten stays live for the rest, and a BROAD write is covered
/// only by a later broad (a following explicit line leaves it live for every other record) — and
/// only UNCONDITIONAL later writes kill (a conditional overwriter may not fire, so its victim is not
/// reported; a conditional EARLIER write killed unconditionally is dead either way — applied or not,
/// the later write decides the field). These are a separate report class, not conflicts — no
/// cross-mod judgment call, just same-author redundancy.</para>
///
/// <para><b>Cross-INI duplicate writes (the second ITM class).</b> Two files SET the same field of
/// the same target to the SAME value — the value-identical complement of a conflict (which requires
/// differing values). Nothing is wrong in game, but one copy is redundant: keep either (the LAST
/// would win if they ever diverge). A value-MIXED group stays a conflict only — the duplicate pair
/// inside it is visible in the conflict's own entry list, not double-reported.</para>
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

    /// <summary>One file's ITM-class finding for one field: the DEAD writes only (every entry is
    /// removable — a write only lands here when ALL its targets are unconditionally re-covered by
    /// later lines of the same file), in line order. Entry count IS the physical dead-write count.</summary>
    public sealed record SkyPatcherItm(
        string Subfolder,
        string Field,
        string File,
        IReadOnlyList<SkyPatcherItmEntry> Entries);

    /// <summary>One dead write inside a <see cref="SkyPatcherItm"/>: its line, op, value, the target
    /// token(s) it wrote, and the same-file line(s) whose later unconditional writes re-cover every
    /// target (the nearest coverer per target). <see cref="Conditional"/> = the dead line itself
    /// carries non-primary filters — informational only; the write is dead either way, because the
    /// overwrite is unconditional.</summary>
    public sealed record SkyPatcherItmEntry(
        int Line, string Op, string Value, string Targets, bool Conditional, IReadOnlyList<int> KillerLines);

    /// <summary>One cross-INI duplicate: ≥2 files write the same field/target with ONE distinct value
    /// (case-insensitive). Same entry shape as a conflict — the classes differ only by the value gate.</summary>
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

    /// <summary>Detect the same-field set collisions (INI-vs-INI) and the intra-file dead lines
    /// (ITM-class) in one folder's ordered, game-visible union.</summary>
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

        // ---- group by field, then by target token in ONE forward pass; a per-token re-scan would be
        //      O(tokens × events), quadratic exactly when most lines target distinct records — the
        //      common shape. Broad events keep their own group (the broad-vs-broad view) AND append
        //      to every explicit token's group, because broad collides with everything.
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

        // ---- intra-file dead writes (ITM-class): a later line of the SAME file unconditionally
        //      re-covers EVERY target of an earlier write. A file's lines are contiguous in apply
        //      order (files apply whole, in filename sort), so same-file coverage needs only that
        //      file's own events — walked BACKWARD with a running token → nearest-unconditional-
        //      coverer map: O(events × targets-per-event), not a per-token re-scan. Only
        //      unconditional writes enter the map (a conditional
        //      overwriter may not fire, so it kills nothing); broad coverage is tracked separately
        //      (broad covers every explicit token; nothing but broad covers broad). ----
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
                    // The nearest later unconditional line covering t: its own-token coverer or the
                    // nearest broad, whichever sits closer (both are later than e by construction).
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

    /// <summary>The one bare-primary test — a filter segment that NAMES records outright (primary
    /// kind, no Excluded/other connective). Shared by the conflict/ITM grouping and the layer no-op
    /// scan's target collection so the two recognisers can't silently diverge.</summary>
    public static bool IsBarePrimary(SkyPatcherKeyClass cls)
        => cls.Role == SkyPatcherKeyRole.Filter
           && cls.Filter!.Kind == SkyPatcherFilterKind.Primary && (cls.Connective ?? "") == "";

    /// <summary>Collect one patch line's explicit PRIMARY targets for the no-op (true-ITM) scan:
    /// FormID values → <paramref name="forms"/>, non-FormID values → raw EditorID strings (the caller
    /// resolves them against its folder's record types). A line with operations but NO bare primary
    /// is counted into <paramref name="broadLines"/> — it writes type-wide and the scan's note says
    /// it is only evaluated against the explicitly-targeted records.</summary>
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

    /// <summary>The no-op (true-ITM) candidate test the layer scan applies to a replay's applied ops:
    /// a SET-class op whose before == after leaf token (the overlay's documented no-op contract),
    /// excluding deliberate 'none' leave-unchanged values. Accumulating before==after cases (e.g.
    /// re-adding a present keyword) are NOT this class — they stay skypatcher_read's lane.</summary>
    public static bool IsNoOpWrite(SkyPatcherOverlay.SkyPatcherAppliedOp a, RecordMap? map)
    {
        if (a.Before is null || a.Before != a.After) return false;
        if (a.RawValue.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return false;
        var op = map?.Ops.GetValueOrDefault(a.Op);
        return op is not null && !op.IsUnmapped && SetClassSemantics.Contains(op.Semantic);
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
            if (IsBarePrimary(cls))
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
    /// REPLACES an earlier one, the collision class this detector reports. Public: the layer no-op
    /// (true-ITM) scan uses the same partition to flag only literal same-value SETs.</summary>
    public static readonly IReadOnlySet<SkyPatcherOpSemantic> SetClassSemantics = new HashSet<SkyPatcherOpSemantic>
    {
        SkyPatcherOpSemantic.Set, SkyPatcherOpSemantic.SetFromOwnField, SkyPatcherOpSemantic.ModelPath,
        SkyPatcherOpSemantic.VecComponent, SkyPatcherOpSemantic.ColorChannel, SkyPatcherOpSemantic.FlagBool,
        SkyPatcherOpSemantic.DictSet, SkyPatcherOpSemantic.TeachSpell, SkyPatcherOpSemantic.TeachSkill,
    };

    /// <summary>The accumulating / stateful / collection semantics — order matters but nothing is
    /// silently dropped, so they are NOT conflicts. Together with <see cref="SetClassSemantics"/>
    /// this must cover EVERY <see cref="SkyPatcherOpSemantic"/> member; a new semantic left out of
    /// both defaults to "accumulating" and makes the detector under-report.</summary>
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
