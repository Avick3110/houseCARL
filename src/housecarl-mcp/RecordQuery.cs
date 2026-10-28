using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

internal sealed partial class RecordReads
{
    // ---- cross-plugin query ----------------------------------------------------------------------------

    /// <summary>Scan the order for records matching a filter, in a SINGLE enumeration pass with the matching record's
    /// body in hand: type= streams the winner body, plugins= each scoped plugin's own, conflicts_only= alone reads
    /// the index.</summary>
    public CrossQueryOutcome CrossQuery(string? type, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn = false, string? groupBy = null, int offset = 0, string? whereSource = null,
                                        IReadOnlyList<ArtifactDemand>? artifactDemands = null,
                                        IReadOnlyList<FormKey>? referencesNone = null,
                                        CancellationToken ct = default)
        => CrossQuery(type is null ? null : new[] { type }, references, editoridContains, conflictsOnly, plugins, where,
                      limit, definedIn, groupBy, offset, whereSource, artifactDemands, formidSet: null, pinnedView: null, referencesNone: referencesNone, ct: ct);

    /// <summary>The set-valued-types overload: each entry resolves through the same
    /// <see cref="TypeLookup.Resolve"/>, and the scan streams the union of the resolved type groups.</summary>
    /// <param name="formidSet">The formids-by-scan composition: intersects the selection with an explicit identity set.</param>
    public CrossQueryOutcome CrossQuery(IReadOnlyList<string>? typeSet, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn, string? groupBy, int offset, string? whereSource,
                                        IReadOnlyList<ArtifactDemand>? artifactDemands,
                                        IReadOnlyList<FormKey>? formidSet,
                                        LoadOrderResolver.IndexView? pinnedView,
                                        IReadOnlyList<FormKey>? referencesNone,
                                        CancellationToken ct)
    {
        var resolver = _host.Resolver;
        // The caller's own build when its FormID door already captured one, so the tokens it parsed and the
        // records this scan matches come from ONE build.
        var view = pinnedView ?? resolver.Capture();
        var pin = new LoadOrderService.ViewPin(resolver, view);   // the outcome's and every prefilled row's
        // A plugins= scope naming a plugin the order does not carry answers for the ones it does; the missing
        // names ride the scan note, and only an ALL-missing scope is refused.
        string? scopeMissingNote = null;
        if (plugins is { Count: > 0 })
        {
            var split = ScopeSplit.Of(view, plugins);
            if (split.BlankRefusal is not null) return CrossQueryOutcome.Fail(split.BlankRefusal);
            if (split.Missing.Count > 0)
            {
                if (split.Present.Count == 0) return CrossQueryOutcome.Fail(split.NothingToScanRefusal(view));
                scopeMissingNote = split.ServedNote(view);
                plugins = split.Present;
            }
        }
        bool hasPlugins = plugins is { Count: > 0 };
        bool hasType = typeSet is { Count: > 0 };
        bool hasWhere = where is { Count: > 0 };
        bool hasReferences = references is { Count: > 0 };
        // The negated half of references=: a record is kept only when it links to NONE of these.
        var refNone = referencesNone is { Count: > 0 } ? new HashSet<FormKey>(referencesNone) : null;
        bool bodyFilter = hasReferences || refNone is not null || !string.IsNullOrEmpty(editoridContains) || hasWhere;
        bool hasFormidSet = formidSet is { Count: > 0 };

        if (!hasType && !conflictsOnly && !hasPlugins && !bodyFilter && !hasFormidSet)
            return CrossQueryOutcome.Fail("a scan needs at least one of: types=, plugins=, formids=, conflicts_only=true, where=, or references=.");
        // A formid set is itself a bound. An unbounded references= is no longer one: the reverse-reference index
        // supplies the scan universe and everything downstream is the ordinary scan. Every OTHER body filter still
        // needs a bound — the index knows links, not field values.
        string? reverseNote = null;
        bool indexUniverse = false;                                    // the scan universe came from the index, not from the caller
        if (bodyFilter && !hasType && !hasPlugins && !hasFormidSet)
        {
            bool reverseOnly = !hasWhere && string.IsNullOrEmpty(editoridContains);
            if (!reverseOnly)
                return CrossQueryOutcome.Fail("where=/editorid_contains= is a body scan and must be combined with types=, plugins=, or a formids= set to bound it (conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). Only references= is unbounded, off the reverse-reference index.");
            var built = view.EnsureReverseIndex();
            var universe = HousecarlCore.ReverseSelection.Universe(view, view.ReverseIndex!, references);
            var universeNote = HousecarlCore.ReverseSelection.UniverseNote(references, universe.Count);
            // The index's own line rides EVERY answer it serves, not only the call that paid the build. Which lane
            // asked decides how an unreadable plugin reads — short for the positive question, over-inclusive for
            // the sweep — so the note is told for the lane.
            bool orphanSweep = references is not { Count: > 0 };
            reverseNote = built.NoteFor(orphanSweep) + (universeNote is null ? "" : " " + universeNote);
            formidSet = universe;
            hasFormidSet = true;                                       // an empty universe is still the universe: 0 matches, not a refusal
            indexUniverse = true;
        }

        // defined_in= keeps only records DEFINED in the scoped plugins, which is distinct from plugins= meaning
        // everything a plugin touches, so it is refused loudly rather than silently ignored.
        if (definedIn && !hasPlugins)
            return CrossQueryOutcome.Fail("defined_in=true keeps only records DEFINED in a scoped plugin, so it requires plugins= to name that scope. Add plugins=, or drop defined_in= (a bare scan already reports each match's defining plugin via its FormID suffix).");
        HashSet<ModKey>? scopedModKeys = null;
        if (definedIn)
        {
            scopedModKeys = new();
            foreach (var p in plugins!)
                try { scopedModKeys.Add(ModKey.FromFileName(p.Trim())); }
                catch (Exception ex) { return CrossQueryOutcome.Fail($"defined_in: '{p}' is not a valid plugin filename: {ex.Message}"); }
        }

        // offset= pages the match window, validated up front: negative is meaningless, and under group_by= there
        // is no match window to page. The tool door refuses that pair first (RecordsTools, on the aggregate form),
        // so this arm is only reachable through the service API — kept, because the service is a caller too.
        if (offset < 0)
            return CrossQueryOutcome.Fail($"offset={offset} — offset must be >= 0 (it skips that many matches before returning rows).");
        if (offset > 0 && groupBy is not null)
            return CrossQueryOutcome.Fail(ReadSentences.NoOffsetOnCountTable("group_by="));

        // where_source= chooses which body the body filters decide on: 'scoped' (default) is the body the scan
        // streams, 'winner' the live load-order winner.
        bool whereWinner = false;
        if (whereSource is not null)
        {
            var ws = whereSource.Trim().ToLowerInvariant();
            if (ws is not ("scoped" or "winner"))
                return CrossQueryOutcome.Fail($"where_source='{whereSource}' is not a known source — use 'scoped' (default; the scanned body) or 'winner' (the live load-order winner).");
            whereWinner = ws == "winner";
        }
        if (whereWinner && !bodyFilter)
            return CrossQueryOutcome.Fail("where_source=winner retargets the body filters (where=/references=/editorid_contains=) onto the live load-order winner, but none of those was given — add a body filter, or drop where_source= (a bare type=/plugins= scope already reports each match's winner).");
        // Under a type=-only scope the scan already streams the winner body, so where_source=winner is satisfied:
        // accept it, but say so rather than silently no-op.
        bool whereWinnerActive = whereWinner && hasPlugins;
        string? whereSourceNote = (whereWinner && !hasPlugins)
            ? "note: where_source=winner is redundant here — a type=-only scan already reads the load-order winner, so the match used the winner regardless."
            : null;

        // group_by= aggregates matches into a count table, validated up front.
        if (groupBy is not null)
        {
            groupBy = groupBy.Trim().ToLowerInvariant();
            if (groupBy is not ("winner" or "type" or "defined_in"))
                return CrossQueryOutcome.Fail($"group_by='{groupBy}' is not a known aggregation key — use 'winner', 'type', or 'defined_in'.");
            if (groupBy == "type" && !hasType && !hasPlugins && !hasFormidSet)
                return CrossQueryOutcome.Fail("group_by=type needs each match's type, which requires a body-bearing scope — add type= or plugins= (winner/defined_in group without a body).");
        }
        var refSet = hasReferences ? new HashSet<FormKey>(references!) : null;
        bool multiTarget = references is { Count: >= 2 };

        // where= becomes the field-value predicate set, parsed up front so a malformed predicate refuses the call.
        FieldPredicateSet? predicate = null;
        if (hasWhere)
        {
            var (set, perr) = FieldPredicateSet.Parse(where!, FormIdDoor.On(view).Parse);
            if (perr is not null) return CrossQueryOutcome.Fail(perr);
            predicate = set;
        }

        // Artifact re-entry: checked HERE against the view this scan will answer from, not at the tool layer,
        // where a freshness rebuild between check and scan would let a stale artifact through.
        foreach (var demand in (artifactDemands ?? Array.Empty<ArtifactDemand>()).Concat(
                     predicate?.ArtifactDemands ?? (IReadOnlyList<ArtifactDemand>)Array.Empty<ArtifactDemand>()))
            if (demand.Epoch != view.Epoch)
                return CrossQueryOutcome.Fail(ArtifactEpochMismatch(demand, view.Epoch)) with { Stamp = view.Stamp };

        IReadOnlyList<Type>? types;
        try { types = _host.Types.ResolveSet(hasType ? typeSet : null); }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }   // unknown type

        if (predicate is not null && SchemaPlanRefusal(hasType ? typeSet : null, predicate) is { } qerr)
            return CrossQueryOutcome.Fail(qerr) with { Stamp = view.Stamp };

        var keys = new List<FormKey>();
        var sources = new List<string?>();                                    // parallel to keys: the plugin whose body matched (null ⇒ winner), so the render displays the SAME body it filtered
        List<string?>? matched = multiTarget ? new() : null;                  // parallel to keys: which target(s) each hit referenced (multi-target references= un-merge); null when 0/1 target
        List<RecordSummary>? prefilled = (hasType || hasPlugins || hasFormidSet) ? new() : null;   // parallel to keys; null = renderer fills lazily
        // OrdinalIgnoreCase so case-variant spellings of the SAME plugin merge into one group instead of splitting
        // the count; first-seen casing becomes the display key. Harmless for group_by=type.
        Dictionary<string, int>? groups = groupBy is not null ? new(StringComparer.OrdinalIgnoreCase) : null;   // group_by= aggregation (bumped per match, over ALL matches — the TALLY is not limit-capped; the render's table rows are)
        SeedRequestedTypes(groups, groupBy, types);
        int total = 0;
        int unscannable = 0;                                                // records whose body tests threw (Mutagen-unparseable content) — excluded and accounted, never silent
        var unscannableSamples = new List<string>();
        // Records whose links were read leniently — scanned, but with a named gap.
        var lenientKeys = new HashSet<FormKey>();
        var lenientSamples = new List<string>();
        void NoteLenient(FormKey fk, string? note)
        {
            if (note is null || !lenientKeys.Add(fk)) return;
            if (lenientSamples.Count < 3) lenientSamples.Add(note);
        }
        // Plugins the winner scan could not open at all — a whole-plugin coverage gap, named in the response.
        var unreadablePlugins = new List<PluginUnreadableException>();

        // The filters before where=: conflicts_only by touching plugins (when asked), editorid_contains, and a body with nothing live to test.
        PreWhere BeforeWhere(IMajorRecordGetter body, bool touching)
        {
            if (touching && conflictsOnly && (view.TouchingPlugins(body.FormKey)?.Count ?? 0) <= 1) return PreWhere.Filtered;
            if (!string.IsNullOrEmpty(editoridContains)
                && (body.EditorID is null || body.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                return PreWhere.Filtered;
            return DeletedRecordRule.HasNoLiveBody(body) && (refSet is not null || predicate is { NeedsLiveBody: true })
                ? PreWhere.NoLiveBody : PreWhere.Pass;
        }

        // Whether a row passes the body filters that run before where=, so a '*parent' gather skips the rows they drop.
        bool ReachesWhere(IMajorRecordGetter body, bool touching = false)
        {
            try { return BeforeWhere(body, touching) == PreWhere.Pass && ReferenceVerdict(body, refSet, refNone, references, false, out _, out _); }
            catch (Exception) { return true; }   // the row's own scan meets the fault and names it
        }

        // Only the type=/plugins= branches consult this as a pre-filter, and neither can co-occur with an
        // index-supplied universe.
        HashSet<FormKey>? setFilter = hasFormidSet && !indexUniverse ? new HashSet<FormKey>(formidSet!) : null;
        // The set-alone branch also owns conflicts_only combined with formidSet, via its in-loop touching-count
        // test: routing that pair to the index-only branch would drop every parsed body filter silently.
        if (!hasType && !hasPlugins && hasFormidSet)                          // the formid set ALONE is the universe: per-key winner fetch
        {
            LoadOrderResolver.OverlaySession? setSession = null;
            try
            {
                setSession = resolver.OpenSession();
                var sess = setSession;
                predicate?.BindResolution(
                    fk => view.ResolveWinner(fk)?.WinnerPlugin,
                    predicate.NeedsBodyResolution
                        ? fk =>
                        {
                            var w = view.ResolveWinner(fk);
                            return w is null ? null : view.GetRecord(sess, w.Value.WinnerPlugin, fk);
                        }
                        : null,
                    predicate.NeedsContainment ? fk => view.ParentOf(fk) : null,
                    predicate.NeedsContainment ? ParentGather(view, sess, unreadablePlugins, ct) : null);
                var seenSet = new HashSet<FormKey>();
                // The universe's bodies are gathered a CHUNK at a time, one enumeration per winner plugin, instead of
                // the whole-overlay seek per record.
                const int SetGatherChunk = 10_000;
                var setPending = new List<FormKey>(SetGatherChunk);
                bool setStopped = false;
                if (predicate is null) JudgeSetByGroup();
                else
                foreach (var fk in formidSet!)
                {
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan between records, and the gather stops it between plugin walks
                    if (!seenSet.Add(fk)) continue;
                    if (view.ResolveWinner(fk) is null) continue;             // not in the order — a clean non-match for a scan; per-item errors belong to the formids= list lane
                    setPending.Add(fk);
                    if (setPending.Count == SetGatherChunk && !DrainSet()) { setStopped = true; break; }
                }
                if (!setStopped && setPending.Count > 0) DrainSet();

                // With no where=, judge the universe a group at a time through the door, then count rows in universe order.
                void JudgeSetByGroup()
                {
                    var all = new List<(FormKey Key, WinnerInfo W)>();
                    foreach (var fk in formidSet!)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (seenSet.Add(fk) && view.ResolveWinner(fk) is { } w) all.Add((fk, w));
                    }
                    var at = new Dictionary<FormKey, int>(all.Count);
                    for (int i = 0; i < all.Count; i++) at[all[i].Key] = i;
                    var judged = new SetVerdict[all.Count];
                    // A kept row's summary, held while at most offset+limit kept rows precede it in the universe.
                    long cap = groups is null && limit > 0 ? (long)offset + limit : 0;
                    var rows = new SortedList<int, (List<FormKey>? Hit, RecordSummary? Summary, Exception? Fault)>();
                    var faults = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
                    OrderedGather.ForEachGroup(view, sess, all.Select(x => (x.W.WinnerPlugin, x.Key)), (_, bodies, asked) =>
                    {
                        foreach (var fk in asked)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!bodies.TryGetValue(fk, out var body)) continue;
                            int i = at[fk];
                            try
                            {
                                if (BeforeWhere(body, touching: true) != PreWhere.Pass)
                                { judged[i] = new SetVerdict(SetKind.Dropped, false, null, null, null); continue; }
                                bool keep = ReferenceVerdict(body, refSet, refNone, references, multiTarget && groups is null,
                                                             out var hitTargets, out var lenientNote);
                                judged[i] = new SetVerdict(SetKind.Judged, keep, lenientNote, body.GetType(), null);
                                if (!keep || rows.Count >= cap && (cap == 0 || i > rows.Keys[^1])) continue;
                                RecordSummary? summary = null; Exception? fault = null;
                                try
                                {
                                    summary = Summarize(fk, body, all[i].W);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { fault = ex; }
                                rows[i] = (hitTargets, summary, fault);
                                if (rows.Count > cap) rows.RemoveAt(rows.Count - 1);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                            { judged[i] = new SetVerdict(SetKind.Threw, false, null, null, $"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}"); }
                        }
                    }, faults, ct);
                    for (int i = 0; i < all.Count; i++)
                    {
                        var (fk, w) = all[i];
                        var j = judged[i];
                        if (faults.TryGetValue(w.WinnerPlugin, out var f) && !unreadablePlugins.Contains(f)) unreadablePlugins.Add(f);
                        if (j.Kind == SetKind.Missing) { NoteMissing(fk, w.WinnerPlugin, f); continue; }
                        if (j.Kind == SetKind.Threw) { Unscannable(j.Error!); continue; }
                        if (j.Kind == SetKind.Dropped) continue;
                        NoteLenient(fk, j.Note);
                        if (!j.Keep) continue;
                        var row = rows.GetValueOrDefault(i);
                        LandRow(fk, w.WinnerPlugin, j.Type!, () => row.Summary is { } held ? (row.Hit, held)
                                                                  : row.Fault is { } e ? throw e : Refetch(fk, w));
                    }
                }

                // A kept row that reached the page after its held summary was given up, read again from its winner.
                (List<FormKey>?, RecordSummary) Refetch(FormKey fk, WinnerInfo w)
                {
                    var body = view.GetRecord(sess, w.WinnerPlugin, fk)
                               ?? throw new InvalidOperationException($"winner '{w.WinnerPlugin}' did not yield the record on fetch");
                    ReferenceVerdict(body, refSet, refNone, references, multiTarget && groups is null, out var hit, out _);
                    return (hit, Summarize(fk, body, w));
                }

                RecordSummary Summarize(FormKey fk, IMajorRecordGetter body, WinnerInfo w)
                    => RecordSummary.Of(fk, body, w.WinnerPlugin, w.OverrideDepth).WithRuntime(view.RuntimeAddressOf(fk)) with { Pin = pin };

                void Unscannable(string sample) { unscannable++; if (unscannableSamples.Count < 3) unscannableSamples.Add(sample); }

                void NoteMissing(FormKey fk, string winner, PluginUnreadableException? f)
                    => Unscannable(f is not null ? $"{FormIdToken.Of(fk)} — {f.GetType().Name}: {f.Message}"
                                                 : $"{FormIdToken.Of(fk)} — winner '{winner}' did not yield the record on fetch");

                // Count one kept row: bump its group_by key, or land it on the page if its summary builds.
                void LandRow(FormKey fk, string winner, Type bodyType, Func<(List<FormKey>? Hit, RecordSummary Summary)> land)
                {
                    total++;
                    if (groups is not null)
                    {
                        var gk = groupBy == "type" ? RecordNaming.StripOverlay(bodyType.Name)
                               : groupBy == "defined_in" ? FormIdToken.Plugin(fk.ModKey.FileName.String)
                               : winner;
                        groups[gk] = groups.GetValueOrDefault(gk) + 1;
                    }
                    else if (total > offset && keys.Count < limit)
                    {
                        (List<FormKey>? hit, RecordSummary shown) row;
                        try { row = land(); }
                        catch (Exception ex) { Unscannable($"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}"); return; }
                        keys.Add(fk);
                        sources.Add(null);                            // the winner body is what matched and displays
                        matched?.Add(row.hit is not null ? string.Join(", ", row.hit) : null);
                        prefilled?.Add(row.shown);
                    }
                }

                // Gather one chunk's winner bodies and scan its keys in the universe's own order; false stops.
                bool DrainSet()
                {
                    var bodies = WinnerBodies.For(view, sess, setPending, out var faults, ct);
                    // A winner plugin that would not open is a whole-plugin coverage gap, named once.
                    unreadablePlugins.AddRange(faults.Values);
                    // The containing records of the rows that reach where=, one walk per winner plugin.
                    predicate?.HoldParents(bodies.Values.Where(b => ReachesWhere(b, touching: true)));
                    bool go = true;
                    try
                    {
                        foreach (var fk in setPending)
                        {
                            ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                            var w = view.ResolveWinner(fk);
                            if (w is null) continue;
                            try
                            {
                                if (!bodies.TryGetValue(fk, out var body))
                                { NoteMissing(fk, w.Value.WinnerPlugin, faults.GetValueOrDefault(w.Value.WinnerPlugin)); continue; }
                                var pre = BeforeWhere(body, touching: true);
                                // A deleted record only another where= term drops still counts toward the 'in' list members.
                                if (pre == PreWhere.NoLiveBody && refSet is null) predicate!.NoteMembers(body);
                                if (pre != PreWhere.Pass) continue;
                                // The same one-read verdict the scoped lane makes, so the formids-as-universe lane —
                                // where an unbounded references= also lands — answers identically.
                                bool keep = ReferenceVerdict(body, refSet, refNone, references, multiTarget && groups is null,
                                                             out var hitTargets, out var lenientNote);
                                NoteLenient(fk, lenientNote);
                                if (!keep) continue;
                                if (predicate is not null && !predicate.Matches(body))
                                {
                                    if (predicate.FatalError is not null) { go = false; break; }
                                    continue;
                                }
                                LandRow(fk, w.Value.WinnerPlugin, body.GetType(), () => (hitTargets, Summarize(fk, body, w.Value)));
                            }
                            catch (Exception ex) { Unscannable($"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}"); }
                        }
                    }
                    finally { predicate?.ReleaseParents(); }
                    setPending.Clear();
                    return go;
                }
            }
            finally { setSession?.Dispose(); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError);
        }
        else if (hasType || hasPlugins)                                       // a body-bearing scope: stream + filter in hand
        {
            // RecordsIn and WinnerRecordsOfType are lazy iterators: their throws happen on ENUMERATION, so the
            // try must wrap the foreach or the clean message escapes as a generic framework error.
            var seen = new HashSet<FormKey>();
            // Under where_source=winner the match decides on the live winner body, fetched via this ONE session.
            LoadOrderResolver.OverlaySession? winnerSession =
                (whereWinnerActive || predicate is { NeedsBodyResolution: true }) ? resolver.OpenSession() : null;
            // The `winner` term and the `->` link step read the view's resolution, bound off the SAME captured
            // view the scan answers from.
            predicate?.BindResolution(
                fk => view.ResolveWinner(fk)?.WinnerPlugin,
                predicate.NeedsBodyResolution
                    ? fk =>
                    {
                        var w = view.ResolveWinner(fk);
                        return w is null ? null : view.GetRecord(winnerSession!, w.Value.WinnerPlugin, fk);
                    }
                    : null,
                // The containment map is WHOLE-ORDER and later-wins on both lanes, including plugins=, because
                // which record contains a child is a fact about the assembled order rather than about one file.
                predicate.NeedsContainment ? fk => view.ParentOf(fk) : null,
                predicate.NeedsContainment ? ParentGather(view, winnerSession!, unreadablePlugins, ct) : null);
            // where_source=winner needs one body per CANDIDATE, and fetching them one at a time is a whole-overlay
            // walk each; a '*parent' term needs one per containing record, and gathers them the same way.
            const int WinnerGatherChunk = 10_000;
            bool chunked = whereWinnerActive || predicate is { NeedsContainment: true };
            // The chunk's rows, filtered by everything that needs no body, held only until the chunk drains.
            var pending = chunked
                ? new List<(FormKey fk, int depth, IMajorRecordGetter body, string? source, string? winner)>(WinnerGatherChunk)
                : null;
            // A winner scan that reads every GMST also builds the shared-EditorID lookup its rows' notes read.
            var names = !hasPlugins && SharedEditorIds.Covers(types, _host.Types) ? new SharedEditorIds(view) : null;
            var streamUnread = new List<PluginUnreadableException>();
            try
            {
                // Carry the source plugin per record so the render shows the body the scan filtered: plugins= gives
                // the scoped plugin's filename, type= gives null, meaning the winner.
                IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string? source)> stream =
                    hasPlugins ? view.RecordsIn(plugins!, types).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)x.source))  // the scoped plugin's own body
                               : view.WinnerRecordsOfType(types!, streamUnread).Select(x =>
                               {
                                   names?.Add(x.fk, x.body);
                                   return (fk: x.fk, depth: x.depth, body: x.body, source: (string?)null);    // the load-order winner's body
                               });
                // A '->' or '*parent' term reads its targets and containers through the door, each group once.
                if (predicate is { NeedsBodyResolution: true } && !whereWinnerActive)
                {
                    var doorFaults = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
                    void Door(IEnumerable<FormKey> wanted, Action<IReadOnlyDictionary<FormKey, IMajorRecordGetter>, IReadOnlyCollection<FormKey>> judge)
                        => OrderedGather.ForEachGroup(view, winnerSession!, wanted, judge, doorFaults, ct, () => predicate.FatalError is not null);
                    bool Kept(FormKey fk, int depth) => (setFilter is null || setFilter.Contains(fk)) && !(conflictsOnly && depth <= 1)
                                                        && !(definedIn && !scopedModKeys!.Contains(fk.ModKey));
                    if (!hasPlugins && !predicate.NeedsLinkStep && string.IsNullOrEmpty(editoridContains) && refSet is null)
                    {
                        // Containment alone: the candidates and their containers are index facts, so no candidate body is read.
                        var classOk = new Dictionary<Type, bool>();
                        bool OfType(Type c) { if (!classOk.TryGetValue(c, out var ok)) classOk[c] = ok = types!.Any(t => t.IsAssignableFrom(c)); return ok; }
                        predicate.PrimeOrdered(null,
                            view.RecordKeys().Where(fk => view.WinnerClassOf(fk) is { } c && OfType(c) && view.ResolveWinner(fk) is { } w && Kept(fk, w.OverrideDepth)),
                            Door, ct);
                    }
                    else
                    {
                        var preSeen = new HashSet<FormKey>();
                        IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> pre =
                            hasPlugins ? view.RecordsIn(plugins!, types).Select(x => (x.fk, x.depth, x.body))
                                       : view.WinnerRecordsOfType(types!, new List<PluginUnreadableException>());
                        // A plugin that faults ends the collect pass quietly; the scan meets the fault in its own order.
                        predicate.PrimeOrdered(UntilFault(pre).Where(x => preSeen.Add(x.fk) && Kept(x.fk, x.depth) && ReachesWhere(x.body)).Select(x => x.body),
                                               null, Door, ct);
                    }
                }
                bool stopped = false;
                foreach (var (fk, depth, body, source) in stream)
                {
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                    if (!seen.Add(fk)) continue;   // one row per key, judged on the copy shown, even if a plugin changed since the build
                    if (setFilter is not null && !setFilter.Contains(fk)) continue;   // the identity intersection, cheapest first
                    if (conflictsOnly && depth <= 1) continue;
                    // defined_in= keeps only records whose origin FormKey is a scoped plugin — a definition, not an
                    // override this plugin merely touches. A FormKey test needing no body, so it runs before the try.
                    if (definedIn && !scopedModKeys!.Contains(fk.ModKey)) continue;
                    if (!chunked)
                    {
                        if (!ScanRow(fk, depth, body, source)) { stopped = true; break; }
                        continue;
                    }
                    string? winner = null;
                    if (whereWinnerActive)
                    {
                        // A record the order gives no winner at all is a clean non-match, exactly as the per-record
                        // fetch treated it — never an unscannable row naming a winner there is none of.
                        if (view.ResolveWinner(fk) is not { } w) continue;
                        winner = w.WinnerPlugin;
                    }
                    pending!.Add((fk, depth, body, source, winner));
                    if (pending.Count == WinnerGatherChunk && !DrainChunk()) { stopped = true; break; }
                }
                if (!stopped && chunked && pending!.Count > 0) DrainChunk();
                unreadablePlugins.AddRange(streamUnread);
                if (names is not null && !stopped) pin.Seed(names.Finish(streamUnread));

                // Fetch one chunk's winner and containing-record bodies and scan its rows, in stream order; false stops the scan.
                bool DrainChunk()
                {
                    var needed = new List<FormKey>(pending!.Count);
                    if (whereWinnerActive)
                        foreach (var p in pending)
                            if (!string.Equals(p.winner, p.source, StringComparison.OrdinalIgnoreCase)) needed.Add(p.fk);
                    var bodies = WinnerBodies.For(view, winnerSession!, needed, out var faults, ct);
                    // A winner plugin that would not open is a whole-plugin coverage gap, named once in the
                    // response rather than only sampled three rows deep.
                    unreadablePlugins.AddRange(faults.Values);
                    // The body each row's filters decide on; null for a row whose winner did not yield it.
                    var filterBodies = new IMajorRecordGetter?[pending.Count];
                    for (int i = 0; i < pending.Count; i++)
                    {
                        var p = pending[i];
                        filterBodies[i] = !whereWinnerActive || string.Equals(p.winner, p.source, StringComparison.OrdinalIgnoreCase)
                            ? p.body
                            : bodies.GetValueOrDefault(p.fk);
                    }
                    predicate?.HoldParents(filterBodies.OfType<IMajorRecordGetter>().Where(b => ReachesWhere(b)));
                    bool go = true;
                    try
                    {
                        for (int i = 0; i < pending.Count; i++)
                        {
                            var p = pending[i];
                            if (filterBodies[i] is not { } filterBody)
                            {
                                unscannable++;
                                if (unscannableSamples.Count < 3)
                                    unscannableSamples.Add(faults.TryGetValue(p.winner!, out var f)
                                        ? $"{p.fk} — {f.Message}"
                                        : $"{p.fk} — winner '{p.winner}' did not yield the record on winner-source re-fetch");
                                continue;
                            }
                            if (!ScanRow(p.fk, p.depth, filterBody, p.source)) { go = false; break; }
                        }
                    }
                    finally { predicate?.ReleaseParents(); }
                    pending.Clear();
                    return go;
                }

                // One row's content filtering, on the body the FILTERS decide on.
                bool ScanRow(FormKey fk, int depth, IMajorRecordGetter filterBody, string? source)
                {
                    try
                    {
                        // Deleted records carry no body to scan (the rule is DeletedRecordRule's): the content
                        // filters cannot match one, so it is excluded as a clean non-match before the scan touches
                        // its body.
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (filterBody.EditorID is null || filterBody.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            return true;
                        // A deleted record only another where= term drops still counts toward the 'in' list members.
                        if (DeletedRecordRule.HasNoLiveBody(filterBody)
                            && (refSet is not null || predicate is { NeedsLiveBody: true }))
                        { if (refSet is null) predicate!.NoteMembers(filterBody); return true; }
                        // references= is a list with OR semantics, and BOTH arms come off one link read, so a record
                        // cannot be judged twice on two walks.
                        if (!ReferenceVerdict(filterBody, refSet, refNone, references, multiTarget && groups is null,
                                              out var hitTargets, out var lenientNote))
                        { NoteLenient(fk, lenientNote); return true; }
                        NoteLenient(fk, lenientNote);
                        if (predicate is not null && !predicate.Matches(filterBody))    // value filter on the same in-hand body, no extra fetch
                        {
                            if (predicate.FatalError is not null) return false;   // e.g. a numeric op against a non-numeric field — abort and surface it
                            return true;
                        }
                        total++;
                        if (groups is not null)                                   // group_by=: aggregate over all matches, no keys or prefill, no limit cap
                        {
                            var gk = groupBy == "type" ? RecordNaming.StripOverlay(filterBody.GetType().Name)
                                   : groupBy == "defined_in" ? FormIdToken.Plugin(fk.ModKey.FileName.String)
                                   : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";  // "winner"
                            groups[gk] = groups.GetValueOrDefault(gk) + 1;
                        }
                        else if (total > offset && keys.Count < limit)            // in-hand body → fill the summary for free; offset= skips the first N matches, and total already counts this one
                        {
                            keys.Add(fk);
                            sources.Add(source);                                  // the scoped plugin's display body; null means the winner. where_source=winner keeps the scoped source so "match on winner, show origin" works.
                            matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);   // parallel to keys, multi-target only
                            // The winner comes off the SAME view the scan runs on; type, editorid and name come
                            // from the body that MATCHED.
                            var rowWinner = view.ResolveWinner(fk)?.WinnerPlugin;
                            // A scoped body that is not the winner's owes no shared-EditorID note, so it carries no pin.
                            bool winnerBody = source is null || string.Equals(source, rowWinner, StringComparison.OrdinalIgnoreCase);
                            prefilled!.Add(RecordSummary.Of(fk, filterBody, rowWinner ?? "?", depth)
                                           .WithRuntime(view.RuntimeAddressOf(fk)) with { Pin = winnerBody ? pin : null });
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 3)
                            unscannableSamples.Add($"{FormIdToken.Of(fk)}{(source is null ? "" : $" in {source}")} — {ex.GetType().Name}: {ex.Message}");
                    }
                    return true;
                }
            }
            catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); } // plugin not in order / unknown type
            // The caller's own cancellation is not a scan fault and has to finish as one.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // Anything else escaping the stream still gets a named failure: the MCP layer's generic message must
            // never be the terminal diagnostic for a data failure.
            catch (Exception ex) { return CrossQueryOutcome.Fail($"scan aborted: {ex.GetType().Name}: {ex.Message}"); }
            finally { winnerSession?.Dispose(); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError); // typed predicate error — fail fast, named
        }
        else                                                                  // conflicts_only alone — index keys only, no body fetch
        {
            // The render lane fills the summaries through the door, once, before it renders.
            foreach (var fk in view.ConflictKeys())
            {
                ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                if (setFilter is not null && !setFilter.Contains(fk)) continue;   // the identity intersection on the index-only branch
                total++;
                if (groups is not null)
                {
                    // group_by=winner here does an index-level ResolveWinner per conflict key — a resolve, not a
                    // body parse, and unavoidable for the aggregate. Deliberate: accuracy over speed.
                    var gk = groupBy == "defined_in" ? FormIdToken.Plugin(fk.ModKey.FileName.String) : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";
                    groups[gk] = groups.GetValueOrDefault(gk) + 1;
                }
                else if (total > offset && keys.Count < limit) { keys.Add(fk); sources.Add(null); }   // no scoped plugin → display the winner; offset= skips the first N
            }
        }
        // Unscannable accounting: the count, the first few offenders with the reason, and what a caller can still do.
        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned and were skipped where the failure occurred "
              + "(Mutagen could not parse their content, or — under where_source=winner — a winner body the index named did not re-resolve on fetch): "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "")
              + $". Inspect one with {ToolNames.Records} formids=[the FormID] (per-field fault isolation applies).";
        // Records the scan DID filter, but only after reading around content Mutagen refused.
        if (lenientKeys.Count > 0)
            scanNote = scanNote is null ? LenientNote(lenientKeys.Count, lenientSamples)
                                        : scanNote + " " + LenientNote(lenientKeys.Count, lenientSamples);
        // Whole-plugin coverage gap: the scan carried on past a plugin it could not open, so the answer covers
        // the rest of the order but not that plugin's winners. Named once, whichever lane or gather met it.
        unreadablePlugins = unreadablePlugins.DistinctBy(u => u.PluginName, StringComparer.OrdinalIgnoreCase).ToList();
        if (unreadablePlugins.Count > 0)
        {
            string gap = $"coverage gap: {unreadablePlugins.Count} plugin(s) could not be read, so any record they win is missing from this answer: "
                       + string.Join("; ", unreadablePlugins.Select(u => u.Message));
            scanNote = scanNote is null ? gap : scanNote + " " + gap;
        }
        // A legal editorid= that matched nothing on the WINNER lane: the name may be carried by a losing copy the
        // winner renames, which a bare "0 matches" cannot say.
        bool nearMissShape = total == 0 && groups is null && !hasPlugins && !hasFormidSet && !conflictsOnly
                             && refSet is null && refNone is null && string.IsNullOrEmpty(editoridContains)
                             && types is { Count: > 0 };
        if (nearMissShape && predicate?.ExactEditorId is { } wantedEid
            && EditorIdNearMiss.Sentence(resolver, view, types, wantedEid, ct) is { } nearMiss)
            scanNote = scanNote is null ? nearMiss : scanNote + " " + nearMiss;
        // The scope's own gap leads: it says which of the plugins the caller named are not in this answer at all.
        if (scopeMissingNote is not null)
            scanNote = scanNote is null ? scopeMissingNote : scopeMissingNote + " " + scanNote;
        // The group_by= TALLY is not limit-capped (the render's table rows are), so Capped is a match-line concern only.
        var groupRows = groups?.Select(kv => new GroupCount(kv.Key, kv.Value))
                              .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        // Capped means matches exist BEYOND the returned window; the matches offset= skipped were asked to be, and
        // limit 0 (counts_only) asks for no window at all.
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && limit > 0 && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote,
                                     matched, groupRows, groupBy, definedIn ? string.Join(", ", plugins!) : null, offset,
                                     whereWinner, whereSourceNote)
               { Stamp = view.Stamp, Pin = pin,
                 ReverseIndexNote = reverseNote,
                 UnreadPlugins = unreadablePlugins.Select(u => u.PluginName).ToList(),
                 Unmatched = predicate?.Unmatched ?? Array.Empty<string>(),
                 UnmatchedGap = UnjudgedClause((view.Stamp?.ExcludedPlugins.Count ?? 0) + unreadablePlugins.Count, unscannable) };
    }

    /// <summary>Why the scan left records unjudged (a plugin the build or the scan could not read, a record it could not scan), as one clause for the unmatched note; null when it judged everything.</summary>
    static string? UnjudgedClause(int unreadablePlugins, int unscannable)
    {
        var parts = new List<string>(2);
        if (unreadablePlugins > 0) parts.Add($"{unreadablePlugins:N0} plugin(s) could not be read");
        if (unscannable > 0) parts.Add($"{unscannable:N0} record(s) could not be scanned");
        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    /// <summary>The schema's plan for the where= set over the scan's types, or over every record type without types=; null when the scan may run.</summary>
    string? SchemaPlanRefusal(IReadOnlyList<string>? typeTokens, FieldPredicateSet predicate)
    {
        var roots = new List<TypeSchema>();
        foreach (var token in typeTokens ?? Array.Empty<string>())
            foreach (var ts in _host.Rulebook.RecordTypesNamed(token))
                if (!roots.Contains(ts)) roots.Add(ts);
        return predicate.Plan(_host.Rulebook.PathWalk, roots.Count == 0 ? null : roots);
    }

    /// <summary>The one sentence a scan owes for records it filtered only after reading around content Mutagen
    /// refused: they are answers, not skips, and the gap is named because it cannot prove a non-match.</summary>
    static string LenientNote(int count, IReadOnlyList<string> samples) =>
        $"note: {count} record(s) were read leniently — part of their content is encoded in a way Mutagen refuses, "
        + "so the filters ran on what houseCARL could still decode: "
        + string.Join("; ", samples)
        + (count > samples.Count ? $"; and {count - samples.Count} more" : "")
        + $". Read one with {ToolNames.Records} formids=[the FormID] to see the marked row.";

    /// <summary>BOTH reference arms off ONE read of the record's links — the shared verdict every scan lane uses, so
    /// one FormID cannot be called leniently read by one arm and unscannable by the other.</summary>
    static bool ReferenceVerdict(IMajorRecordGetter body, HashSet<FormKey>? refSet, HashSet<FormKey>? refNone,
                                 IReadOnlyList<FormKey>? references, bool wantTargets,
                                 out List<FormKey>? hitTargets, out string? lenientNote)
    {
        hitTargets = null;
        lenientNote = null;
        if (refSet is null && refNone is null) return true;
        // A deleted record carries no live body: it can never match references=, and references_none= does not
        // exclude it either.
        if (DeletedRecordRule.HasNoLiveBody(body) || body is not IFormLinkContainerGetter) return refSet is null;

        // A struct visitor, so the walk allocates neither a closure nor a delegate per scanned record.
        var v = new ReferenceVisitor(refSet, refNone);
        lenientNote = RecordLinks.Walk(body, ref v);
        if (v.Excluded) return false;
        if (refSet is null) return true;
        if (v.Hits is not { Count: > 0 }) return false;
        if (wantTargets) hitTargets = references!.Where(v.Hits.Contains).Distinct().ToList();
        return true;
    }

    /// <summary>Both reference arms in one pass over a record's links: collect the wanted targets it hits, and stop
    /// the moment it hits an excluded one.</summary>
    struct ReferenceVisitor : RecordLinks.IVisitor
    {
        readonly HashSet<FormKey>? _wanted, _excluded;
        public HashSet<FormKey>? Hits;
        public bool Excluded;

        public ReferenceVisitor(HashSet<FormKey>? wanted, HashSet<FormKey>? excluded)
        {
            _wanted = wanted; _excluded = excluded;
            Hits = wanted is null ? null : new HashSet<FormKey>();
            Excluded = false;
        }

        public RecordLinks.Step Link(FormKey key)
        {
            if (_excluded is not null && _excluded.Contains(key)) { Excluded = true; return RecordLinks.Step.Stop; }
            if (_wanted is not null && _wanted.Contains(key)) Hits!.Add(key);
            return RecordLinks.Step.Continue;
        }
    }

    // ---- the off-order scan ----------------------------------------------------------------------------

    /// <summary>The off-order scan: the file's own records are the universe, with the same filter grammar the
    /// in-order scan runs.</summary>
    public CrossQueryOutcome OffOrderQuery(PoleInfo pole, IReadOnlyList<string>? typeSet,
        IReadOnlyList<FormKey>? references, string? editoridContains, IReadOnlyList<string>? scopePlugins,
        bool definedIn, IReadOnlyList<string>? where, int limit, string? groupBy, int offset,
        IReadOnlyList<FormKey>? formidSet, IReadOnlyList<ArtifactDemand>? artifactDemands,
        LoadOrderResolver.IndexView? pinnedView,
        IReadOnlyList<FormKey>? referencesNone,
        CancellationToken ct)
    {
        var resolver = _host.Resolver;
        var view = pinnedView ?? resolver.Capture();   // the caller's door build when it captured one — see CrossQuery
        var pin = new LoadOrderService.ViewPin(resolver, view);

        if (groupBy is not null)
        {
            groupBy = groupBy.Trim().ToLowerInvariant();
            if (groupBy is not ("winner" or "type" or "defined_in"))
                return CrossQueryOutcome.Fail($"group_by='{groupBy}' is not a known aggregation key — use 'winner', 'type', or 'defined_in'.");
        }
        if (offset < 0)
            return CrossQueryOutcome.Fail($"offset={offset} — offset must be >= 0.");
        // Same pair, same sentence, on the off-order scan: reachable through the service API only, as above.
        if (offset > 0 && groupBy is not null)
            return CrossQueryOutcome.Fail(ReadSentences.NoOffsetOnCountTable("group_by="));

        FieldPredicateSet? predicate = null;
        if (where is { Count: > 0 })
        {
            var (set, perr) = FieldPredicateSet.Parse(where, FormIdDoor.On(view).Parse);
            if (perr is not null) return CrossQueryOutcome.Fail(perr);
            predicate = set;
            // The containment map is built from the ACTIVE order only, and a file sharing a filename with an
            // active plugin resolves to the ACTIVE order's parent for the same FormID — so the scan would filter
            // this file's bodies against another file's edges. Refused by name rather than answered wrong.
            if (predicate.NeedsContainment)
                return CrossQueryOutcome.Fail(
                    $"'{ContainmentIndex.ParentToken}' reads the containment map built from the ACTIVE load order, and this scan streams an " +
                    $"out-of-load-order FILE whose own containment was never indexed — the answer would come from a different file's " +
                    $"edges. Drop source= to filter on containment in the active order, or filter this file on its own body instead.")
                    with { Stamp = view.Stamp };
        }
        foreach (var demand in (artifactDemands ?? Array.Empty<ArtifactDemand>()).Concat(
                     predicate?.ArtifactDemands ?? (IReadOnlyList<ArtifactDemand>)Array.Empty<ArtifactDemand>()))
            if (demand.Epoch != view.Epoch)
                return CrossQueryOutcome.Fail(ArtifactEpochMismatch(demand, view.Epoch)) with { Stamp = view.Stamp };

        IReadOnlyList<Type>? types;
        try { types = _host.Types.ResolveSet(typeSet); }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }

        if (predicate is not null && SchemaPlanRefusal(typeSet, predicate) is { } qerr)
            return CrossQueryOutcome.Fail(qerr) with { Stamp = view.Stamp };

        // The same split the in-order scan makes: a name the active order does not carry costs that name's share
        // of the scope, not the whole answer.
        string? scopeMissingNote = null;
        HashSet<string>? scopeSet = null;
        if (scopePlugins is { Count: > 0 })
        {
            var split = ScopeSplit.Of(view, scopePlugins);
            if (split.BlankRefusal is not null) return CrossQueryOutcome.Fail(split.BlankRefusal) with { Stamp = view.Stamp };
            if (split.Missing.Count > 0)
            {
                string meaning = " Over an out-of-load-order file the scope keeps the file's records that ACTIVE plugins also touch, so the scope names active plugins.";
                if (split.Present.Count == 0)
                    return CrossQueryOutcome.Fail(split.NothingToScanRefusal(view) + meaning) with { Stamp = view.Stamp };
                scopeMissingNote = split.ServedNote(view) + meaning;
            }
            scopeSet = new HashSet<string>(split.Present, StringComparer.OrdinalIgnoreCase);
        }

        ModKey fileKey;
        try { fileKey = ModKey.FromFileName(pole.Plugin); }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"'{pole.Plugin}' is not a valid plugin filename: {ex.Message}"); }

        ISkyrimModGetter ov;
        try { ov = MidCallChange.Open(pole.Path!, () => LoadOrderResolver.OpenOverlay(pole.Path!, string.IsNullOrEmpty(pole.DataDir) ? null : pole.DataDir)); }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"could not open '{pole.Path}' as a Skyrim plugin: {ex.Message}") with { Stamp = view.Stamp }; }

        var refSet = references is { Count: > 0 } ? new HashSet<FormKey>(references) : null;
        var refNone = referencesNone is { Count: > 0 } ? new HashSet<FormKey>(referencesNone) : null;
        bool multiTarget = references is { Count: >= 2 };
        var setFilter = formidSet is { Count: > 0 } ? new HashSet<FormKey>(formidSet) : null;

        var keys = new List<FormKey>();
        var sources = new List<string?>();
        List<string?>? matched = multiTarget ? new() : null;
        var prefilled = new List<RecordSummary>();
        Dictionary<string, int>? groups = groupBy is not null ? new(StringComparer.OrdinalIgnoreCase) : null;
        SeedRequestedTypes(groups, groupBy, types);
        int total = 0, unscannable = 0;
        var unscannableSamples = new List<string>();
        // Records read leniently here, keyed like the in-order lane so the count is records and not copies.
        var lenientKeys = new HashSet<FormKey>();
        var lenientSamples = new List<string>();
        LoadOrderResolver.OverlaySession? session = null;
        try
        {
            if (predicate is not null)
            {
                // The provenance and link terms read the ACTIVE order's resolution, the same binding discipline
                // the in-order scan keeps.
                session = (predicate.NeedsBodyResolution ? resolver.OpenSession() : null);
                var sess = session;
                predicate.BindResolution(
                    fk => view.ResolveWinner(fk)?.WinnerPlugin,
                    predicate.NeedsBodyResolution
                        ? fk =>
                        {
                            var w = view.ResolveWinner(fk);
                            return w is null ? null : view.GetRecord(sess!, w.Value.WinnerPlugin, fk);
                        }
                        : null,
                    parentOf: null);   // refused above: this file's containment is not in the active order's map
            }
            var seen = new HashSet<FormKey>();
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                var fk = rec.FormKey;
                if (!seen.Add(fk)) continue;
                try
                {
                    if (setFilter is not null && !setFilter.Contains(fk)) continue;
                    if (definedIn && fk.ModKey != fileKey) continue;
                    if (types is not null && !types.Any(t => t.IsInstanceOfType(rec))) continue;
                    if (scopeSet is not null)
                    {
                        var touchers = view.TouchingPlugins(fk);
                        if (touchers is null || !touchers.Any(scopeSet.Contains)) continue;
                    }
                    if (!string.IsNullOrEmpty(editoridContains)
                        && (rec.EditorID is null || rec.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    // A deleted record only another where= term drops still counts toward the 'in' list members.
                    if (DeletedRecordRule.HasNoLiveBody(rec)
                        && (refSet is not null || predicate is { NeedsLiveBody: true }))
                    { if (refSet is null) predicate!.NoteMembers(rec); continue; }
                    // The same one-read verdict the in-order lanes make.
                    bool keep = ReferenceVerdict(rec, refSet, refNone, references, multiTarget && groups is null,
                                                 out var hitTargets, out var lenientNote);
                    if (lenientNote is not null && lenientKeys.Add(fk) && lenientSamples.Count < 3) lenientSamples.Add(lenientNote);
                    if (!keep) continue;
                    if (predicate is not null && !predicate.Matches(rec))
                    {
                        if (predicate.FatalError is not null) break;
                        continue;
                    }
                    total++;
                    if (groups is not null)
                    {
                        var gk = groupBy == "type" ? RecordNaming.StripOverlay(rec.GetType().Name)
                               : groupBy == "defined_in" ? FormIdToken.Plugin(fk.ModKey.FileName.String)
                               : view.ResolveWinner(fk)?.WinnerPlugin ?? "(not in the active order)";
                        groups[gk] = groups.GetValueOrDefault(gk) + 1;
                    }
                    else if (total > offset && keys.Count < limit)
                    {
                        var w = view.ResolveWinner(fk);
                        keys.Add(fk);
                        sources.Add(pole.Plugin);
                        matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);
                        // No pin: an off-order body is never the copy the game loads, so it owes no shared-EditorID note.
                        prefilled.Add(RecordSummary.Of(fk, rec, w?.WinnerPlugin ?? "(not in the active order)", w?.OverrideDepth ?? 0)
                                      .WithRuntime(view.RuntimeAddressOf(fk)));
                    }
                }
                catch (Exception ex)
                {
                    unscannable++;
                    if (unscannableSamples.Count < 3)
                        unscannableSamples.Add($"{FormIdToken.Of(fk)} in {pole.Plugin} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        // The caller's own cancellation is not a fault in the file: it has to finish as one.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"file '{pole.Plugin}' could not be fully read — {ex.GetType().Name}: {ex.Message}") with { Stamp = view.Stamp }; }
        finally { session?.Dispose(); (ov as IDisposable)?.Dispose(); }
        if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError) with { Stamp = view.Stamp };

        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record(s) in '{pole.Plugin}' could not be scanned and were skipped where the failure occurred: "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "") + ".";
        // Records the scan DID filter, but only after reading around content Mutagen refused — the same sentence
        // the in-order lanes carry.
        if (lenientKeys.Count > 0)
            scanNote = (scanNote is null ? "" : scanNote + " ") + LenientNote(lenientKeys.Count, lenientSamples);
        // The scope's own gap leads, exactly as it does on the in-order scan.
        if (scopeMissingNote is not null)
            scanNote = scanNote is null ? scopeMissingNote : scopeMissingNote + " " + scanNote;
        var groupRows = groups?.Select(kv => new GroupCount(kv.Key, kv.Value))
                              .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && limit > 0 && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote, matched, groupRows, groupBy,
                                     definedIn ? pole.Plugin : null, offset, false, null)
               { Stamp = view.Stamp, Pin = pin,
                 Unmatched = predicate?.Unmatched ?? Array.Empty<string>(),
                 UnmatchedGap = UnjudgedClause(0, unscannable) };
    }

    /// <summary>Seat every type the scan NAMED in a group_by=type census at zero, so a requested type with no records reads as a 0 row rather than being absent from the table. No-op for the other count keys.</summary>
    static void SeedRequestedTypes(Dictionary<string, int>? groups, string? groupBy, IReadOnlyList<Type>? types)
    {
        if (groups is null || groupBy != "type") return;
        foreach (var name in TypeLookup.DisplayNames(types) ?? Array.Empty<string>()) groups.TryAdd(name, 0);
    }

    // ---- effect-chain resolver -------------------------------------------------------------------------

    /// <summary>Which SPEL/ENCH/ALCH/SCRL/INGR apply each of <paramref name="mgefs"/>, with each entry's magnitude, area and duration, off one carrier pass.</summary>
    public IReadOnlyList<EffectChainResult> ResolveEffectChains(IReadOnlyList<FormKey> mgefs, IReadOnlyList<string>? typesNarrow,
                                                                int limit, CancellationToken ct = default)
    {
        IReadOnlyList<EffectChainResult> All(string error) => mgefs.Select(_ => EffectChainResult.Fail(error)).ToList();
        IReadOnlyList<Type> scope;
        if (typesNarrow is { Count: > 0 })
        {
            var picked = new List<Type>();
            foreach (var ts in typesNarrow)
            {
                IReadOnlyList<Type> resolved;
                try { resolved = _host.Types.Resolve(ts.Trim()); }         // unknown type → named error, as on the scan
                catch (ArgumentException ex) { return All(ex.Message); }
                foreach (var t in resolved)
                {
                    if (!EffectChain.CarrierTypes.Contains(t))
                        return All(
                            $"type '{ts}' is not effect-bearing — the chain form scans only Spell/ObjectEffect/Ingestible/Scroll/Ingredient " +
                            "(SPEL/ENCH/ALCH/SCRL/INGR), the records that carry an Effects list. Drop it or pass one of those.");
                    if (!picked.Contains(t)) picked.Add(t);
                }
            }
            scope = picked;
        }
        else scope = EffectChain.CarrierTypes;

        return EffectChain.ResolveMany(_host.Resolver, mgefs, scope, limit, ct);
    }

    /// <summary><paramref name="source"/> up to the first fault, which ends it quietly; cancellation and out-of-memory still throw.</summary>
    static IEnumerable<T> UntilFault<T>(IEnumerable<T> source)
    {
        using var e = source.GetEnumerator();
        while (true)
        {
            try { if (!e.MoveNext()) yield break; }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { yield break; }
            yield return e.Current;
        }
    }

    /// <summary>A scan's '*parent' bulk fetch of a chunk's containing records; a winner that would not open goes to <paramref name="unreadable"/>.</summary>
    static Func<IReadOnlyCollection<FormKey>, IReadOnlyDictionary<FormKey, IMajorRecordGetter>> ParentGather(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        List<PluginUnreadableException> unreadable, CancellationToken ct) => wanted =>
    {
        var bodies = ContainmentIndex.GatherContainers(view, session, wanted, out var faults, ct);
        unreadable.AddRange(faults.Values);
        return bodies;
    };

    /// <summary>One universe key's outcome from the read door.</summary>
    readonly record struct SetVerdict(SetKind Kind, bool Keep, string? Note, Type? Type, string? Error);

    /// <summary>Which filter before where= drops a row: none, a body filter, or a body with nothing live to test.</summary>
    enum PreWhere { Pass, Filtered, NoLiveBody }

    /// <summary>What the read door made of one universe key; a key it never met keeps the array default, Missing.</summary>
    enum SetKind { Missing = 0, Dropped, Judged, Threw }
}
