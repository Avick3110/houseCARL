using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The load-order integrity sweep (housecarl_check_errors — audit A1): the data-layer twin of the Creation Kit's
/// "Check For Errors" / xEdit's error check. For each plugin in scope it walks EVERY record's FormLinks (Mutagen's
/// own <see cref="IFormLinkContainerGetter.EnumerateFormLinks"/> — the by-construction link surface, the same one
/// cross_plugin_query references= rides) and reports three error classes:
///   • DANGLING — a non-null FormLink whose target NO plugin in the active order defines (<see cref="LoadOrderResolver.IndexView.ResolveWinner"/>
///     is null): a broken reference in the resolvable order. Engine-implicit forms (PlayerRef 000014, Player 000007 —
///     hardcoded refs the index can't resolve but that are never actually broken) are exempted via <see cref="EngineImplicit"/>,
///     so the standard player-state pattern doesn't false-flag (HCBR: 531/531 dangling all → 000014 before this exemption).
///     A container/leveled-list item's OWNERSHIP "variable" word (an <see cref="IUntypedOwnerGetter.VariableData"/> — a
///     RequiredRank int, not a reference, that Mutagen exposes AS a FormLink whenever it can't type the owner; #207) is
///     exempted too, so a rank of -1 (0xFFFFFFFF) is not read as a dangling ref — see <see cref="UntypedOwnerVariableData"/>.
///   • MISSING MASTER — a plugin DECLARES a master that is not present in the active order
///     (<see cref="LoadOrderResolver.IndexView.ContainsPlugin"/> is false): the plugin's dependency is not installed /
///     enabled, the most common load-order break (and the root cause behind a cluster of that master's refs dangling).
///   • PARSE failures — per-record (a body whose link walk THROWS is excluded + accounted, never a silent skip — the
///     fault-isolation twin of the cross_plugin_query scan) and whole-plugin (plugins the index build could not parse,
///     surfaced via <see cref="LoadOrderResolver.IndexView.ExcludedPlugins"/>).
///
/// WHY NOT the master-TABLE diff the CK/xEdit also show (declared-vs-used) — the honest scope boundary, Q3:
///   • "used-but-undeclared master" is STRUCTURALLY UNDETECTABLE through Mutagen: a FormID's master-index byte is
///     decoded against the plugin's declared master list, so every FormKey Mutagen yields has a ModKey that is, by
///     construction, a declared master or the plugin itself — the "references a form from a plugin it does not master"
///     corruption lives in the RAW master-index byte, below what Mutagen models (adjacent to the mesh-byte residual).
///     Its OBSERVABLE effect — refs that no longer resolve — is caught as DANGLING. We do not claim to diagnose it.
///   • "declared-but-unused master" (xEdit's unused-master cleanup) is advisory only and a FormLink scan cannot prove
///     it (a master used solely by scripts or unmodeled refs would read as unused — a false positive). Deferred as a
///     named future item rather than shipped as an unprovable claim.
///
/// HONEST BOUNDARY (Q3 — the sweep claims exactly this, never more): it covers the FormLink-resolution / missing-master
/// / parse class. It does NOT verify navmesh or terrain SPATIAL integrity (the CrcHash / NavmeshGrid recompute — a known
/// Mutagen-delta residual), does NOT flag a REQUIRED field left null (needs per-field requiredness; a null FormLink is a
/// legal optional here), and does NOT list unused-master cleanup (see above). All named in the rendered footer so a
/// clean result is never read as byte-for-byte xEdit parity. It also exempts an <see cref="IUntypedOwnerGetter"/>'s
/// ambiguous "variable" word (#207 — see <see cref="UntypedOwnerVariableData"/>); the one thing that gives up is a
/// genuinely-dangling Global on an NPC-OWNED item whose owner NPC lives in a master (vanishingly rare, and that owner
/// NPC is still checked), which we trade for never false-flagging the far commoner faction-owner rank. DELETED records
/// are excluded from the link walk entirely (#279 — see <see cref="DeletedRecordRule"/>): their content is not live,
/// so a link one carries is not a dangling reference, and a malformed deleted body no longer reads as a parse hole.
///
/// Composes existing primitives only — no new dependency (audit A1 "Verified 2026-06-25"): the per-plugin record stream
/// (<c>RecordsIn</c>), the O(1) resolution test (<c>ResolveWinner</c>), presence (<c>ContainsPlugin</c>), the declared-
/// master read (<c>DeclaredMasters</c>), and per-record fault isolation. Holds nothing past each yield (Option B).
/// </summary>
public static class ErrorCheck
{
    /// <summary>The BASELINE plugin set — Mutagen's own <c>Implicits.BaseMasters</c> for Skyrim SE (Skyrim.esm,
    /// Update.esm, Dawnguard.esm, HearthFires.esm, Dragonborn.esm), in the library's own order. Never a hand-kept list
    /// (#344): what ships with the game is what Mutagen says ships with it, so an edition change arrives with the
    /// Mutagen bump instead of as a literal here. Matching is by FILENAME, which is what the sweep has.
    /// <para>NOT in this set, measured rather than assumed (2026-08-18, against <c>Implicits.Get(SkyrimSE)</c> and a
    /// live 3800-plugin order carrying 55 of them): Creation Club plugins and <c>_ResourcePack.esl</c>. The engine
    /// force-loads those, and houseCARL's own load-order status groups them WITH the base masters as "implicit" — but
    /// that grouping comes from their absence from plugins.txt, not from Mutagen, and the two sets are not the same
    /// thing. Baseline here means Mutagen's base set, and the render says so rather than letting "baseline" be read as
    /// "vanilla + CC".</para></summary>
    static readonly string[] BaseMasterNames =
        Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters
            .Select(m => m.FileName.String).ToArray();

    static readonly HashSet<string> BaseMasterSet = new(BaseMasterNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>The base-game masters, in Mutagen's order — for a render that has to NAME what it counted as baseline
    /// (a summary line saying "baseline" without saying which plugins that means is not a claim a reader can check).</summary>
    public static IReadOnlyList<string> BaseMasters => BaseMasterNames;

    /// <summary>Is this plugin filename one of <see cref="BaseMasters"/>?</summary>
    public static bool IsBaseMaster(string pluginName) => BaseMasterSet.Contains(pluginName);

    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null/empty = the whole active order minus excluded
    /// plugins) over the order <paramref name="resolver"/> holds. One <see cref="LoadOrderResolver.Capture"/> pins the
    /// whole sweep. <paramref name="limit"/> caps the number of dangling refs collected across the sweep (the true
    /// total is always counted); missing-master findings are few and never capped. That budget is spent on every other
    /// plugin BEFORE the base-game masters (#344 — see <see cref="BaseMasters"/>), whose dangling refs are permanent
    /// vanilla leftovers: on a large order the baseline used to be able to consume it at load-order index 0, and a
    /// plugin that collects an empty list is dropped from <see cref="ErrorCheckResult.Reports"/> entirely. What the
    /// budget DID drop is reported per source plugin by the response layer, which subtracts against what it actually
    /// emitted so the claim covers the render's own cut too. A bad/excluded scope name fails
    /// LOUD (Q3) with no partial result.
    /// <para><paramref name="offOrder"/> — plugin FILES to sweep that are NOT in the active order (name + on-disk path;
    /// the caller located them), the pre-enable verify lane (HCBR-2026-07-14-02 gap 3: a patch houseCARL just wrote is
    /// not in plugins.txt until the MO2 refresh, yet its pre-ship dangling-ref sweep is exactly when check_errors is
    /// wanted). Each is opened as its OWN overlay; its links resolve against the active order PLUS the file's own
    /// records (a patch's link to its own new record is not dangling), and a declared master absent from the active
    /// order is a MISSING MASTER finding — same classes, same rendering, plus an OFF-ORDER stamp in the result.</para>
    ///
    /// <para>#282 — the narrowing knobs. <paramref name="recordScope"/> restricts WHICH records the link walk visits
    /// (<see cref="SweepScope"/>: type at the stream, formids / editorid_contains per record), and
    /// <paramref name="classes"/> restricts which error classes are looked for at all — excluding
    /// <see cref="ErrorFindingClass.Dangling"/> SKIPS the per-record walk entirely, which is what makes "is any master
    /// missing anywhere in my order" a master-table read instead of a full sweep. Both narrow the reported TOTALS as
    /// well as the listing; <see cref="ErrorCheckResult.FilterNote"/> says so in words, and the render prints an
    /// excluded class as "not checked", never as a zero (Q3 — a skipped check must not read as a clean one).
    /// <paramref name="countsOnly"/> collects no per-plugin reports (bar scan errors) and returns a
    /// dangling-by-TARGET-plugin <see cref="ErrorCheckResult.Histogram"/> — which plugin the broken refs point INTO,
    /// the answer the per-plugin grouping never gave. The dangling-by-SOURCE tally
    /// (<see cref="ErrorCheckResult.DanglingBySource"/>) is collected on EVERY sweep, not just this mode.</para></summary>
    public static ErrorCheckResult Run(LoadOrderResolver resolver, IReadOnlyList<string>? scope, int limit,
                                       IReadOnlyList<(string Name, string Path)>? offOrder = null,
                                       SweepScope? recordScope = null,
                                       ErrorFindingClass classes = ErrorFindingClass.All, bool countsOnly = false,
                                       SweepExclusion.Resolved? exclude = null)
        => Run(resolver, resolver.Capture(), scope, limit, offOrder, recordScope, classes, countsOnly, exclude);

    /// <summary>The view-threaded body (PR #305 re-review): a caller that already captured — the service, which
    /// vets the scope and stamps refusals off ITS view — hands that view in, so the membership gate, the sweep,
    /// and the epoch stamp all name ONE build. The convenience overload above captures for direct callers.</summary>
    public static ErrorCheckResult Run(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                                       IReadOnlyList<string>? scope, int limit,
                                       IReadOnlyList<(string Name, string Path)>? offOrder = null,
                                       SweepScope? recordScope = null,
                                       ErrorFindingClass classes = ErrorFindingClass.All, bool countsOnly = false,
                                       SweepExclusion.Resolved? exclude = null)
    {
        bool wantDangling = classes.HasFlag(ErrorFindingClass.Dangling);
        bool wantMasters = classes.HasFlag(ErrorFindingClass.MissingMasters);
        // The missing-master count comes off the plugin's master TABLE, so a RECORD scope cannot narrow it. Saying
        // "every count below is for this narrowed scope" directly under a plugin-level number is a false claim about the
        // number printed above it, so the claim is qualified whenever both are in play (PR #288 review, finding 3).
        // The claim fires only under a RECORD scope — that is the one thing here that makes a reported count a subset of
        // its own label. A findings= class filter leaves every number complete for what it names (an excluded class
        // renders "NOT CHECKED"), so claiming otherwise over a true whole-order total would be false (re-review finding 3).
        // The exclude= narrowing note is composed BELOW, after the exclusion has actually run: its number is what
        // THIS SCOPE lost, and read off the resolved set it was the size of the group the token expanded to.
        // plugins=["MyMod.esp"] exclude=["base_masters"] said "left out 5 plugin(s)" over a sweep that removed none.
        int excludedFromScope = 0;
        // counts_only=: the dangling-by-target-plugin tally, over EVERY dangling ref in scope (never limit-capped).
        // Built only when the walk that fills it actually RUNS — with 'dangling' excluded there is nothing to tally, and
        // an empty-but-present histogram would render as "nothing found" for a walk that never happened (PR #288 review,
        // finding 2). A null histogram means "not computed", never "empty".
        var histogram = countsOnly && wantDangling ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        // #344 — the SOURCE axis, tallied on EVERY sweep the walk runs on (the existing histogram is keyed by TARGET
        // plugin, which cannot answer "how much of this is vanilla" or "which plugin lost its entries to the budget").
        // Uncapped by limit= like the totals it decomposes; it costs one dictionary bump per dangling ref.
        var bySource = wantDangling ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
        // WHICH plugins the link walk actually examined a record in, under the scope in force. A record scope
        // (type=/formids=/editorid_contains=) can admit nothing from a plugin the sweep opened, and "swept" has to
        // mean examined rather than opened — otherwise the baseline line reports vanilla as covered-and-clean when
        // the scope filtered every vanilla record out. The layer that applies the filter is the layer that knows.
        var examined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // --- resolve the plugin set to scan (Q3: a bad or excluded explicit scope name fails loud, never a silent skip). ---
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                // Membership refusals are decided against THIS view — stamped (PR #305 re-review: the same logical
                // refusal must not stamp through one tool frame and not another).
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

        // #344's exclusion axis. Applied to the SWEEP, not to the listing: a plugin the caller excluded costs no
        // record walk and no budget, which is the half of #344 the phase order could not reach.
        // Runs whenever the caller PASSED an exclusion, even one that expands to nothing — otherwise a group with
        // no members in this order left no trace at all and the response never mentioned exclude= had been written.
        if (exclude is not null)
        {
            // Case-insensitive here, explicitly, rather than relying on whatever comparer the caller's collection
            // happens to carry: plugin filenames are matched case-insensitively everywhere else in this sweep.
            var drop = new HashSet<string>(exclude.Names, StringComparer.OrdinalIgnoreCase);
            // Only the names the CALLER TYPED are held against the scope. A typed name that matches nothing is a
            // typo and refuses; a GROUP member that is not here is the ordinary case — see SweepExclusion.Resolved
            // for the refusal this separation removes.
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
            // What the refusal below claims about: the WHOLE scope this sweep would have covered, captured before
            // either filter runs. Read off targets alone it was 0 over a plugins= that resolved entirely off-order —
            // that path leaves targets deliberately empty (see the offOrder branch above), so the refusal told a
            // caller their one-plugin scope had held nothing.
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

        var filterNote = SweepFindings.FilterNote(
            recordScope is null ? null
                : wantMasters
                    ? "the dangling / unscannable counts below are for THIS narrowed scope; the missing-master count is "
                      + "PLUGIN-level (read off the master table) and is NOT narrowed by it."
                    : SweepFindings.ScopedCountsClaim,
            recordScope?.Label, SweepFindings.Describe(classes),
            // Stated whenever the caller PASSED an exclusion, zero included: a group with no members in this order
            // still narrowed nothing, and an exclude= that leaves no trace reads as one that was ignored.
            exclude is not null ? $"exclude= left out {excludedFromScope} plugin(s)" : null);

        var reports = new List<PluginErrors>();
        int totalDangling = 0, totalMissing = 0, totalUnscannable = 0;
        int danglingBudget = limit;

        // #344 — the LISTING BUDGET's phase order. limit= is ONE counter, and it used to be spent plugin by plugin in
        // LOAD ORDER with the base-game masters at index 0: on a large order the vanilla baseline could consume it
        // before any mod plugin was reached, and a plugin that collects an empty dangling list fails the
        // report-inclusion test below — so its findings did not read as "buried", they were absent from the output
        // with no per-plugin trace. The per-plugin sweep is therefore a function, called in two phases (below): every
        // non-base plugin first, then the base-game masters with whatever budget is left. What each plugin FINDS is
        // unchanged — the totals, the histograms and the missing-master reads never depended on limit= — only which
        // findings get LISTED, and the reports are re-sorted back into load order before they are returned.
        void SweepActivePlugin(string plugin)
        {
            string? scanError = null;

            // Missing masters: a declared master not present in the active order (the dependency is not installed /
            // enabled). Read independently of the record walk so a record-walk fault still reports the master state.
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

            // findings= excluded 'dangling' ⇒ the per-record link walk is skipped WHOLESALE (the sweep's entire cost).
            // The render must then print the dangling AND unscannable lines as "not checked" rather than 0 — a skipped
            // check that reads as a clean one is the Q3 break this whole tool exists to avoid.
            try
            {
                foreach (var (fk, _, body, _) in wantDangling
                             ? view.RecordsIn(new[] { plugin }, recordScope?.Types)
                             : Enumerable.Empty<(FormKey, int, IMajorRecordGetter, string)>())
                {
                    // PER-RECORD FAULT ISOLATION (twin of the cross_plugin_query scan, HCBR-2026-06-09-03):
                    // EnumerateFormLinks lazily parses subrecord content, so ONE record Mutagen can't parse is excluded
                    // + accounted, never an opaque whole-call abort and never a silent skip (Q3).
                    try
                    {
                        // #282 record scope, tested BEFORE the deleted-record rule and the link walk — the two things
                        // this sweep actually spends its time on.
                        if (recordScope is not null && !recordScope.Matches(fk, body)) continue;
                        examined.Add(plugin);
                        // A DELETED record links to nothing (#279 — the shared rule, see DeletedRecordRule): its
                        // content is not live, so none of its FormLinks can be a dangling reference, and an
                        // engine-authored deleted body can throw on the walk below and land here as an untyped
                        // unscannable skip (Q3). Excluded before the walk, at all three walkers alike.
                        if (DeletedRecordRule.HasNoLiveBody(body)) continue;
                        if (body is not IFormLinkContainerGetter flc) continue;
                        Dictionary<FormKey, int>? ownerVarExempt = null;   // #207: built lazily on this record's first otherwise-dangling link (see UntypedOwnerVariableData)
                        foreach (var link in flc.EnumerateFormLinks())
                        {
                            var target = link.FormKey;
                            if (target.IsNull) continue;            // a null FormLink is a legal optional — not an error (see the class-doc boundary)
                            if (view.ResolveWinner(target) is not null) continue;   // resolves → fine
                            if (EngineImplicit.IsImplicit(target)) continue;        // engine-implicit (PlayerRef 000014 / Player 000007): the index can't resolve these hardcoded forms, but they are real, never dangling — same precise exemption the dialogue lints use (HCBR: was 531/531 false dangling → 000014)
                            ownerVarExempt ??= UntypedOwnerVariableData(body);      // #207: an UntypedOwner's VariableData word is a RequiredRank int (esp. -1 → FFFFFFFF), not a reference
                            if (ownerVarExempt.TryGetValue(target, out int rank) && rank > 0) { ownerVarExempt[target] = rank - 1; continue; }
                            totalDangling++;
                            Bump(bySource, plugin);                                                   // #344: the SOURCE axis, on every mode
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
                        if (unscannableSamples.Count < 3) unscannableSamples.Add($"{fk} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // The plugin enumeration itself faulting (a record that throws on top-level enumeration rather than on a
            // link walk) is NAMED per-plugin and the sweep continues — never the MCP layer's opaque transport error (Q3).
            catch (Exception ex)
            {
                scanError = (scanError is null ? "" : scanError + "; ")
                          + $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}";
            }

            totalMissing += missingMasters.Count;
            totalUnscannable += unscannable;

            // counts_only=: the reports list carries the HONESTY LAYER only — a plugin whose records could not be read.
            // Findings themselves live in the totals + histogram, so the render has no per-plugin body to size (and no
            // second place where the two modes could drift).
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
        //     order PLUS the file's own records. Same fault-isolation contract as the active loop (Q3).
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
                    ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);

                    if (wantMasters)
                    {
                        foreach (var m in ov.ModHeader.MasterReferences)
                            if (!view.ContainsPlugin(m.Master.FileName)) missingMasters.Add(m.Master.FileName);
                        missingMasters.Sort(StringComparer.OrdinalIgnoreCase);
                    }

                    // Pass 1 — the file's OWN FormKeys: a link into a record this same file defines is satisfied the
                    // moment the plugin is enabled, so it must not read as dangling (the patch-links-its-own-new-record
                    // case). An enumeration abort leaves a PARTIAL set — named below, and pass 2 aborts the same way.
                    // Deliberately NOT record-scoped (#282): a link into a record the file defines is satisfied whether
                    // or not that record is inside the caller's scope, so scoping this set would manufacture dangling refs.
                    var selfKeys = new HashSet<FormKey>();
                    if (wantDangling)
                    {
                        try { foreach (var r in ov.EnumerateMajorRecords()) selfKeys.Add(r.FormKey); }
                        catch (Exception ex) { scanError = $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"; }
                    }

                    // Pass 2 — the link walk, per-record fault isolation (the active loop's exact contract).
                    try
                    {
                        foreach (var rec in wantDangling ? OffOrderRecords(ov, recordScope) : Enumerable.Empty<IMajorRecordGetter>())
                        {
                            try
                            {
                                if (recordScope is not null && !recordScope.Matches(rec.FormKey, rec)) continue;   // #282
                                examined.Add(name);
                                if (DeletedRecordRule.HasNoLiveBody(rec)) continue;   // #279 — same rule as the active pass above
                                if (rec is not IFormLinkContainerGetter flc) continue;
                                Dictionary<FormKey, int>? ownerVarExempt = null;   // #207 (see UntypedOwnerVariableData)
                                foreach (var link in flc.EnumerateFormLinks())
                                {
                                    var target = link.FormKey;
                                    if (target.IsNull) continue;
                                    if (view.ResolveWinner(target) is not null) continue;
                                    if (selfKeys.Contains(target)) continue;            // defined by this very file
                                    if (EngineImplicit.IsImplicit(target)) continue;
                                    ownerVarExempt ??= UntypedOwnerVariableData(rec);   // #207: RequiredRank int mis-exposed as a FormLink
                                    if (ownerVarExempt.TryGetValue(target, out int rank) && rank > 0) { ownerVarExempt[target] = rank - 1; continue; }
                                    totalDangling++;
                                    Bump(bySource, name);                                                     // #344: the SOURCE axis, keyed by the off-order FILE (which can itself be a base master — see BaseMastersSwept below)
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
                                if (unscannableSamples.Count < 3) unscannableSamples.Add($"{rec.FormKey} — {ex.GetType().Name}: {ex.Message}");
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

        // Phase 2 — the base-game masters LAST, on what the rest of the order left. Their dangling refs are vanilla
        // leftovers rather than anything a load order introduced, so spending the caller's limit= on them first made mod findings
        // unreachable; spending it on them LAST costs nothing when the budget is ample and costs only baseline noise
        // when it is not. An off-order file swept in the same call was listed above them, which is the pre-enable
        // verify lane's whole point — and reaching this phase at all with an off-order file present means the caller
        // NAMED a base master in plugins=, since an unscoped sweep has no off-order files.
        foreach (var plugin in targets)
            if (IsBaseMaster(plugin)) SweepActivePlugin(plugin);

        // Reports are BUILT in budget-phase order but READ in load order: which section comes first is a load-order
        // fact, and encoding the phase in it would make the fix visible as a reordered report for no reason.
        if (reports.Count > 1 && targets.Any(IsBaseMaster))
        {
            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targets.Count; i++) position[targets[i]] = i;
            for (int i = 0; i < offOrderScanned.Count; i++) position[offOrderScanned[i]] = targets.Count + i;
            reports = reports.OrderBy(p => position.TryGetValue(p.Plugin, out var i) ? i : int.MaxValue).ToList();
        }

        // #344 — the baseline split, derived from the ONE source tally so "is this plugin baseline" is asked in exactly
        // one place. The swept SUBSET below is not (BaselineDangling > 0): a swept baseline that came back CLEAN is a
        // different fact from a sweep that never looked at one, and a render that conflates them says "0 of N are
        // vanilla" about an order whose vanilla was never in scope.
        int baselineDangling = bySource is null ? 0 : bySource.Where(kv => IsBaseMaster(kv.Key)).Sum(kv => kv.Value);
        // WHICH base masters this sweep actually looked at — the swept SUBSET, not a yes/no. A render that says
        // "N of M come from the base-game masters" has to name the ones it counted, and naming all five over a sweep
        // that touched one states a figure about four plugins it never opened (round-1 review, two reviewers).
        // Built from `examined`, so it covers both lanes at once: an off-order base master swept as a FILE counts (its
        // findings land in the same tally, and asking only the active targets would let BaselineDangling be positive
        // while this said no baseline was swept), and a base master whose every record a record scope filtered out does
        // NOT count — the sweep opened it but examined nothing in it (round-2 review). In Mutagen's own order, so the
        // render is stable across sweeps.
        var baseSwept = BaseMasterNames.Where(examined.Contains).ToList();
        // Whether the sweep had any NON-base plugin in scope at all. A render cannot get this by comparing
        // PluginsScanned against the swept-base count: the two measure different things and diverge whenever a
        // base master is in targets but contributes nothing to `examined` — a record scope that filters it out,
        // or a name repeated in plugins= (Aaron's PR #360 review). The layer that resolved the targets is the
        // layer that knows, so it states the fact rather than leaving a subtraction to stand in for it.
        bool nonBaseInScope = targets.Any(t => !IsBaseMaster(t));

        // What the budget dropped, by plugin, is NOT computed here any more. It was `bySource` minus what the
        // reports listed — the BUDGET's omission — and the render then dropped more without either sentence
        // knowing. HousecarlMcp.CheckAccounting now subtracts against what the RESPONSE emitted, which covers both
        // truncators at once and is the one place either is claimed (#361). Everything it needs is below:
        // DanglingBySource is the per-plugin truth, and the reports carry what the budget admitted.

        return new ErrorCheckResult(reports, targets.Count + offOrderScanned.Count, totalDangling, totalMissing,
                                    totalUnscannable, view.ExcludedPlugins, null, offOrderScanned,
                                    filterNote, classes, histogram is null ? null : SweepFindings.Histogram(histogram),
                                    countsOnly, view.Epoch,
                                    bySource is null ? null : SweepFindings.Histogram(bySource),
                                    baselineDangling, baseSwept, nonBaseInScope, limit);
    }

    /// <summary>The off-order file's record stream, type-scoped when the caller asked for one (#282) — the overlay
    /// counterpart of <c>RecordsIn</c>'s getter-type filter, so a <c>type=</c> scope costs nothing per skipped record on
    /// this lane either.</summary>
    static IEnumerable<IMajorRecordGetter> OffOrderRecords(ISkyrimModGetter ov, SweepScope? scope)
        => scope?.Types is { Count: > 0 } types
            ? types.SelectMany(t => ov.EnumerateMajorRecords(t, throwIfUnknown: true)).Cast<IMajorRecordGetter>()
            : ov.EnumerateMajorRecords();

    /// <summary>Bump the <c>counts_only=</c> dangling histogram for one broken target: keyed by the PLUGIN the target
    /// form lives in, which is the diagnostic the per-source-plugin grouping never gave — "480 of these point into
    /// SomeMissingMod.esp" names the one absent dependency behind a wall of findings.</summary>
    static void BumpTarget(Dictionary<string, int> acc, FormKey target)
        => Bump(acc, target.ModKey.FileName.String);

    /// <summary>Bump one plugin-keyed tally. Null accumulator = that axis is not being collected (the walk did not
    /// run), so the call is a no-op rather than the caller's problem at every bump site.</summary>
    static void Bump(Dictionary<string, int>? acc, string key)
    {
        if (acc is null) return;
        acc[key] = acc.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>Every FormKey carried in an <see cref="IUntypedOwnerGetter.VariableData"/> slot in <paramref name="body"/>,
    /// as a MULTISET (FormKey → occurrence count) — the FormKeys the dangling sweep must NOT flag (#207).
    ///
    /// <para>WHY (Mutagen 0.53.1, confirmed at source — <c>ExtraDataBinaryCreateTranslation.GetBinaryOwner</c> +
    /// <c>RecordTypeInfoCacheReader.IsOfRecordType</c>): a container / leveled-list item's ownership is a COED block — an
    /// owner FormID plus a SECOND 4-byte word that is a <c>RequiredRank</c> int when the owner is a FACTION, or a Global
    /// FormLink when it is an NPC. Mutagen picks the arm by resolving the owner form's record TYPE; when the owner lives
    /// in a MASTER and the overlay carries no link cache — which is EVERY override this sweep reads — that resolution
    /// throws <c>LinkCacheMissingException</c> and the arm falls back to <see cref="IUntypedOwnerGetter"/>, which exposes
    /// BOTH words as <c>FormLink&lt;ISkyrimMajorRecordGetter&gt;</c>. <c>EnumerateFormLinks</c> then walks the second word;
    /// for the common faction case it is a rank, and a rank of -1 (0xFFFFFFFF → FFFFFF:&lt;self&gt;) resolves to nothing and
    /// was reported as a false dangling reference — the same COED reads correctly (as FactionOwner + RequiredRank) only
    /// when the owner faction lives in the very plugin being parsed.</para>
    ///
    /// <para>So we drop ONLY that second (VariableData) word from the scan. The owner form itself
    /// (<see cref="IUntypedOwnerGetter.OwnerData"/> — the FIRST word) is still checked, so a genuinely broken owner still
    /// surfaces. Exemption is per-record and by exact FormKey with a count, so it can never mask an unrelated dangling
    /// link elsewhere in the record. Owner targets live ONLY on <see cref="IExtraDataGetter.Owner"/>, carried by exactly
    /// these four record types (by the generated schema — a fifth would be an upstream Mutagen change, caught by the
    /// schema regen), so this switch is the complete surface.</para></summary>
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

/// <summary>One broken reference: the SOURCE record (FormKey + catalog type + editorid) and the TARGET FormKey no
/// active plugin defines.</summary>
public sealed record DanglingRef(FormKey Source, string SourceType, string? SourceEditorId, FormKey Target);

/// <summary>Every error found in one plugin: its dangling references (capped across the sweep), the masters it declares
/// that are not present in the active order, the count + samples of records that could not be scanned, and — if the
/// plugin's own enumeration faulted — a <paramref name="ScanError"/>.
/// <para><paramref name="InstalledButInactiveMasters"/> is the subset of <paramref name="MissingMasters"/> whose file
/// IS somewhere in the MO2 install — a copy in a disabled mod, or an unticked plugin — so its remedy is ENABLE where
/// the rest want INSTALL. The sweep itself cannot tell the two apart (it knows the active order, not the install), so
/// the split is filled in by the layer that reads the MO2 composition. <b>null means NOT CLASSIFIED</b>, never "none
/// of them" — a render that reads null must state the union remedy rather than claim every master is uninstalled
/// (the same null-is-not-empty rule <paramref name="ErrorCheckResult.Histogram"/> carries).</para></summary>
public sealed record PluginErrors(
    string Plugin,
    IReadOnlyList<DanglingRef> Dangling,
    IReadOnlyList<string> MissingMasters,
    int UnscannableRecords,
    IReadOnlyList<string> UnscannableSamples,
    string? ScanError,
    IReadOnlyList<string>? InstalledButInactiveMasters = null);

/// <summary>The result of <see cref="ErrorCheck.Run"/>: the per-plugin reports (only plugins WITH findings; clean
/// plugins are counted in <paramref name="PluginsScanned"/> but omitted), the sweep totals, whether the dangling list
/// was capped at the caller's limit, the plugins the index build excluded as unparseable, and — on a Q3 scope error —
/// a recoverable <see cref="Error"/> with no reports.
/// <para><paramref name="FilterNote"/> (#282) names every narrowing the caller applied and states that the totals above
/// are for that narrowed scope; null when nothing was narrowed. <paramref name="Classes"/> is the finding-class filter
/// that was in force — the render reads it to print an EXCLUDED class as "not checked" instead of as a zero (a class
/// nobody looked for must not read as a class that came back clean). <paramref name="Histogram"/> is the
/// dangling-by-target-plugin tally, present ONLY under <paramref name="CountsOnly"/> (null = not computed, never "none
/// found"), under which <paramref name="Reports"/> carries only plugins whose records could not be read.</para>
/// <para>#344 — the SOURCE axis. <paramref name="DanglingBySource"/> tallies every dangling ref by the plugin it came
/// FROM (never limit-capped; null = the link walk did not run, never "none found"), the decomposition the
/// target-keyed <paramref name="Histogram"/> cannot give. <paramref name="BaselineDangling"/> is how many of
/// <paramref name="TotalDangling"/> came from a base-game master (<see cref="ErrorCheck.BaseMasters"/>) — vanilla
/// leftovers rather than anything a load order introduced — and <paramref name="BaseMastersSwept"/> names WHICH base
/// masters this sweep actually looked at (empty = none, so "0 baseline findings" and "no baseline was swept" stay
/// distinguishable; a render must name this subset rather than <see cref="ErrorCheck.BaseMasters"/>, which would
/// attribute a count to plugins the sweep never opened).</para>
/// <para>Which plugins LOST entries is no longer a field here: it is a fact about the RESPONSE, not about the sweep,
/// and it is computed in the render's accounting against what that response emitted. Carried on the result it could
/// only ever report the listing budget's own omissions, so a plugin whose entries the budget listed and max_chars
/// then dropped appeared in no sentence at all.</para></summary>
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
    string? Epoch = null,   // the swept INDEXED build's fingerprint (SPEC §2.1.1). Stamped on success and on refusals decided against a captured build (membership/locate); null on parse-level refusals that consulted none. OffOrderScanned files are located OUTSIDE the index — their content is not under this fingerprint (the renders qualify the stamp when any were swept; PR #305 review)
    IReadOnlyList<SweepCount>? DanglingBySource = null,
    int BaselineDangling = 0,
    IReadOnlyList<string>? BaseMastersSwept = null,
    bool NonBaseInScope = false,
    int Limit = 0)   // the listing budget this sweep was GIVEN. Carried so the response can name the knob it is telling the caller to raise without being passed it a second time — a render-side copy defaults, and a default that disagrees with the sweep puts a wrong number in front of the caller

{
    public bool Success => Error is null;
    public static ErrorCheckResult Fail(string error) =>
        new(Array.Empty<PluginErrors>(), 0, 0, 0, 0,
            new Dictionary<string, string>(), error);
}
