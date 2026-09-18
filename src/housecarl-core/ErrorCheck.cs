using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The load-order integrity sweep (the errors family on housecarl_check): for each plugin in scope it walks
/// every record's FormLinks and reports dangling references, missing masters, and parse failures per record and per
/// plugin. What it claims and what it does not — the scope boundary, the exemptions, the budget phases and the
/// baseline split — is in docs/architecture/check-family-tests.md.</summary>
public static class ErrorCheck
{
    /// <summary>The BASELINE plugin set — Mutagen's own <c>Implicits.BaseMasters</c> for Skyrim SE, in the library's
    /// own order, matched by filename. Never a hand-kept list, and not the same set as houseCARL's "implicit" group.</summary>
    static readonly string[] BaseMasterNames =
        Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters
            .Select(m => m.FileName.String).ToArray();

    static readonly HashSet<string> BaseMasterSet = new(BaseMasterNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>The base-game masters, in Mutagen's order — for a render that has to NAME what it counted as
    /// baseline.</summary>
    public static IReadOnlyList<string> BaseMasters => BaseMasterNames;

    /// <summary>Is this plugin filename one of <see cref="BaseMasters"/>?</summary>
    public static bool IsBaseMaster(string pluginName) => BaseMasterSet.Contains(pluginName);

    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null or empty = the whole active order minus excluded
    /// plugins) under one capture. <paramref name="limit"/> caps the dangling refs COLLECTED, never the totals, and a
    /// bad or excluded scope name fails loud. The narrowing knobs: <paramref name="offOrder"/> sweeps plugin FILES not
    /// in the active order, <paramref name="recordScope"/> which records the walk visits, <paramref name="classes"/>
    /// which error classes are looked for, <paramref name="countsOnly"/> returns histograms not listings.</summary>
    public static ErrorCheckResult Run(LoadOrderResolver resolver, IReadOnlyList<string>? scope, int limit,
                                       IReadOnlyList<(string Name, string Path)>? offOrder = null,
                                       SweepScope? recordScope = null,
                                       ErrorFindingClass classes = ErrorFindingClass.All, bool countsOnly = false,
                                       SweepExclusion.Resolved? exclude = null)
        => Run(resolver, resolver.Capture(), scope, limit, offOrder, recordScope, classes, countsOnly, exclude);

    /// <summary>The view-threaded body: a caller that already captured hands that view in, so the membership gate, the
    /// sweep and the epoch stamp all name ONE build.</summary>
    public static ErrorCheckResult Run(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                                       IReadOnlyList<string>? scope, int limit,
                                       IReadOnlyList<(string Name, string Path)>? offOrder = null,
                                       SweepScope? recordScope = null,
                                       ErrorFindingClass classes = ErrorFindingClass.All, bool countsOnly = false,
                                       SweepExclusion.Resolved? exclude = null)
    {
        bool wantDangling = classes.HasFlag(ErrorFindingClass.Dangling);
        bool wantMasters = classes.HasFlag(ErrorFindingClass.MissingMasters);
        int excludedFromScope = 0;
        // counts_only=: the dangling-by-target-plugin tally, over every dangling ref in scope, never limit-capped.
        // Built only when the walk that fills it runs, so null means "not computed", never "empty".
        var histogram = countsOnly && wantDangling ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        // The SOURCE axis, tallied on every sweep the walk runs on, and uncapped by limit= like the totals it decomposes.
        var bySource = wantDangling ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        // Which plugins the link walk actually EXAMINED a record in, under the scope in force — not which it opened.
        var examined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- resolve the plugin set to scan. A bad or excluded explicit scope name fails loud, never a silent skip. ---
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                // Membership refusals are decided against THIS view and stamped with its epoch.
                if (!view.ContainsPlugin(name))
                    return ErrorCheckResult.Fail($"plugin not in the load order: {name}.{view.AbsenceClause(name)}")
                           with { Epoch = view.Epoch };
                if (view.ExcludedPlugins.TryGetValue(name, out var why))
                    return ErrorCheckResult.Fail(
                        $"plugin '{name}' was excluded from this session because it could not be parsed ({why}) — fix or remove it upstream; it cannot be checked.")
                           with { Epoch = view.Epoch };
                targets.Add(name);
            }
        }
        else if (offOrder is { Count: > 0 })
        {
            targets = new List<string>();   // the caller's explicit scope resolved ENTIRELY off-order — don't widen to the whole order
        }
        else
        {
            targets = new List<string>();
            foreach (var n in resolver.PluginNames)
                if (!view.ExcludedPlugins.ContainsKey(n)) targets.Add(n);
        }

        // The exclusion axis, applied to the SWEEP rather than to the listing, and run whenever the caller passed one
        // even where it expands to nothing.
        if (exclude is not null)
        {
            // Case-insensitive explicitly: plugin filenames are matched that way everywhere else in this sweep.
            var drop = new HashSet<string>(exclude.Names, StringComparer.OrdinalIgnoreCase);
            // Only the names the CALLER TYPED are held against the scope; a group member that is not here is ordinary.
            var inScope = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            foreach (var name in offOrder ?? Array.Empty<(string Name, string Path)>()) inScope.Add(name.Name);
            foreach (var name in exclude.TypedNames)
                if (!inScope.Contains(name))
                    return ErrorCheckResult.Fail(
                        $"exclude= names '{name}', which is not in the scope this sweep would cover.{view.AbsenceClause(name)} " +
                        "Nothing was swept — an exclusion that matches nothing would return the findings you asked to leave out.")
                           with { Epoch = view.Epoch };

            int targetsBefore = targets.Count;
            int offBefore = offOrder?.Count ?? 0;
            // The whole scope this sweep would have covered, captured before either filter runs — the off-order count
            // included, because a plugins= resolving entirely off-order leaves targets deliberately empty.
            int scopeBefore = targetsBefore + offBefore;
            targets.RemoveAll(drop.Contains);
            excludedFromScope = targetsBefore - targets.Count;
            if (offOrder is { Count: > 0 })
            {
                offOrder = offOrder.Where(o => !drop.Contains(o.Name)).ToList();
                excludedFromScope += offBefore - offOrder.Count;
            }
            if (targets.Count == 0 && (offOrder is null || offOrder.Count == 0))
                return ErrorCheckResult.Fail(
                    $"exclude= removed every plugin this sweep would have covered ({scopeBefore} in scope, all excluded) — " +
                    "there is nothing left to check. Narrow exclude=, or widen plugins=.")
                       with { Epoch = view.Epoch };
        }

        // The subset claim is qualified under a record scope whenever the plugin-level master count is also in play.
        var filterNote = SweepFindings.FilterNote(
            recordScope is null ? null
                : wantMasters
                    ? "the dangling / unscannable counts below are for THIS narrowed scope; the missing-master count is "
                      + "PLUGIN-level (read off the master table) and is NOT narrowed by it."
                    : SweepFindings.ScopedCountsClaim,
            recordScope?.Label, SweepFindings.Describe(classes),
            // Stated whenever the caller PASSED an exclusion, zero included.
            exclude is not null ? $"exclude= left out {excludedFromScope} plugin(s)" : null);

        var reports = new List<PluginErrors>();
        int totalDangling = 0, totalMissing = 0, totalUnscannable = 0;
        int danglingBudget = limit;

        // The per-plugin sweep, called in two phases below so the vanilla baseline cannot consume limit= before any
        // mod plugin is reached. What each plugin FINDS never depends on limit=; only what gets LISTED.
        void SweepActivePlugin(string plugin)
        {
            string? scanError = null;

            // Missing masters, read independently of the record walk so a walk fault still reports the master state.
            var missingMasters = new List<string>();
            if (wantMasters)
            {
                try
                {
                    foreach (var m in view.DeclaredMasters(plugin))
                        if (!view.ContainsPlugin(m)) missingMasters.Add(m);
                }
                catch (Exception ex) { scanError = $"could not read the master list: {ex.GetType().Name}: {ex.Message}"; }
                missingMasters.Sort(StringComparer.OrdinalIgnoreCase);
            }

            var dangling = new List<DanglingRef>();
            int unscannable = 0;
            var unscannableSamples = new List<string>();

            // findings= excluded 'dangling' ⇒ the per-record link walk is skipped wholesale, and the render must then
            // print the dangling and unscannable lines as "not checked" rather than 0.
            try
            {
                foreach (var (fk, _, body, _) in wantDangling
                             ? view.RecordsIn(new[] { plugin }, recordScope?.Types)
                             : Enumerable.Empty<(FormKey, int, IMajorRecordGetter, string)>())
                {
                    // Per-record fault isolation: one record Mutagen cannot parse is excluded and accounted.
                    try
                    {
                        // Record scope tested before the deleted-record rule and the link walk.
                        if (recordScope is not null && !recordScope.Matches(fk, body)) continue;
                        examined.Add(plugin);
                        // A DELETED record links to nothing — the shared rule, excluded before the walk.
                        if (DeletedRecordRule.HasNoLiveBody(body)) continue;
                        if (body is not IFormLinkContainerGetter flc) continue;
                        Dictionary<FormKey, int>? ownerVarExempt = null;   // built lazily on this record's first otherwise-dangling link (see UntypedOwnerVariableData)
                        foreach (var link in flc.EnumerateFormLinks())
                        {
                            var target = link.FormKey;
                            if (target.IsNull) continue;            // a null FormLink is a legal optional — not an error (see the class-doc boundary)
                            if (view.ResolveWinner(target) is not null) continue;   // resolves → fine
                            if (EngineImplicit.IsImplicit(target)) continue;        // engine-implicit (PlayerRef 000014 / Player 000007): the index can't resolve these hardcoded forms, but they are real, never dangling — the same exemption the dialogue lints use
                            ownerVarExempt ??= UntypedOwnerVariableData(body);      // an UntypedOwner's VariableData word is a RequiredRank int (esp. -1 → FFFFFFFF), not a reference
                            if (ownerVarExempt.TryGetValue(target, out int rank) && rank > 0) { ownerVarExempt[target] = rank - 1; continue; }
                            totalDangling++;
                            Bump(bySource, plugin);                                                   // the SOURCE axis, on every mode
                            if (histogram is not null) { BumpTarget(histogram, target); continue; }   // counts_only=: tally, list nothing
                            if (danglingBudget > 0)
                            {
                                dangling.Add(new DanglingRef(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, target));
                                danglingBudget--;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 3) unscannableSamples.Add($"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // The plugin enumeration itself faulting is NAMED per-plugin and the sweep continues.
            catch (Exception ex)
            {
                scanError = (scanError is null ? "" : scanError + "; ")
                          + $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}";
            }

            totalMissing += missingMasters.Count;
            totalUnscannable += unscannable;

            // counts_only=: the reports list carries the honesty layer only — a plugin whose records could not be read.
            if (countsOnly)
            {
                if (unscannable > 0 || scanError is not null)
                    reports.Add(new PluginErrors(plugin, Array.Empty<DanglingRef>(), Array.Empty<string>(),
                                                 unscannable, unscannableSamples, scanError));
            }
            else if (dangling.Count > 0 || missingMasters.Count > 0 || unscannable > 0 || scanError is not null)
                reports.Add(new PluginErrors(plugin, dangling, missingMasters, unscannable, unscannableSamples, scanError));
        }

        // Phase 1 — every plugin that is NOT a base-game master, in load order.
        foreach (var plugin in targets)
            if (!IsBaseMaster(plugin)) SweepActivePlugin(plugin);

        // --- off-order files (the pre-enable verify lane): the file's OWN overlay, links resolved against the active
        //     order PLUS the file's own records. Same fault-isolation contract as the active loop.
        var offOrderScanned = new List<string>();
        if (offOrder is { Count: > 0 })
        {
            foreach (var (name, path) in offOrder)
            {
                offOrderScanned.Add(name);
                string? scanError = null;
                var missingMasters = new List<string>();
                var dangling = new List<DanglingRef>();
                int unscannable = 0;
                var unscannableSamples = new List<string>();

                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));

                    if (wantMasters)
                    {
                        foreach (var m in ov.ModHeader.MasterReferences)
                            if (!view.ContainsPlugin(m.Master.FileName)) missingMasters.Add(m.Master.FileName);
                        missingMasters.Sort(StringComparer.OrdinalIgnoreCase);
                    }

                    // Pass 1 — the file's OWN FormKeys, so a link into a record this same file defines does not read as
                    // dangling. Deliberately NOT record-scoped: scoping this set would manufacture dangling refs.
                    var selfKeys = new HashSet<FormKey>();
                    if (wantDangling)
                    {
                        try { foreach (var r in ov.EnumerateMajorRecords()) selfKeys.Add(r.FormKey); }
                        catch (Exception ex) { scanError = $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"; }
                    }

                    // Pass 2 — the link walk, per-record fault isolation (the active loop's exact contract).
                    try
                    {
                        foreach (var rec in wantDangling ? SweepScope.RecordsFrom(ov, recordScope) : Enumerable.Empty<IMajorRecordGetter>())
                        {
                            try
                            {
                                if (recordScope is not null && !recordScope.Matches(rec.FormKey, rec)) continue;
                                examined.Add(name);
                                if (DeletedRecordRule.HasNoLiveBody(rec)) continue;   // same rule as the active pass above
                                if (rec is not IFormLinkContainerGetter flc) continue;
                                Dictionary<FormKey, int>? ownerVarExempt = null;   // see UntypedOwnerVariableData
                                foreach (var link in flc.EnumerateFormLinks())
                                {
                                    var target = link.FormKey;
                                    if (target.IsNull) continue;
                                    if (view.ResolveWinner(target) is not null) continue;
                                    if (selfKeys.Contains(target)) continue;            // defined by this very file
                                    if (EngineImplicit.IsImplicit(target)) continue;
                                    ownerVarExempt ??= UntypedOwnerVariableData(rec);   // RequiredRank int mis-exposed as a FormLink
                                    if (ownerVarExempt.TryGetValue(target, out int rank) && rank > 0) { ownerVarExempt[target] = rank - 1; continue; }
                                    totalDangling++;
                                    Bump(bySource, name);                                                     // the SOURCE axis, keyed by the off-order FILE (which can itself be a base master — see BaseMastersSwept below)
                                    if (histogram is not null) { BumpTarget(histogram, target); continue; }   // counts_only=
                                    if (danglingBudget > 0)
                                    {
                                        dangling.Add(new DanglingRef(rec.FormKey, RecordNaming.StripOverlay(rec.GetType().Name), rec.EditorID, target));
                                        danglingBudget--;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                unscannable++;
                                if (unscannableSamples.Count < 3) unscannableSamples.Add($"{FormIdToken.Of(rec.FormKey)} — {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        scanError = (scanError is null ? "" : scanError + "; ")
                                  + $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}";
                    }
                }
                catch (Exception ex)
                {
                    scanError = $"could not open '{path}' as a Skyrim plugin: {ex.GetType().Name}: {ex.Message}";
                }
                finally { (ov as IDisposable)?.Dispose(); }

                totalMissing += missingMasters.Count;
                totalUnscannable += unscannable;

                if (countsOnly)                                   // the honesty layer only — see the active loop above
                {
                    if (unscannable > 0 || scanError is not null)
                        reports.Add(new PluginErrors(name, Array.Empty<DanglingRef>(), Array.Empty<string>(),
                                                     unscannable, unscannableSamples, scanError));
                }
                else if (dangling.Count > 0 || missingMasters.Count > 0 || unscannable > 0 || scanError is not null)
                    reports.Add(new PluginErrors(name, dangling, missingMasters, unscannable, unscannableSamples, scanError));
            }
        }

        // Phase 2 — the base-game masters LAST, on what the rest of the order left.
        foreach (var plugin in targets)
            if (IsBaseMaster(plugin)) SweepActivePlugin(plugin);

        // Reports are BUILT in budget-phase order but READ in load order.
        if (reports.Count > 1 && targets.Any(IsBaseMaster))
        {
            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++) position[targets[i]] = i;
            for (int i = 0; i < offOrderScanned.Count; i++) position[offOrderScanned[i]] = targets.Count + i;
            reports = reports.OrderBy(p => position.TryGetValue(p.Plugin, out var i) ? i : int.MaxValue).ToList();
        }

        // The baseline split, derived from the ONE source tally, so "is this plugin baseline" is asked in one place.
        int baselineDangling = bySource is null ? 0 : bySource.Where(kv => IsBaseMaster(kv.Key)).Sum(kv => kv.Value);
        // WHICH base masters this sweep actually looked at — the swept SUBSET, built from `examined` so it covers the
        // active and off-order lanes alike. In Mutagen's own order, so the render is stable across sweeps.
        var baseSwept = BaseMasterNames.Where(examined.Contains).ToList();
        // Whether the sweep had any NON-base plugin in scope at all — stated by the layer that resolved the targets,
        // because PluginsScanned and the swept-base count measure different things and diverge.
        bool nonBaseInScope = targets.Any(t => !IsBaseMaster(t));

        // What the budget dropped, by plugin, is deliberately NOT computed here: CheckAccounting subtracts against
        // what the RESPONSE emitted, which covers the listing budget and max_chars at once.

        return new ErrorCheckResult(reports, targets.Count + offOrderScanned.Count, totalDangling, totalMissing,
                                    totalUnscannable, view.ExcludedPlugins, null, offOrderScanned,
                                    filterNote, classes, histogram is null ? null : SweepFindings.Histogram(histogram),
                                    countsOnly, view.Epoch,
                                    bySource is null ? null : SweepFindings.Histogram(bySource),
                                    baselineDangling, baseSwept, nonBaseInScope, limit,
                                    recordScope?.TypeScopeLabel);
    }

    /// <summary>Bump the <c>counts_only=</c> dangling histogram for one broken target, keyed by the PLUGIN the target
    /// form lives in — the diagnostic the per-source-plugin grouping never gave.</summary>
    static void BumpTarget(Dictionary<string, int> acc, FormKey target)
        => Bump(acc, target.ModKey.FileName.String);

    /// <summary>Bump one plugin-keyed tally; a null accumulator means that axis is not being collected, so the call is
    /// a no-op rather than the caller's problem at every bump site.</summary>
    static void Bump(Dictionary<string, int>? acc, string key)
    {
        if (acc is null) return;
        acc[key] = acc.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>Every FormKey carried in an <see cref="IUntypedOwnerGetter.VariableData"/> slot in
    /// <paramref name="body"/>, as a multiset — the FormKeys the dangling sweep must NOT flag, because that second
    /// COED word is a RequiredRank int Mutagen exposes as a FormLink. Only that word is dropped; the owner form itself
    /// is still checked. Contract in docs/architecture/check-family-tests.md.</summary>
    static Dictionary<FormKey, int> UntypedOwnerVariableData(IMajorRecordGetter body)
    {
        var acc = new Dictionary<FormKey, int>();
        void Add(IExtraDataGetter? ed)
        {
            if (ed?.Owner is not IUntypedOwnerGetter uo) return;
            var vk = uo.VariableData.FormKey;
            if (vk.IsNull) return;                                     // a null second word is a legal optional anyway (never flagged)
            acc[vk] = acc.TryGetValue(vk, out var c) ? c + 1 : 1;
        }
        switch (body)
        {
            case IContainerGetter cont:    if (cont.Items   is { } items)   foreach (var it in items) Add(it.Data);      break;
            case ILeveledItemGetter lvli:  if (lvli.Entries is { } liEnts)  foreach (var e in liEnts) Add(e.ExtraData);  break;
            case ILeveledNpcGetter lvln:   if (lvln.Entries is { } lnEnts)  foreach (var e in lnEnts) Add(e.ExtraData);  break;
            case ILeveledSpellGetter lvsp: if (lvsp.Entries is { } lsEnts)  foreach (var e in lsEnts) Add(e.ExtraData);  break;
        }
        return acc;
    }
}

/// <summary>One broken reference: the SOURCE record (FormKey, catalog type, editorid) and the TARGET FormKey no active
/// plugin defines.</summary>
public sealed record DanglingRef(FormKey Source, string SourceType, string? SourceEditorId, FormKey Target);

/// <summary>Every error found in one plugin: its dangling references (capped across the sweep), the masters it declares
/// that are not present in the active order, the count and samples of records that could not be scanned, and a
/// <paramref name="ScanError"/> if the plugin's own enumeration faulted.
/// <paramref name="InstalledButInactiveMasters"/> is the subset of <paramref name="MissingMasters"/> whose file IS
/// somewhere in the MO2 install; <b>null means NOT CLASSIFIED</b>, never "none of them".</summary>
public sealed record PluginErrors(
    string Plugin,
    IReadOnlyList<DanglingRef> Dangling,
    IReadOnlyList<string> MissingMasters,
    int UnscannableRecords,
    IReadOnlyList<string> UnscannableSamples,
    string? ScanError,
    IReadOnlyList<string>? InstalledButInactiveMasters = null);

/// <summary>The result of <see cref="ErrorCheck.Run"/>: the per-plugin reports (only plugins WITH findings), the sweep
/// totals, the plugins the index build excluded as unparseable, and — on a scope error — a recoverable
/// <see cref="Error"/> with no reports. What each field may and may not be read as, including the null-means-not-
/// computed rule on both histograms and the swept-baseline subset, is in
/// docs/architecture/check-family-tests.md.</summary>
public sealed record ErrorCheckResult(
    IReadOnlyList<PluginErrors> Reports,
    int PluginsScanned,
    int TotalDangling,
    int TotalMissingMasters,
    int TotalUnscannableRecords,
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Error,
    IReadOnlyList<string>? OffOrderScanned = null,
    string? FilterNote = null,
    ErrorFindingClass Classes = ErrorFindingClass.All,
    IReadOnlyList<SweepCount>? Histogram = null,
    bool CountsOnly = false,
    string? Epoch = null,   // the swept INDEXED build's fingerprint. Stamped on success and on refusals decided against a captured build (membership/locate); null on parse-level refusals that consulted none. OffOrderScanned files are located OUTSIDE the index — their content is not under this fingerprint, so the renders qualify the stamp when any were swept
    IReadOnlyList<SweepCount>? DanglingBySource = null,
    int BaselineDangling = 0,
    IReadOnlyList<string>? BaseMastersSwept = null,
    bool NonBaseInScope = false,
    int Limit = 0,   // the listing budget this sweep was GIVEN. Carried so the response can name the knob it is telling the caller to raise without being passed it a second time — a render-side copy defaults, and a default that disagrees with the sweep puts a wrong number in front of the caller
    string? TypeScopeLabel = null)   // the scope's types, spelled with the arms any entry expanded to, when it covered MORE than one; null otherwise. There is ONE listing for the whole scope, filled plugin by plugin and type by type inside each, so a short listing can be missing any of those types entirely, and the response says so rather than letting the narrowing line read as "the rest are clean"

{
    public bool Success => Error is null;
    public static ErrorCheckResult Fail(string error) =>
        new(Array.Empty<PluginErrors>(), 0, 0, 0, 0,
            new Dictionary<string, string>(), error);
}
