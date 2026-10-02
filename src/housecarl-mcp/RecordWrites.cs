using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Everything the writes area takes from outside itself beyond the shared door.</summary>
internal interface IWriteHost : ILoadOrderHost
{
    /// <summary>A FormID door for a write verb's tokens, which refuses a runtime FormID.</summary>
    FormIdDoor OpenWriteFormIdDoor();

    /// <summary>The configured check, the four roots and the built resolver's plugin names in one hold of the head's index lock.</summary>
    OutputRoots ConfiguredRoots();
}

public sealed partial class LoadOrderService
{
    // ---- writes ----------------------------------------------------------------------------------------

    /// <summary>Every head member this area takes, and nothing else.</summary>
    IWriteHost Host => this;

    /// <summary>Test seam: invoked once inside <see cref="ApplyEdits"/>'s write gate; null in the product.</summary>
    internal static Action? InsideWriteGateForGuard;

    /// <summary>Apply one or more edits as a single patch: map each op to a core PatchEdit, resolve the output, then
    /// drive <see cref="WritePatchBuilder.Apply"/>. All-or-nothing, and a new patch unless <paramref name="into"/> extends one.</summary>
    public WritePatchBuilder.PatchOutcome ApplyEdits(IReadOnlyList<BulkOp> ops, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false, IReadOnlyList<string?>? fromRecords = null, IReadOnlyList<string?>? opOrigins = null)
    {
        if (ops.Count == 0)
            return WritePatchBuilder.PatchOutcome.Fail("no operations supplied.");

        // In-place is the explicit, named-file opt-in; its contract is validated up front (docs/architecture/write-path.md).
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to edit in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place edits an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to edit in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        // Map every op to a core PatchEdit, collecting ALL parse problems first; outside the write gate.
        var edits = new List<WritePatchBuilder.PatchEdit>(ops.Count);
        var problems = new List<string>();
        var editDoor = Host.OpenWriteFormIdDoor();
        for (int i = 0; i < ops.Count; i++)
        {
            // fromRecords[i] is the zip's per-op source record, carried parallel to the op list.
            var edit = MapEdit(editDoor, ops[i], i, out var err,
                fromRecords is not null && i < fromRecords.Count ? fromRecords[i] : null,
                opOrigins is not null && i < opOrigins.Count ? opOrigins[i] : null);
            if (err is not null) problems.Add(err); else edits.Add(edit!);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"refused — {problems.Count} of {ops.Count} operation(s) malformed; NO patch written:\n  - " + string.Join("\n  - ", problems));

        // Lock order is the write gate, then the index lock; contract in docs/architecture/load-order-service.md.
        lock (Host.WriteGate)                                            // one write at a time, resolve through commit
        {
            var resolver = Host.Resolver;                                 // builds/refreshes the index
            // Cannot throw once Resolver succeeded: it derived the roots, nothing empties them, and SetInstance waits for the write gate.
            var snapshot = Host.ConfiguredRoots();                        // the lane's one read of the MO2 roots and plugin names
            var roots = snapshot.Roots;
            var rulebook = Host.Rulebook;
            InsideWriteGateForGuard?.Invoke();                            // test seam; null in the product

            if (inPlace)
            {
                // The overlays must stay OPEN across the whole in-place write, so they are disposed after it returns.
                Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? ipSources = null;
                List<IDisposable>? ipOverlays = null;
                var ipError = PrepareCopyFromSources(resolver, roots, edits, ref ipSources, ref ipOverlays, out var ipEpoch);
                if (ipError is not null)
                {
                    if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose();
                    return WritePatchBuilder.PatchOutcome.Fail(ipError) with { Stamp = ipEpoch };
                }
                try { return ApplyEditsInPlace(resolver, roots, rulebook, edits, target!.Trim(), acknowledge, dryRun, ipSources); }
                finally { if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose(); }
            }

            // A dry run resolves the would-be output path WITHOUT creating the mod folder; the fresh name is only a preview.
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(snapshot, patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            // Pre-resolve any CopyFrom source that is off-order; an active-order source is resolved inside Apply.
            Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null;
            List<IDisposable>? offOrderOverlays = null;
            var cfError = PrepareCopyFromSources(resolver, roots, edits, ref copyFromSources, ref offOrderOverlays, out var cfEpoch);
            if (cfError is not null)
            {
                if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose();
                if (created) RemoveFolderCreatedThisCall(outPath);   // a refused write leaves no orphan folder
                return WritePatchBuilder.PatchOutcome.Fail(cfError) with { Stamp = cfEpoch };
            }
            try
            {
                var outcome = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend, fullReadback, copyFromSources, dryRun);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused write leaves no orphan folder
                return outcome;
            }
            finally { if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose(); }
        }
    }

    /// <summary>Locate every OFF-ORDER CopyFrom source and fetch its version of the target record, holding each overlay
    /// OPEN for the caller to dispose after the serialize; a named refusal if one cannot be located, opened or read.
    /// MUTATES <paramref name="edits"/> to re-spell a path that names an active copy. Contracts in docs/architecture/write-path.md.</summary>
    string? PrepareCopyFromSources(LoadOrderResolver resolver, Mo2Roots roots, IList<WritePatchBuilder.PatchEdit> edits,
        ref Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? sources, ref List<IDisposable>? overlays,
        out OrderStamp? epoch)
    {
        // This helper takes its OWN capture, so its refusals are stamped like every other post-capture outcome.
        // A write pins one resolver whose name table is never rebuilt, so this capture and the engine's cannot
        // disagree about membership, and a body pre-fetched here is only used while the engine still agrees.
        epoch = null;
        if (!edits.Any(e => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))) return null;   // no CopyFrom → no source work
        var view = resolver.Capture();
        epoch = view.Stamp;
        RespellActiveCopySourcePaths(view, edits);   // before the predicate, and before any edit is used as a key
        Mo2Composition? comp = null;
        var problems = new List<string>();
        foreach (var e in edits)
        {
            // The shared predicate the engine consumes through, not a restatement of it.
            if (!WritePatchBuilder.IsOffOrderCopySource(e, view)) continue;   // not a CopyFrom, or active — Apply resolves it off the shared build
            comp ??= Mo2LoadOrder.ReadComposition(roots.ProfileDir);
            var loc = OutputLocations.LocatePluginFileOnDisk(comp, roots, e.FromPlugin!, null);
            if (loc.Error is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' is not in the load order and {loc.Error}"); continue; }
            if (loc.Ambiguous is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' matches several mod folders on disk — pass an exact path to disambiguate."); continue; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(roots.DataDir) ? null : roots.DataDir); }
            catch (Exception ex) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be opened as a Skyrim plugin ({ex.Message})."); continue; }
            IMajorRecordGetter? body;
            try { body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == e.CopySource); }
            catch (Exception ex) { (ov as IDisposable)?.Dispose(); problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be read ({ex.Message})."); continue; }
            if (body is null)
            {
                (ov as IDisposable)?.Dispose();
                // Name WHICH record the file is missing: the target's own version, or the zip's source record.
                problems.Add(e.FromTarget is null
                    ? $"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' does not define or override this record — there is no version of it there to copy."
                    : $"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' does not define or override the SOURCE record {FormIdToken.Of(e.CopySource)} — there is no version of it there to copy from.");
                continue;
            }
            (overlays ??= new()).Add((IDisposable)ov);
            (sources ??= new())[e] = body;   // distinct Path-array refs make each PatchEdit a distinct key under value equality; the indexer is collision-safe regardless
        }
        return problems.Count > 0
            ? $"refused — {problems.Count} CopyFrom source problem(s); NO patch written:\n  - " + string.Join("\n  - ", problems)
            : null;
    }

    /// <summary>Re-spell every <c>CopyFrom</c> source that is a PATH to the ACTIVE copy of a plugin into that plugin's
    /// NAME, in place, before any edit is used as a key. Contract in docs/architecture/write-path.md.</summary>
    static void RespellActiveCopySourcePaths(LoadOrderResolver.IndexView view, IList<WritePatchBuilder.PatchEdit> edits)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)) continue;
            // The same LooksLikePath check the other pole-resolving sites use.
            if (string.IsNullOrWhiteSpace(e.FromPlugin) || !PluginPaths.LooksLikePath(e.FromPlugin!)) continue;
            if (PluginPaths.ActiveNameForPath(view, e.FromPlugin!) is { } activeName)
                edits[i] = e with { FromPlugin = activeName };
        }
    }

    /// <summary>Locate the one <c>source=</c> plugin a forward call shares when the active order does not contain it,
    /// open it once and pre-fetch every requested record's body, handing the overlay back OPEN. Null with a null
    /// <paramref name="error"/> when the source IS active. Contracts in docs/architecture/write-path.md.</summary>
    WritePatchBuilder.OffOrderForwardSource? ResolveOffOrderForwardSource(
        LoadOrderResolver resolver, Mo2Roots roots, string fromPlugin, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs,
        out IDisposable? overlay, out OrderStamp? epoch, out string? error, out string sourceName)
    {
        overlay = null; error = null; sourceName = fromPlugin;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (view.ContainsPlugin(fromPlugin)) return null;      // active — the engine resolves it off the shared build
        if (PluginPaths.LooksLikePath(fromPlugin) && PluginPaths.ActiveNameForPath(view, fromPlugin) is { } activeName)
        {
            sourceName = activeName;                           // a path to the ACTIVE copy — in-order after all
            return null;
        }

        var comp = Mo2LoadOrder.ReadComposition(roots.ProfileDir);
        // offerModParam is false: this tool has no mod= parameter, and a direct path is this lane's disambiguator.
        var loc = OutputLocations.LocatePluginFileOnDisk(comp, roots, fromPlugin, null, offerModParam: false);
        if (loc.Error is not null)
        {
            // A did-you-mean over every plugin the locate SEARCHED, empty when nothing is close.
            var pool = Mo2LoadOrder.AllPluginFileNames(comp, roots.ModsDir, roots.DataDir, roots.OverwriteDir);
            error = $"source plugin '{fromPlugin}' is not in the load order and {loc.Error}" +
                    PluginNameSuggest.DidYouMean(fromPlugin, pool);
            return null;
        }
        if (loc.Ambiguous is not null)
        {
            error = $"source plugin '{fromPlugin}' is not in the load order and {loc.Ambiguous.Count} mod folders provide a file " +
                    $"with that name ({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which " +
                    "version to forward. Pass the full path to the copy you mean as the source.";
            return null;
        }

        // Disclose an excluded-but-active source reached by PATH, judged by file identity rather than by name.
        string? excludedWhy = null;
        var locName = Path.GetFileName(loc.Path!);
        if (view.ExcludedPlugins.TryGetValue(locName, out var exWhy)
            && view.PluginPath(locName) is { } servedPath && PluginPaths.SamePluginFile(servedPath, loc.Path!))
            excludedWhy = exWhy;

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(roots.DataDir) ? null : roots.DataDir); }
        catch (Exception ex) { error = $"source file '{fromPlugin}' ({loc.Path}) could not be opened as a Skyrim plugin ({ex.Message})."; return null; }

        // One walk of the overlay collecting every wanted key.
        var wanted = specs.Select(s => s.Target).ToHashSet();
        var bodies = new Dictionary<FormKey, IMajorRecordGetter>();
        // In the SAME walk, the local IDs this file originates under its own ModKey — the renamed-copy diagnosis.
        var selfIds = new HashSet<uint>();
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                if (wanted.Contains(rec.FormKey)) bodies[rec.FormKey] = rec;
                if (rec.FormKey.ModKey == ov.ModKey) selfIds.Add(rec.FormKey.ID);
            }
        }
        catch (Exception ex)
        {
            (ov as IDisposable)?.Dispose();
            error = $"source file '{fromPlugin}' ({loc.Path}) could not be read ({ex.Message}).";
            return null;
        }

        var missing = specs.Select(s => s.Target).Where(k => !bodies.ContainsKey(k)).Distinct().ToList();
        if (missing.Count > 0)
        {
            // The renamed-copy diagnosis, stated only when the ID is present under the file's own ModKey.
            var renamed = missing.Where(k => k.ModKey != ov.ModKey && selfIds.Contains(k.ID)).ToList();
            var hint = renamed.Count == 0 ? "" :
                $"\n  NOTE: this file DOES carry {(renamed.Count == 1 ? "that FormID" : "those FormIDs")} — but under its own " +
                $"name, as {string.Join(", ", renamed.Take(3).Select(k => FormIdToken.Of(new FormKey(ov.ModKey, k.ID))))}. A plugin's records are keyed by its " +
                "FILENAME, so a copy saved under a different name is a DIFFERENT plugin. Keep the original filename and " +
                "park the copy in another folder, or name the FormIDs as this file spells them.";
            (ov as IDisposable)?.Dispose();
            error = $"refused — source file '{fromPlugin}' ({loc.Where}) does NOT define or override {missing.Count} of the " +
                    $"{specs.Count} record(s) named; there is no version of them there to forward, and NOTHING was written:\n  - " +
                    string.Join("\n  - ", missing.Select(k => FormIdToken.Of(k))) + hint;
            return null;
        }

        overlay = ov as IDisposable;
        return new WritePatchBuilder.OffOrderForwardSource
        {
            Plugin = fromPlugin, Path = loc.Path!, Where = loc.Where, Bodies = bodies, Overlay = ov,
            ExcludedReason = excludedWhy,
        };
    }

    /// <summary>The in-place branch of <see cref="ApplyEdits"/>, under _writeGate: resolve <paramref name="target"/>
    /// through the load order, take the consent handshake, check the parent, write with the verify forced on, stamp the marker.</summary>
    WritePatchBuilder.PatchOutcome ApplyEditsInPlace(
        LoadOrderResolver resolver, Mo2Roots roots, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
        string target, bool acknowledge, bool dryRun = false,
        IReadOnlyDictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null)
    {
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place edits the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };

        // A localized target is refused BEFORE the dry-run branch below, in this lane's words.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.PatchOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path; a dry run bypasses it.
        bool already = Host.InPlaceConsent.IsAcknowledged(targetPath);
        string? ackNote = null;
        bool owesConsent = false;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                return WritePatchBuilder.PatchOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade; kept in the dry run too.
        if (Host.InPlaceConsent.ParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.PatchOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, targetPath, targetName, fullReadback: true, dryRun, copyFromSources);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success record the acknowledgement, then stamp the audit marker and flag a stale .seq; all best-effort.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = Host.InPlaceConsent.Persist(owesConsent, targetPath, "edit");
            var markerNote = MergeEditedInPlaceMarker(roots, Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Resolve an active plugin's on-disk path by filename — exact match, then a lenient retry adding each plugin extension; null means no such active plugin.</summary>
    static string? ResolveActivePluginPath(LoadOrderResolver.IndexView view, string raw, out string resolvedName)
    {
        resolvedName = raw;
        var direct = view.PluginPath(raw);
        if (direct is not null) return direct;
        if (!OutputLocations.PluginExts.Any(e => raw.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            foreach (var ext in OutputLocations.PluginExts)
            {
                var cand = raw + ext;
                var p = view.PluginPath(cand);
                if (p is not null) { resolvedName = cand; return p; }
            }
        return null;
    }

    /// <summary>The first-touch in-place consent prompt for a PLUGIN: the shared lead plus the plugin-specific trade-off, waiving the CONSENT axis only.</summary>
    string InPlaceHandshakeText(string pluginName, string path) =>
        Host.InPlaceConsent.HandshakeLead(pluginName, path, "plugin", "writes to") +
        "  • houseCARL re-lays-out the WHOLE plugin the way xEdit/CK do on save (every record re-serialized), VERIFIES the records you edit, and trusts Mutagen for the rest.\n" +
        "  • It still refuses if the file can't be parsed, or carries engine-reserved (sub-0x800) records.\n" +
        "  • The default lane (a NEW patch, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    /// <summary>Stamp the <c>[houseCARL] editedInPlace=&lt;ISO&gt;</c> audit line into the target mod's <c>meta.ini</c>,
    /// preserving every other line and only under ModsDir. Best-effort: a note on failure. Contract in docs/architecture/write-path.md.</summary>
    static string? MergeEditedInPlaceMarker(Mo2Roots roots, string? modFolder)
    {
        try
        {
            if (string.IsNullOrEmpty(modFolder) || !IsUnderModsDir(roots, modFolder)) return null;   // N/A for a non-MO2 target
            var meta = Path.Combine(modFolder, "meta.ini");
            var stamp = $"editedInPlace={DateTime.UtcNow:o}";
            var lines = File.Exists(meta) ? File.ReadAllLines(meta).ToList() : new List<string>();

            int sec = lines.FindIndex(l => l.Trim().Equals(HousecarlOwnerMeta.Section, StringComparison.OrdinalIgnoreCase));
            if (sec < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add(HousecarlOwnerMeta.Section);
                lines.Add(stamp);
            }
            else
            {
                int edited = -1;
                for (int i = sec + 1; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith('[') && t.EndsWith(']')) break;                          // next section — stop
                    if (t.Replace(" ", "").StartsWith("editedInPlace=", StringComparison.OrdinalIgnoreCase)) { edited = i; break; }
                }
                if (edited >= 0) lines[edited] = stamp; else lines.Insert(sec + 1, stamp);    // update-or-insert within the section
            }
            File.WriteAllText(meta, string.Join("\r\n", lines) + "\r\n");
            return null;
        }
        catch (Exception ex)
        {
            return $"the editedInPlace audit marker could not be written to the target's meta.ini ({ex.GetType().Name}) — the edit itself succeeded.";
        }
    }

    /// <summary>True iff <paramref name="folder"/> is ModsDir or a folder under it — the gate that keeps the marker out of the game Data dir.</summary>
    static bool IsUnderModsDir(Mo2Roots roots, string folder)
    {
        if (string.IsNullOrEmpty(roots.ModsDir)) return false;
        try
        {
            var full = Path.GetFullPath(folder);
            var mods = Path.GetFullPath(roots.ModsDir);
            return full.Equals(mods, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Join any number of optional notes into one space-separated string, skipping the null and blank ones; null when none.</summary>
    static string? JoinNotes(params string?[] notes)
    {
        var present = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    /// <summary>Flag, never auto-regenerate, a stale .seq after an in-place write: the .seq is resolved through the
    /// captured VFS view, and a loose one no longer listing an SGE quest at its current FormID is a warning. Best-effort.</summary>
    string? SeqStaleInPlaceNote(string targetPath, string targetName)
    {
        try
        {
            var av = Host.Assets.Capture();
            var seqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(targetPath)}.seq";
            var seqSource = av.ResolveForPlacement(seqRel).Sources.FirstOrDefault();
            if (seqSource?.LooseFilePath is not { } seqPath) return null;      // no .seq, or a BSA-only one (bytes uncheckable here) → nothing to flag
            var uncovered = SeqFile.UncoveredSgeQuests(targetPath, File.ReadAllBytes(seqPath));
            if (uncovered.Count == 0) return null;                            // the .seq still lists every SGE quest → not staled
            var names = string.Join(", ", uncovered.Select(q => q.EditorId ?? FormIdToken.Of(q.FormKey)));
            bool one = uncovered.Count == 1;
            return $"the .seq for '{targetName}' no longer lists {(one ? "its start-game-enabled quest" : $"{uncovered.Count} of its start-game-enabled quests")} "
                 + $"at {(one ? "its" : "their")} current on-disk FormID(s) ({names}), so {(one ? "it" : "they")} would silently never start on a fresh save "
                 + "(a master prune in an in-place write shifts these FormIDs; the .seq may also have been stale before this edit). Regenerate it with " + ToolNames.WriteSeq + ".";
        }
        catch (Exception ex)
        {
            return $"could not check whether '{targetName}'s .seq is still current after this edit ({ex.GetType().Name}) — "
                 + "if it has start-game-enabled quests, run " + ToolNames.Check + " findings=[\"dialogue\"] seeds=[the quest] to confirm the .seq still lists them.";
        }
    }

    /// <summary>Remove whole records a houseCARL patch carries, the companion to <see cref="ApplyEdits"/>: <paramref
    /// name="patch"/> is required and ownership-gated, or <paramref name="inPlace"/> drops from an existing plugin.</summary>
    public WritePatchBuilder.RemovalOutcome RemoveRecords(IReadOnlyList<string> formids, string? patch,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.RemovalOutcome.Fail("no formids supplied — pass the FormID(s) of the record(s) to remove.");

        // In-place is the explicit, named-file opt-in, with the same contract as ApplyEdits.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to remove the record from in place. (Omit in_place to drop the record from a houseCARL patch instead — the default.)");
        if (inPlace && !string.IsNullOrWhiteSpace(patch))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "in_place=true and patch= are mutually exclusive: patch= drops a record from a houseCARL patch, while in_place removes it from an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to remove from in place). For the default lane omit target=; use patch= to name the houseCARL patch.");
        if (!inPlace && string.IsNullOrWhiteSpace(patch))
            // housecarl_remove answers a lane-less call in its own words before reaching here.
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it).");

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path). Pure — outside the gate.
        var keys = new List<FormKey>(formids.Count);
        var problems = new List<string>();
        var door = Host.OpenWriteFormIdDoor();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formids[{i}]: empty."); continue; }
            try { keys.Add(door.Parse(raw)); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formids[{i}]: ", $"formids[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING removed:\n  - " + string.Join("\n  - ", problems));

        lock (Host.WriteGate)                                            // removal re-serializes the patch — same gate
        {
            var resolver = Host.Resolver;                                 // builds/refreshes the index and the overlays for the re-serialize
            var snapshot = Host.ConfiguredRoots();                        // the lane's one read of the MO2 roots and plugin names
            var roots = snapshot.Roots;

            if (inPlace)
                return RemoveRecordsInPlace(resolver, roots, keys, target!.Trim(), acknowledge);

            // Resolve and ownership-gate the patch the way an extend does; no fresh-patch remedy is offered on this lane.
            string outPath;
            try { outPath = ResolveOutputPath(snapshot, patchName: null, into: patch, out _, out _,
                                              noFreshRule: WriteSentences.RemoveNoFreshPatch); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>The in-place branch of <see cref="RemoveRecords"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.RemoveRecordsInPlace"/> with the absence verify forced on. No rulebook: a removal pre-flights nothing.</summary>
    WritePatchBuilder.RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, Mo2Roots roots, IReadOnlyList<FormKey> keys, string target, bool acknowledge)
    {
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place removes from the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write, with this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemoveNoEquivalent) is { } locRefusal)
            return WritePatchBuilder.RemovalOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the shared first-touch handshake keyed off the resolved path.
        bool already = Host.InPlaceConsent.IsAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (Host.InPlaceConsent.ParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the absence verify forced on.
        var outcome = WritePatchBuilder.RemoveRecordsInPlace(resolver, keys, targetPath, targetName);

        // On success record the acknowledgement, stamp the audit marker and flag a stale .seq; both best-effort.
        if (outcome.Success)
        {
            var ackNote = Host.InPlaceConsent.Persist(owesConsent, targetPath, "removal");
            var markerNote = MergeEditedInPlaceMarker(roots, Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>source= is the forward surface's own word for the source pole, handed to the shared engine refusals.</summary>
    const string ForwardSourceParam = "source=";

    /// <summary>Forward a named plugin's version of one or more records into a patch as an override — xEdit's "copy as
    /// override into". The SOURCE plugin decides the content, so forwarding the origin master reverts a record to vanilla.</summary>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"{ForwardSourceParam} is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.ForwardOutcome.Fail("no formids supplied — pass the FormID(s) to forward from the source plugin.");

        // In-place is the explicit, named-file opt-in, with the same contract as the sibling write tools.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to forward into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place forwards into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to forward into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        // Parse every formid first, collecting all problems, like the edit and remove paths. Pure, so outside the gate.
        var fp = fromPlugin.Trim();
        var specs = new List<WritePatchBuilder.ForwardSpec>(formids.Count);
        var problems = new List<string>();
        var door = Host.OpenWriteFormIdDoor();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formids[{i}]: empty."); continue; }
            try { specs.Add(new WritePatchBuilder.ForwardSpec { Target = door.Parse(raw), FromPlugin = fp }); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formids[{i}]: ", $"formids[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING forwarded:\n  - " + string.Join("\n  - ", problems));

        lock (Host.WriteGate)                                            // one write at a time, resolve through commit
        {
            var resolver = Host.Resolver;                                 // builds/refreshes the index and the overlays for the source fetch and serialize
            var snapshot = Host.ConfiguredRoots();                        // the lane's one read of the MO2 roots and plugin names
            var roots = snapshot.Roots;

            // A source the active order does not contain is located and pre-fetched here, on both lanes; its overlay outlives the serialize.
            var offOrder = ResolveOffOrderForwardSource(resolver, roots, fp, specs, out var offOverlay, out var offEpoch, out var offError, out var sourceName);
            if (offError is not null)
                return WritePatchBuilder.ForwardOutcome.Fail(offError) with { Stamp = offEpoch };
            // A path that named the ACTIVE copy resolves as that plugin, so re-spell every spec's source.
            if (!string.Equals(sourceName, fp, StringComparison.Ordinal))
                specs = specs.Select(s => new WritePatchBuilder.ForwardSpec { Target = s.Target, FromPlugin = sourceName }).ToList();
            try
            {
                if (inPlace)
                    return ForwardRecordsInPlace(resolver, roots, specs, target!.Trim(), acknowledge, dryRun, offOrder);

                // A dry run resolves the would-be output path without creating the mod folder.
                string outPath; bool extend, created;
                try { outPath = ResolveOutputPath(snapshot, patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
                // Stamped like every post-capture outcome: the source resolve above already consulted the build.
                catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message) with { Stamp = offEpoch }; }

                var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, ForwardSourceParam, fullReadback, dryRun, offOrder);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused forward leaves no orphan folder
                return outcome;
            }
            finally { offOverlay?.Dispose(); }
        }
    }

    /// <summary>The in-place branch of <see cref="ForwardRecords"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.ForwardRecordsInPlace"/> with the touched-record verify forced on.</summary>
    WritePatchBuilder.ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, Mo2Roots roots, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs, string target, bool acknowledge,
        bool dryRun = false, WritePatchBuilder.OffOrderForwardSource? offOrder = null)
    {
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place forwards into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is refused BEFORE the dry-run branch below, in this lane's words.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.ForwardOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the shared first-touch handshake; a dry run bypasses it and notes it instead.
        bool already = Host.InPlaceConsent.IsAcknowledged(targetPath);
        string? ackNote = null;
        bool owesConsent = false;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                return WritePatchBuilder.ForwardOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade; kept in the dry run.
        if (Host.InPlaceConsent.ParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.ForwardOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ForwardRecordsInPlace(resolver, specs, targetPath, targetName, ForwardSourceParam, fullReadback: true, dryRun, offOrder);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success record the acknowledgement, stamp the audit marker and flag a stale .seq; both best-effort.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = Host.InPlaceConsent.Persist(owesConsent, targetPath, "forward");
            var markerNote = MergeEditedInPlaceMarker(roots, Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Resolve a patch's output path under the folder-per-patch model: a fresh, marker-stamped mod folder, or
    /// <paramref name="into"/> an existing houseCARL-owned one, with <paramref name="createdFolder"/> reporting whether THIS
    /// call cut it. The remedy arguments and the one-gate rule: docs/architecture/write-path.md. Where output lands:
    /// docs/architecture/output-and-artifacts.md.</summary>
    string ResolveOutputPath(OutputRoots snapshot, string? patchName, string? into, out bool extend, out bool createdFolder, bool create = true,
                             FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                             bool? stemFromCaller = null, OutputLocations.StemRefusal? refuseTaken = null)
    {
        createdFolder = false;
        var roots = snapshot.Roots;
        if (!Directory.Exists(roots.ModsDir))
            throw new InvalidOperationException($"cannot write: ModsDir '{roots.ModsDir}' does not exist. Check HouseCarl:ModsDir.");

        if (!string.IsNullOrWhiteSpace(into))
        {
            extend = true;
            // The .esp write lane shares the extend resolver with the rider and asset lanes; needEsp:true picks the .esp inside the folder.
            var folder = OutputLocations.ResolveOwnedPatchFolder(roots, into, needEsp: true, freshPatch, noFreshRule);
            var direct = Path.Combine(folder, OutputLocations.PatchStem(into) + ".esp");
            if (File.Exists(direct)) return direct;
            var sole = SoleEspInFolder(folder, out var why);
            if (sole is not null) return sole;
            throw new InvalidOperationException($"cannot extend: houseCARL folder '{Path.GetFileName(folder)}' {why}.");
        }

        extend = false;
        var baseStem = OutputLocations.PatchStem(string.IsNullOrWhiteSpace(patchName) ? "Patch" : patchName!);
        var active = OutputLocations.ActivePluginBasenames(roots, snapshot.BuiltPluginNames);
        // Every record lane that reaches here declares patch= and writes "<stem>.esp".
        lock (_outputLocations.FolderAllocationGate)                    // the same allocation lock as the rider lanes
        {
            var freeStem = OutputLocations.UniqueStem(roots, active, baseStem, stemFromCaller ?? !string.IsNullOrWhiteSpace(patchName),
                                      new PatchStemShadow.Target(s => s + ".esp", "patch"), refuseTaken);
            var newFolder = Path.Combine(roots.ModsDir, OutputLocations.ModFolderName(freeStem));
            var plugin = freeStem + ".esp";
            // A dry run (create:false) resolves the would-be path only — no folder, no meta.ini.
            if (create)
            {
                Directory.CreateDirectory(newFolder);
                createdFolder = true;
                OutputLocations.WriteOwnerMeta(newFolder, plugin);
            }
            return Path.Combine(newFolder, plugin);
        }
    }

    /// <summary>The single top-level plugin in a houseCARL folder, so <c>into=</c> a folder name needs no basename; null plus a named <paramref name="reason"/> when the folder holds none or more than one.</summary>
    static string? SoleEspInFolder(string folder, out string reason)
    {
        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => OutputLocations.PluginExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (plugins.Count == 1) { reason = ""; return plugins[0]; }
        reason = plugins.Count == 0
            ? "holds no plugin (.esp/.esm/.esl) to extend"
            : $"holds {plugins.Count} plugins ({string.Join(", ", plugins.Select(Path.GetFileName))}) — name the one to extend by passing its filename as into=";
        return null;
    }

    /// <summary>Remove a fresh folder <see cref="ResolveOutputPath"/> cut for a write that was then refused, gated on that
    /// folder holding nothing beyond our own meta.ini and an empty staging leftover. Best-effort.</summary>
    static void RemoveFolderCreatedThisCall(string outPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(outPath);
            if (folder is null || !Directory.Exists(folder)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry) && name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry) && name.Equals(".housecarl-tmp", StringComparison.OrdinalIgnoreCase)
                    && !Directory.EnumerateFileSystemEntries(entry).Any()) continue;
                return;                                       // real content appeared — leave the folder alone
            }
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

}
