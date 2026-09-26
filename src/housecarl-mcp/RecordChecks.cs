using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Everything the checks area takes from outside itself.</summary>
internal interface ICheckHost : ILoadOrderHost
{
    /// <summary>A FormID door for a sweep's <c>formids=</c> tokens, with no captured view of its own.</summary>
    FormIdDoor OpenFormIdDoor();
}

/// <summary>The checks area: the errors, scripts, facegen and dialogue sweeps.</summary>
internal sealed class RecordChecks
{
    /// <summary>Every head member the checks area takes, and nothing else.</summary>
    readonly ICheckHost _host;

    internal RecordChecks(ICheckHost host) => _host = host;

    /// <summary>The on-demand whole-topic dialogue-graph validator: a DIAL validates its own graph, a QUST fans out to
    /// every topic it owns, everything judged against the resolved winners. Never throws over a verify step — a
    /// mid-run failure rides <see cref="DialogueValidationReport.CheckError"/>, a bad input is a named
    /// <see cref="DialogueValidationReport.Error"/>.</summary>
    public DialogueValidationReport ValidateDialogue(FormKey fk)
        => DialogueValidate.Run(_host.Resolver, _host.Assets, fk, null, ForceLoadedPluginNames(_host.CaptureRoots().ProfileDir));

    /// <summary>The force-loaded plugin names in <paramref name="profileDir"/>, for a check that must not blame a modder
    /// for content they did not author. Null, never an empty set, when the MO2 profile cannot be read.</summary>
    static IReadOnlyCollection<string>? ForceLoadedPluginNames(string profileDir)
    {
        var (names, err) = ImplicitPluginNames(profileDir);
        return err is null ? names : null;
    }

    /// <summary>The merged <c>check</c> surface's dialogue family: <see cref="ValidateDialogue"/> over a seed list,
    /// tallied for one section of a merged response. Deliberately thin — the family's own grammar (seed parse,
    /// cost refusal, seed budget, tally) lives in <see cref="DialogueSweep"/> rather than in this file.</summary>
    /// <param name="foldArm">an already-probed OFF-ORDER plugin, folded in at the END of the order, opened once for
    /// the whole sweep and closed when it ends.</param>
    public DialogueCheckResult CheckDialogue(IReadOnlyList<string>? seeds, int limit, bool countsOnly,
                                             LoadOrderService.PoleInfo? foldArm)
        // Bound LAZILY: the sweep calls this only once it has seeds, so a call with no seeds= refuses without
        // building the index.
        => DialogueSweep.Run(() =>
        {
            // One resolver, one asset resolver, one view and one composition read for the whole call, so every seed
            // is validated against the same build and the stamp names it.
            var resolver = _host.Resolver;
            var assets = _host.Assets;
            var view = resolver.Capture();
            // The seed door is pinned to that same view, so the seeds cannot name records from another build.
            var forceLoaded = ForceLoadedPluginNames(_host.CaptureRoots().ProfileDir);
            // The fold is opened ONCE for the whole sweep and the sweep closes it; a file that will not open is the
            // family's own named refusal.
            DialogueFold? fold = null;
            string? foldError = null;
            if (foldArm is not null)
            {
                fold = LoadOrderService.OpenDialogueFold(foldArm, out foldError, LoadOrderService.FoldLabel(foldArm), withRecords: true);
                // Placed HERE, where the file is opened and the build is in hand: a fold that reaches a render
                // unplaced would print the field's default position, which is a guess.
                fold?.PlaceIn(view);
            }
            try
            {
                return new DialogueSweep.Binding(fk => DialogueValidate.Run(resolver, assets, fk, view, forceLoaded, fold),
                                                 FormIdDoor.On(view).Parse, view.Epoch, fold, foldError);
            }
            catch
            {
                // The sweep takes ownership of the fold only once this returns, so a throw here must close the file.
                fold?.Dispose();
                throw;
            }
        }, seeds, limit, countsOnly);

    // ---- integrity sweep -------------------------------------------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for record integrity errors:
    /// dangling FormLinks, missing masters and parse failures. Thin wiring over the core
    /// <see cref="ErrorCheck.Run"/>. Read-only. A scope name NOT in the active order is located on disk and swept
    /// off-order — the pre-enable verify lane; a name found nowhere, or in several folders, still fails loudly. The
    /// record-scope, class-filter and counts-only knobs are parsed here, before any sweep runs.</summary>
    public ErrorCheckResult CheckErrors(IReadOnlyList<string>? plugins, int limit,
                                        IReadOnlyList<string>? formids, string? editoridContains,
                                        IReadOnlyList<string>? types, IReadOnlyList<string>? findings,
                                        bool countsOnly, IReadOnlyList<string>? exclude,
                                        SweepOffOrderMemo? offOrderMemo)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return ErrorCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseErrorClasses(findings, out var classes, out var classErr))
            return ErrorCheckResult.Fail(classErr!);

        // One resolver and one view for the whole call: the scope check, the refusal stamps and the sweep all name
        // the same build.
        var resolver = _host.Resolver;
        var viewAll = resolver.Capture();
        var roots = _host.CaptureRoots();

        // The exclude= axis. The `implicit` group is a fact about the MO2 composition, so it is read here and the
        // core sweep receives plain filenames, before anything is swept. Gated on the caller having written the
        // token, so a named-plugin exclusion over an unreadable profile is not refused about a group they never named.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames(roots.ProfileDir) : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ErrorCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ErrorCheckResult.Fail(excludeErr);

        if (plugins is { Count: > 0 })
        {
            var view = viewAll;
            // Membership and locate refusals are decided against THIS build, so they are stamped; a blank name
            // consulted no build and stays unstamped.
            if (SweepOffOrderScope.Split(view, plugins, roots,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ErrorCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ErrorCheckResult.Fail(splitErr.Message);
            return ClassifyMissingMasters(
                ErrorCheck.Run(resolver, viewAll, active, limit, offOrder.Count > 0 ? offOrder : null,
                               recordScope, classes, countsOnly, excluded), roots);
        }
        return ClassifyMissingMasters(
            ErrorCheck.Run(resolver, viewAll, plugins, limit, null, recordScope, classes, countsOnly, excluded), roots);
    }

    /// <summary>Fill in each report's install-vs-enable split for the masters the sweep found unsatisfied — a fact
    /// about the MO2 composition, which lives at this layer, with one home in
    /// <see cref="Mo2LoadOrder.SplitUnsatisfiedMasters"/>. A composition that cannot be read leaves every report's
    /// subset null, not empty, and the render falls back to the union remedy. <paramref name="roots"/> are the ones the sweep took.</summary>
    static ErrorCheckResult ClassifyMissingMasters(ErrorCheckResult r, Mo2Roots roots)
    {
        if (r.Error is not null || r.Reports.Count == 0) return r;
        if (!r.Reports.Any(p => p.MissingMasters.Count > 0)) return r;

        Mo2Composition comp;
        IReadOnlyCollection<string> installed;
        try
        {
            comp = Mo2LoadOrder.ReadComposition(roots.ProfileDir);
            // The install's plugin-name set, read ONCE for the whole sweep: the answer does not depend on which
            // report asked, so neither does the read.
            installed = Mo2LoadOrder.AllPluginFileNames(comp, roots.ModsDir, roots.DataDir, roots.OverwriteDir);
        }
        catch { return r; }

        var classified = new List<PluginErrors>(r.Reports.Count);
        foreach (var p in r.Reports)
            classified.Add(p.MissingMasters.Count == 0
                ? p with { InstalledButInactiveMasters = Array.Empty<string>() }
                : p with { InstalledButInactiveMasters =
                               Mo2LoadOrder.SplitUnsatisfiedMasters(installed, p.MissingMasters).InstalledButInactive });
        return r with { Reports = classified };
    }

    /// <summary>The force-loaded plugin names — in the order, absent from plugins.txt — for
    /// <see cref="SweepExclusion.ImplicitToken"/>, or the reason they could not be read. A read that did not happen
    /// is not a set that is empty.</summary>
    static (IReadOnlyList<string> Names, string? Error) ImplicitPluginNames(string profileDir)
    {
        try { return (Mo2LoadOrder.ReadComposition(profileDir).ImplicitPluginNames, null); }
        catch (Exception ex)
        {
            return (Array.Empty<string>(),
                $"exclude= could not be resolved: the MO2 profile's plugin list at '{profileDir}' could not be read " +
                $"({ex.GetType().Name}: {ex.Message}). The '{SweepExclusion.ImplicitToken}' group is defined by which " +
                "plugins that file does NOT list, so it cannot be widened without it. Nothing was swept.");
        }
    }

    /// <summary>The record-scope parse's REFUSAL alone, for the merged surface's shared-input check: the judgement
    /// that a value is malformed has to be reachable without selecting a family that uses the scope.</summary>
    internal string? SweepScopeError(IReadOnlyList<string>? formids, string? editoridContains,
                                     IReadOnlyList<string>? types)
        => BuildSweepScope(formids, editoridContains, types).Error;

    /// <summary>Parse the sweep families' shared record-scope params into a <see cref="SweepScope"/>, through the same
    /// type lookup the scan uses. Every malformed input is a named refusal before the sweep starts; (null, null) when
    /// nothing was narrowed.</summary>
    (SweepScope? Scope, string? Error) BuildSweepScope(IReadOnlyList<string>? formids, string? editoridContains,
                                                       IReadOnlyList<string>? typeSet)
    {
        HashSet<FormKey>? keys = null;
        if (formids is { Count: > 0 })
        {
            keys = new HashSet<FormKey>();
            var door = _host.OpenFormIdDoor();
            foreach (var raw in formids)
            {
                var t = raw?.Trim() ?? "";
                if (t.Length == 0) return (null, "a blank entry in formids= — pass FormID tokens (e.g. '0BCC84:Skyrim.esm').");
                try { keys.Add(door.Parse(t)); }
                catch (Exception ex) { return (null, $"bad FormID '{raw}' in formids=: {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0BCC84:Skyrim.esm'."); }
            }
        }

        // The set is the union of its entries, resolved the way records= resolves types= — one type is a set of one.
        IReadOnlyList<Type>? types = null;
        string? typeLabel = null;
        string? armLabel = null;
        if (typeSet is { Count: > 0 })
        {
            try { types = _host.Types.ResolveSet(typeSet, out armLabel); }
            catch (ArgumentException ex) { return (null, ex.Message); }
            typeLabel = string.Join(", ", typeSet.Select(t => (t ?? "").Trim()));
        }

        var scope = new SweepScope(keys, editoridContains, types, typeLabel, armLabel);
        return (scope.IsEmpty ? null : scope, null);
    }

    // ---- script-property sweep (housecarl_validate_scripts) --------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for VMAD script properties
    /// declared in the attached script's .pex chain but left unbound on the record. Thin wiring over the core
    /// <see cref="ScriptPropertyCheck.Run"/>, passing the asset build captured with the view. Read-only. The knobs are
    /// parsed here, before any sweep runs, and a named plugin the active order does not hold is swept OFF-ORDER
    /// through the same split <see cref="CheckErrors"/> uses.</summary>
    public ScriptCheckResult ValidateScripts(IReadOnlyList<string>? plugins, int limit,
                                             IReadOnlyList<string>? formids, string? editoridContains,
                                             IReadOnlyList<string>? types, string? propertyContains,
                                             IReadOnlyList<string>? findings, bool countsOnly,
                                             IReadOnlyList<string>? exclude,
                                             SweepOffOrderMemo? offOrderMemo)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return ScriptCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseScriptClasses(findings, out var classes, out var classErr))
            return ScriptCheckResult.Fail(classErr!);
        // One resolver, view, asset build and set of roots threaded through, taken in one hold.
        var (pin, captured) = _host.CapturePinAndAssets(AfterCheckPinForGuard);
        var resolver = pin.Resolver;
        var view = pin.View;
        // The exclusion resolves here, where the MO2 composition lives, exactly as it does for CheckErrors.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames(captured.Roots.ProfileDir) : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ScriptCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ScriptCheckResult.Fail(excludeErr);

        // The off-order lane, resolved exactly as CheckErrors resolves it.
        if (plugins is { Count: > 0 })
        {
            if (SweepOffOrderScope.Split(view, plugins, captured.Roots,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ScriptCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ScriptCheckResult.Fail(splitErr.Message);
            return WithWarnings(ScriptPropertyCheck.Run(resolver, view, captured.View, active, limit, recordScope,
                                                        propertyContains, classes, countsOnly, excluded,
                                                        offOrder.Count > 0 ? offOrder : null));
        }
        return WithWarnings(ScriptPropertyCheck.Run(resolver, view, captured.View, plugins, limit, recordScope,
                                                    propertyContains, classes, countsOnly, excluded));

        // The build's warnings, taken in this one hold, ride only a sweep that ran.
        ScriptCheckResult WithWarnings(ScriptCheckResult r) => r.Success ? r with { AssetWarnings = captured.Warnings } : r;
    }

    /// <summary>Test seam: invoked in the facegen and script sweeps after the pin and before the asset capture; null in the product.</summary>
    internal Action? AfterCheckPinForGuard;

    /// <summary>Sweep the facegen join. <paramref name="plugins"/> takes the same active/off-order split the errors and scripts families take, through the same memo.</summary>
    public FaceGenCheckResult CheckFaceGen(IReadOnlyList<string>? plugins, int limit,
                                           IReadOnlyList<string>? formids, string? editoridContains,
                                           IReadOnlyList<string>? types, IReadOnlyList<string>? findings,
                                           bool countsOnly, IReadOnlyList<string>? exclude,
                                           SweepOffOrderMemo? offOrderMemo)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return FaceGenCheckResult.Fail(scopeErr);
        if (!TryParseFaceGenClasses(findings, out var classes, out var classErr))
            return FaceGenCheckResult.Fail(classErr!);

        var (pin, captured) = _host.CapturePinAndAssets(AfterCheckPinForGuard);   // one VFS build for every path this sweep resolves
        var resolver = pin.Resolver;
        var view = pin.View;
        var assets = captured.View;
        var roots = captured.Roots;

        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames(roots.ProfileDir) : (Array.Empty<string>(), null);
        if (implicitErr is not null) return FaceGenCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return FaceGenCheckResult.Fail(excludeErr);

        List<(string Name, string Path)> offOrder = new();
        if (plugins is { Count: > 0 })
        {
            if (SweepOffOrderScope.Split(view, plugins, roots,
                                         out _, out offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? FaceGenCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : FaceGenCheckResult.Fail(splitErr.Message);
        }

        // Which plugins one provider ships, memoized; null when its folder would not list, which the view names in RootFailures.
        var shipped = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? PluginsIn(string provider)
        {
            if (shipped.TryGetValue(provider, out var got)) return got;
            return shipped[provider] = assets.LooseRootFiles(provider)?
                .Where(f => Path.GetExtension(f) is var ext
                         && (ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var result = FaceGenCheck.Run(resolver, view, assets, PluginsIn, plugins, limit,
                                      offOrder.Count > 0 ? offOrder : null, recordScope, classes, countsOnly, excluded);
        // The build's warnings, taken in this one hold, ride only a sweep that ran.
        return result.Success ? result with { AssetWarnings = captured.Warnings } : result;
    }

    /// <summary>The facegen family's <c>findings=</c> class tokens. An unrecognized token is a named refusal listing the vocabulary, never a silent widening.</summary>
    static bool TryParseFaceGenClasses(IReadOnlyList<string>? names, out FaceGenFindingClass classes, out string? error)
    {
        classes = FaceGenFindingClass.All; error = null;
        if (names is not { Count: > 0 }) return true;
        var acc = FaceGenFindingClass.None;
        foreach (var raw in names)
        {
            var token = (raw ?? "").Trim().Replace('-', '_').ToLowerInvariant();
            if (FaceGenCheck.ClassFor(token) is { } c) { acc |= c; continue; }
            error = $"findings='{raw}' is not a facegen finding class — use {FaceGenCheck.Vocabulary}.";
            return false;
        }
        classes = acc;
        return true;
    }
}
