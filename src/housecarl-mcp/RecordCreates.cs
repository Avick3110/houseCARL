namespace HousecarlMcp;

internal sealed partial class RecordWrites
{
    /// <summary>Create brand-new records in one patch — the sibling of <see cref="ApplyEdits"/>, and the one-shot route for
    /// a nested unit whose child names a same-call sibling by editorid. Each new record's FormID is auto-allocated at 0x800
    /// and above and reported. All-or-nothing, one serialize for the lot, and originals are never touched.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback,
        string? target, bool inPlace, bool acknowledge, bool replace)
    {
        if (records is null || records.Count == 0)
            return WritePatchBuilder.CreateOutcome.Fail("no records to create supplied — pass one or more {record_type, editorid, operations?, parent?, collection?, grid?} specs.");

        var problems = new List<string>();
        var specs = new List<WritePatchBuilder.CreateSpec>(records.Count);
        // One write door for the whole call: parent= is the only token here that can be a FormID.
        var door = _host.OpenWriteFormIdDoor();
        // The editorids this call declares: a parent naming one of them is a sibling reference, not a FormID.
        var siblings = new HashSet<string>(
            records.Where(x => !string.IsNullOrWhiteSpace(x.Editorid)).Select(x => x.Editorid!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < records.Count; r++)
        {
            var rec = records[r];
            // records[r] is the create surface's own member word, so a refusal names the spec the caller can act on.
            var where = $"records[{r}]";
            var spec = BuildCreateSpec(door, rec.RecordType, rec.Editorid, rec.Operations ?? Array.Empty<BulkOp>(), rec.Parent, rec.Collection, rec.Grid, where, problems, siblings);
            if (spec is not null) specs.Add(spec);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) across {records.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(specs, patchName, into, fullReadback, target, inPlace, acknowledge, replace);
    }

    /// <summary>Build one core <see cref="WritePatchBuilder.CreateSpec"/> from wire parts, shared by the single create and
    /// the batch. Every problem is tagged with <paramref name="where"/>, and this returns null iff this record contributed any.</summary>
    WritePatchBuilder.CreateSpec? BuildCreateSpec(FormIdDoor door, string? recordType, string? editorid, IReadOnlyList<BulkOp> operations,
        string? parent, string? collection, string? grid, string where, List<string> problems,
        IReadOnlySet<string>? siblingEditorids = null)
    {
        var prefix = where + ": ";
        int before = problems.Count;

        // parent= takes an EditorID as well as a FormID, so only a runtime FormID is judged here.
        bool parentIsSibling = parent is not null && siblingEditorids is not null && siblingEditorids.Contains(parent.Trim());
        if (!parentIsSibling && door.RuntimeRefusal(parent) is { } parentRefusal) problems.Add($"{prefix}parent: {parentRefusal}");

        string? catalogName = null;
        if (string.IsNullOrWhiteSpace(recordType))
            problems.Add($"{prefix}record_type is required (a catalog name like 'Keyword'/'Spell'/'Weapon' or a 4-char signature like 'KYWD').");
        else
        {
            try
            {
                var types = _host.Types.Resolve(recordType.Trim());
                if (types.Count != 1)
                    problems.Add($"{prefix}record_type '{recordType}' is ambiguous ({types.Count} matches) — use a specific catalog name (e.g. one of: {string.Join(", ", types.Select(t => RecordNaming.StripGetterInterface(t.Name)))}).");
                else catalogName = RecordNaming.StripGetterInterface(types[0].Name);
            }
            catch (ArgumentException ex) { problems.Add($"{prefix}{ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(editorid))
            problems.Add($"{prefix}editorid is required — the EditorID the new record is referenced by (e.g. in SkyPatcher/SPID).");

        // Map each field op to a core WriteRequest rooted at the create type, only once the type resolved.
        var edits = new List<WriteRequest>(operations.Count);
        if (catalogName is not null)
            for (int i = 0; i < operations.Count; i++)
            {
                var req = MapCreateEdit(operations[i], i, catalogName, out var err);
                if (err is not null) problems.Add($"{prefix}{err}"); else edits.Add(req!);
            }

        if (problems.Count != before) return null;
        return new WritePatchBuilder.CreateSpec
        {
            RecordType = catalogName!, EditorId = editorid!.Trim(), Edits = edits,
            ParentRef = string.IsNullOrWhiteSpace(parent) ? null : parent.Trim(),
            IntoCollection = string.IsNullOrWhiteSpace(collection) ? null : collection.Trim(),
            Grid = string.IsNullOrWhiteSpace(grid) ? null : grid.Trim(),
        };
    }

    /// <summary>Resolve the folder-per-patch output, then drive the core multi-record create and serialize under the write gate. Shared by the single and batch create.</summary>
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback,
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
    {
        // In-place is the explicit, named-file opt-in, with the same contract as ApplyEdits.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to create into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place creates into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to create into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");
        // replace= answers the in-place collision refusal and nothing else.
        if (!inPlace && replace)
            return WritePatchBuilder.CreateOutcome.Fail(
                "replace=true overwrites a record the in-place TARGET already defines under the same editorid, and is only meaningful with in_place=true. Drop it, or name the file to create into.");

        lock (_host.WriteGate)                                           // one write at a time, resolve through commit
        {
            var resolver = _host.Resolver;
            var snapshot = _host.ConfiguredRoots();                       // the lane's one read of the MO2 roots and plugin names
            var roots = snapshot.Roots;
            var rulebook = _host.Rulebook;

            if (inPlace)
                return CommitCreateInPlace(resolver, roots, rulebook, specs, target!.Trim(), acknowledge, replace);

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(snapshot, patchName, into, out extend, out created, freshPatch: FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            // Post-write verify steps, each a no-op unless the call created that record kind, and none can fail the create.
            return outcome.Success ? EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver))) : outcome;
        }
    }

    /// <summary>The in-place branch of <see cref="CommitCreate"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.CreateRecordsInPlace"/>; it also runs the patch lane's post-write coverage checks.</summary>
    WritePatchBuilder.CreateOutcome CommitCreateInPlace(
        LoadOrderResolver resolver, Mo2Roots roots, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.CreateSpec> specs,
        string target, bool acknowledge, bool replace = false)
    {
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The shared in-place pre-flight, before the consent prompt.
        if (InPlacePreflight(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } refusal)
            return WritePatchBuilder.CreateOutcome.Fail(refusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the shared first-touch handshake keyed off the resolved path.
        bool already = _host.InPlaceConsent.IsAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceConsent.ParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the created-record verify forced on.
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true,
                                                             replaceExisting: replace);

        // On success record the acknowledgement, run the same post-write checks the patch lane runs, then stamp the marker.
        if (outcome.Success)
        {
            var ackNote = _host.InPlaceConsent.Persist(owesConsent, targetPath, "create");
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(roots, Path.GetDirectoryName(targetPath));
            // enriched.Note FIRST, as the other in-place lanes join: the core's master-grow note must survive.
            var note = JoinNotes(enriched.Note, ackNote, markerNote);
            return note is not null ? enriched with { Note = note } : enriched;
        }
        return outcome;
    }

    /// <summary>The on-disk voice (.fuz/.lip) presence check, a post-write step on a successful create; it never fails the create.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithVoiceCheck(WritePatchBuilder.CreateOutcome outcome, LoadOrderResolver resolver)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        VoiceReport report;
        try { report = VoiceCheck.Run(outcome.OutputPath, outcome.Created, resolver, _host.Assets); }
        catch (Exception ex) { report = VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { Voice = report };
    }

    /// <summary>The per-create result-script binding check, a post-write step like <see cref="EnrichWithVoiceCheck"/>; it never fails the create.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithScriptCheck(WritePatchBuilder.CreateOutcome outcome)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        ScriptBindingReport report;
        try { report = DialogueScriptCheck.Run(outcome.OutputPath, outcome.Created, _host.Assets); }
        catch (Exception ex) { report = ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { ScriptBinding = report };
    }

    /// <summary>The cell structural-shell report, a post-write step naming the world content houseCARL does not author; it never fails the create.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithCellShell(WritePatchBuilder.CreateOutcome outcome)
    {
        bool anyCell = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, CellShellCheck.CellCatalogName, StringComparison.Ordinal)) { anyCell = true; break; }
        if (!anyCell) return outcome;

        CellShellReport report;
        try { report = CellShellCheck.Run(outcome.OutputPath, outcome.Created); }
        catch (Exception ex) { report = CellShellReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { CellShell = report };
    }
}
