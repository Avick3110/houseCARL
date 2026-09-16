using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>The on-demand whole-topic dialogue-graph validator: resolve <paramref name="fk"/> to its load-order
    /// winner and, when it is a dialogue topic (DIAL), validate that topic's whole graph; when it is a quest (QUST),
    /// fan out to every topic the quest owns. Everything is judged against the resolved winners, which is what the
    /// game sees. The Skyrim-typed walk lives in the core (<see cref="DialogueValidate"/>) so this assembly stays free
    /// of Mutagen.Skyrim; here it just hands core the record resolver and the VFS asset resolver. It never throws over
    /// a verify step: a mid-run resolve or asset failure rides
    /// <see cref="DialogueValidationReport.CheckError"/>, and a not-in-order or wrong-type input is a named
    /// <see cref="DialogueValidationReport.Error"/>.</summary>
    public DialogueValidationReport ValidateDialogue(FormKey fk)
        => DialogueValidate.Run(Resolver, Assets, fk, null, ForceLoadedPluginNames());

    /// <summary>The force-loaded plugin names — base masters aside, the Creation Club and <c>_ResourcePack.esl</c>
    /// plugins the load-order status groups as implicit — for a check that must not blame a modder for content they
    /// did not author. Null, never an empty set, when the MO2 profile cannot be read: a check told "nothing is
    /// force-loaded" would warn on that content, and the caller says which way it then errs.</summary>
    IReadOnlyCollection<string>? ForceLoadedPluginNames()
    {
        var (names, err) = ImplicitPluginNames();
        return err is null ? names : null;
    }

    /// <summary>The merged <c>check</c> surface's dialogue family: <see cref="ValidateDialogue"/> over a seed list,
    /// tallied for one section of a merged response. Deliberately thin — the family's own grammar (seed parse,
    /// cost refusal, seed budget, tally) lives in <see cref="DialogueSweep"/> rather than in this file.</summary>
    /// <param name="foldArm">an already-probed OFF-ORDER plugin, folded in at the END of the order: every seed is
    /// then validated against the active order's winners plus that file. The file is opened once for the whole
    /// sweep and closed when it ends — its record bodies live only while it is open.</param>
    public DialogueCheckResult CheckDialogue(IReadOnlyList<string>? seeds, int limit, bool countsOnly = false,
                                             PoleInfo? foldArm = null)
        // Bound LAZILY: the sweep calls this only once it has seeds to validate, so a call with no seeds= refuses
        // without building the index — the rule SweepSharedInput states, and what this family did before it stamped.
        => DialogueSweep.Run(() =>
        {
            // One resolver and one view for the whole call, the contract the sibling families keep: every seed is
            // validated against the same build, so the stamp names the build the response was actually answered from
            // rather than whichever one the last seed happened to catch.
            var resolver = Resolver;
            // The asset resolver is taken once here for the same reason, and where the scripts family takes it: the
            // property re-gates on every read and can rebuild outright, so reading it per seed would let one
            // response's .fuz and .pex verdicts come off two different asset builds under one tally and one stamp.
            var assets = Assets;
            var view = resolver.Capture();
            // The seed door is pinned to that same view: a door of its own would capture a second build on the first
            // runtime FormID, so the seeds could name records from a build other than the one the response stamps.
            // Read once for the whole sweep, for the same reason the resolver and view are: every seed's ownership
            // gate reads one composition, so one response cannot mix two answers to "who force-loads this".
            var forceLoaded = ForceLoadedPluginNames();
            // The fold is opened ONCE for the whole sweep — one file read, one set of bodies every seed resolves
            // against — and the sweep closes it. A file that will not open is the family's own refusal, named.
            DialogueFold? fold = null;
            string? foldError = null;
            if (foldArm is not null)
            {
                fold = OpenDialogueFold(foldArm, out foldError, FoldLabel(foldArm), withRecords: true);
                // Placed HERE, where the file is opened and the build is in hand: a fold that reaches a render
                // without having been placed would print the field's default position, which is a guess.
                fold?.PlaceIn(view);
            }
            try
            {
                return new DialogueSweep.Binding(fk => DialogueValidate.Run(resolver, assets, fk, view, forceLoaded, fold),
                                                 FormIdDoor.On(view).Parse, view.Epoch, fold, foldError);
            }
            catch
            {
                // The sweep takes ownership of the fold only once this returns, so anything that throws while the
                // binding is being built has to close the file here or it stays open for the process's life.
                fold?.Dispose();
                throw;
            }
        }, seeds, limit, countsOnly);

    // ---- integrity sweep -------------------------------------------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for record integrity errors:
    /// dangling FormLinks, missing masters and parse failures. Thin wiring over the core
    /// <see cref="ErrorCheck.Run"/>, which holds all the scan logic so a test can drive this same path over
    /// synthetic plugins. Read-only.
    /// <para>A scope name NOT in the active order is resolved on disk by the shared plugin-locate contract —
    /// enabled, disabled and unlisted mod folders — and swept off-order against its own overlay, with links resolved
    /// against the active order plus the file's own records. That is the pre-enable verify lane: the dangling-ref
    /// sweep of a patch houseCARL just wrote, before an MO2 refresh puts it in plugins.txt. A name found nowhere, or
    /// in several folders, still fails loudly.</para>
    /// <para>The record-scope, class-filter and counts-only knobs are parsed here — a bad FormID, an unknown record
    /// type or an unrecognized finding class refuses the call before any sweep runs — and handed to the core as
    /// typed values.</para></summary>
    public ErrorCheckResult CheckErrors(IReadOnlyList<string>? plugins, int limit,
                                        IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                        IReadOnlyList<string>? types = null, IReadOnlyList<string>? findings = null,
                                        bool countsOnly = false, IReadOnlyList<string>? exclude = null,
                                        SweepOffOrderMemo? offOrderMemo = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return ErrorCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseErrorClasses(findings, out var classes, out var classErr))
            return ErrorCheckResult.Fail(classErr!);

        // One resolver and one view for the whole call: the scope check, the refusal stamps and the sweep below all
        // name the same build. Passing the property down would let the core re-gate and capture an adjacent build,
        // so a refusal could stamp one build while the sweep stamped the next.
        var resolver = Resolver;
        var viewAll = resolver.Capture();

        // The exclude= axis. The `implicit` group is a fact about the MO2 composition — the plugins the order loads
        // that plugins.txt does not list — so it is read here, where that composition lives, and the core sweep
        // receives plain filenames. Resolved before anything is swept, so a bad value refuses having done no work.
        // The check is gated on the caller having written the `implicit` token, not merely on an exclusion being
        // passed: otherwise a named-plugin exclusion over an unreadable profile is refused with a message about a
        // group the caller never named.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames() : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ErrorCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ErrorCheckResult.Fail(excludeErr);

        if (plugins is { Count: > 0 })
        {
            var view = viewAll;
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            // Membership and locate refusals are decided against THIS captured build and its composition, so they
            // are stamped; a blank name consulted no build and stays unstamped.
            if (SweepOffOrderScope.Split(view, plugins, modsDir, dataDir, overwriteDir, profileDir,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ErrorCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ErrorCheckResult.Fail(splitErr.Message);
            return ClassifyMissingMasters(
                ErrorCheck.Run(resolver, viewAll, active, limit, offOrder.Count > 0 ? offOrder : null,
                               recordScope, classes, countsOnly, excluded));
        }
        return ClassifyMissingMasters(
            ErrorCheck.Run(resolver, viewAll, plugins, limit, null, recordScope, classes, countsOnly, excluded));
    }

    /// <summary>Fill in each report's install-vs-enable split for the masters the sweep found unsatisfied. The core
    /// sweep knows the ACTIVE ORDER and stops there; which of those masters is nonetheless sitting in the install —
    /// in a disabled mod, or unticked — is a fact about the MO2 composition, which lives at this layer. Done here so
    /// the split has one home (<see cref="Mo2LoadOrder.SplitUnsatisfiedMasters"/>) rather than a second spelling
    /// inside the core.
    /// <para>A composition that cannot be read leaves every report's subset null, not empty. Empty would say "none
    /// of these is merely disabled", a claim about an install nobody looked at; null says the split was not made,
    /// and the render falls back to the union remedy.</para></summary>
    ErrorCheckResult ClassifyMissingMasters(ErrorCheckResult r)
    {
        if (r.Error is not null || r.Reports.Count == 0) return r;
        if (!r.Reports.Any(p => p.MissingMasters.Count > 0)) return r;

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch { return r; }
        Mo2Composition comp;
        IReadOnlyCollection<string> installed;
        try
        {
            comp = Mo2LoadOrder.ReadComposition(profileDir);
            // The install's plugin-name set, read ONCE for the whole sweep rather than per report per name. The
            // expensive path is the common one: a master that is not installed short-circuits nowhere, so it walks
            // the enabled mods, the disabled mods, the unlisted folders and Data before returning false. The answer
            // does not depend on which report asked, so neither does the read.
            installed = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
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
    /// is not a set that is empty: swallowing the failure would expand the group to nothing, exclude nothing, and
    /// leave the response silent about it, while the parameter promises that a value matching nothing is
    /// refused.</summary>
    (IReadOnlyList<string> Names, string? Error) ImplicitPluginNames()
    {
        string profileDir;
        lock (_gate) { EnsurePathsDerived(); profileDir = _profileDir; }
        try { return (Mo2LoadOrder.ReadComposition(profileDir).ImplicitPluginNames, null); }
        catch (Exception ex)
        {
            return (Array.Empty<string>(),
                $"exclude= could not be resolved: the MO2 profile's plugin list at '{profileDir}' could not be read " +
                $"({ex.GetType().Name}: {ex.Message}). The '{SweepExclusion.ImplicitToken}' group is defined by which " +
                "plugins that file does NOT list, so it cannot be widened without it. Nothing was swept.");
        }
    }

    /// <summary>The record-scope parse's REFUSAL alone, for the merged surface's shared-input check
    /// (<see cref="SweepSharedInput"/>). The scope itself belongs to whichever family is about to sweep with it;
    /// what is shared is the judgement that a value is malformed, and that judgement has to be reachable without
    /// selecting a family that uses it.</summary>
    internal string? SweepScopeError(IReadOnlyList<string>? formids, string? editoridContains,
                                     IReadOnlyList<string>? types)
        => BuildSweepScope(formids, editoridContains, types).Error;

    /// <summary>Parse the sweep families' shared record-scope params into a <see cref="SweepScope"/>: FormID tokens,
    /// an EditorID substring, and a record type SET resolved through the same type lookup the scan uses. Every
    /// malformed input is a named refusal returned before the sweep starts, never a scope that silently matched
    /// nothing. Returns (null, null) when nothing was narrowed, so the unscoped path stays untouched.</summary>
    (SweepScope? Scope, string? Error) BuildSweepScope(IReadOnlyList<string>? formids, string? editoridContains,
                                                       IReadOnlyList<string>? typeSet)
    {
        HashSet<FormKey>? keys = null;
        if (formids is { Count: > 0 })
        {
            keys = new HashSet<FormKey>();
            var door = OpenFormIdDoor();
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
            try { types = ResolveTypeFilterSet(typeSet, out armLabel); }
            catch (ArgumentException ex) { return (null, ex.Message); }
            typeLabel = string.Join(", ", typeSet.Select(t => (t ?? "").Trim()));
        }

        var scope = new SweepScope(keys, editoridContains, types, typeLabel, armLabel);
        return (scope.IsEmpty ? null : scope, null);
    }

    // ---- script-property sweep (housecarl_validate_scripts) --------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for VMAD script properties
    /// declared in the attached script's .pex (or an ancestor it extends) but left unbound on the record — a silent
    /// <c>None</c>. Thin wiring over the core <see cref="ScriptPropertyCheck.Run"/>, which holds all the cross-check
    /// logic so a test can drive this same path over synthetic records and a planted .pex. Passes the live
    /// <see cref="Assets"/> resolver so a script's .pex is found loose or BSA-packed. Read-only.
    /// <para>The record-scope, property-name, class-filter and counts-only knobs are parsed here, so a bad FormID,
    /// unknown record type or unrecognized finding class refuses the call before any sweep runs.</para>
    /// <para>A named plugin the active order does not hold is located on disk and swept OFF-ORDER, the same lane
    /// <see cref="CheckErrors"/> has and through the same split — the pre-enable verify sweep for a patch houseCARL
    /// has just written.</para></summary>
    public ScriptCheckResult ValidateScripts(IReadOnlyList<string>? plugins, int limit,
                                             IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                             IReadOnlyList<string>? types = null, string? propertyContains = null,
                                             IReadOnlyList<string>? findings = null, bool countsOnly = false,
                                             IReadOnlyList<string>? exclude = null,
                                             SweepOffOrderMemo? offOrderMemo = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
        if (scopeErr is not null) return ScriptCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseScriptClasses(findings, out var classes, out var classErr))
            return ScriptCheckResult.Fail(classErr!);
        // One resolver and view threaded through, same contract as CheckErrors.
        var resolver = Resolver;
        // The exclusion resolves here, where the MO2 composition lives, exactly as it does for CheckErrors: the core
        // sweep receives plain filenames, and a bad value refuses having done no work.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames() : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ScriptCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ScriptCheckResult.Fail(excludeErr);
        var view = resolver.Capture();

        // The off-order lane, resolved exactly as CheckErrors resolves it: a named plugin the active order does not
        // hold is located on disk and swept from its own file, which is how a fresh patch's script bindings can be
        // checked BEFORE it is enabled.
        if (plugins is { Count: > 0 })
        {
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            if (SweepOffOrderScope.Split(view, plugins, modsDir, dataDir, overwriteDir, profileDir,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ScriptCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ScriptCheckResult.Fail(splitErr.Message);
            return ScriptPropertyCheck.Run(resolver, view, Assets, active, limit, recordScope,
                                           propertyContains, classes, countsOnly, excluded,
                                           offOrder.Count > 0 ? offOrder : null);
        }
        return ScriptPropertyCheck.Run(resolver, view, Assets, plugins, limit, recordScope,
                                       propertyContains, classes, countsOnly, excluded);
    }

}
