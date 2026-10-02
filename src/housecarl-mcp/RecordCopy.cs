using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>Build a walk's ordered source universe from the caller's pole list: each element is one pole, first hit
    /// wins, and an off-order element's overlay is appended OPEN. Contracts in docs/architecture/write-path.md.</summary>
    internal SourceChain? BuildSourceChain(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, Mo2Roots roots,
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
                return view.PluginPath(pluginName) is { } p ? PluginPaths.InstallLayerOfPath(p, roots) : null;
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
            if (PluginPaths.LooksLikePath(spelling) && PluginPaths.ActiveNameForPath(view, spelling) is { } activeName)
            {
                arms.Add(new SourceArm(activeName, SourceArmKind.ActiveOrder, $"'{activeName}' (active in the load order; named by path)",
                    fk => view.GetRecord(session, activeName, fk), ActiveLayer(activeName)));
                continue;
            }

            comp ??= Mo2LoadOrder.ReadComposition(roots.ProfileDir);

            // offerModParam is false: this refusal names a LIST element, whose disambiguator is a full path in that element.
            var loc = OutputLocations.LocatePluginFileOnDisk(comp, roots, spelling, null, offerModParam: false);
            if (loc.Error is not null)
            {
                // Suggested from every plugin the locate SEARCHED, not just the active order; empty when nothing is close.
                var pool = Mo2LoadOrder.AllPluginFileNames(comp, roots.ModsDir, roots.DataDir, roots.OverwriteDir);
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
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(roots.DataDir) ? null : roots.DataDir); }
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
            var layer = PluginPaths.InstallLayerOfPath(loc.Path!, roots);
            // A mod folder the profile is not loading travels as such, and both off standings are carried.
            if (layer is { Kind: SourceLayerKind.ModFolder })
                layer = loc.Served switch
                {
                    OutputLocations.ServedStanding.ModDisabled => layer with { Folder = ModFolderStanding.SwitchedOff },
                    OutputLocations.ServedStanding.ModUnregisteredLayer => layer with { Folder = ModFolderStanding.Unregistered },
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
        lock (Host.WriteGate)
        {
            var resolver = Host.Resolver;
            var snapshot = Host.ConfiguredRoots();                        // the lane's one read of the MO2 roots and plugin names
            var roots = snapshot.Roots;
            var view = resolver.Capture();
            using var session = resolver.OpenSession();
            var overlays = new List<IDisposable>();
            try
            {
                var chain = BuildSourceChain(view, session, roots, sourcePoles, "from_source", overlays, out var chainError);
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
                try { outPath = ResolveOutputPath(snapshot, patchName ?? (into is null ? newEditorid?.Trim() : null), into, out extend, out created,
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
                    ex => WritePatchBuilder.SerializeFailure("", ex, session, ""),
                    patch => WritePatchBuilder.ExtendRoundTripRefusal(patch, outPath, session));

                wrote = outcome.Success;
                return outcome;
                }
                finally { if (!wrote && created) RemoveFolderCreatedThisCall(outPath); }
            }
            finally { foreach (var d in overlays) { try { d.Dispose(); } catch { } } }
        }
    }

    /// <summary>Test seam for <see cref="BuildSourceChain"/>: drives the real builder and hands the chain to <paramref name="body"/> while its sources are still open.</summary>
    // Holds no write gate, unlike every lane, so another thread's refresh can move the roots under it; a test seam only.
    internal T WithSourceChainForGuard<T>(IReadOnlyList<string> poles, string paramName, Func<SourceChain?, string?, T> body)
    {
        var resolver = Host.Resolver;
        var roots = Host.CaptureRoots();
        var view = resolver.Capture();
        using var session = resolver.OpenSession();
        var overlays = new List<IDisposable>();
        try
        {
            var chain = BuildSourceChain(view, session, roots, poles, paramName, overlays, out var error);
            return body(chain, error);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* test teardown */ } } }
    }
}
