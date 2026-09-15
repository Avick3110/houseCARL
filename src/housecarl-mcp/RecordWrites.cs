using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    // ---- writes ----------------------------------------------------------------------------------------

    /// <summary>Apply one or more edits as a single patch. Parses each op's FormID, field path and optional
    /// composition spec into the core's <see cref="WritePatchBuilder.PatchEdit"/>, resolves the output path as a new
    /// MO2 mod folder under ModsDir, then drives <see cref="WritePatchBuilder.Apply"/>: resolve winner, derive type,
    /// pre-flight all, override, apply the verb, serialize with the right masters. All-or-nothing — a single
    /// malformed op or pre-flight rejection refuses the whole call with no file written. Writes go to a new patch by
    /// default; <paramref name="into"/> extends an existing houseCARL-owned patch. Success is a null-Error outcome.
    /// <paramref name="fullReadback"/> additionally reads every touched record back in full off the written file,
    /// which is the pre-enable verify loop.</summary>
    public WritePatchBuilder.PatchOutcome ApplyEdits(IReadOnlyList<BulkOp> ops, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false, IReadOnlyList<string?>? fromRecords = null, IReadOnlyList<string?>? opOrigins = null)
    {
        if (ops.Count == 0)
            return WritePatchBuilder.PatchOutcome.Fail("no operations supplied.");

        // In-place is the explicit, named-file opt-in: edit an existing plugin, including one houseCARL did not
        // author, instead of writing a new patch. The contract is validated up front — it requires target=, and it
        // is mutually exclusive with into=, which extends a houseCARL patch. target= without in_place is a no-op the
        // caller likely did not mean, so it is named rather than silently ignored.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to edit in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place edits an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to edit in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        // Map every op to a core PatchEdit, collecting ALL parse problems first (all-or-nothing, like the cleave).
        // Runs outside the write gate so a malformed call never queues behind a real write. A write door, so a
        // runtime FormID is refused with the plugin form to use instead.
        var edits = new List<WritePatchBuilder.PatchEdit>(ops.Count);
        var problems = new List<string>();
        var editDoor = OpenWriteFormIdDoor();
        for (int i = 0; i < ops.Count; i++)
        {
            // fromRecords[i] is the zip's per-op source record, carried parallel to the op list because the
            // published wire shape deliberately gains no new member.
            var edit = MapEdit(editDoor, ops[i], i, out var err,
                fromRecords is not null && i < fromRecords.Count ? fromRecords[i] : null,
                opOrigins is not null && i < opOrigins.Count ? opOrigins[i] : null);
            if (err is not null) problems.Add(err); else edits.Add(edit!);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"refused — {problems.Count} of {ops.Count} operation(s) malformed; NO patch written:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index
            var rulebook = Rulebook;

            if (inPlace)
            {
                // The in-place lane resolves off-order CopyFrom sources exactly as the patch lane does. The overlays
                // must stay OPEN across the whole in-place write — CopyField deep-copies through them and the
                // re-serialize follows — so they are disposed only after ApplyEditsInPlace returns.
                Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? ipSources = null;
                List<IDisposable>? ipOverlays = null;
                var ipError = PrepareCopyFromSources(resolver, edits, ref ipSources, ref ipOverlays, out var ipEpoch);
                if (ipError is not null)
                {
                    if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose();
                    return WritePatchBuilder.PatchOutcome.Fail(ipError) with { Stamp = ipEpoch };
                }
                try { return ApplyEditsInPlace(resolver, rulebook, edits, target!.Trim(), acknowledge, dryRun, ipSources); }
                finally { if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose(); }
            }

            // A dry run resolves the would-be output path WITHOUT creating the mod folder — the one disk side effect
            // the pre-serialize pipeline otherwise has. The fresh-lane name is only a preview: the real write
            // re-picks a free stem, so a concurrent write can shift the auto-suffix.
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            // Pre-resolve any CopyFrom source that is off-order — on disk but not in the active order, the "copy
            // from the disabled old patch" case. Active-order sources are resolved inside Apply via its own captured
            // view, sharing the winner's build; only off-order files need the on-disk locate here, and their
            // overlays must stay open through the serialize because CopyField deep-copies through them.
            Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null;
            List<IDisposable>? offOrderOverlays = null;
            var cfError = PrepareCopyFromSources(resolver, edits, ref copyFromSources, ref offOrderOverlays, out var cfEpoch);
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

    /// <summary>P8b — locate every OFF-ORDER CopyFrom source (from_plugin present on disk but NOT in the active order)
    /// and fetch its version of the target record, holding each overlay OPEN (returned in <paramref name="overlays"/> for
    /// the caller to dispose AFTER the patch serialize — CopyField deep-copies through them). Active-order sources are
    /// left for <see cref="WritePatchBuilder.Apply"/> to resolve via its shared view (so they read the winner's build).
    /// Returns a named refusal string if any off-order source cannot be located, opened or read, or does not define the record
    /// (all-or-nothing, before any write); null on success. Uses the SAME on-disk locate as the records source= pole
    /// and the copy-npc-appearance donor lane, so the tools can never disagree on which file a filename names.
    /// <para>This capture is its own — the engine captures again — so a body pre-fetched here is only used when the
    /// engine's build still agrees the source is off-order. A write pins one resolver instance whose name table is
    /// never rebuilt, so the two captures cannot disagree about membership.</para>
    /// <para>MUTATES <paramref name="edits"/>, after the no-CopyFrom early return and this helper's own capture and
    /// before anything reads an edit: a CopyFrom source addressed by a PATH that names the very file the order loads
    /// is re-spelled to that plugin's NAME (<see cref="RespellActiveCopySourcePaths"/>). Stated because a resolve
    /// helper rewriting its argument is a surprise; this list is the one both the pre-locate and the engine consume,
    /// which is what makes one rewrite reach both.</para></summary>
    string? PrepareCopyFromSources(LoadOrderResolver resolver, IList<WritePatchBuilder.PatchEdit> edits,
        ref Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? sources, ref List<IDisposable>? overlays,
        out OrderStamp? epoch)
    {
        // This helper takes its OWN capture, so its refusals are decided after a build was consulted and are stamped
        // like every other post-capture outcome. Null only when no CopyFrom op exists, when nothing consults a build.
        epoch = null;
        if (!edits.Any(e => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))) return null;   // no CopyFrom → no source work
        var view = resolver.Capture();
        epoch = view.Stamp;
        RespellActiveCopySourcePaths(view, edits);   // before the predicate, and before any edit is used as a key
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";
        Mo2Composition? comp = null;
        var problems = new List<string>();
        foreach (var e in edits)
        {
            // The shared predicate, not a restatement of it: the engine consumes what this fetches through the same
            // rule, so a clause added to one can never fail to reach the other.
            if (!WritePatchBuilder.IsOffOrderCopySource(e, view)) continue;   // not a CopyFrom, or active — Apply resolves it off the shared build
            if (comp is null)
            {
                try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
                catch (Exception ex) { return $"CopyFrom off-order source locate failed to derive the MO2 roots: {ex.Message}"; }
                comp = Mo2LoadOrder.ReadComposition(profileDir);
            }
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, e.FromPlugin!, null);
            if (loc.Error is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' is not in the load order and {loc.Error}"); continue; }
            if (loc.Ambiguous is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' matches several mod folders on disk — pass an exact path to disambiguate."); continue; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be opened as a Skyrim plugin ({ex.Message})."); continue; }
            IMajorRecordGetter? body;
            try { body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == e.CopySource); }
            catch (Exception ex) { (ov as IDisposable)?.Dispose(); problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be read ({ex.Message})."); continue; }
            if (body is null)
            {
                (ov as IDisposable)?.Dispose();
                // Name WHICH record the file is missing: the target's own version for a same-record copy, or the
                // zip's source record for a cross-record one — "this record" would point at the wrong one.
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

    /// <summary>Re-spell every <c>CopyFrom</c> source that is a PATH to the ACTIVE copy of a plugin into that
    /// plugin's NAME, in place, so the rest of the write speaks the load order's vocabulary.
    /// <para>Off-order-ness is decided by a lookup in the plugin-NAME table, and a full path is never a key there, so
    /// a path to a plugin the order is actively serving would answer "off-order": the body would be read off the
    /// file directly, bypassing the build the rest of the call resolves against. Usually that is only a wrong label,
    /// but under a profile switch, where a filename is served by a different mod folder, it is a wrong body.</para>
    /// <para>A path to an EXCLUDED-but-active plugin deliberately still reads the file directly rather than taking
    /// the exclusion refusal: <see cref="ActiveNameForPath"/> declines excluded plugins, which is the read surface's
    /// escape hatch, and the forward lane behaves the same way.</para>
    /// <para>Applied BEFORE the pre-locate loop for two reasons: a PatchEdit is the key of the pre-fetched source
    /// dictionary, so re-spelling one afterwards would leave a key the engine can never look up; and the same list
    /// goes to the engine, so one rewrite reaches the arm decision, the winner comparison and every rendered
    /// sentence at once.</para></summary>
    static void RespellActiveCopySourcePaths(LoadOrderResolver.IndexView view, IList<WritePatchBuilder.PatchEdit> edits)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)) continue;
            // The same LooksLikePath check the other pole-resolving sites use. Harmless without it — a bare filename
            // that is active never reaches the off-order arm anyway — but kept so the convention has no exception.
            if (string.IsNullOrWhiteSpace(e.FromPlugin) || !LooksLikePath(e.FromPlugin!)) continue;
            if (ActiveNameForPath(view, e.FromPlugin!) is { } activeName)
                edits[i] = e with { FromPlugin = activeName };
        }
    }

    /// <summary>Locate the one <c>source=</c> plugin a forward call shares when the active order does not contain it
    /// — a disabled mod, an unticked plugin, an unregistered folder, or a direct path — open it, and pre-fetch every
    /// requested record's body off its own overlay. The forward twin of <see cref="PrepareCopyFromSources"/>, and
    /// simpler: every forward in a call names the same source, so this locates once, opens once and fetches N. Uses
    /// the same on-disk locate as every other lane, so two tools cannot disagree about which file a filename names.
    /// <para>Returns null with a null <paramref name="error"/> when the source IS in the active order — the ordinary
    /// path, which pays no locate and no overlay. Returns null with <paramref name="error"/> set when the file cannot
    /// be located, opened or read, when its name is ambiguous across mod folders, or when it does not define a
    /// requested record: refused by name, all-or-nothing, before any write.</para>
    /// <para><paramref name="overlay"/> is handed back OPEN, because the bodies are deep-copied during the write, so
    /// the caller disposes it only after the serialize returns. <paramref name="epoch"/> is this helper's own
    /// capture, since it decides membership against a build; the reported outcome's stamp still names the build the
    /// write was decided from.</para>
    /// <para><paramref name="sourceName"/> is the spelling the ENGINE should resolve against: <paramref
    /// name="fromPlugin"/> unchanged, except when a caller's PATH names the very file the order loads — then it is
    /// that plugin's name, and this returns null so the in-order arm handles it. Membership cannot be decided by
    /// ContainsPlugin alone once a path is an advertised spelling: a full path never matches the name table, so the
    /// live copy of an active plugin would take the off-order arm, be described as not in the load order, have its
    /// epoch disclaimed, and lose the already-the-winner flag, reporting that it out-ranks itself.
    /// <see cref="ActiveNameForPath"/> is a full-path identity compare, so a same-named backup keeps the off-order
    /// lane, and so does a path to an excluded plugin.</para></summary>
    WritePatchBuilder.OffOrderForwardSource? ResolveOffOrderForwardSource(
        LoadOrderResolver resolver, string fromPlugin, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs,
        out IDisposable? overlay, out OrderStamp? epoch, out string? error, out string sourceName)
    {
        overlay = null; error = null; sourceName = fromPlugin;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (view.ContainsPlugin(fromPlugin)) return null;      // active — the engine resolves it off the shared build
        // The same LooksLikePath check the other pole-resolving sites use. Harmless without it — a bare filename
        // already failed ContainsPlugin above — but kept so the convention has no exception.
        if (LooksLikePath(fromPlugin) && ActiveNameForPath(view, fromPlugin) is { } activeName)
        {
            sourceName = activeName;                           // a path to the ACTIVE copy — in-order after all
            return null;
        }

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex) { error = $"source plugin '{fromPlugin}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}"; return null; }

        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        // offerModParam is false because this tool has no mod= parameter, and a refusal must never point at a
        // parameter the caller's tool does not expose. A direct path is this lane's disambiguator.
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, fromPlugin, null, offerModParam: false);
        if (loc.Error is not null)
        {
            // A did-you-mean, because this is the one lane where a source name is typed by hand and the locate has
            // just proven the file is in no layer at all — so a spelling suggestion is the whole remedy, and there
            // is nothing for the absence explainer to explain. It is empty when nothing is close, so a genuinely
            // unknown name is never answered with an invented guess.
            // The pool is every plugin the locate SEARCHED, drawn from the same folder sequence, rather than the
            // active order's names: this lane makes disabled plugins first-class sources, so a typo of one must
            // still get a suggestion, and "not found" and "did you mean" cannot disagree about which places count.
            // It costs a listing per mod folder, spent only on this refusal.
            var pool = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
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

        // Is the located file the order's own copy of a plugin this session excluded because Mutagen could not fully
        // parse it at index time? By NAME such a source is refused in the engine; by PATH it reaches here, because
        // ActiveNameForPath declines excluded plugins. The asymmetry is deliberate — forwarding copies one body out,
        // not the whole-file re-serialize the exclusion refusal exists to prevent — but it must be DISCLOSED rather
        // than silent. Judged by file identity, never by name: a same-named copy elsewhere is a different file.
        string? excludedWhy = null;
        var locName = Path.GetFileName(loc.Path!);
        if (view.ExcludedPlugins.TryGetValue(locName, out var exWhy)
            && view.PluginPath(locName) is { } servedPath && SamePluginFile(servedPath, loc.Path!))
            excludedWhy = exWhy;

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
        catch (Exception ex) { error = $"source file '{fromPlugin}' ({loc.Path}) could not be opened as a Skyrim plugin ({ex.Message})."; return null; }

        // One walk of the overlay collecting every wanted key: the overlay is ours alone and the whole call shares
        // it, so there is no reason to re-enumerate per record.
        var wanted = specs.Select(s => s.Target).ToHashSet();
        var bodies = new Dictionary<FormKey, IMajorRecordGetter>();
        // In the SAME walk, the local IDs this file originates under its own ModKey. A plugin's records are keyed by
        // its FILENAME, so a parked copy renamed 'MyPatch_old.esp' declares a different ModKey and its records match
        // nothing the caller asked for — which the miss below would otherwise report as "does not define or override
        // this record", a true sentence with a misleading cause.
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
            // The renamed-copy diagnosis, stated only when it is a fact about this file: the ID is present under the
            // file's own ModKey. Never a guess — the ordinary miss says nothing about renaming, and a FormKey whose
            // origin is some other master is not this case either.
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

    /// <summary>Build a walk's ordered source universe from the caller's pole list. Each element is one pole; the
    /// chain resolves a key by trying them in order, first hit wins (<see cref="SourceChain"/> carries the
    /// fallback-never-merge boundary and the fault-versus-miss rule).
    /// <para>There is deliberately no separate single-pole path: a length-1 list is this same loop running once, so
    /// an off-order element cannot behave one way alone and another way in a chain.</para>
    /// <para>The element kinds are the ordinary poles: <c>winner</c> is the active order as one universe, and a
    /// plugin NAME is that plugin's version wherever it lives, active or an off-order file, resolved through the same
    /// <see cref="LocatePluginFileOnDisk"/> contract every other lane uses. <c>previous_provider</c> is
    /// subject-relative and a walk has no per-key subject, so it refuses loudly rather than inventing a
    /// winner-relative reading.</para>
    /// <para>Overlays opened for off-order elements are appended to <paramref name="overlays"/> OPEN: the walk holds
    /// bodies off them for its whole run, so the caller disposes them only after the write completes. A refusal
    /// disposes what it opened before returning, so a failed build leaks nothing.</para></summary>
    internal SourceChain? BuildSourceChain(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyList<string> poles, string paramName, List<IDisposable> overlays, out string? error)
    {
        error = null;
        if (poles is null || poles.Count == 0)
        {
            error = $"{paramName} is empty — name at least one source: 'winner' for the active load order's winning " +
                    "version of each record, or a plugin filename for that plugin's version.";
            return null;
        }

        var arms = new List<SourceArm>(poles.Count);
        var openedHere = new List<IDisposable>();
        Mo2Composition? comp = null;
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";

        string Fail(string message)
        {
            foreach (var d in openedHere) { try { d.Dispose(); } catch { /* disposing a failed build */ } }
            return message;
        }

        // The MO2 layer an ACTIVE plugin's own file sits in — the folder the placement after a copy has to be given
        // for an ENABLED donor, which is otherwise the one case the readback leaves the caller to guess. Best
        // effort: roots that will not derive cost the caller the folder name, never the source.
        SourceLayer? ActiveLayer(string pluginName)
        {
            try
            {
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                return view.PluginPath(pluginName) is { } p ? InstallLayerOfPath(p, modsDir, overwriteDir, dataDir) : null;
            }
            catch { return null; }
        }

        // Every refusal path routes through Fail, but a THROW out of the loop bypassed all of them and leaked the
        // overlays opened so far — the caller's finally only disposes what reached `overlays`, which happens on the
        // last line. A leaked overlay holds a plugin file handle open, which is exactly what MO2 and xEdit must be
        // free to move.
        try
        {
        for (int i = 0; i < poles.Count; i++)
        {
            var spelling = (poles[i] ?? "").Trim();
            var at = poles.Count == 1 ? paramName : $"{paramName}[{i}]";
            if (spelling.Length == 0)
            {
                error = Fail($"{at} is blank — every element must name a source ('winner', or a plugin filename).");
                return null;
            }

            // ---- pole: winner ----------------------------------------------------------------------------
            if (string.Equals(spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase))
            {
                arms.Add(new SourceArm(SourcePoles.Winner, SourceArmKind.ActiveOrder, "the active load order (each record's winning version)",
                    fk =>
                    {
                        var w = view.ResolveWinner(fk);
                        return w is null ? null : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    }));
                continue;
            }

            // ---- pole: previous_provider — refused, with the path to making it legal ---------------------
            if (string.Equals(spelling, SourcePoles.PreviousProvider, StringComparison.OrdinalIgnoreCase))
            {
                error = Fail(
                    $"{at}: '{SourcePoles.PreviousProvider}' cannot name a source for a walk. It is SUBJECT-relative — the provider " +
                    "immediately below a named subject plugin — and a walk reaches records through links, with no subject plugin " +
                    "for each one to be relative to. Name the plugin you mean, or 'winner' for the active order's winning version. " +
                    "If you have a case where it does have a defined meaning here, file it as a gap report — that is what would " +
                    "define it.");
                return null;
            }

            // ---- pole: named(plugin) — active, or an off-order file --------------------------------------
            // ACTIVE arm first: the plugin is in the order under this very view, so its bodies come off the shared
            // captured build rather than a second overlay of the same file.
            if (view.ContainsPlugin(spelling))
            {
                var active = spelling;
                arms.Add(new SourceArm(spelling, SourceArmKind.ActiveOrder, $"'{active}' (active in the load order)",
                    fk => view.GetRecord(session, active, fk), ActiveLayer(active)));
                continue;
            }
            // A path that names the order's own copy of an active plugin is that plugin, not an off-order file: a
            // full path never matches the name table, so without this the live copy of an active plugin takes the
            // off-order arm and is described as not in the load order.
            if (LooksLikePath(spelling) && ActiveNameForPath(view, spelling) is { } activeName)
            {
                arms.Add(new SourceArm(activeName, SourceArmKind.ActiveOrder, $"'{activeName}' (active in the load order; named by path)",
                    fk => view.GetRecord(session, activeName, fk), ActiveLayer(activeName)));
                continue;
            }

            if (comp is null)
            {
                try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
                catch (Exception ex)
                {
                    error = Fail($"{at}: '{spelling}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}");
                    return null;
                }
                comp = Mo2LoadOrder.ReadComposition(profileDir);
            }

            // offerModParam is false because this refusal names a LIST element, and the disambiguator that works
            // here is a full path in that element, not a tool-level mod= applying to every element at once.
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, spelling, null, offerModParam: false);
            if (loc.Error is not null)
            {
                // Suggested from every plugin the locate SEARCHED, not just the active order, so a typo of a disabled
                // plugin gets a suggestion instead of silence. Empty when nothing is close.
                var pool = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
                error = Fail($"{at}: source '{spelling}' is not in the load order and {loc.Error}" +
                             PluginNameSuggest.DidYouMean(spelling, pool));
                return null;
            }
            if (loc.Ambiguous is not null)
            {
                error = Fail($"{at}: source '{spelling}' is not in the load order and {loc.Ambiguous.Count} mod folders provide a file " +
                             $"with that name ({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess " +
                             "which version to read. Put the full path to the copy you mean in this element.");
                return null;
            }

            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex)
            {
                error = Fail($"{at}: source file '{spelling}' ({loc.Path}) could not be opened as a Skyrim plugin ({ex.Message}).");
                return null;
            }
            openedHere.Add((IDisposable)ov);

            // Lazy per-type link cache, so there is no eager whole-file parse. A per-record parse fault throws out of
            // the fetch on purpose: SourceChain turns it into a fault that STOPS the chain, because substituting a
            // later arm's version of a record this arm actually carries would be a silently wrong answer.
            var cache = ov.ToImmutableLinkCache();
            var where = $"file '{Path.GetFileName(loc.Path!)}' ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})";
            // The layer this file physically sits in, read off the path by the shared rule rather than parsed back
            // out of `where`: a mod folder's name is what a following asset placement passes as its provider, and
            // the BRANCH travels with it so the sentence never calls a mod folder the layer it merely spells like.
            var layer = InstallLayerOfPath(loc.Path!, modsDir, overwriteDir, dataDir);
            // A mod folder the profile is not loading travels as such, so a readback can say the game is not loading
            // this source instead of naming the folder as though it were live. Both off standings are carried: an
            // unregistered folder is as unloaded as a switched-off one, and its remedy is the other one.
            if (layer is { Kind: SourceLayerKind.ModFolder })
                layer = loc.Served switch
                {
                    ServedStanding.ModDisabled => layer with { Folder = ModFolderStanding.SwitchedOff },
                    ServedStanding.ModUnregisteredLayer => layer with { Folder = ModFolderStanding.Unregistered },
                    _ => layer,
                };
            arms.Add(new SourceArm(spelling, SourceArmKind.File, where,
                fk => cache.TryResolve(fk, out var body) ? body : null, layer));
        }

        overlays.AddRange(openedHere);
        return new SourceChain(arms);
        }
        catch { foreach (var d in openedHere) { try { d.Dispose(); } catch { } } throw; }
    }

    /// <summary>The closure-copy operation's service half: resolve the ordered source universe, walk the source
    /// record's seed links, then hand the result to the core to build and serialize.
    /// <para>The layer split is the write path's: the core does records and serialize, the service does lanes,
    /// folders and MO2. So this resolves poles, the walk, the output path and the active target body, and
    /// <see cref="ClosureCopy.BuildAndWrite"/> owns everything from the patch mod onward.</para>
    /// <para>Prose-free by design: inputs arrive already validated and refusals come back as typed data, because the
    /// tool layer owns every user-facing sentence.</para></summary>
    internal ClosureCopyOutcome CopyClosure(
        FormKey sourceKey, IReadOnlyList<string> sourcePoles,
        IReadOnlyList<string> seedPaths, IReadOnlyList<WalkExclusion> exclusions,
        FormKey? targetKey, string? newEditorid,
        string? patchName, string? into)
    {
        lock (_writeGate)
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            using var session = resolver.OpenSession();
            var overlays = new List<IDisposable>();
            try
            {
                var chain = BuildSourceChain(view, session, sourcePoles, "from_source", overlays, out var chainError);
                if (chain is null) return ClosureCopyOutcome.Fail(engine: chainError);
                var consulted = chain.Arms.Select(SourceArmRef.Of).ToList();

                var srcFetch = chain.Fetch(sourceKey, "from");
                if (srcFetch.Fault is { } f)
                    return ClosureCopyOutcome.Fail(
                        walk: new WalkRefusal(WalkRefusalKind.SourceFault, sourceKey, "from",
                            new[] { sourceKey }, f.Cause, Fault: f), sources: consulted);
                if (srcFetch.Hit is not { } srcHit)
                    return ClosureCopyOutcome.Fail(
                        walk: new WalkRefusal(WalkRefusalKind.SourceMiss, sourceKey, "from",
                            new[] { sourceKey }, "", Miss: chain.Miss(sourceKey, "from")), sources: consulted);

                // The BOUND universe: the source record's own plugin plus every FILE arm named (the plugins being
                // copied away from), never an implicit base master — copying a vanilla-defined record must not
                // classify vanilla as "the source" and wholesale-internalize it.
                var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
                var bound = new HashSet<ModKey>();
                if (!baseMasters.Contains(sourceKey.ModKey)) bound.Add(sourceKey.ModKey);
                // EVERY arm the caller named, whatever kind it resolved to. Binding only the File arms would make
                // the artifact depend on an MO2 checkbox: an enabled override's records would stay mastered links
                // while the same plugin disabled would be internalized. Naming a plugin in from_source= IS the
                // caller saying it is a source being copied away from, and that is what makes the standalone claim
                // true. `winner` stays exempt: it is the whole load order, not a plugin, and binding it would
                // internalize vanilla.
                foreach (var arm in chain.Arms)
                {
                    if (string.Equals(arm.Spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase)) continue;
                    ModKey mk;
                    try { mk = ModKey.FromFileName(Path.GetFileName(arm.Spelling)); } catch { continue; }
                    if (!baseMasters.Contains(mk)) bound.Add(mk);
                }
                bool IsBound(FormKey fk) => bound.Contains(fk.ModKey);

                // The transplant note belongs to the case where the donor-bound set is EMPTY — nothing is being
                // copied away from at all. Keying it on `from`'s own defining plugin answers a different question:
                // a base-game FormID whose bound set holds a named overhaul still internalizes and strips that
                // plugin's records, so the note would claim nothing was being removed directly above the list that
                // removed them. Empty is the only state in which the note is true.
                var nothingBound = bound.Count == 0;

                if (ClosureWalk.ResolveSeeds(srcHit.Body, seedPaths, out var seeds) is { } seedRefusal)
                    return ClosureCopyOutcome.Fail(walk: seedRefusal.Refusal, sources: consulted);

                var scope = WalkScope.StandaloneFrom(bound, fk => view.ResolveWinner(fk) is not null);
                var walk = ClosureWalk.Run(seeds, chain, scope, exclusions);
                if (!walk.Success) return ClosureCopyOutcome.Fail(walk: walk.Refusal, sources: consulted);

                string outPath; bool extend, created;
                // The stem falls back to the new EditorID, but only patch= is the caller's own name: a shadow on an
                // EditorID-derived stem steps to the next suffix rather than naming a parameter they never passed.
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? newEditorid?.Trim() : null), into, out extend, out created,
                                                  freshPatch: FreshPatchRemedy.CreatedByOmittingInto,
                                                  stemFromCaller: !string.IsNullOrWhiteSpace(patchName)); }
                catch (Exception ex) { return ClosureCopyOutcome.Fail(engine: ex.Message, sources: consulted); }
                var patchModKey = ModKey.FromFileName(Path.GetFileName(outPath));

                // The ACTIVE target body is the service's to fetch (it needs the view); an IN-PATCH target is NOT,
                // and is deliberately left null here so core resolves it off the OPENED patch mod. Fetching it
                // here would mean resolving a record through a load order the patch is not part of.
                IMajorRecordGetter? targetActiveBody = null;
                if (targetKey is { } tk && tk.ModKey != patchModKey)
                {
                    var tw = view.ResolveWinner(tk);
                    targetActiveBody = tw is null ? null : view.GetRecord(session, tw.Value.WinnerPlugin, tk);
                    if (targetActiveBody is null)
                    {
                        if (created) RemoveFolderCreatedThisCall(outPath);
                        return ClosureCopyOutcome.Fail(
                            copy: new CopyRefusal(CopyRefusalKind.Transplant, "the target is not in the active load order", Key: tk),
                            sources: consulted);
                    }
                }

                // Cleanup is finally-shaped rather than success-flag-guarded: a throw out of BuildAndWrite would
                // bypass a `!outcome.Success` check and leave the fresh mod folder on disk, so the next call would
                // start suffixing _001 — the accretion RemoveFolderCreatedThisCall exists to prevent.
                var wrote = false;
                try
                {
                var outcome = ClosureCopy.BuildAndWrite(
                    outPath, extend, sourceKey, srcHit, walk, seedPaths,
                    targetKey, targetActiveBody, newEditorid, IsBound, bound, nothingBound,
                    mk => view.ContainsPlugin(mk.FileName.String),
                    pf => { session.ReleaseOverlay(pf); return session.AllMastersExcept(pf); },
                    consulted,
                    ex => WritePatchBuilder.SerializeFailure("", ex, session, ""));

                wrote = outcome.Success;
                return outcome;
                }
                finally { if (!wrote && created) RemoveFolderCreatedThisCall(outPath); }
            }
            finally { foreach (var d in overlays) { try { d.Dispose(); } catch { } } }
        }
    }

    /// <summary>Test seam for <see cref="BuildSourceChain"/>: drives the real builder over the real MO2 resolution,
    /// under the same view, session and overlay lifetime the production call gives it, and hands the result to
    /// <paramref name="body"/> while the sources are still open. A chain whose overlays are disposed resolves
    /// nothing, so a seam that returned the chain could only test its refusals.</summary>
    internal T WithSourceChainForGuard<T>(IReadOnlyList<string> poles, string paramName, Func<SourceChain?, string?, T> body)
    {
        var resolver = Resolver;
        var view = resolver.Capture();
        using var session = resolver.OpenSession();
        var overlays = new List<IDisposable>();
        try
        {
            var chain = BuildSourceChain(view, session, poles, paramName, overlays, out var error);
            return body(chain, error);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* test teardown */ } } }
    }

    /// <summary>The in-place branch of <see cref="ApplyEdits"/>, running under _writeGate. It resolves
    /// <paramref name="target"/> to its real on-disk path via the load order rather than the houseCARL-owned folder
    /// model; enforces the persistent first-touch consent handshake, keyed off the resolved path; checks the parent
    /// is writable; drives <see cref="WritePatchBuilder.ApplyInPlace"/> with the touched-record verify forced on; and
    /// on success stamps the distinct <c>editedInPlace=</c> marker — never <c>generated=true</c>, because the user's
    /// mod must keep failing <see cref="IsHouseCarlOwned"/> so a later into= cannot blind-overwrite it.
    /// <paramref name="acknowledge"/> waives the consent axis only; the verify is a corruption-axis fact no
    /// acknowledgement overrides.</summary>
    WritePatchBuilder.PatchOutcome ApplyEditsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
        string target, bool acknowledge, bool dryRun = false,
        IReadOnlyDictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename, which is unique in an
        // order. Refuse loudly if it is not a real active plugin, which closes the coincidental-folder collision.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place edits the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.PatchOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: a persistent, server-enforced first-touch handshake keyed off the resolved path. It is
        // not a sticky mode — each in-place write still names its own target=, so this only stops re-explaining the
        // trade-off and never routes an ambiguous request to in-place. A dry run bypasses the handshake and never
        // persists an acknowledgement, because consent gates touching the original and a dry run touches nothing;
        // the pending consent is surfaced as a note instead. The check gates entry here, while a real write's
        // acknowledgement is recorded only once the edit has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
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
                // Stamped like every other post-capture outcome: this branch is reached only after the view above
                // resolved the target, and it is the most common in-place response shape, so an unstamped one would
                // break the "every write response carries an epoch" contract where callers meet it most.
                return WritePatchBuilder.PatchOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade: the swap stages a sibling temp in this directory,
        // so a read-only or locked parent is caught up front with a clear message before any work. Kept in the dry
        // run too, since an unwritable parent is exactly what the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.PatchOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, targetPath, targetName, fullReadback: true, dryRun, copyFromSources);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note, since those describe a
        // write that happened; only the core's would-grow note and the pending-consent note ride along.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq. Both are
        // best-effort and neither failing fails the done edit. An in-place edit can prune a master and shift the
        // plugin's own on-disk FormIDs, staling its .seq — surfaced as a note, never auto-regenerated.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "edit");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Resolve an active plugin's on-disk path by filename: exact match first, then a lenient retry
    /// appending each plugin extension if the caller dropped it. <paramref name="resolvedName"/> echoes the canonical
    /// filename that matched, and null means no such active plugin. The path is the load order's winning path for
    /// that filename — the file the game loads.</summary>
    static string? ResolveActivePluginPath(LoadOrderResolver.IndexView view, string raw, out string resolvedName)
    {
        resolvedName = raw;
        var direct = view.PluginPath(raw);
        if (direct is not null) return direct;
        if (!PluginExts.Any(e => raw.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            foreach (var ext in PluginExts)
            {
                var cand = raw + ext;
                var p = view.PluginPath(cand);
                if (p is not null) { resolvedName = cand; return p; }
            }
        return null;
    }

    /// <summary>The opening claims both first-touch prompts make — the plugin one and the mesh one — in one place.
    /// <para>The header states when the prompt stops: not "once", but once a write LANDS. A refused call records
    /// nothing (see <see cref="PersistInPlaceConsent"/>), so a caller can legitimately meet this prompt more than
    /// once, and a prompt calling itself one-time would say that cannot happen.</para>
    /// <para>The file claim is deliberately direction-neutral. Asserting a state transition would be false when the
    /// transition already happened: a write that lands and then fails its verify mutates the file while recording no
    /// consent, so the next call re-prompts against an already-modified file. Stating the durable fact instead is
    /// true either way and needs no state to distinguish them.</para></summary>
    static string InPlaceHandshakeLead(string name, string path, string subject, string verb) =>
        $"in-place edit of '{name}' — first-time confirmation (shown until an in-place write to this {subject} LANDS; " +
        "a call that is refused records nothing, so you may see this again):\n" +
        $"  • This {verb} your ORIGINAL file ({path}) — not a copy. houseCARL keeps NO backup or undo and cannot " +
        "restore what it overwrites, so keep your own.\n";

    /// <summary>The first-touch in-place CONSENT prompt for a PLUGIN (server-enforced). Opens with the shared lead
    /// (<see cref="InPlaceHandshakeLead"/> — when the prompt stops, and what it costs the original), then states the
    /// plugin-specific trade-off: the whole plugin is re-laid-out like xEdit/CK do on save with the touched records
    /// VERIFIED and Mutagen trusted for the rest, and the default new-patch lane stays recommended. Waives the CONSENT
    /// axis only (re-call with acknowledge=true).</summary>
    static string InPlaceHandshakeText(string pluginName, string path) =>
        InPlaceHandshakeLead(pluginName, path, "plugin", "writes to") +
        "  • houseCARL re-lays-out the WHOLE plugin the way xEdit/CK do on save (every record re-serialized), VERIFIES the records you edit, and trusts Mutagen for the rest.\n" +
        "  • It still refuses if the file can't be parsed, or carries engine-reserved (sub-0x800) records.\n" +
        "  • The default lane (a NEW patch, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    /// <summary>PERSIST the one-time in-place acknowledgement for <paramref name="targetPath"/> — called by every
    /// in-place lane AFTER the write it gated has actually landed, never before. <paramref name="owed"/> is the consent
    /// gate's own answer: a first touch of this file that carried <c>acknowledge=true</c>. False (already acknowledged,
    /// or a dry run, which touches nothing and so records nothing) makes this a no-op. Returns the store's error
    /// when the config write failed, for the caller's own note; null when there was nothing to record or it recorded.
    /// <para>Ordering is the point. Between the consent check and the byte that changes on disk, every lane runs a
    /// chain of refusals that leave the original untouched — the writable-parent pre-flight, then the builder's own
    /// checks: a target Mutagen cannot fully parse, a record the file does not carry, a link into a plugin that is
    /// not loaded, the localized backstop. Recording the acknowledgement ahead of them would spend the first-touch
    /// confirmation on a write that never happened, letting the next call — the first real rewrite of the original —
    /// through unprompted. Persisting last makes that whole class unreachable, including a check added later. The
    /// gate itself does not move: <c>already || acknowledge</c> still decides whether the call runs.</para>
    /// <para>Callers persist on the lane's own success, which is the conservative reading: a lane that mutated the
    /// file and then failed its post-write verify records nothing and re-prompts next time. Over-prompting costs a
    /// confirmation, under-prompting costs a file.</para>
    /// <para><paramref name="what"/> names what just happened ("edit", "removal", "create", "forward") and
    /// <paramref name="subject"/> the thing being remembered; those two words are the whole per-lane variation, and
    /// the shared sentence lives here rather than once per lane. It says "the next in-place call" rather than "a
    /// future session" because <see cref="UserConfigStore"/> caches nothing, so a failed write re-prompts
    /// immediately.</para></summary>
    string? PersistInPlaceConsent(bool owed, string targetPath, string what, string subject = "plugin")
    {
        if (!owed) return null;
        string? err;
        // The store returns its write failures rather than throwing, but its cross-process lock handling sits outside
        // that try. This runs AFTER the file changed, so a throw escaping here would report a failure for a write
        // that landed — the last step of a successful call must not be able to throw.
        try { err = _store.RecordInPlaceAcknowledged(targetPath) is { ok: false, error: var e } ? (e ?? "unknown error") : null; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; }
        return err is null ? null
            : $"the in-place acknowledgement could not be saved ({err}) — the {what} proceeded, " +
              $"but the next in-place call will ask for this {subject} again.";
    }

    /// <summary>Writable-parent pre-flight for the in-place swap: the staged temp is a sibling of the target, so
    /// prove the parent is writable now rather than degrade to a non-atomic write later. True, with a named
    /// <paramref name="why"/>, means refuse. Probes by writing and deleting an empty sibling temp. It checks the
    /// PARENT is writable, not that the target file is unlocked by another process; that case surfaces loudly at the
    /// <c>File.Replace</c> swap with the original byte-intact, so it needs no separate pre-flight.</summary>
    static bool InPlaceParentUnwritable(string targetPath, out string why)
    {
        why = "";
        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            why = $"in-place refused: the target's parent folder '{dir}' does not exist — nothing written.";
            return true;
        }
        try
        {
            var probe = Path.Combine(dir, ".housecarl-writeprobe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return false;
        }
        catch (Exception ex)
        {
            why = $"in-place refused: the target's folder '{dir}' is not writable ({ex.GetType().Name}: {ex.Message}) — houseCARL " +
                  "won't degrade to a non-atomic write. Make the mod folder writable (or move the plugin somewhere writable) and retry. Nothing written.";
            return true;
        }
    }

    /// <summary>Stamp the distinct <c>[houseCARL] editedInPlace=&lt;ISO&gt;</c> audit line into the target mod's
    /// <c>meta.ini</c> — a breadcrumb that houseCARL touched this user mod, without ever writing
    /// <c>generated=true</c>, so <see cref="IsHouseCarlOwned"/> still reads false and a later into= cannot
    /// blind-overwrite it. Preserves every existing line, merging into or creating the <c>[houseCARL]</c> section,
    /// and only for an MO2 mod folder under ModsDir, so it never pollutes the game Data dir for a loose plugin.
    /// Best-effort: returns a note on failure, since the edit already succeeded, and null on success or N/A.</summary>
    string? MergeEditedInPlaceMarker(string? modFolder)
    {
        try
        {
            if (string.IsNullOrEmpty(modFolder) || !IsUnderModsDir(modFolder)) return null;   // N/A for a non-MO2 target
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

    /// <summary>True iff <paramref name="folder"/> is ModsDir itself or a folder directly/indirectly under it — the gate
    /// that keeps the editedInPlace marker out of the game Data dir for a loose (non-MO2-managed) in-place target.</summary>
    bool IsUnderModsDir(string folder)
    {
        if (string.IsNullOrEmpty(_modsDir)) return false;
        try
        {
            var full = Path.GetFullPath(folder);
            var mods = Path.GetFullPath(_modsDir);
            return full.Equals(mods, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Join any number of optional notes into one space-separated string, skipping the null and blank ones;
    /// null when none are present. Variadic so a lane can merge several best-effort side-effect notes into the
    /// single <c>Note</c> the outcome carries.</summary>
    static string? JoinNotes(params string?[] notes)
    {
        var present = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    /// <summary>Flag, never auto-regenerate, a stale .seq after an in-place write. A master prune may have shifted
    /// every own record's on-disk FormID and staled the plugin's <c>.seq</c>, whose start-game-enabled quests would
    /// then silently never start on a fresh save. The .seq is resolved through the same captured VFS view the compact
    /// gate uses — loose roots plus active BSAs, not a bare folder check, which would miss a filed or BSA-packed one
    /// — and if a loose .seq exists but no longer lists one or more SGE quests at their current on-disk FormIDs, this
    /// returns a warning naming them and the fix. Null when there is nothing to flag: no .seq, a BSA-only one whose
    /// bytes cannot be checked here, or every SGE quest still covered. Best-effort: any failure yields a soft
    /// advisory rather than a throw, because the write already succeeded.</summary>
    string? SeqStaleInPlaceNote(string targetPath, string targetName)
    {
        try
        {
            AssetResolver assetResolver;
            lock (_gate) { assetResolver = Assets; }                          // reentrant under the held _writeGate
            var av = assetResolver.Capture();
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

    /// <summary>Remove whole records a houseCARL patch carries — a literal drop from the plugin, the companion to
    /// <see cref="ApplyEdits"/>. In the default lane <paramref name="patch"/> is required and names an existing
    /// houseCARL-owned patch, resolved and ownership-gated the same way an extend is, because a removal only makes
    /// sense against a patch that already carries the record. In the in-place lane it drops the record from an
    /// existing plugin instead, including one houseCARL did not author. Parses every formid all-or-nothing, then
    /// drives <see cref="WritePatchBuilder.RemoveRecords"/>: present-check, remove, re-serialize, with clean-masters
    /// riding along. The default lane never touches originals.</summary>
    public WritePatchBuilder.RemovalOutcome RemoveRecords(IReadOnlyList<string> formids, string? patch,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.RemovalOutcome.Fail("no formids supplied — pass the FormID(s) of the record(s) to remove.");

        // In-place is the explicit, named-file opt-in: drop a record from an existing plugin, including one houseCARL
        // did not author, instead of from a houseCARL patch. The contract is validated up front — it requires
        // target=, and it is mutually exclusive with patch=. target= without in_place is a no-op the caller likely
        // did not mean, so it is named rather than silently ignored. Mirrors ApplyEdits' contract.
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
            // No lane is offered: housecarl_remove answers a lane-less call in its own words before reaching here,
            // so a second spelling handed down for this arm would be one nothing renders.
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it).");

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path). Pure — outside the gate.
        var keys = new List<FormKey>(formids.Count);
        var problems = new List<string>();
        var door = OpenWriteFormIdDoor();
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

        lock (_writeGate)                                                 // removal re-serializes the patch — same gate
        {
            var resolver = Resolver;                                      // builds/refreshes the index and the overlays for the re-serialize

            if (inPlace)
                return RemoveRecordsInPlace(resolver, keys, target!.Trim(), acknowledge);

            // Resolve and ownership-gate the patch path the same way an extend does: it must exist and carry the
            // houseCARL marker. No fresh-patch remedy is offered, because removal cannot create a patch and this
            // tool's patch= already names an existing one, so that remedy would tell the caller to re-issue the call
            // that just failed. The lane states that rule instead, on both of the resolver's refusals.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _,
                                              noFreshRule: WriteSentences.RemoveNoFreshPatch); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>The in-place branch of <see cref="RemoveRecords"/>, running under _writeGate. The remove counterpart
    /// of <see cref="ApplyEditsInPlace"/>, reusing every in-place seam: the same foreign-target resolver, the same
    /// persistent first-touch consent handshake keyed off the resolved path and shared with the edit and create lanes
    /// so acknowledging a plugin once covers all three, the same writable-parent pre-flight, and the same
    /// <c>editedInPlace=</c> marker rather than <c>generated=true</c>. It drives
    /// <see cref="WritePatchBuilder.RemoveRecordsInPlace"/> with the absence verify forced on.
    /// <paramref name="acknowledge"/> waives the consent axis only. There is no rulebook here: a removal pre-flights
    /// nothing, and the present-check that the target carries the record is the whole gate.</summary>
    WritePatchBuilder.RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> keys, string target, bool acknowledge)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin, which closes the coincidental-folder collision. Same resolver as the other lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place removes from the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemoveNoEquivalent) is { } locRefusal)
            return WritePatchBuilder.RemovalOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // and create lanes because it is the same "touch your original" trade-off. The check gates entry here; the
        // acknowledgement is recorded only once the removal has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: the most common in-place response shape.
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the absence verify forced on.
        var outcome = WritePatchBuilder.RemoveRecordsInPlace(resolver, keys, targetPath, targetName);

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq — both
        // best-effort, and neither failing fails the done removal. A removal can drop the last reference to a master
        // and shift on-disk FormIDs, staling the plugin's .seq; that is surfaced, never auto-regenerated.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "removal");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>source= is the forward surface's own word for the source pole, handed to the shared engine refusals
    /// so they name the parameter housecarl_forward publishes.</summary>
    const string ForwardSourceParam = "source=";

    /// <summary>Forward a named plugin's version of one or more records into a patch as an override — xEdit's "copy
    /// as override into", the inverse of <see cref="ApplyEdits"/>'s winner-override. Parses every formid
    /// all-or-nothing, pre-locates <paramref name="fromPlugin"/> when the active order does not contain it, resolves
    /// the folder-per-patch output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch), then drives
    /// <see cref="WritePatchBuilder.ForwardRecords"/>. The whole source record is copied verbatim, so the SOURCE
    /// plugin rather than the load-order winner decides the content — and forwarding the origin master reverts a
    /// record to vanilla. Originals are never touched in the default lane;
    /// <paramref name="target"/> with <paramref name="inPlace"/> is the explicit opt-in third route, forwarding into
    /// an existing plugin's own file under the same consent gate as the sibling write tools.</summary>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"{ForwardSourceParam} is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.ForwardOutcome.Fail("no formids supplied — pass the FormID(s) to forward from the source plugin.");

        // In-place is the explicit, named-file opt-in, with the same contract as the sibling write tools: in_place
        // requires target=, is mutually exclusive with into=, and target= without in_place is a no-op the caller
        // likely did not mean. Each misuse is named rather than silently ignored.
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
        var door = OpenWriteFormIdDoor();
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

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index and the overlays for the source fetch and serialize

            // A source the active order does not contain is located on disk and pre-fetched here, on both lanes: the
            // in-place TARGET must stay active by that lane's contract, but the SOURCE has no such need. A no-op for
            // an active source. The overlay must outlive the serialize, because the bodies are deep-copied during
            // the write, so it is disposed in the finally below.
            var offOrder = ResolveOffOrderForwardSource(resolver, fp, specs, out var offOverlay, out var offEpoch, out var offError, out var sourceName);
            if (offError is not null)
                return WritePatchBuilder.ForwardOutcome.Fail(offError) with { Stamp = offEpoch };
            // A path that named the ACTIVE copy resolves as that plugin, so re-spell every spec's source and the
            // engine can look it up in the index — a path is not a key there. That is also what makes the winner
            // comparison, the self-forward name check and the report's "copied from" speak the order's vocabulary.
            if (!string.Equals(sourceName, fp, StringComparison.Ordinal))
                specs = specs.Select(s => new WritePatchBuilder.ForwardSpec { Target = s.Target, FromPlugin = sourceName }).ToList();
            try
            {
                if (inPlace)
                    return ForwardRecordsInPlace(resolver, specs, target!.Trim(), acknowledge, dryRun, offOrder);

                // A dry run resolves the would-be output path without creating the mod folder.
                string outPath; bool extend, created;
                try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
                // Stamped like every post-capture outcome: the source resolve above already consulted the build.
                catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message) with { Stamp = offEpoch }; }

                var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, ForwardSourceParam, fullReadback, dryRun, offOrder);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused forward leaves no orphan folder
                return outcome;
            }
            finally { offOverlay?.Dispose(); }
        }
    }

    /// <summary>The in-place branch of <see cref="ForwardRecords"/>, running under _writeGate. Reuses every in-place
    /// seam: the same foreign-target resolver, the same persistent first-touch consent handshake keyed off the
    /// resolved path and shared across all in-place lanes, the same writable-parent pre-flight, and the same
    /// <c>editedInPlace=</c> marker rather than <c>generated=true</c>. Drives
    /// <see cref="WritePatchBuilder.ForwardRecordsInPlace"/> with the touched-record verify forced on.
    /// <paramref name="acknowledge"/> waives the consent axis only.</summary>
    WritePatchBuilder.ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs, string target, bool acknowledge,
        bool dryRun = false, WritePatchBuilder.OffOrderForwardSource? offOrder = null)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin. Same resolver as the other in-place lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place forwards into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.ForwardOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the other
        // in-place lanes because it is the same "touch your original" trade-off. A dry run bypasses the handshake and
        // never persists an acknowledgement, surfacing the pending consent as a note instead. The check gates entry
        // here; a real write's acknowledgement is recorded only once the forward has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
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
                // Stamped for the reason the edit lane's twin states (the most common in-place response shape).
                return WritePatchBuilder.ForwardOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade. Kept in the dry run, which predicts exactly what
        // the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.ForwardOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ForwardRecordsInPlace(resolver, specs, targetPath, targetName, ForwardSourceParam, fullReadback: true, dryRun, offOrder);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq — both
        // best-effort, and neither failing fails the done forward.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "forward");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Create an empty, header-only plugin: a valid TES4 header with zero records, no masters, optionally
    /// ESL-flagged, named exactly <paramref name="pluginName"/>. The primitive for "plugin Foo.esp needs to exist" —
    /// a basename-bound SKSE config trigger, a placeholder ESL, a dummy master — and it authors no record, so it adds
    /// no conflict footprint. Unlike the patch-write paths the name is used verbatim and never auto-suffixed, because
    /// a trigger plugin's whole job is that its basename matches the config bound to it; a collision therefore
    /// refuses loudly rather than renaming or overwriting, whether a plugin of that basename is already active in the
    /// order, a houseCARL mod folder of that name is already on disk, or a file of that basename sits somewhere the
    /// order is not loading (#561). The core
    /// <see cref="WritePatchBuilder.CreatePlugin"/> builds, serializes and re-reads to confirm, and a refused create
    /// that just made the output folder leaves no orphan.</summary>
    public WritePatchBuilder.CreatePluginOutcome CreatePlugin(string pluginName, bool esl = false, string? author = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                "patch is required — a header-only plugin has no record to derive a name from, so name it explicitly (e.g. 'Authoria - CraftingCategories').");

        var stem = PatchStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                $"patch '{pluginName}' has no usable name once path parts and the plugin extension are stripped — give a plain name like 'MyTrigger'.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            // Touch Resolver FIRST: in instance mode _modsDir is derived lazily inside the Resolver getter, so a cold
            // first call would otherwise see an empty _modsDir and misreport "ModsDir '' does not exist". Capturing
            // the view here both derives the paths and gives the collision check below what it needs.
            var view = Resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CreatePluginOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            // The basename is load-bearing for a trigger, so a collision is never auto-suffixed — refuse instead.
            // (a) an active plugin already owns this basename — a second one would shadow it (MO2 picks one by mod order).
            foreach (var ext in PluginExts)                              // .esp / .esm / .esl
                if (view.ContainsPlugin(stem + ext))
                    return WritePatchBuilder.CreatePluginOutcome.Fail(
                        $"a plugin named '{stem + ext}' is already active in your load order — a header-only trigger needs a UNIQUE basename (a second one would shadow it, MO2 picking the winner by mod order). Choose a different name.");
            // (b) a houseCARL mod folder of this exact name already exists — don't overwrite (could clobber a real patch
            //     sharing the name) and don't auto-rename (would break the basename trigger): refuse and point at it.
            var folder = Path.Combine(_modsDir, ModFolderName(stem));
            if (Directory.Exists(folder))
                return WritePatchBuilder.CreatePluginOutcome.Fail(
                    $"a houseCARL output folder '{ModFolderName(stem)}' already exists — houseCARL won't auto-rename a header-only plugin (its exact basename is what makes the trigger resolve). Remove that folder in MO2, or choose a different name.");
            // (c) a plugin of this BASENAME sits somewhere the order is NOT loading — the same shadow the fresh patch
            //     lanes take (#561), swept over all three extensions like (a) above and for (a)'s reason: the exact
            //     basename is what makes a trigger resolve, so a second file carrying it is what this tool avoids.
            var plugin = stem + ".esp";
            var active = ActivePluginBasenames();
            if (active.Count > 0 && ReadCompositionForShadow() is { } comp)
                foreach (var ext in PluginExts)                       // .esp / .esm / .esl — the basename is what binds
                    if (PatchStemShadow.Find(comp, _modsDir, _dataDir, _overwriteDir, stem + ext, active) is { } shadow)
                        return WritePatchBuilder.CreatePluginOutcome.Fail(
                            PatchStemShadow.Refusal(plugin, shadow, "patch", stem + ext,
                                                    "a header-only trigger needs a UNIQUE basename"));

            Directory.CreateDirectory(folder);
            WriteOwnerMeta(folder, plugin);
            var outPath = Path.Combine(folder, plugin);

            var outcome = WritePatchBuilder.CreatePlugin(outPath, esl, author, description);
            if (!outcome.Success) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            return outcome;
        }
    }

    /// <summary>Compact / ESL-renumber a plugin — the data-layer twin of xEdit's "Compact FormIDs for ESL".
    /// Renumbers <paramref name="pluginName"/>'s originating records, flat and nested (cells, placed refs, dialog
    /// INFOs), into the light range 0x800–0xFFF; with <paramref name="esl"/> false it renumbers contiguously without
    /// the light flag or ceiling. It repoints every internal reference, keeps overrides at their master FormIDs, and
    /// emits the result. By default the output is a new plugin keeping the source's exact basename, so external
    /// masters still resolve, in a fresh houseCARL mod folder, leaving the original untouched and reviewable before
    /// the swap; <paramref name="inPlace"/> overwrites the original instead, under the in-place consent and with no
    /// backup.
    /// <para>The load-bearing safety: renumbering breaks any reference from OUTSIDE the plugin, which would point at
    /// FormIDs that no longer exist. The identify pass finds those external referencers across the whole order. With
    /// none, the default path just emits the new plugin; with some, the call is refused loudly with the list unless
    /// <paramref name="repointExternals"/> is set, which also rewrites each of them in place to follow the renumber.
    /// Any in-place overwrite requires <paramref name="acknowledge"/>, and a first call without it returns a confirm
    /// prompt listing exactly what will be rewritten.</para>
    /// <para>An inactive target — on disk but not in the load order, such as a fresh patch before an MO2 refresh, or
    /// a disabled mod — is resolved by filename via the shared locate contract and compacted off-order; its declared
    /// masters must still be active. An override-only target with esl=true takes the flag-only lane, with an empty
    /// remap and the write setting the light flag.</para>
    /// <para>Refuses loudly and writes nothing when the plugin is not found on disk, is ambiguous, was excluded as
    /// unparseable, needs more IDs than the light window holds, declares a master that is not active, or hits a
    /// serialize fault. Serialized on the write gate; the identify pass is one whole-order link walk, a deliberate
    /// one-shot cost.</para></summary>
    public WritePatchBuilder.CompactOutcome CompactPlugin(
        string pluginName, bool esl = true, bool inPlace = false, bool repointExternals = false,
        bool acknowledge = false, string? patchName = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CompactOutcome.Fail("plugin is required — name the plugin filename to compact (e.g. 'CoolMod.esp').");

        lock (_writeGate)                                                 // one write at a time; the whole resolve→build→repoint runs under it
        {
            var resolver = Resolver;                                      // builds/refreshes; reentrant with _writeGate
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CompactOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            var name = pluginName.Trim();
            string? srcPath;
            string? offOrderNote = null;
            if (view.ContainsPlugin(name))
            {
                if (view.ExcludedPlugins.TryGetValue(name, out var excluded))
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"cannot compact '{name}': it was EXCLUDED from this session ({excluded}) — houseCARL won't renumber a plugin it can't fully parse. The file is untouched.");
                srcPath = view.PluginPath(name);
                if (srcPath is null || !File.Exists(srcPath))
                    return WritePatchBuilder.CompactOutcome.Fail($"'{name}' not found on disk at {srcPath ?? "<unresolved>"} — nothing to compact.");
            }
            else
            {
                // Not in the active order → resolve the file on disk through the shared locate contract, covering
                // enabled, disabled and unlisted mod folders. This is the pre-enable finishing lane: ESL-flagging a
                // patch before an MO2 refresh puts it in plugins.txt. The requirement that protects correctness is
                // unchanged — every declared master must be active — and the external-referencer scan still runs
                // over the active order, which for a plugin nothing active masters is correctly empty.
                string modsDir, dataDir, overwriteDir, profileDir;
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                var comp = Mo2LoadOrder.ReadComposition(profileDir);
                var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, name, null);
                if (loc.Error is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not an active plugin in your load order, and no on-disk copy was found either ({loc.Error})");
                if (loc.Ambiguous is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                        $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to compact. " +
                        "Enable the one you mean in MO2, or remove the duplicates.");
                srcPath = loc.Path!;
                offOrderNote = $"'{name}' is not in the active load order (found: {loc.Where}) — compacted OFF-ORDER; " +
                               "masters resolved from the active order. Enable the result in MO2 to use it.";
            }

            ModKey modKey;
            try { modKey = ModKey.FromFileName(name); }
            catch (Exception ex) { return WritePatchBuilder.CompactOutcome.Fail($"'{name}' is not a valid plugin filename ({ex.Message})."); }

            // A localized target refuses the in-place lane, checked as early as possible: before the identify pass,
            // the consent gate, and anything written or staged. A caller whose target also has external referencers
            // would otherwise meet the referencer refusal first, follow its repoint remedy, and only then be told the
            // operation was never possible.
            // The in-place write's own check cannot fire here: a compaction does not re-serialize the target, it
            // builds a fresh plugin and writes that over the original, so the mod handed to the write is never
            // flagged localized. What makes it refusable is what the rebuild does to a localized plugin, and both
            // outcomes are silent and land on a file with no review step and no undo: when the strings resolve, the
            // result is de-localized, with one language baked in and the mod's .STRINGS set no longer describing it;
            // when they do not, the same path bakes in blanks.
            // Keyed on the header flag, deliberately wider than the strings-resolve-nowhere case, because detecting
            // that case precisely is machinery that does not exist and neither outcome may happen silently. The
            // new-file lane is untouched: its output is a plugin the modder reviews before swapping it in, which is
            // the distinction this refusal rests on. Read once: the in-place lane refuses on it, the new-file lane
            // reports on it below.
            // Every shape refuses in place, and a source houseCARL could not READ refuses too — unreadable is not
            // not-localized. The shape decides only which sentence the caller gets, never the outcome.
            var srcShape = LocalizedStrings.Assess(srcPath, view.DataDir);
            // The decision collapses and stays fail-closed: anything that is not a read-and-clear flag refuses. The
            // WORDS do not — see CompactInPlaceRefusal. Same boolean, two jobs, only one of which may collapse.
            bool srcLocalized = srcShape.Shape != LocalizedShape.NotLocalized;
            if (inPlace && srcLocalized)
                return WritePatchBuilder.CompactOutcome.Fail(CompactInPlaceRefusal(name, srcShape));

            // The NEW-FILE lane's own refusal: a localized source whose strings houseCARL can find NOWHERE reads
            // every value EMPTY, and this lane copies that read into a plugin the caller keeps. The in-place refusal
            // above covers the wider flag, so this fires only for the new-file lane and only for that one shape.
            if (LocalizedStrings.ResolvesNowhere(srcShape.Shape))
                return WritePatchBuilder.CompactOutcome.Fail(
                    UnresolvableStringsRefusal(name, srcShape, "compact"));

            // 1. originating record keys + the remap into the (light, by default) window.
            if (!WritePatchBuilder.TryReadOriginatingKeys(srcPath, modKey, out var keys, out var keyErr))
                return WritePatchBuilder.CompactOutcome.Fail(keyErr!);
            string? flagOnlyNote = null;
            uint floor = RemapEngine.EslFloor;
            IReadOnlyDictionary<FormKey, FormKey> remapDict;
            if (keys.Count == 0)
            {
                // An override-only or empty plugin has nothing to renumber, but with esl=true the job the caller
                // wants — make it light — is trivially satisfiable, because the light window only constrains
                // originating records. Proceed with an empty remap: every record copies verbatim and the write sets
                // the light flag.
                if (!esl)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' defines no originating records to renumber (it carries only overrides, or is empty) — nothing to compact. " +
                        "(With esl=true this would still set the ESL/light header flag — always valid for an override-only plugin.)");
                remapDict = new Dictionary<FormKey, FormKey>();
                flagOnlyNote = $"'{name}' defines no originating records — nothing renumbered; every record copied verbatim with the ESL (light) flag set (always valid for an override-only plugin).";
            }
            else
            {
                uint ceiling = esl ? RemapEngine.EslCeiling : FormIdRange.ObjectIdMax;   // light window, or the full 24-bit object-ID range
                var plan = RemapEngine.BuildSequentialRemap(keys, modKey, floor, ceiling);
                if (!plan.Success) return WritePatchBuilder.CompactOutcome.Fail(plan.Error!);
                remapDict = plan.Dict;
            }

            // The identify pass: which plugins outside the target reference a record being renumbered — the break
            // risk. Nothing being renumbered means nothing can break, so the whole-order walk is skipped.
            var targets = remapDict.Keys.ToHashSet();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var id = targets.Count == 0
                ? new RemapEngine.IdentifyResult(Array.Empty<RemapEngine.ExternalRef>(), Array.Empty<string>(), 0, 0,
                                                 Array.Empty<string>(), Array.Empty<RemapEngine.ExternalOverride>(), Array.Empty<string>())
                : RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);

            // External-referencer policy: never silently ship a compaction that dangles an external reference.
            if (id.HasExternalReferencers)
            {
                var refList = $"{string.Join(", ", id.ExternalPlugins.Take(25))}{(id.ExternalPlugins.Count > 25 ? $", … (+{id.ExternalPlugins.Count - 25} more)" : "")}";
                if (!repointExternals)
                {
                    // This refusal's remedy is "re-run with repoint_externals", so it has to know whether that re-run
                    // would itself be refused — which happens when a referencer's strings are in a state houseCARL
                    // cannot rewrite. The caller learns that here rather than by following the instruction into a
                    // second refusal. The check runs only on the referencers already named, and only on this branch;
                    // the repoint branch below has its own, which refuses before anything is written.
                    var blocked = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                    var repointClause = blocked.Count == 0
                        ? "Re-run with repoint_externals=true AND in_place=true (+ acknowledge=true) to ALSO rewrite those plugins in place to follow "
                          + "the renumber, or handle them yourself first."
                        // Split by class: LocalizedAmong fails closed on a referencer it could not read, so its hits
                        // are not homogeneous, and one list would call every one of them localized including the
                        // file nobody managed to open.
                        : $"Re-running with repoint_externals=true will NOT work here: {BlockedReferencerCensus(blocked)}. "
                          + "houseCARL rewrites neither a localized plugin nor one it cannot read in place, so the "
                          + "repoint would refuse before touching anything. "
                          // Reasons are attributed once per class: blocked referencers can be in different shapes,
                          // so an unattributed reason reads as the reason for all of them and hands one plugin's
                          // account of where its text lives to another.
                          + BlockedReferencerReasons(blocked)
                          + " Until that is resolved, handle the references yourself instead.";
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — {id.ExternalPlugins.Count} plugin(s) outside '{name}' reference records it is about to renumber; compacting it " +
                        $"WOULD BREAK those references (they would point at FormIDs that no longer exist). Referencers: {refList}. " +
                        repointClause + " Nothing was written.");
                }
                // Repointing is only coherent paired with in_place: in the new-file lane the renumbered records live
                // only in the not-yet-active output, so repointing the externals now would leave them dangling
                // against the still-active original until the MO2 swap, and broken if the user rejects the output.
                if (!inPlace)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — repoint_externals requires in_place=true. {id.ExternalPlugins.Count} plugin(s) reference records being renumbered " +
                        $"({refList}); in the new-file lane those records exist ONLY in the not-yet-active P′, so repointing the externals now would leave " +
                        "them dangling against the still-active original until you complete the MO2 swap (and broken if you reject P′). Either compact IN " +
                        "PLACE (in_place=true) so the target and its referrers move together, or handle the externals yourself after enabling P′. Nothing was written.");
            }

            // The consent gate: any in-place overwrite — the target, the external referencers, or both — needs acknowledge.
            bool willOverwriteTarget = inPlace;
            bool willRepoint = id.HasExternalReferencers && repointExternals;

            // A plugin the identify pass could not read through REFUSES the in-place lane, before anything is
            // written and whatever acknowledge says. The referencer list is only as good as the scan behind it: an
            // unread plugin may reference records about to be renumbered, and nothing downstream would catch it —
            // the repoint pre-flight is fed only the referencers the scan DID find. In place there is no backup and
            // no review step, so a note in a prompt is the wrong instrument (and acknowledge=true on the first call
            // skips the prompt entirely). This matches the existing refusal for a referencer houseCARL cannot
            // rewrite: the better-known case already refuses, and this is the strictly less-known one.
            // The new-plugin lane keeps the note — its output is reviewed before it replaces anything.
            if ((willOverwriteTarget || willRepoint) && id.UnscannablePlugins is { Count: > 0 } unread)
            {
                var c = new System.Text.StringBuilder();
                c.Append($"refused — this is an IN-PLACE rewrite (no houseCARL backup or undo) and the external-reference pass could not read ")
                 .Append(unread.Count).Append(unread.Count == 1 ? " plugin, so houseCARL cannot tell whether it references records "
                                                               : " plugins, so houseCARL cannot tell whether they reference records ")
                 .Append($"'{name}' is about to renumber: ");
                c.Append(string.Join("; ", unread.Take(25).Select(WriteSentences.UnscannablePlugin)));
                if (unread.Count > 25) c.Append($"; … (+{unread.Count - 25} more)");
                c.Append(". NOTHING was written — ").Append($"'{name}' is untouched. ")
                 .Append("Either resolve that and run this again, or compact into a NEW plugin (in_place=false), which renumbers the same records into a ")
                 .Append("file you review and swap in yourself, leaving the original and its referencers alone.");
                return WritePatchBuilder.CompactOutcome.Fail(c.ToString());
            }

            // Before the consent gate, not after it: houseCARL cannot re-serialize a localized plugin without
            // scrambling its text, so a run whose referencer rewrites include one can never happen, and the gate
            // below would otherwise ask the modder to authorize an irreversible rewrite of their originals. Also
            // before ANY write, because the referencer rewrites run only after the compacted plugin is on disk: a
            // refusal discovered there would leave the target renumbered and its referencers on the old FormIDs,
            // which nothing downstream can undo.
            // No remedy is named. "Repoint them yourself first" is false: the new FormIDs do not exist until the
            // compaction runs and this verb never discloses the mapping, and a referencer repointed to guessed ids
            // stops matching the identify pass, so the follow-up compaction succeeds and reports a clean run over
            // links that now point nowhere.
            if (willRepoint)
            {
                var localized = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                if (localized.Count > 0)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — compacting '{name}' means rewriting the plugins that reference it, and houseCARL " +
                        // Split by class, count and label both: calling every hit localized would be false about the
                        // ones houseCARL could not open, a different problem with a different fix.
                        $"cannot rewrite all of them: {BlockedReferencerCensus(localized)}. " +
                        // The referencer's own reason, verbatim from the same decision the write would have made,
                        // attributed once per class: a caller refused here is being told about a plugin they did not
                        // name, so they need where that plugin's text is, or that it could not be opened.
                        $"{BlockedReferencerReasons(localized)} " +
                        "NOTHING was written and nothing was staged — " +
                        $"'{name}' is untouched. Following the renumber means rewriting those referencers in place, and " +
                        "houseCARL rewrites neither a localized plugin nor one it cannot read in place.");
            }
            if ((willOverwriteTarget || willRepoint) && !acknowledge)
            {
                var c = new System.Text.StringBuilder();
                c.Append("CONFIRM in-place rewrite (your ORIGINAL file(s) will be rewritten — no houseCARL backup or undo; keep your own):\n");
                if (willOverwriteTarget) c.Append($"  - '{name}' will be OVERWRITTEN in place with its compacted form.\n");
                if (willRepoint)
                {
                    c.Append($"  - {id.ExternalPlugins.Count} external referencer(s) will be REWRITTEN in place to repoint to the new FormIDs:\n");
                    foreach (var pl in id.ExternalPlugins.Take(25)) c.Append($"      · {pl}\n");
                    if (id.ExternalPlugins.Count > 25) c.Append($"      · … (+{id.ExternalPlugins.Count - 25} more)\n");
                }
                // No unread-plugin note here: this lane refuses above when the scan could not read a plugin
                // through, so by the time the prompt is composed the referencer list is the whole story.
                c.Append("Re-call with acknowledge=true to proceed.");
                return WritePatchBuilder.CompactOutcome.Confirm(c.ToString());
            }

            // Pre-flight that the in-place target's parent is writable before any work — an early refusal rather than
            // a failure deep in the atomic swap. Each external referencer gets the same guarantee inside
            // RepointInPlace's own all-or-nothing write.
            if (inPlace && InPlaceParentUnwritable(srcPath, out var unwritable))
                return WritePatchBuilder.CompactOutcome.Fail(unwritable);

            // Output location: in place over the original, or a new file keeping the source's exact basename in a
            // fresh houseCARL mod folder, so its masters still resolve and the user swaps the folder in MO2.
            string outPath; bool createdFresh = false; RiderFolder rf = default;
            if (inPlace) outPath = srcPath;
            else
            {
                try { rf = ResolvePatchModFolder(patchName, null, Path.GetFileNameWithoutExtension(name) + " compacted", naming: null); }
                catch (InvalidOperationException ex) { return WritePatchBuilder.CompactOutcome.Fail(ex.Message); }
                createdFresh = rf.CreatedFresh;
                WriteOwnerMeta(rf.ModFolder, name);                       // the output keeps the source's exact basename
                outPath = Path.Combine(rf.OutputDir, name);
            }

            // Build and write the compacted plugin.
            var build = WritePatchBuilder.CompactBuild(srcPath, modKey, remapDict, view.PluginPath, outPath, esl, floor, view.DataDir);
            if (!build.Success)
            {
                if (!inPlace && createdFresh) RemoveOrNameRiderResidue(rf);   // a refused build leaves no orphan folder
                return WritePatchBuilder.CompactOutcome.Fail(build.Error!);
            }

            // Opt-in: repoint each external referencer in place, per-plugin all-or-nothing, with every result reported.
            var repointed = new List<WritePatchBuilder.RepointReport>();
            if (willRepoint)
                foreach (var ext in id.ExternalPlugins)
                {
                    var rep = RemapEngine.RepointInPlace(resolver, ext, remapDict);
                    repointed.Add(new WritePatchBuilder.RepointReport(ext, rep.Success, rep.Error));
                }

            // Carry the FormID-keyed assets a renumber moves: FaceGen head mesh and tint, and voice .fuz/.lip. The
            // records were renumbered, so the asset files the engine looks up BY FormID must follow, or a compacted
            // NPC mod silently dark-faces and a voiced mod goes mute. One captured asset view feeds both carries and
            // the SEQ check below, so all three agree on what is in the VFS. Best-
            //     effort and reported: the records are already written, so an asset that cannot be carried is a named warning in the
            //     outcome, never a failure of the compaction — and the asset layer failing to build never fails the compact
            //     either. outDir = the P′ mod-folder root (the directory holding the plugin) in BOTH lanes (new-file: the
            //     fresh folder; in-place: the target's own folder).
            //   SEQ-gate (for 7c, refresh-only): "did the source SHIP a .seq?" is a VFS question, not a single-folder one — a
            //   prior housecarl_write_seq run files the .seq in its OWN houseCARL_SEQ mod folder, and a packed mod ships it in
            //   a BSA. So resolve SEQ\<basename>.seq through the SAME captured view (mirrors the dialogue validator's CheckSeq),
            //   never a loose File.Exists on the source folder — which would miss both and re-open the silent failure A3 closes.
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS gate result — SET the moment the view resolves, BEFORE the carries
            var srcSeqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(srcPath)}.seq";
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
                var assetView = assetResolver.Capture();
                seqGate = assetView.ResolveForPlacement(srcSeqRel).Sources.Count > 0;   // VFS-aware (loose roots + active BSAs)
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, remapDict, assetView, outDir);
                voiceRename = AssetRenameService.CarryVoice(outPath, remapDict, assetView, outDir);
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // The check is the VFS answer whenever the view resolved: a later carry throwing must not downgrade a good
            // result. Only when the view never resolved does it fall back to the degraded loose-only check.
            bool sourceHadSeq = seqGate ?? File.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, srcSeqRel));

            // Refresh the start-game-enabled-quest .seq from the renumbered plugin when the source shipped one. A
            // renumber shifts every SGE quest's master-relative on-disk FormID, so a shipped .seq is now stale and
            // its quests would silently never start. Refresh only: if the source shipped no .seq, none is invented,
            // and RegenerateSeq returns a named advisory. The regeneration reads the new plugin and needs no
            // resolver; only the check above consults the view. Best-effort and reported: it never throws and never
            // fails the compact, and the outer try is belt and braces.
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, sourceHadSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if '{name}' has start-game-enabled quests, run {ToolNames.WriteSeq} on the compacted plugin." });
            }

            // Audit markers: stamp the editedInPlace breadcrumb into the meta.ini of every file rewritten in place —
            // the target and each successfully repointed external — matching the traceability the in-place edit lane
            // gives. The consent model deliberately stays compact's own per-call confirm rather than the persistent
            // acknowledgement the edit lane uses: a compaction can rewrite a broad surface, so each call re-confirms
            // with its exact overwrite list rather than letting a stale field-edit acknowledgement authorize a full
            // renumber. Markers are best-effort; a miss never fails the done write and is surfaced in Note.
            var markerNotes = new List<string>();
            if (offOrderNote is not null) markerNotes.Add(offOrderNote);
            // The new-file lane produces the SAME de-localized plugin the in-place lane is refused for; only where it
            // lands differs, so a caller who never meets that refusal still needs to be told. It states its own
            // behaviour only and does not claim the strings resolved, because when they resolve nowhere this same
            // path writes blanks and the sentence must stay true there too. It names no count of surviving languages:
            // what the source shipped is a fact about the source, but which of them survived into the plugin cannot
            // be read back out of a de-localized output.
            // Gated on the SHAPE, not on srcLocalized. That boolean is deliberately fail-closed for the refusal
            // above, and fail-closed is the wrong answer for a note, where the honest response to "houseCARL never
            // read the file" is to say nothing: a plain non-localized plugin briefly locked during the assessment
            // would otherwise be told its text lives in .STRINGS files that do not exist. ConfirmedLocalized asks the
            // narrower question — was the flag actually read and set.
            if (!inPlace && LocalizedStrings.ConfirmedLocalized(srcShape.Shape))
                markerNotes.Add(
                    $"'{name}' is flagged LOCALIZED — its text lives in separate .STRINGS files rather than in the "
                    + "plugin"
                    + (srcShape.Languages.Count > 0 ? " (" + string.Join(", ", srcShape.Languages) + ")" : "")
                    + ". The compacted plugin houseCARL wrote is NOT localized: it carries whatever this read of the "
                    + "source produced, written into the plugin itself, with no .STRINGS files of its own — so the "
                    + "source's .STRINGS files do not describe it, and any language it shipped that this read did not "
                    + "resolve is not in the output. Read the output before you enable it in place of the original.");
            if (flagOnlyNote is not null) markerNotes.Add(flagOnlyNote);
            if (inPlace) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(srcPath)); if (n is not null) markerNotes.Add(n); }
            foreach (var r in repointed.Where(r => r.Success))
            {
                var rp = view.PluginPath(r.Plugin);
                if (rp is not null) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(rp)); if (n is not null) markerNotes.Add(n); }
            }

            return new WritePatchBuilder.CompactOutcome(
                true, null, false, outPath, name, inPlace, esl, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                build.Bytes, id.ExternalPlugins, repointed, id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples,
                markerNotes.Count > 0 ? string.Join(" ", markerNotes) : null, assetRename, id.ExternalOverriders, voiceRename, seqRegen,
                id.UnscannablePlugins);
        }
    }

    /// <summary>A blocked referencer list, split into the two classes it holds.
    /// <see cref="RemapEngine.LocalizedAmong"/> fails closed on a referencer it could not open, so its hits are a mix
    /// of "flagged LOCALIZED" and "could not be read". Both block the repoint, but they are not the same problem and
    /// do not have the same fix, so rendering them as one list would report the unreadable file as localized.</summary>
    static (IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Localized,
            IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Unread)
        SplitBlockedReferencers(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
        => (blocked.Where(b => LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList(),
            blocked.Where(b => !LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList());

    /// <summary>"2 flagged LOCALIZED (A.esp, B.esp), and 1 houseCARL could not read (C.esp)" — counts and names per
    /// class, and a class with no hits contributes nothing.</summary>
    internal static string BlockedReferencerCensus(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0) parts.Add($"{localized.Count} flagged LOCALIZED ({NameList(localized)})");
        if (unread.Count > 0) parts.Add($"{unread.Count} houseCARL could not read ({NameList(unread)})");
        return string.Join(", and ", parts);

        static string NameList(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> l)
            => string.Join(", ", l.Take(25).Select(x => x.Plugin)) + (l.Count > 25 ? $", … (+{l.Count - 25} more)" : "");
    }

    /// <summary>An attributed reason for the first of EACH class, never only the first hit overall, which would leave
    /// a whole class unmentioned and send the modder looking for .STRINGS files instead of for the file they cannot
    /// open. The lead-in differs because the two facts differ.</summary>
    internal static string BlockedReferencerReasons(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0)
        {
            parts.Add($"Where {localized[0].Plugin}'s text is: {localized[0].Why}");
            if (localized.Count > 1)
                parts.Add($"(The other {localized.Count - 1} localized referencer(s) are reported the same way if you compact them.)");
        }
        if (unread.Count > 0)
        {
            parts.Add($"Why {unread[0].Plugin} is blocked: {unread[0].Why}");
            if (unread.Count > 1)
                parts.Add($"(The other {unread.Count - 1} unreadable referencer(s) are the same.)");
        }
        return string.Join(" ", parts);
    }

    /// <summary>The refusal for a plugin that IS flagged localized and whose <c>.STRINGS</c> houseCARL can find
    /// nowhere — the one shape where the read itself is empty, so a lane that copies the read into a new file writes
    /// blanks. Shared by compact and merge, one wording: both bake the same read into a plugin the caller keeps, and
    /// neither may do it silently.
    ///
    /// <para>It says houseCARL cannot FIND the tables, never that the plugin has none: MO2's VFS merges mod folders
    /// at runtime, so a plugin's strings can sit in an archive in another mod folder that no path walked here can
    /// see.</para>
    ///
    /// <para>Three shapes arrive here and the words differ per shape — what the read WILL be, and what to do about it.
    /// Nothing found anywhere is fixed by putting the tables somewhere houseCARL can see; a folder that is there and
    /// would not list — the plugin's <c>Strings\</c> folder, or the mod folder itself — is fixed by freeing THAT
    /// folder, and telling its caller to place tables in it describes a folder they already have.</para></summary>
    /// <param name="verb">The operation, as the report names it — "compact", "merge".</param>
    internal static string UnresolvableStringsRefusal(string name, LocalizedAssessment a, string verb)
    {
        // WHERE the text is comes from the one renderer that already gets it right for both shapes: it names what
        // the Strings folder beside the plugin actually holds rather than claiming it is empty, and it drops the
        // game-Data clause when there was no Data folder to search. Hand-rolling it here asserted both.
        //
        // What the READ will be is per shape, and neither claim may be made for the other: nothing resolves a
        // plugin whose text is nowhere, so its values are empty; a folder that could not be listed was never read,
        // so what comes back from it is unknown.
        var unlistable = a.Shape is LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable;
        var consequence = unlistable
            ? "houseCARL cannot tell what its text reads as, or an empty value from a real one"
            : "every name, description and message it carries reads back EMPTY";

        // And so is the REMEDY. "Put the tables where houseCARL can see them" is the answer when nothing was found
        // anywhere; told to a plugin whose folder is already sitting there unreadable, it sends the caller to fill a
        // folder nothing could open. Those shapes need the folder freed, not populated — and the sentence names WHICH
        // folder, because the two are different places on disk and only one of them is the one to fix.
        var folder = a.Shape == LocalizedShape.StringsFolderUnreadable
            ? $"the Strings folder beside '{name}'"
            : $"the folder '{name}' sits in";
        var remedy = unlistable
            ? $"Let houseCARL read {folder} — close whatever is holding it open, or fix its permissions — and run "
              + "this again."
            : "Put this plugin's .STRINGS where houseCARL can see them — enable the mod that provides them, or place "
              + "them in a Strings folder beside the plugin — and run this again.";
        return $"refused — houseCARL did not {verb} '{name}'. "
             + LocalizedTargetUnsupportedException.WhereTheTextIs(a) + " "
             + $"So {consequence}, and a {verb} writes whatever this read produced into a NEW plugin you keep, with "
             + "nothing left in it to tell that text from a plugin that never had any. "
             + remedy + " Nothing was written.";
    }

    /// <summary>The in-place compaction's refusal, rendered per shape. The refusal decision is one fail-closed
    /// boolean, but its words cannot be: the localized arm's clauses are all about a translated plugin's
    /// <c>.STRINGS</c> files and end on the new-file lane, and told to a source houseCARL could not open that would
    /// describe tables nobody established exist and point at a lane that reads the same file and fails the same
    /// way.</summary>
    static string CompactInPlaceRefusal(string name, LocalizedAssessment a)
    {
        var head = $"houseCARL did not compact '{name}' in place — the file is unchanged and nothing was staged. "
                 + LocalizedTargetUnsupportedException.ShapeClause(a) + " ";
        return a.Shape switch
        {
            // Never opened: no claim about tables, and no lane to switch to, because the new-file lane reads the
            // same file and fails the same way.
            LocalizedShape.Unreadable =>
                head + "houseCARL does not rewrite a destination it cannot classify. Compacting into a NEW plugin is "
                     + "not the lane to switch to either — it reads the same file and fails the same way. "
                     + LocalizedTargetUnsupportedException.RemedyUnreadable,

            LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
                or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly
                or LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable
                or LocalizedShape.Nowhere =>
                head + "A compaction does not re-serialize your plugin, it builds a NEW one and writes that over the "
                     + "original, and houseCARL will not replace a translated plugin's .STRINGS files on your own copy: "
                     + "it cannot swap the plugin and its tables as one operation, and the file the game loads would "
                     + "stop being the translated plugin you have, with no backup and nothing to undo it. "
                     + $"Re-run without in_place to compact '{name}' into a NEW plugin instead: the same renumber, left "
                     + "in its own mod folder for you to check and enable yourself. That output is NOT localized — it "
                     + "carries the text that resolved when houseCARL read the source, written into the plugin itself, "
                     + "and the source's .STRINGS files do not describe it. Read it before you swap it in.",

            // NotLocalized cannot reach here — the caller's check excludes it — and a new shape has no wording, so
            // this arm says only what is certain rather than borrowing either branch above.
            _ => head + "houseCARL will not compact this plugin in place.",
        };
    }

    /// <summary>Merge one or more active plugins into one new plugin. A merge is a records operation: the donors'
    /// records combine into a fresh plugin under a new name, with a collision-only renumber — the first donor in load
    /// order keeps its object IDs, cross-donor conflicts on the same record resolve to the load-order winner and are
    /// reported, and a losing donor's un-relisted nested children graft into the winner. The donors are never
    /// touched: new-file lane only, no consent gate. The user reviews the output, enables its folder, and deactivates
    /// the donor PLUGINS in MO2 while leaving the donor mod FOLDERS enabled, because the merged records still
    /// reference the donors' path-keyed assets, which only those folders serve; the carries cover only the
    /// FormID-keyed facegen, voice and .seq. External referencers and overriders of donor records are warned about
    /// and named rather than refused, because nothing breaks at write time — the donors stay active until the user
    /// swaps — and the remedy is to include the patch in the merge set or repoint it before disabling the donors.
    /// The FormID-keyed assets follow per donor: every donor NPC's facegen and every voiced line move to the new
    /// plugin-name folders, since the plugin name is part of those paths, and a <c>.seq</c> is refreshed when any
    /// donor shipped one. With a single donor there is nothing to combine and the operation IS a rename: the same
    /// records under a new plugin identity, keeping every object id already inside the writable range, though an id
    /// below the write floor renumbers exactly as it does for the first donor of any merge and the per-donor line
    /// reports it. A rename's side effects are reported rather than refused: the output lands in a new mod folder
    /// beside the donor's, the swap instruction applies unchanged, and the existing-saves warning covers the break a
    /// changed plugin name causes. <paramref name="patch"/> names the output MOD FOLDER, as it does on every tool
    /// that writes one, and the merged plugin inside it takes that folder's name: patch="MyMerge" writes
    /// "houseCARL - MyMerge\MyMerge.esp" through the same fresh-write resolver the record lanes use.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? patch)
    {
        // ---- argument shape; every refusal names the fix ----
        var donorsRaw = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // One donor is legitimate and IS the rename: the remap moves every donor key to the output ModKey whether or
        // not anything collided, and the facegen/voice carry and .seq refresh are per-donor because the plugin name
        // is a folder segment of those paths. So the single-donor path is the same walk with an empty collision set;
        // below-floor ids still renumber, so "nothing can collide" is not "every id is kept". The donor list stays a
        // SET, so duplicate names collapse here exactly as they do for many donors.
        if (donorsRaw.Count == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "merge needs at least ONE donor plugin — pass plugins=[\"A.esp\"] to move one plugin's records to a new " +
                "name (a rename), or plugins=[\"A.esp\", \"B.esp\", …] to combine several.");
        var patchName = (patch ?? "").Trim();
        if (patchName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "patch is required — name the NEW mod folder to create (e.g. 'MyMerge'). The merged plugin inside it takes that name ('MyMerge.esp'), and it must not already exist in your load order.");
        // patch= names the FOLDER and the plugin takes the folder's name, the rule on every tool that writes one.
        // The requested stem is what these pre-flight refusals are about, and it is also the file finally written:
        // ResolveOutputPath refuses a collision on it rather than suffixing, because the basename is load-bearing.
        var outName = PatchStem(patchName) + ".esp";
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"patch='{patchName}' does not name a valid plugin: '{outName}' ({ex.Message})."); }
        // The .esl spelling is REFUSED rather than stripped to .esp like any other extension, because it asks for
        // something the merge cannot deliver, and silently handing back a full plugin would be a degraded mode.
        if (patchName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            return WritePatchBuilder.MergeOutcome.Fail(
                // The reason is what the merge does NOT do, and only that. Neither "the donors' ids stay in the full
                // range" nor "it keeps each donor's object ids where they already are" is true on every path: an
                // already-light donor's ids are all inside the window by definition, and BuildMergeRemap renumbers
                // collisions and below-floor ids from 0x800 up — a count the report prints.
                $"refused — patch='{patchName}' asks for the .esl extension, which the game engine force-treats as a LIGHT master regardless " +
                "of the header flag, but a merge never constrains object ids to the light window: it renumbers only what it must " +
                "(cross-donor collisions, and ids below the write floor), so an id above 0xFFF would be misread in game. Pass " +
                $"patch='{PatchStem(patchName)}' instead, which writes '{outName}': if every donor was light and every merged id landed in the window, the output is written LIGHT " +
                "already; otherwise the report says so, and " + ToolNames.CompactPlugin + " on it renumbers every id into the light " +
                "window (the tools compose). Nothing was written.");
        if (donorsRaw.Any(d => string.Equals(d, outName, StringComparison.OrdinalIgnoreCase)))
            return WritePatchBuilder.MergeOutcome.Fail($"the output '{outName}' (the plugin patch='{patchName}' names) cannot also be a donor — pass patch= a NEW name.");

        lock (_writeGate)                                                 // one write at a time
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.MergeOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");
            if (view.ContainsPlugin(outName))
                return WritePatchBuilder.MergeOutcome.Fail(
                    $"'{outName}' — the plugin patch='{patchName}' names — is already an active plugin in your load order, and the merge " +
                    "output must be a NEW plugin name (merging over an existing plugin would shadow it in MO2). Pass patch= another name.");

            // ---- validate and load-order-sort the donors: merge semantics are load-order semantics, so sort rather
            //      than trusting argument order. One name-to-position index serves this sort and the master sort. ----
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < resolver.PluginNames.Count; i++) orderIndex[resolver.PluginNames[i]] = i;
            var donorInfos = new List<(string Name, string Path, ModKey Key, int Order)>();
            foreach (var d in donorsRaw)
            {
                if (!view.ContainsPlugin(d))
                {
                    // Once a cause is stated it carries its own remedy. An unconditional "Enable it in MO2 first
                    // (pass the exact filename)" both conflates the vocabulary — a MOD is enabled, a PLUGIN is
                    // activated — and asks for a filename that has already resolved to a real installed plugin.
                    var dWhy = view.ExplainAbsence(d);
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"donor '{d}' is not an active plugin in your load order." +
                        (dWhy is not null ? " " + dWhy : view.NameSuggestion(d)) +
                        " Merge reads each donor's records and conflict position from the ACTIVE order." +
                        (dWhy is not null ? "" : " Activate it in MO2 first (pass the exact plugin filename, e.g. 'CoolMod.esp')."));
                }
                if (view.ExcludedPlugins.TryGetValue(d, out var excluded))
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge '{d}': it was EXCLUDED from this session ({excluded}) — houseCARL won't merge a plugin it " +
                        "can't fully parse (it would risk dropping records it couldn't read, Q3). Nothing was written.");
                var p = view.PluginPath(d);
                if (p is null || !File.Exists(p))
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' not found on disk at {p ?? "<unresolved>"} — nothing to merge.");
                ModKey dk;
                try { dk = ModKey.FromFileName(d); }
                catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"'{d}' is not a valid plugin filename ({ex.Message})."); }
                if (!orderIndex.TryGetValue(d, out var order))            // unreachable after ContainsPlugin (same source table) — refuse rather than mis-sort
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' has no load-order position (index inconsistency, Q3). Nothing was written.");
                donorInfos.Add((d, p, dk, order));
            }
            donorInfos.Sort((a, b) => a.Order.CompareTo(b.Order));
            var donorNames = donorInfos.Select(d => d.Name).ToList();
            var transformSet = new HashSet<string>(donorNames, StringComparer.OrdinalIgnoreCase);

            // ---- 2. masters = union(donor declared masters) − donors, load-order sorted (each donor's own header order
            //      is already load-order-consistent; the union sorts by the active order so the merged header is too).
            //      This reads the donor HEADERS only — the same read check's missing_masters pass makes — and it runs
            //      HERE, before the record reads and the identify pass, because a master the active order does not
            //      carry refuses the whole merge and the caller should hear that in seconds (#729). ----
            var masterSet = new List<string>();
            var seenMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dName, _, _, _) in donorInfos)
            {
                IReadOnlyList<string> declared;
                try { declared = view.DeclaredMasters(dName); }
                catch (Exception ex)
                {
                    return WritePatchBuilder.MergeOutcome.Fail($"cannot read donor '{dName}' masters ({ex.Message}) — nothing written.");
                }
                foreach (var mfn in declared)
                    if (!transformSet.Contains(mfn) && seenMasters.Add(mfn)) masterSet.Add(mfn);
            }
            masterSet.Sort((a, b) =>
                (orderIndex.TryGetValue(a, out var ia) ? ia : int.MaxValue).CompareTo(orderIndex.TryGetValue(b, out var ib) ? ib : int.MaxValue));
            // The same predicate the serialize applies (MergeBuild resolves each master to an overlay and refuses when
            // one is absent), asked from the headers instead of after the walk. MergeBuild still asks it: a master can
            // go away between here and the write.
            foreach (var mfn in masterSet)
                if (view.PluginPath(mfn) is null)
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");

            // ---- the donors' strings, read once and used twice. A donor whose .STRINGS resolve NOWHERE reads every
            //      value EMPTY, and the merge would copy those blanks into M — refused here, before a rider folder
            //      exists and before anything is written. A donor that IS localized (and resolves) earns the
            //      de-localization note the report carries: M is a bare mod, so the text comes out inline and the
            //      donor's .STRINGS stop describing it. ----
            var localizedDonors = new List<string>();
            foreach (var (dName, dPath, _, _) in donorInfos)
            {
                var shape = LocalizedStrings.Assess(dPath, view.DataDir);
                if (LocalizedStrings.ResolvesNowhere(shape.Shape))
                    return WritePatchBuilder.MergeOutcome.Fail(UnresolvableStringsRefusal(dName, shape, "merge"));
                // ConfirmedLocalized, not "anything but NotLocalized": the note ASSERTS where a donor's text lives,
                // and a donor houseCARL could not read gives it nothing to assert. That donor fails loudly at the
                // open in MergeBuild instead.
                if (LocalizedStrings.ConfirmedLocalized(shape.Shape)) localizedDonors.Add(dName);
            }

            // ---- 3. what each donor holds, one enumeration per donor: the records it defines, the donor-space records
            //      it carries without defining (injected records), and its links into donor space. ----
            var donorModKeys = donorInfos.Select(d => d.Key).ToHashSet();
            var scans = new List<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)>();
            var donorLinks = new List<(string Donor, IReadOnlyList<FormKey> Records, IReadOnlyList<(FormKey Source, FormKey Target)> Links)>();
            foreach (var (dName, dPath, dKey, _) in donorInfos)
            {
                if (!WritePatchBuilder.TryScanMergeDonor(dPath, dKey, donorModKeys, out var scan, out var keyErr))
                    return WritePatchBuilder.MergeOutcome.Fail(keyErr!);
                scans.Add((dName, scan.Originating, scan.Carried));       // a pure-override donor (0 originating keys) is a legit patch donor
                donorLinks.Add((dName, scan.Records, scan.DonorLinks));
            }
            // An injected record — one whose FormID names a donor while another plugin carries it — is renumbered with
            // the donor carrying it, instead of being copied at an identity the merge is about to remove (#715). A
            // plugin outside the merge that carries it too is warned about by the identify pass below, like any other
            // external overrider, because the key is now in the dict.
            var donorKeys = MergeInjection.Renumberable(scans);
            // ---- output folder and plugin: the same fresh-write resolver the record lanes use, so patch= names the
            //      mod folder "houseCARL - <stem>" and the merged plugin inside it is "<stem>.esp". Resolved HERE,
            //      before the remap, because the remap is keyed on the output ModKey and the stem can still be
            //      REFUSED by a mod folder of that name (an active plugin of it was refused above). ----
            // The merged plugin's basename is load-bearing — a _DISTR.ini, a _KID.ini, a config keyed by plugin name,
            // another plugin listing it as a master all bind to it — so a folder collision refuses by name here, the
            // way create_plugin does, rather than writing "<stem>_001.esp" that none of them resolve.
            string outPath;
            bool createdFolder;
            try
            {
                outPath = ResolveOutputPath(patchName, into: null, out _, out createdFolder,
                    refuseTaken: new StemRefusal(
                        "the merged plugin",
                        "Remove it in MO2, or pass patch= a name no mod folder or active plugin already carries."));
            }
            catch (InvalidOperationException ex) { return WritePatchBuilder.MergeOutcome.Fail(ex.Message); }
            outName = Path.GetFileName(outPath);
            try { outKey = ModKey.FromFileName(outName); }
            catch (Exception ex) { return FailAfterFolder($"'{outName}' is not a valid plugin filename ({ex.Message})."); }

            // A refusal past the folder allocation removes the folder again, so "nothing was written" is true of the
            // disk too and no orphan accretes suffixes on retry.
            WritePatchBuilder.MergeOutcome FailAfterFolder(string msg)
            {
                if (createdFolder) RemoveFolderCreatedThisCall(outPath);
                return WritePatchBuilder.MergeOutcome.Fail(msg);
            }

            var plan = RemapEngine.BuildMergeRemap(donorKeys, outKey, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
            if (!plan.Success) return FailAfterFolder(plan.Error!);

            // A donor reference the remap cannot carry is named HERE, before the identify pass and the build, rather
            // than surviving the renumber and failing the serialize.
            if (MergeInjection.UnremappableLink(plan.Dict, donorLinks) is { } linkRefusal)
                return FailAfterFolder(linkRefusal);

            // ---- 4. identify-pass — WARN-and-proceed (the A4 posture; unlike compact this NEVER refuses: the donors stay
            //      installed and ACTIVE until the user swaps in MO2, so nothing breaks at write time. The report names each
            //      affected plugin with the remedy — include it in the merge set, or handle it before disabling the donors.) ----
            var targets = plan.Dict.Keys.ToHashSet();
            // readDeclaredMasters: a merge RENAMES the donors' records into a new plugin, so a dependent that only
            // lists a donor as a master loses it at the swap. Sound here because BuildMergeRemap enters every
            // originating key of every donor into the dict, so a referencer is always a declarer too and the
            // declarer-only filter cannot hide one.
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet, readDeclaredMasters: true);

            // ---- build and write the merged plugin ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath, view.DataDir);
            if (!build.Success)
            {
                return FailAfterFolder(build.Error!);                      // a refused build leaves no orphan folder
            }

            // ---- FormID-keyed assets follow the renumber, per donor: a merge renames the plugin, and the plugin
            //      NAME is a segment of the facegen and voice paths, so the carry covers every donor NPC and voiced
            //      line rather than just the id collisions. One captured asset view feeds the carries and the SEQ
            //      check. Best-effort and reported: the records are written, so an asset miss is a named warning. ----
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS answer, set the moment the view resolves
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
                var assetView = assetResolver.Capture();
                seqGate = false;                                          // the view resolved — the answer below is authoritative
                foreach (var (dName, _, _, _) in donorInfos)              // did any donor ship a .seq? VFS-aware, per donor
                    if (assetView.ResolveForPlacement($@"SEQ\{Path.GetFileNameWithoutExtension(dName)}.seq").Sources.Count > 0)
                        { seqGate = true; break; }
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, plan.Dict, assetView, outDir);
                var voiceParts = donorInfos
                    .Select(d => AssetRenameService.CarryVoice(outPath, plan.Dict, assetView, outDir, sourcePlugin: d.Name))
                    .ToList();
                voiceRename = new VoiceCarryOutcome(
                    voiceParts.Sum(v => v.FilesScanned), voiceParts.Sum(v => v.FilesCarried), voiceParts.Sum(v => v.LinesCarried),
                    voiceParts.SelectMany(v => v.Failures).ToList(), voiceParts.Any(v => v.ReadIncomplete));
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // Only when the view never resolved does this fall back to a loose per-donor-folder check, so an
            // asset-layer fault cannot silently downgrade a donor-shipped .seq to "the donors shipped none" and skip
            // the refresh with a factually wrong advisory.
            bool anyDonorSeq = seqGate ?? donorInfos.Any(d =>
                File.Exists(Path.Combine(Path.GetDirectoryName(d.Path)!, "SEQ", Path.GetFileNameWithoutExtension(d.Name) + ".seq")));

            // ---- SEQ, refresh-only, off the merged plugin: rebuilt when any donor shipped a .seq, because all their
            //      SGE quests now live in the output, whose .seq must list the new on-disk FormIDs. Donors with SGE
            //      quests but no shipped .seq get the same named advisory a compaction gives. ----
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, anyDonorSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if the donors have start-game-enabled quests, run {ToolNames.WriteSeq} on '{outName}'." });
            }

            // Surface the one behaviour change the any-donor rule can introduce: the rebuild lists EVERY SGE quest in
            // the output, so a quest from a donor that shipped no .seq — and so was not auto-starting — gains an entry.
            string? note = seqRegen.Written
                ? "the regenerated .seq lists EVERY start-game-enabled quest in the output — including quests no donor's own .seq " +
                  "listed, whether because that donor shipped none or because its .seq was trimmed. Such quests were NOT " +
                  "auto-starting before; they will now."
                : null;

            // Where the output has to sit, off the positions, masters and dependents this call already computed.
            var placement = MergeLoadPosition.Derive(
                donorInfos.Select(d => (d.Name, d.Order)).ToList(), build.Masters,
                p => orderIndex.TryGetValue(p, out var i) ? i : null);

            return new WritePatchBuilder.MergeOutcome(
                true, null, outPath, outName, donorNames, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                plan.Donors, build.Conflicts, id.ExternalPlugins, id.ExternalOverriders,
                id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples, build.Bytes, note,
                assetRename, voiceRename, seqRegen, build.LightDonors, build.HeaderMetaDonors, build.MasterDonors,
                id.UnscannablePlugins, localizedDonors, id.MasterDeclarers, build.LightCarried, build.OriginatingRecords,
                placement);
        }
    }

    /// <summary>Create brand-new records in one patch — the net-new authoring capability, the sibling of
    /// <see cref="ApplyEdits"/>, and the one-shot route for a nested unit (a dialogue topic and its lines, a cell and
    /// its placed refs) where a child's <c>parent</c> names a same-call sibling by editorid. Each spec resolves its
    /// record type (a catalog name or 4-char signature) to one concrete catalog name, refusing an unknown or ambiguous
    /// one; maps its field operations to core write requests rooted at that type, since a create op takes no formid
    /// and sets fields on the new record; a flat top-level record needs no parent, a nested child passes one — an
    /// existing parent's FormKey, or a record created in a prior into= call — plus a collection when the parent holds
    /// more than one fitting child list. Then it resolves the folder-per-patch output, fresh or <paramref name="into"/>
    /// an existing houseCARL-owned patch, and drives <see cref="WritePatchBuilder.CreateRecords"/>. Each new record's
    /// FormID is auto-allocated at 0x800 and above and reported, and originals are never touched. All-or-nothing: any
    /// malformed spec refuses the whole call with per-record reasons, and the core likewise refuses the whole batch on
    /// any creatability or parent problem. One serialize for the lot.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback = false,
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
    {
        if (records is null || records.Count == 0)
            return WritePatchBuilder.CreateOutcome.Fail("no records to create supplied — pass one or more {record_type, editorid, operations?, parent?, collection?, grid?} specs.");

        var problems = new List<string>();
        var specs = new List<WritePatchBuilder.CreateSpec>(records.Count);
        // One write door for the whole call, as the sibling verbs open: parent= is the only token here that can be
        // a FormID, and it is a write's, so a runtime one that is not a sibling editorid is refused with the plugin
        // form to use.
        var door = OpenWriteFormIdDoor();
        // The editorids this call declares: a parent naming one of them is a sibling reference, not a FormID, even
        // when it happens to read as eight hex characters ('DEADBEEF').
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

    /// <summary>Build one core <see cref="WritePatchBuilder.CreateSpec"/> from wire parts, shared by the single
    /// create and the batch: resolve <paramref name="recordType"/> to one concrete catalog name, require an editorid,
    /// map each field op to a core <see cref="WriteRequest"/> rooted at that type, and carry
    /// <paramref name="parent"/> and <paramref name="collection"/> through for a nested child — null means a flat
    /// top-level record. Every problem, tagged with the <paramref name="where"/> label, is appended to
    /// <paramref name="problems"/>, and this returns null iff this record contributed any.</summary>
    WritePatchBuilder.CreateSpec? BuildCreateSpec(FormIdDoor door, string? recordType, string? editorid, IReadOnlyList<BulkOp> operations,
        string? parent, string? collection, string? grid, string where, List<string> problems,
        IReadOnlySet<string>? siblingEditorids = null)
    {
        var prefix = where + ": ";
        int before = problems.Count;

        // parent= takes an EditorID as well as a FormID, so only a runtime FormID is judged here; everything else
        // is left to the core's own parse. An eight-hex EditorID this call declares is a sibling reference and is
        // never read as a FormID, so the runtime refusal runs only once that lookup has missed.
        bool parentIsSibling = parent is not null && siblingEditorids is not null && siblingEditorids.Contains(parent.Trim());
        if (!parentIsSibling && door.RuntimeRefusal(parent) is { } parentRefusal) problems.Add($"{prefix}parent: {parentRefusal}");

        string? catalogName = null;
        if (string.IsNullOrWhiteSpace(recordType))
            problems.Add($"{prefix}record_type is required (a catalog name like 'Keyword'/'Spell'/'Weapon' or a 4-char signature like 'KYWD').");
        else
        {
            try
            {
                var types = ResolveTypeFilter(recordType.Trim());
                if (types.Count != 1)
                    problems.Add($"{prefix}record_type '{recordType}' is ambiguous ({types.Count} matches) — use a specific catalog name (e.g. one of: {string.Join(", ", types.Select(t => RecordNaming.StripGetterInterface(t.Name)))}).");
                else catalogName = RecordNaming.StripGetterInterface(types[0].Name);
            }
            catch (ArgumentException ex) { problems.Add($"{prefix}{ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(editorid))
            problems.Add($"{prefix}editorid is required — the EditorID the new record is referenced by (e.g. in SkyPatcher/SPID).");

        // Map each field op to a core WriteRequest rooted at the create type, only once the type resolved, and
        // collect every malformed op.
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

    /// <summary>Resolve the folder-per-patch output, fresh or <paramref name="into"/> an existing houseCARL-owned
    /// patch, then drive the core multi-record create and serialize under the write gate, one write at a time. A
    /// refused create that just made the output folder leaves no orphan. Shared by the single and batch
    /// create.</summary>
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback,
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
    {
        // In-place is the explicit, named-file opt-in: create into an existing plugin, including one houseCARL did
        // not author, instead of writing a new patch. The contract is validated up front — it requires target=, is
        // mutually exclusive with into=, and target= without in_place is a no-op the caller likely did not mean, so
        // it is named rather than silently ignored. Mirrors ApplyEdits' in-place contract exactly.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to create into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place creates into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to create into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");
        // replace= answers the in-place collision refusal and nothing else: a fresh patch has nothing to collide with,
        // and into= already rebuilds its own record at a stable FormKey so a re-run stays idempotent.
        if (!inPlace && replace)
            return WritePatchBuilder.CreateOutcome.Fail(
                "replace=true overwrites a record the in-place TARGET already defines under the same editorid, and is only meaningful with in_place=true. Drop it, or name the file to create into.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;
            var rulebook = Rulebook;

            if (inPlace)
                return CommitCreateInPlace(resolver, rulebook, specs, target!.Trim(), acknowledge, replace);

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, freshPatch: FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            // Post-write verify steps, leaving the create path itself untouched: voice (.fuz/.lip) coverage, the
            // result-script binding, then the cell structural-shell report. Each is a no-op unless the call created
            // the relevant record kind, and none can fail the create, which already succeeded.
            return outcome.Success ? EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver))) : outcome;
        }
    }

    /// <summary>The in-place branch of <see cref="CommitCreate"/> — the create-side companion of
    /// <see cref="ApplyEditsInPlace"/>, reusing every in-place seam: the same foreign-target resolver, the same
    /// persistent first-touch consent handshake keyed off the resolved path, the same writable-parent pre-flight, and
    /// the same <c>editedInPlace=</c> marker rather than <c>generated=true</c>. It diverges in three ways: it drives
    /// <see cref="WritePatchBuilder.CreateRecordsInPlace"/>, which allocates into the target rather than editing an
    /// existing record; it returns a <see cref="WritePatchBuilder.CreateOutcome"/>; and because in-place create can
    /// author dialogue lines and cells under any parent, it runs the same post-write voice, result-script and
    /// cell-shell coverage checks the patch-lane create runs. The created-record verify is forced on, and
    /// <paramref name="acknowledge"/> waives the consent axis only. Runs under <c>_writeGate</c>, which the caller
    /// holds.</summary>
    WritePatchBuilder.CreateOutcome CommitCreateInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.CreateSpec> specs,
        string target, bool acknowledge, bool replace = false)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin, which closes the coincidental-folder collision. Same resolver as the edit lane.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.CreateOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // lane because acknowledging a plugin once covers both editing and creating into it — the same "touch your
        // original" trade-off. The check gates entry here; the acknowledgement is recorded only once the create has
        // landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: this branch is reached only after the view above
            // resolved the target, and it is the most common in-place response shape.
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the created-record verify forced on.
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true,
                                                             replaceExisting: replace);

        // On success, record the acknowledgement, then run the same post-write checks the patch lane runs, since the
        // service owns the live asset resolver and in-place create can author dialogue lines and cells under any
        // parent. Each is a no-op unless that record kind was created, and none can fail the write. Then stamp the
        // audit marker, best-effort: a marker miss never fails the done create.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "create");
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            // enriched.Note FIRST, exactly as the other three in-place lanes join: the core create lane now emits the
            // master-grow re-sort note, and dropping it here would lose the one note the caller must act on.
            var note = JoinNotes(enriched.Note, ackNote, markerNote);
            return note is not null ? enriched with { Note = note } : enriched;
        }
        return outcome;
    }

    /// <summary>The on-disk voice (.fuz/.lip) presence check, run as a post-write step on a successful create, since
    /// the service owns the live <see cref="Assets"/> resolver and the core create path stays asset-free. Only fires
    /// when the call created at least one dialogue line: <see cref="VoiceCheck.Run"/> re-opens the written patch
    /// read-only, computes each created voiced line's expected path, and checks the VFS, with the report riding back
    /// on <see cref="WritePatchBuilder.CreateOutcome.Voice"/>. It never fails the create, which already succeeded: a
    /// check failure is surfaced on the report's CheckError, and even a thrown asset-layer build is caught here.
    /// Caller holds <see cref="_writeGate"/>, where the reentrant Assets getter is safe.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithVoiceCheck(WritePatchBuilder.CreateOutcome outcome, LoadOrderResolver resolver)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        VoiceReport report;
        try { report = VoiceCheck.Run(outcome.OutputPath, outcome.Created, resolver, Assets); }
        catch (Exception ex) { report = VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { Voice = report };
    }

    /// <summary>The per-create result-script binding check, a post-write step on a successful create exactly like
    /// <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created at least one dialogue line:
    /// <see cref="DialogueScriptCheck.Run"/> re-opens the written patch read-only, validates each created INFO's VMAD
    /// result-script binding and checks its compiled `.pex` on disk, with the report riding back on
    /// <see cref="WritePatchBuilder.CreateOutcome.ScriptBinding"/>. It never fails the create; a check failure is
    /// surfaced on the report's CheckError. It needs no resolver, because the binding lives wholly on the INFO and
    /// the on-disk `.pex`.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithScriptCheck(WritePatchBuilder.CreateOutcome outcome)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        ScriptBindingReport report;
        try { report = DialogueScriptCheck.Run(outcome.OutputPath, outcome.Created, Assets); }
        catch (Exception ex) { report = ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { ScriptBinding = report };
    }

    /// <summary>The cell structural-shell report, a post-write step on a successful create exactly like
    /// <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created at least one cell:
    /// <see cref="CellShellCheck.Run"/> re-opens the written patch read-only, reads each created cell's
    /// interior/exterior kind, and lists the world content houseCARL does not author — lighting, terrain, water,
    /// navmesh — with the report riding back on <see cref="WritePatchBuilder.CreateOutcome.CellShell"/>. It never
    /// fails the create, since the cell IS written and this only says what the author must still provide; a check
    /// failure is surfaced on the report's CheckError. It needs no resolver or assets: the kind comes off the written
    /// cell's flag.</summary>
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

    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for a create: RecordType is the create type
    /// rather than derived, and a create op carries no formid because it sets a field on the new record, whose id is
    /// auto-allocated — a stray formid is refused rather than silently ignored. Builds the composition
    /// <see cref="StructSpec"/> the same way <see cref="MapEdit"/> does, so a created record's nested lists compose
    /// identically.</summary>
    WriteRequest? MapCreateEdit(BulkOp op, int index, string recordType, out string? error)
    {
        error = null;
        // ops[i] is the create surface's own member word, so a refusal names the handle the caller can act on.
        var where = $"ops[{index}]";
        if (!string.IsNullOrWhiteSpace(op.Formid))
        {
            error = $"{where}: a create operation sets a field on the NEW record, so it takes no formid (the new record's id is auto-allocated). Remove formid='{op.Formid}'.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where}: field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where}: field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        if (string.Equals(op.Verb, WriteVerbs.Transplanting, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // Named as the create surface spells it. This is reachable even though that surface declares no
            // from_plugin member, because the strict reader gates undeclared members and `op` is declared, so a
            // CopyFrom verb arrives here and must not be answered with a member the caller cannot remove.
            error = $"{where}: op=\"CopyFrom\" copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
            return null;
        }

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
        };
    }

    /// <summary>Map a wire op to a core <see cref="WritePatchBuilder.PatchEdit"/>: parse the FormID, split the dotted
    /// field path, and build the composition <see cref="StructSpec"/> when present. RecordType is deliberately not
    /// taken from the wire — the engine derives it from the resolved winner. Returns null and a named error on any
    /// malformed input.</summary>
    WritePatchBuilder.PatchEdit? MapEdit(FormIdDoor door, BulkOp op, int index, out string? error,
                                         string? fromRecord = null, string? origin = null)
    {
        error = null;
        // The caller's own spelling for this edit: inline ops are ops[i], the member housecarl_apply publishes, while
        // zip-generated ops are named by the pair and path they came from. A refusal pointing at an op index the
        // caller never wrote sends anyone fixing it to a line that does not exist.
        var where = origin ?? $"ops[{index}]";
        if (string.IsNullOrWhiteSpace(op.Formid)) { error = $"{where}: formid is required."; return null; }
        FormKey fk;
        try { fk = door.Parse(op.Formid); }
        catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where}: ", $"{where}: bad formid '{op.Formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where} ({op.Formid}): field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where} ({op.Formid}): field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        var verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb;

        // The cross-record copy source. A named source record makes from_source optional, defaulting to that
        // record's load-order winner, resolved at pre-flight where the captured view lives; without one, the source
        // plugin is the only thing identifying a version to copy, so it stays required. A source equal to the target
        // is a no-op, refused by name rather than written.
        FormKey? fromKey = null;
        if (!string.IsNullOrWhiteSpace(fromRecord))
        {
            try { fromKey = door.Parse(fromRecord); }
            catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where} ({op.Formid}): ", $"{where} ({op.Formid}): bad from '{fromRecord}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
            if (fromKey == fk)
            { error = $"{where} ({op.Formid}): from names the SAME record as formid — copying a record's field onto itself is a no-op. Drop from=, and name the plugin whose version to copy in from_source=."; return null; }
        }

        var fromPlugin = MapFromPlugin(op, verb, $"{where} ({op.Formid})", spec, specs, fromKey is not null, out error);
        if (error is not null) return null;

        return new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = path, Verb = verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
            FromPlugin = fromPlugin, FromTarget = fromKey,
        };
    }

    /// <summary>Validate and extract from_plugin for a CopyFrom op. It is required with, and only with, the CopyFrom
    /// verb, which copies the field from that plugin's version and so takes no value, values, entries, compose or
    /// composes. Both rules refuse loudly rather than silently ignoring. Returns null for a non-CopyFrom
    /// op.</summary>
    static string? MapFromPlugin(BulkOp op, string verb, string where, StructSpec? spec, IReadOnlyList<StructSpec>? specs,
        bool hasSourceRecord, out string? error)
    {
        error = null;
        if (!string.Equals(verb, "CopyFrom", StringComparison.Ordinal))   // match the engine's ordinal verb compare, so a mis-cased verb fails the same way everywhere
        {
            if (!string.IsNullOrWhiteSpace(op.FromPlugin))
                error = $"{where}: from_source is only valid with op=CopyFrom (got op={verb}).";
            return null;
        }
        // The "a copy carries no authored value" rule is independent of whether the pole was named, so it is checked
        // FIRST: below the from_plugin block, the cross-record shape returns early past it and an authored value is
        // silently discarded. Nothing downstream catches that — the rulebook short-circuits CopyFrom to its own
        // legality check, which never sees Value, and the apply takes the copy branch.
        if (op.Value is not null || op.Values is not null || op.Entries is not null || spec is not null || specs is not null)
        {
            error = $"{where}: CopyFrom copies the field from the source record's version — it takes no value/values/entries/compose/composes.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // A named source record identifies what to copy on its own, so the pole is optional and defaults to that
            // record's winner. Without one, the plugin is the only thing distinguishing a source version from the
            // target's own, so it is required or the op means nothing.
            if (hasSourceRecord) return null;
            error = $"{where}: CopyFrom requires from_source — the plugin whose version of this record to copy field_path from.";
            return null;
        }
        return op.FromPlugin.Trim();
    }

    /// <summary>Build a core composition <see cref="StructSpec"/> from the wire shape: flat coercible
    /// <c>fields</c>, positional <c>ctor_args</c>, and nested <c>sets</c>, each a path, verb and value applied to the
    /// built struct. The nested sets' RecordType carries the struct type as a label, since the validator roots them
    /// at the struct schema. A nested set may itself carry a <c>compose</c> — a recursive
    /// <see cref="StructSpec"/> selecting a polymorphic sub-arm — mapped here into the nested
    /// <see cref="WriteRequest.Struct"/> the core applies and validates end to end. Without that propagation a nested
    /// set could only set a coercible scalar, never a sub-arm. A malformed spec is a named error. It is
    /// <c>internal static</c> as a test seam and touches no instance state.</summary>
    internal static StructSpec? MapStruct(StructInput s, string where, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s.Type)) { error = $"{where}: compose.type is required (the arm / element type, e.g. 'LeveledItemEntry')."; return null; }
        List<WriteRequest>? sets = null;
        if (s.Sets is { Length: > 0 })
        {
            sets = new List<WriteRequest>(s.Sets.Length);
            foreach (var ns in s.Sets)
            {
                if (string.IsNullOrWhiteSpace(ns.Path)) { error = $"{where}: each compose.sets[] needs a path."; return null; }
                StructSpec? nestedSpec = null;
                if (ns.Compose is not null)
                {
                    nestedSpec = MapStruct(ns.Compose, where, out error);
                    if (error is not null) return null;
                }
                sets.Add(new WriteRequest
                {
                    RecordType = s.Type!, Path = SplitPath(ns.Path),
                    Verb = string.IsNullOrWhiteSpace(ns.Verb) ? "Set" : ns.Verb, Key = ns.Key, Value = ns.Value,
                    Struct = nestedSpec,
                });
            }
        }
        return new StructSpec { Type = s.Type!, Fields = s.Fields, CtorArgs = s.CtorArgs, Sets = sets };
    }

    /// <summary>Map a wire op's composes[] — many build-from-parts list elements — to core StructSpecs. Mutually
    /// exclusive with the singular compose: both set is refused rather than silently merged. Each element maps via
    /// the same <see cref="MapStruct"/> the singular compose uses, so a composes element can never be shaped
    /// differently from a compose element, and a bad element names itself. Returns null when no composes= is present;
    /// an explicitly empty composes=[] is a named caller mistake, not a silent no-op.</summary>
    static List<StructSpec>? MapComposes(BulkOp op, string where, StructSpec? singular, out string? error)
    {
        error = null;
        if (op.Composes is null) return null;
        if (singular is not null)
        {
            error = $"{where}: pass compose= (one element) OR composes= (many), not both.";
            return null;
        }
        if (op.Composes.Length == 0)
        {
            // An empty composes=[] is the clear intent for a ReplaceAll — empty the modeled list, the twin of
            // ReplaceAll values=[] on a coercible list. For any other verb an empty batch is a caller mistake.
            if (!string.Equals(op.Verb, "ReplaceAll", StringComparison.Ordinal))
            {
                error = $"{where}: composes=[] is empty — supply one or more element specs (or compose= for one); an empty composes= is only meaningful with op=ReplaceAll, to CLEAR the list.";
                return null;
            }
            return new List<StructSpec>();   // ReplaceAll composes=[] clears the modeled list
        }
        var specs = new List<StructSpec>(op.Composes.Length);
        for (int j = 0; j < op.Composes.Length; j++)
        {
            var s = MapStruct(op.Composes[j], $"{where} composes[{j}]", out error);
            if (error is not null) return null;
            specs.Add(s!);
        }
        return specs;
    }

    static string[] SplitPath(string dotted)
        => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resolve a patch's output path under the folder-per-patch model: each patch is its own MO2 mod folder,
    /// <c>&lt;ModsDir&gt;\houseCARL - &lt;name&gt;\&lt;name&gt;.esp</c>, so every houseCARL plugin is a first-class
    /// mod the user enables, orders and removes independently. A new patch always creates a fresh, marker-stamped
    /// folder, auto-suffixed so a prior reviewed patch is never clobbered; <paramref name="into"/> extends an
    /// existing houseCARL-owned patch. Originals-untouched is structural: houseCARL only ever writes a folder that is
    /// brand new or carries its own <c>meta.ini</c> marker, and refuses to write a folder it did not create even on a
    /// name collision. The caller's name is reduced to a bare stem with no directory parts, so it can never escape
    /// ModsDir. Runs under <see cref="_gate"/> like <see cref="ResolvePatchModFolder"/>, because the check-then-create
    /// of a unique stem is only race-free when every folder allocation is serialized on one gate.
    /// <paramref name="createdFolder"/> reports whether THIS call created the fresh folder, so a refused write can
    /// remove it again and "no patch written" leaves no orphan accreting suffixes on retry.
    /// <paramref name="freshPatch"/> and <paramref name="noFreshRule"/> pass through to the extend refusals' remedy,
    /// so the calling operation states how, or whether, its own fresh-write path works. Both default to claiming
    /// nothing, so a caller added later cannot inherit a sentence that is false for it.
    /// <paramref name="stemFromCaller"/> says whether the caller really spelled this name as <c>patch=</c>, which is
    /// what makes a shadow on it a refusal rather than a step to the next suffix. It defaults to reading that off
    /// <paramref name="patchName"/>, and a lane that COALESCES something else into that argument — the copy lane
    /// falls back to the new EditorID — passes its own <c>patch=</c> instead, so a refusal never names a parameter
    /// the caller left out.
    /// <paramref name="refuseTaken"/> is for a lane whose PLUGIN basename is load-bearing — merge, whose output name
    /// a _DISTR.ini or a dependent's master entry binds to: a taken stem refuses by name instead of auto-suffixing.</summary>
    string ResolveOutputPath(string? patchName, string? into, out bool extend, out bool createdFolder, bool create = true,
                             FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                             bool? stemFromCaller = null, StemRefusal? refuseTaken = null)
    {
        lock (_gate)
        {
            createdFolder = false;
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                extend = true;
                // The .esp write lane shares the extend resolver with the rider and asset lanes, so "extend my
                // renamed patch" behaves identically across records, scripts, BSAs and assets. needEsp:true because
                // the fast path only short-circuits a folder that actually holds <stem>.esp; the .esp to extend is
                // then picked inside the resolved folder — the <stem>.esp it holds, or, where the folder and plugin
                // names differ, the folder's single plugin, refusing if it holds none or several.
                var folder = ResolveOwnedPatchFolder(into, needEsp: true, freshPatch, noFreshRule);
                var direct = Path.Combine(folder, PatchStem(into) + ".esp");
                if (File.Exists(direct)) return direct;
                var sole = SoleEspInFolder(folder, out var why);
                if (sole is not null) return sole;
                throw new InvalidOperationException($"cannot extend: houseCARL folder '{Path.GetFileName(folder)}' {why}.");
            }

            extend = false;
            var baseStem = PatchStem(string.IsNullOrWhiteSpace(patchName) ? "Patch" : patchName!);
            // Every record lane that reaches here declares patch= and writes "<stem>.esp", so that is the file the
            // shadow check tests and the spelling its refusal names.
            var freeStem = UniqueStem(baseStem, stemFromCaller ?? !string.IsNullOrWhiteSpace(patchName),
                                      new PatchStemShadow.Target(s => s + ".esp", "patch"), refuseTaken);
            var newFolder = Path.Combine(_modsDir, ModFolderName(freeStem));
            var plugin = freeStem + ".esp";
            // A dry run (create:false) resolves the would-be path only — no folder, no meta.ini — so the disk stays
            // exactly as it was. The real write re-resolves and creates.
            if (create)
            {
                Directory.CreateDirectory(newFolder);
                createdFolder = true;
                WriteOwnerMeta(newFolder, plugin);
            }
            return Path.Combine(newFolder, plugin);
        }
    }

    /// <summary>A write refused after <see cref="ResolveOutputPath"/> created a fresh folder removes that folder
    /// again, so "no patch written" is true of the disk too and no orphan accretes suffixes on retry. Deletion is
    /// gated by a content check rather than trust: only a folder holding nothing beyond our own meta.ini and an empty
    /// <c>.housecarl-tmp</c> staging leftover is removed — anything else means the folder gained real content and
    /// stays. Best-effort: a cleanup failure never masks the write's own reported outcome.</summary>
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
