using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    // ---- writes ----------------------------------------------------------------------------------------

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
        var editDoor = OpenWriteFormIdDoor();
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

        // Lock order is _writeGate then _gate; contract in docs/architecture/load-order-service.md.
        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index
            var rulebook = Rulebook;
            InsideWriteGateForGuard?.Invoke();                            // test seam; null in the product

            if (inPlace)
            {
                // The overlays must stay OPEN across the whole in-place write, so they are disposed after it returns.
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

            // A dry run resolves the would-be output path WITHOUT creating the mod folder; the fresh name is only a preview.
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            // Pre-resolve any CopyFrom source that is off-order; an active-order source is resolved inside Apply.
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

    /// <summary>Locate every OFF-ORDER CopyFrom source and fetch its version of the target record, holding each overlay
    /// OPEN for the caller to dispose after the serialize; a named refusal if one cannot be located, opened or read.
    /// MUTATES <paramref name="edits"/> to re-spell a path that names an active copy. Contracts in docs/architecture/write-path.md.</summary>
    string? PrepareCopyFromSources(LoadOrderResolver resolver, IList<WritePatchBuilder.PatchEdit> edits,
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
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";
        Mo2Composition? comp = null;
        var problems = new List<string>();
        foreach (var e in edits)
        {
            // The shared predicate the engine consumes through, not a restatement of it.
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
            if (string.IsNullOrWhiteSpace(e.FromPlugin) || !LooksLikePath(e.FromPlugin!)) continue;
            if (ActiveNameForPath(view, e.FromPlugin!) is { } activeName)
                edits[i] = e with { FromPlugin = activeName };
        }
    }

    /// <summary>Locate the one <c>source=</c> plugin a forward call shares when the active order does not contain it,
    /// open it once and pre-fetch every requested record's body, handing the overlay back OPEN. Null with a null
    /// <paramref name="error"/> when the source IS active. Contracts in docs/architecture/write-path.md.</summary>
    WritePatchBuilder.OffOrderForwardSource? ResolveOffOrderForwardSource(
        LoadOrderResolver resolver, string fromPlugin, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs,
        out IDisposable? overlay, out OrderStamp? epoch, out string? error, out string sourceName)
    {
        overlay = null; error = null; sourceName = fromPlugin;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (view.ContainsPlugin(fromPlugin)) return null;      // active — the engine resolves it off the shared build
        if (LooksLikePath(fromPlugin) && ActiveNameForPath(view, fromPlugin) is { } activeName)
        {
            sourceName = activeName;                           // a path to the ACTIVE copy — in-order after all
            return null;
        }

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex) { error = $"source plugin '{fromPlugin}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}"; return null; }

        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        // offerModParam is false: this tool has no mod= parameter, and a direct path is this lane's disambiguator.
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, fromPlugin, null, offerModParam: false);
        if (loc.Error is not null)
        {
            // A did-you-mean over every plugin the locate SEARCHED, empty when nothing is close.
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

        // Disclose an excluded-but-active source reached by PATH, judged by file identity rather than by name.
        string? excludedWhy = null;
        var locName = Path.GetFileName(loc.Path!);
        if (view.ExcludedPlugins.TryGetValue(locName, out var exWhy)
            && view.PluginPath(locName) is { } servedPath && SamePluginFile(servedPath, loc.Path!))
            excludedWhy = exWhy;

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
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

    /// <summary>Build a walk's ordered source universe from the caller's pole list: each element is one pole, first hit
    /// wins, and an off-order element's overlay is appended OPEN. Contracts in docs/architecture/write-path.md.</summary>
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

        // The MO2 layer an ACTIVE plugin's own file sits in; best effort, so a root that will not derive costs only the name.
        SourceLayer? ActiveLayer(string pluginName)
        {
            try
            {
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                return view.PluginPath(pluginName) is { } p ? InstallLayerOfPath(p, modsDir, overwriteDir, dataDir) : null;
            }
            catch { return null; }
        }

        // A THROW out of the loop would bypass every Fail path and leak the overlays opened so far.
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
            // ACTIVE arm first: its bodies come off the shared captured build rather than a second overlay of the same file.
            if (view.ContainsPlugin(spelling))
            {
                var active = spelling;
                arms.Add(new SourceArm(spelling, SourceArmKind.ActiveOrder, $"'{active}' (active in the load order)",
                    fk => view.GetRecord(session, active, fk), ActiveLayer(active)));
                continue;
            }
            // A path that names the order's own copy of an active plugin is that plugin, not an off-order file.
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

            // offerModParam is false: this refusal names a LIST element, whose disambiguator is a full path in that element.
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, spelling, null, offerModParam: false);
            if (loc.Error is not null)
            {
                // Suggested from every plugin the locate SEARCHED, not just the active order; empty when nothing is close.
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

            // Lazy per-type link cache; a per-record parse fault throws out of the fetch so SourceChain stops the chain.
            var cache = ov.ToImmutableLinkCache();
            var where = $"file '{Path.GetFileName(loc.Path!)}' ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})";
            // The layer this file sits in, read off the path by the shared rule rather than parsed back out of `where`.
            var layer = InstallLayerOfPath(loc.Path!, modsDir, overwriteDir, dataDir);
            // A mod folder the profile is not loading travels as such, and both off standings are carried.
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

    /// <summary>The closure-copy operation's service half: resolve the ordered source universe, walk the source record's
    /// seed links, then hand the result to <see cref="ClosureCopy.BuildAndWrite"/>. Prose-free — the tool layer owns every sentence.</summary>
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

                // The BOUND universe: the source record's own plugin plus every FILE arm named, never a base master.
                var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
                var bound = new HashSet<ModKey>();
                if (!baseMasters.Contains(sourceKey.ModKey)) bound.Add(sourceKey.ModKey);
                // EVERY arm the caller named, whatever kind it resolved to; `winner` stays exempt, being the whole order.
                foreach (var arm in chain.Arms)
                {
                    if (string.Equals(arm.Spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase)) continue;
                    ModKey mk;
                    try { mk = ModKey.FromFileName(Path.GetFileName(arm.Spelling)); } catch { continue; }
                    if (!baseMasters.Contains(mk)) bound.Add(mk);
                }
                bool IsBound(FormKey fk) => bound.Contains(fk.ModKey);

                // The transplant note belongs to the case where the donor-bound set is EMPTY.
                var nothingBound = bound.Count == 0;

                if (ClosureWalk.ResolveSeeds(srcHit.Body, seedPaths, out var seeds) is { } seedRefusal)
                    return ClosureCopyOutcome.Fail(walk: seedRefusal.Refusal, sources: consulted);

                var scope = WalkScope.StandaloneFrom(bound, fk => view.ResolveWinner(fk) is not null);
                var walk = ClosureWalk.Run(seeds, chain, scope, exclusions);
                if (!walk.Success) return ClosureCopyOutcome.Fail(walk: walk.Refusal, sources: consulted);

                string outPath; bool extend, created;
                // The stem falls back to the new EditorID, but only patch= is the caller's own name.
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? newEditorid?.Trim() : null), into, out extend, out created,
                                                  freshPatch: FreshPatchRemedy.CreatedByOmittingInto,
                                                  stemFromCaller: !string.IsNullOrWhiteSpace(patchName)); }
                catch (Exception ex) { return ClosureCopyOutcome.Fail(engine: ex.Message, sources: consulted); }
                var patchModKey = ModKey.FromFileName(Path.GetFileName(outPath));

                // The ACTIVE target body is the service's to fetch; an IN-PATCH target is left null for core to resolve.
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

                // Cleanup is finally-shaped: a throw out of BuildAndWrite must not leave the fresh mod folder on disk.
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

    /// <summary>Test seam for <see cref="BuildSourceChain"/>: drives the real builder and hands the chain to <paramref name="body"/> while its sources are still open.</summary>
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

    /// <summary>The in-place branch of <see cref="ApplyEdits"/>, under _writeGate: resolve <paramref name="target"/>
    /// through the load order, take the consent handshake, check the parent, write with the verify forced on, stamp the marker.</summary>
    WritePatchBuilder.PatchOutcome ApplyEditsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
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
                return WritePatchBuilder.PatchOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade; kept in the dry run too.
        if (InPlaceParentUnwritable(targetPath, out var why))
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
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "edit");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
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
        if (!PluginExts.Any(e => raw.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            foreach (var ext in PluginExts)
            {
                var cand = raw + ext;
                var p = view.PluginPath(cand);
                if (p is not null) { resolvedName = cand; return p; }
            }
        return null;
    }

    /// <summary>The opening claims both first-touch prompts make: the prompt is shown until an in-place write LANDS,
    /// and the file claim is direction-neutral. Contract in docs/architecture/write-path.md.</summary>
    static string InPlaceHandshakeLead(string name, string path, string subject, string verb) =>
        $"in-place edit of '{name}' — first-time confirmation (shown until an in-place write to this {subject} LANDS; " +
        "a call that is refused records nothing, so you may see this again):\n" +
        $"  • This {verb} your ORIGINAL file ({path}) — not a copy. houseCARL keeps NO backup or undo and cannot " +
        "restore what it overwrites, so keep your own.\n";

    /// <summary>The first-touch in-place consent prompt for a PLUGIN: the shared lead plus the plugin-specific trade-off, waiving the CONSENT axis only.</summary>
    static string InPlaceHandshakeText(string pluginName, string path) =>
        InPlaceHandshakeLead(pluginName, path, "plugin", "writes to") +
        "  • houseCARL re-lays-out the WHOLE plugin the way xEdit/CK do on save (every record re-serialized), VERIFIES the records you edit, and trusts Mutagen for the rest.\n" +
        "  • It still refuses if the file can't be parsed, or carries engine-reserved (sub-0x800) records.\n" +
        "  • The default lane (a NEW patch, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    /// <summary>PERSIST the in-place acknowledgement for <paramref name="targetPath"/>, called by every in-place lane only
    /// AFTER the write it gated has landed; returns the store's error for the caller's note. Ordering contract in docs/architecture/write-path.md.</summary>
    string? PersistInPlaceConsent(bool owed, string targetPath, string what, string subject = "plugin")
    {
        if (!owed) return null;
        string? err;
        // This runs AFTER the file changed, so the last step of a successful call must not be able to throw.
        try { err = _store.RecordInPlaceAcknowledged(targetPath) is { ok: false, error: var e } ? (e ?? "unknown error") : null; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; }
        return err is null ? null
            : $"the in-place acknowledgement could not be saved ({err}) — the {what} proceeded, " +
              $"but the next in-place call will ask for this {subject} again.";
    }

    /// <summary>Writable-parent pre-flight for the in-place swap, probed with a sibling temp; true, with a named <paramref name="why"/>, means refuse.</summary>
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

    /// <summary>Stamp the <c>[houseCARL] editedInPlace=&lt;ISO&gt;</c> audit line into the target mod's <c>meta.ini</c>,
    /// preserving every other line and only under ModsDir. Best-effort: a note on failure. Contract in docs/architecture/write-path.md.</summary>
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

    /// <summary>True iff <paramref name="folder"/> is ModsDir or a folder under it — the gate that keeps the marker out of the game Data dir.</summary>
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

            // Resolve and ownership-gate the patch the way an extend does; no fresh-patch remedy is offered on this lane.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _,
                                              noFreshRule: WriteSentences.RemoveNoFreshPatch); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>The in-place branch of <see cref="RemoveRecords"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.RemoveRecordsInPlace"/> with the absence verify forced on. No rulebook: a removal pre-flights nothing.</summary>
    WritePatchBuilder.RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> keys, string target, bool acknowledge)
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
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the absence verify forced on.
        var outcome = WritePatchBuilder.RemoveRecordsInPlace(resolver, keys, targetPath, targetName);

        // On success record the acknowledgement, stamp the audit marker and flag a stale .seq; both best-effort.
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

            // A source the active order does not contain is located and pre-fetched here, on both lanes; its overlay outlives the serialize.
            var offOrder = ResolveOffOrderForwardSource(resolver, fp, specs, out var offOverlay, out var offEpoch, out var offError, out var sourceName);
            if (offError is not null)
                return WritePatchBuilder.ForwardOutcome.Fail(offError) with { Stamp = offEpoch };
            // A path that named the ACTIVE copy resolves as that plugin, so re-spell every spec's source.
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

    /// <summary>The in-place branch of <see cref="ForwardRecords"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.ForwardRecordsInPlace"/> with the touched-record verify forced on.</summary>
    WritePatchBuilder.ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs, string target, bool acknowledge,
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
                return WritePatchBuilder.ForwardOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade; kept in the dry run.
        if (InPlaceParentUnwritable(targetPath, out var why))
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
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "forward");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Create an empty, header-only plugin named exactly <paramref name="pluginName"/> — the primitive for
    /// "plugin Foo.esp needs to exist". The name is never auto-suffixed: a collision refuses loudly (#561).</summary>
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
            // Touch Resolver FIRST: in instance mode _modsDir is derived lazily inside the getter.
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
            // (c) a plugin of this BASENAME sits somewhere the order is NOT loading — the shadow the fresh patch lanes take (#561).
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

    /// <summary>Compact / ESL-renumber a plugin — the data-layer twin of xEdit's "Compact FormIDs for ESL": renumber the
    /// originating records into the light window, repoint every internal reference, and emit a new plugin keeping the source's
    /// basename, or overwrite the original under <paramref name="inPlace"/>. External referencers refuse unless <paramref name="repointExternals"/>.</summary>
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
                // Not in the active order → resolve the file on disk through the shared locate contract; every declared master must still be active.
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

            // A localized target refuses the in-place lane, checked before the identify pass, the consent gate and anything
            // staged; the new-file lane reports on it below instead. Read once, and keyed on the header flag.
            var srcShape = LocalizedStrings.Assess(srcPath, view.DataDir);
            // The decision collapses and stays fail-closed; the WORDS do not — see CompactInPlaceRefusal.
            bool srcLocalized = srcShape.Shape != LocalizedShape.NotLocalized;
            if (inPlace && srcLocalized)
                return WritePatchBuilder.CompactOutcome.Fail(CompactInPlaceRefusal(name, srcShape));

            // The NEW-FILE lane's own refusal: a localized source whose strings resolve nowhere reads every value EMPTY.
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
                // An override-only or empty plugin has nothing to renumber, but esl=true is still satisfiable: an empty remap.
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

            // The identify pass: which plugins outside the target reference a record being renumbered.
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
                    // This refusal's remedy is a re-run, so it says up front when that re-run would itself be refused.
                    var blocked = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                    var repointClause = blocked.Count == 0
                        ? "Re-run with repoint_externals=true AND in_place=true (+ acknowledge=true) to ALSO rewrite those plugins in place to follow "
                          + "the renumber, or handle them yourself first."
                        // Split by class: LocalizedAmong fails closed on a referencer it could not read.
                        : $"Re-running with repoint_externals=true will NOT work here: {BlockedReferencerCensus(blocked)}. "
                          + "houseCARL rewrites neither a localized plugin nor one it cannot read in place, so the "
                          + "repoint would refuse before touching anything. "
                          // Reasons are attributed once per class.
                          + BlockedReferencerReasons(blocked)
                          + " Until that is resolved, handle the references yourself instead.";
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — {id.ExternalPlugins.Count} plugin(s) outside '{name}' reference records it is about to renumber; compacting it " +
                        $"WOULD BREAK those references (they would point at FormIDs that no longer exist). Referencers: {refList}. " +
                        repointClause + " Nothing was written.");
                }
                // Repointing is only coherent paired with in_place.
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

            // A plugin the identify pass could not read through REFUSES the in-place lane, before anything is written.
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

            // Before the consent gate and before ANY write: a run whose referencer rewrites include a localized plugin can
            // never happen, and the rewrites run only after the compacted plugin is on disk. No remedy is named.
            if (willRepoint)
            {
                var localized = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                if (localized.Count > 0)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — compacting '{name}' means rewriting the plugins that reference it, and houseCARL " +
                        // Split by class, count and label both.
                        $"cannot rewrite all of them: {BlockedReferencerCensus(localized)}. " +
                        // The referencer's own reason, verbatim from the same decision the write would have made, once per class.
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
                // No unread-plugin note here: this lane refuses above when the scan could not read a plugin through.
                c.Append("Re-call with acknowledge=true to proceed.");
                return WritePatchBuilder.CompactOutcome.Confirm(c.ToString());
            }

            // Pre-flight that the in-place target's parent is writable before any work.
            if (inPlace && InPlaceParentUnwritable(srcPath, out var unwritable))
                return WritePatchBuilder.CompactOutcome.Fail(unwritable);

            // Output location: in place over the original, or a new file keeping the source's exact basename.
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

            // Carry the FormID-keyed assets a renumber moves — FaceGen head mesh and tint, voice .fuz/.lip — into the P′
            // mod-folder root. Best-effort and reported, since the records are already written. One captured asset view feeds
            // both carries and the SEQ gate, which asks the VFS whether the source SHIPPED a .seq rather than one folder.
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
            // The check is the VFS answer whenever the view resolved; only an unresolved view falls back to the loose check.
            bool sourceHadSeq = seqGate ?? File.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, srcSeqRel));

            // Refresh the start-game-enabled-quest .seq from the renumbered plugin when the source shipped one, never
            // inventing one. Best-effort and reported: it never throws and never fails the compact.
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, sourceHadSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if '{name}' has start-game-enabled quests, run {ToolNames.WriteSeq} on the compacted plugin." });
            }

            // Audit markers: stamp the editedInPlace breadcrumb into every file rewritten in place. Compact keeps its own
            // per-call confirm rather than the persistent acknowledgement, and the markers are best-effort.
            var markerNotes = new List<string>();
            if (offOrderNote is not null) markerNotes.Add(offOrderNote);
            // The new-file lane produces the SAME de-localized plugin the in-place lane is refused for, so it says so.
            // Gated on the SHAPE, not on srcLocalized: fail-closed is the wrong answer for a note.
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

    /// <summary>A blocked referencer list split into the two classes it holds — flagged LOCALIZED, and could not be read — which do not have the same fix.</summary>
    static (IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Localized,
            IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Unread)
        SplitBlockedReferencers(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
        => (blocked.Where(b => LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList(),
            blocked.Where(b => !LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList());

    /// <summary>"2 flagged LOCALIZED (A.esp, B.esp), and 1 houseCARL could not read (C.esp)" — counts and names per class, and a class with no hits contributes nothing.</summary>
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

    /// <summary>An attributed reason for the first of EACH class, never only the first hit overall.</summary>
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

    /// <summary>The refusal for a plugin flagged localized whose <c>.STRINGS</c> houseCARL can find NOWHERE, so the read
    /// itself is empty; shared by compact and merge, with the words per shape. It says houseCARL cannot FIND the tables,
    /// never that the plugin has none. <paramref name="verb"/> is the operation as the report names it.</summary>
    internal static string UnresolvableStringsRefusal(string name, LocalizedAssessment a, string verb)
    {
        // WHERE the text is comes from the one renderer that already gets it right for both shapes.
        // What the READ will be is per shape: nowhere resolves reads empty, an unlistable folder reads unknown.
        var unlistable = a.Shape is LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable;
        var consequence = unlistable
            ? "houseCARL cannot tell what its text reads as, or an empty value from a real one"
            : "every name, description and message it carries reads back EMPTY";

        // And so is the REMEDY: an unlistable folder needs freeing rather than populating, and the sentence names which folder.
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

    /// <summary>The in-place compaction's refusal, rendered per shape: one fail-closed boolean decides it, but its words cannot be shared.</summary>
    static string CompactInPlaceRefusal(string name, LocalizedAssessment a)
    {
        var head = $"houseCARL did not compact '{name}' in place — the file is unchanged and nothing was staged. "
                 + LocalizedTargetUnsupportedException.ShapeClause(a) + " ";
        return a.Shape switch
        {
            // Never opened: no claim about tables, and no lane to switch to.
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

            // NotLocalized cannot reach here, and a new shape has no wording, so this arm says only what is certain.
            _ => head + "houseCARL will not compact this plugin in place.",
        };
    }

    /// <summary>Merge one or more active plugins into one new plugin: the donors' records combine under a new name with a
    /// collision-only renumber, cross-donor conflicts resolving to the load-order winner. The donors are never touched —
    /// new-file lane only — external referencers are named rather than refused, and the FormID-keyed facegen, voice and
    /// <c>.seq</c> follow per donor. With a single donor the operation IS a rename. <paramref name="patch"/> names the output MOD FOLDER.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? patch)
    {
        // ---- argument shape; every refusal names the fix ----
        var donorsRaw = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // One donor is legitimate and IS the rename; the donor list stays a SET, so duplicates collapse here.
        if (donorsRaw.Count == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "merge needs at least ONE donor plugin — pass plugins=[\"A.esp\"] to move one plugin's records to a new " +
                "name (a rename), or plugins=[\"A.esp\", \"B.esp\", …] to combine several.");
        var patchName = (patch ?? "").Trim();
        if (patchName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "patch is required — name the NEW mod folder to create (e.g. 'MyMerge'). The merged plugin inside it takes that name ('MyMerge.esp'), and it must not already exist in your load order.");
        // patch= names the FOLDER and the plugin takes the folder's name, the rule on every tool that writes one.
        var outName = PatchStem(patchName) + ".esp";
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"patch='{patchName}' does not name a valid plugin: '{outName}' ({ex.Message})."); }
        // The .esl spelling is REFUSED rather than stripped, because it asks for what the merge cannot deliver.
        if (patchName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            return WritePatchBuilder.MergeOutcome.Fail(
                // The reason is what the merge does NOT do, and only that.
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
                    // Once a cause is stated it carries its own remedy.
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

            // ---- 2. masters = union(donor declared masters) − donors, load-order sorted, from the donor HEADERS only.
            //      Run HERE, before the record reads and the identify pass, because a master the
            //      active order does not carry refuses the whole merge (#729). ----
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
            // The same predicate the serialize applies, asked from the headers instead; MergeBuild still asks it.
            foreach (var mfn in masterSet)
                if (view.PluginPath(mfn) is null)
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");

            // ---- the donors' strings, read once and used twice. A donor whose .STRINGS resolve NOWHERE reads every
            //      value EMPTY and is refused here, before anything is written; a donor that IS localized earns the
            //      de-localization note the report carries. ----
            var localizedDonors = new List<string>();
            foreach (var (dName, dPath, _, _) in donorInfos)
            {
                var shape = LocalizedStrings.Assess(dPath, view.DataDir);
                if (LocalizedStrings.ResolvesNowhere(shape.Shape))
                    return WritePatchBuilder.MergeOutcome.Fail(UnresolvableStringsRefusal(dName, shape, "merge"));
                // ConfirmedLocalized, not "anything but NotLocalized": the note ASSERTS where a donor's text lives.
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
            // An injected record is renumbered with the donor carrying it, not copied at an identity the merge removes (#715).
            var donorKeys = MergeInjection.Renumberable(scans);
            // ---- output folder and plugin: the same fresh-write resolver the record lanes use, so patch= names the
            //      mod folder and the plugin inside it takes the stem. Resolved HERE, because the remap is keyed on the
            //      output ModKey and the stem can still be REFUSED by a mod folder of that name. ----
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

            // A refusal past the folder allocation removes the folder again, so no orphan accretes suffixes on retry.
            WritePatchBuilder.MergeOutcome FailAfterFolder(string msg)
            {
                if (createdFolder) RemoveFolderCreatedThisCall(outPath);
                return WritePatchBuilder.MergeOutcome.Fail(msg);
            }

            var plan = RemapEngine.BuildMergeRemap(donorKeys, outKey, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
            if (!plan.Success) return FailAfterFolder(plan.Error!);

            // A donor reference the remap cannot carry is named HERE, before the identify pass and the build.
            if (MergeInjection.UnremappableLink(plan.Dict, donorLinks) is { } linkRefusal)
                return FailAfterFolder(linkRefusal);

            // ---- 4. identify-pass — WARN-and-proceed (the A4 posture); unlike compact this NEVER refuses: the donors
            //      stay active until the user swaps, so nothing breaks at write time, and the report names each affected plugin. ----
            var targets = plan.Dict.Keys.ToHashSet();
            // readDeclaredMasters: a merge renames the donors' records, so a dependent that only lists a donor as a master loses it.
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet, readDeclaredMasters: true);

            // ---- build and write the merged plugin ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath, view.DataDir);
            if (!build.Success)
            {
                return FailAfterFolder(build.Error!);                      // a refused build leaves no orphan folder
            }

            // ---- FormID-keyed assets follow the renumber, per donor, because the plugin NAME is a segment of the
            //      facegen and voice paths. One captured asset view feeds the carries and the SEQ check, best-effort and reported. ----
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
                    voiceParts.SelectMany(v => v.Failures).ToList(), voiceParts.Any(v => v.ReadIncomplete))
                    // The donors share one asset view, so the same root fails for each: named once, not per donor.
                    { RootFailures = voiceParts.SelectMany(v => v.RootFailures).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // Only when the view never resolved does this fall back to a loose per-donor-folder check.
            bool anyDonorSeq = seqGate ?? donorInfos.Any(d =>
                File.Exists(Path.Combine(Path.GetDirectoryName(d.Path)!, "SEQ", Path.GetFileNameWithoutExtension(d.Name) + ".seq")));

            // ---- SEQ, refresh-only, off the merged plugin: rebuilt when any donor shipped a .seq, because all their
            //      quests now live in the output. A donor with SGE quests but no shipped .seq gets a named advisory. ----
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, anyDonorSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if the donors have start-game-enabled quests, run {ToolNames.WriteSeq} on '{outName}'." });
            }

            // Surface the one behaviour change the any-donor rule can introduce: a quest that was not auto-starting gains an entry.
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

    /// <summary>Create brand-new records in one patch — the sibling of <see cref="ApplyEdits"/>, and the one-shot route for
    /// a nested unit whose child names a same-call sibling by editorid. Each new record's FormID is auto-allocated at 0x800
    /// and above and reported. All-or-nothing, one serialize for the lot, and originals are never touched.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback = false,
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
    {
        if (records is null || records.Count == 0)
            return WritePatchBuilder.CreateOutcome.Fail("no records to create supplied — pass one or more {record_type, editorid, operations?, parent?, collection?, grid?} specs.");

        var problems = new List<string>();
        var specs = new List<WritePatchBuilder.CreateSpec>(records.Count);
        // One write door for the whole call: parent= is the only token here that can be a FormID.
        var door = OpenWriteFormIdDoor();
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
                var types = ResolveTypeFilter(recordType.Trim());
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
            // Post-write verify steps, each a no-op unless the call created that record kind, and none can fail the create.
            return outcome.Success ? EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver))) : outcome;
        }
    }

    /// <summary>The in-place branch of <see cref="CommitCreate"/>, reusing every in-place seam and driving
    /// <see cref="WritePatchBuilder.CreateRecordsInPlace"/>; it also runs the patch lane's post-write coverage checks.</summary>
    WritePatchBuilder.CreateOutcome CommitCreateInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.CreateSpec> specs,
        string target, bool acknowledge, bool replace = false)
    {
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write, with this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.CreateOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the shared first-touch handshake keyed off the resolved path.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the created-record verify forced on.
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true,
                                                             replaceExisting: replace);

        // On success record the acknowledgement, run the same post-write checks the patch lane runs, then stamp the marker.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "create");
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
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
        try { report = VoiceCheck.Run(outcome.OutputPath, outcome.Created, resolver, Assets); }
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
        try { report = DialogueScriptCheck.Run(outcome.OutputPath, outcome.Created, Assets); }
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

    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for a create: the type is the create type, and a stray formid is refused rather than ignored.</summary>
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
            // Named as the create surface spells it, which declares no from_plugin member of its own.
            error = $"{where}: op=\"CopyFrom\" copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
            return null;
        }

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
        };
    }

    /// <summary>Map a wire op to a core <see cref="WritePatchBuilder.PatchEdit"/>; RecordType is left to the engine, which derives it from the resolved winner.</summary>
    WritePatchBuilder.PatchEdit? MapEdit(FormIdDoor door, BulkOp op, int index, out string? error,
                                         string? fromRecord = null, string? origin = null)
    {
        error = null;
        // The caller's own spelling for this edit: ops[i] inline, or the pair and path a zip-generated op came from.
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

        // The cross-record copy source: a named source record makes from_source optional, and a source equal to the target is refused.
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

    /// <summary>Validate and extract from_plugin for a CopyFrom op: required with, and only with, CopyFrom, which takes no authored value. Null otherwise.</summary>
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
        // The "a copy carries no authored value" rule is independent of the pole, so it is checked FIRST.
        if (op.Value is not null || op.Values is not null || op.Entries is not null || spec is not null || specs is not null)
        {
            error = $"{where}: CopyFrom copies the field from the source record's version — it takes no value/values/entries/compose/composes.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // A named source record identifies what to copy on its own, so the pole defaults to that record's winner.
            if (hasSourceRecord) return null;
            error = $"{where}: CopyFrom requires from_source — the plugin whose version of this record to copy field_path from.";
            return null;
        }
        return op.FromPlugin.Trim();
    }

    /// <summary>Build a core composition <see cref="StructSpec"/> from the wire shape: flat <c>fields</c>, positional
    /// <c>ctor_args</c>, and nested <c>sets</c>, each of which may itself compose a sub-arm. A malformed spec is a named error.</summary>
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

    /// <summary>Map a wire op's composes[] to core StructSpecs through the same <see cref="MapStruct"/> the singular compose uses; mutually exclusive with it.</summary>
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
            // An empty composes=[] is the clear intent for a ReplaceAll; for any other verb it is a caller mistake.
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

    /// <summary>Resolve a patch's output path under the folder-per-patch model: a fresh, marker-stamped mod folder, or
    /// <paramref name="into"/> an existing houseCARL-owned one, with <paramref name="createdFolder"/> reporting whether THIS
    /// call cut it. The remedy arguments and the one-gate rule: docs/architecture/write-path.md. Where output lands:
    /// docs/architecture/output-and-artifacts.md.</summary>
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
                // The .esp write lane shares the extend resolver with the rider and asset lanes; needEsp:true picks the .esp inside the folder.
                var folder = ResolveOwnedPatchFolder(into, needEsp: true, freshPatch, noFreshRule);
                var direct = Path.Combine(folder, PatchStem(into) + ".esp");
                if (File.Exists(direct)) return direct;
                var sole = SoleEspInFolder(folder, out var why);
                if (sole is not null) return sole;
                throw new InvalidOperationException($"cannot extend: houseCARL folder '{Path.GetFileName(folder)}' {why}.");
            }

            extend = false;
            var baseStem = PatchStem(string.IsNullOrWhiteSpace(patchName) ? "Patch" : patchName!);
            // Every record lane that reaches here declares patch= and writes "<stem>.esp".
            var freeStem = UniqueStem(baseStem, stemFromCaller ?? !string.IsNullOrWhiteSpace(patchName),
                                      new PatchStemShadow.Target(s => s + ".esp", "patch"), refuseTaken);
            var newFolder = Path.Combine(_modsDir, ModFolderName(freeStem));
            var plugin = freeStem + ".esp";
            // A dry run (create:false) resolves the would-be path only — no folder, no meta.ini.
            if (create)
            {
                Directory.CreateDirectory(newFolder);
                createdFolder = true;
                WriteOwnerMeta(newFolder, plugin);
            }
            return Path.Combine(newFolder, plugin);
        }
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
