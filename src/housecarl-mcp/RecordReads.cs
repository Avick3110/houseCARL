using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Everything the reads area takes from outside itself.</summary>
internal interface IReadHost : ILoadOrderHost
{
    /// <summary>A pinned index and the four MO2 roots in one <c>_gate</c> hold; <paramref name="afterPin"/> runs between the two.</summary>
    (LoadOrderService.ViewPin Pin, Mo2Roots Roots) CapturePinAndRoots(Action? afterPin);

    // Relayed from assets: the SkyPatcher replay door that takes its own asset capture, for the SkyPatcher overlay source.
    AssetLayers.SkyPatcherReplay? OpenSkyPatcherReplay(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                                       out string? draftRefusal, SkyPatcherDraft.Plan? draft,
                                                       SkyPatcherOverlay.WarningSink? draftWarnings);
}

/// <summary>The reads area: resolve, batch, poles, walk, cross query, info order.</summary>
internal sealed partial class RecordReads
{
    /// <summary>Every head member the reads area takes, and nothing else.</summary>
    readonly IReadHost _host;

    internal RecordReads(IReadHost host) => _host = host;

    /// <summary>Test seam: invoked in the pole lanes after the pin and before the roots; null in the product.</summary>
    internal Action? AfterReadPinForGuard;

    /// <summary>Resolve + read one record: the WINNER's body by default, or a named <paramref name="plugin"/>'s
    /// override; with <paramref name="conflictTree"/> also the ordered touching-plugin list. Every failure is a
    /// recoverable NAMED error, never a silent empty result; contracts in docs/architecture/read-engine.md.</summary>
    public ReadOutcome ResolveRead(FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                                   bool resolveNames, LinkMemo? linkMemo,
                                   string? containerHint,
                                   IReadOnlyList<int>? depths,
                                   IReadOnlyCollection<string>? countFields)
    {
        var resolver = _host.Resolver;
        var view = resolver.Capture();
        return ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint,
                           new ChildUnionMemo(), depths: depths, countFields: countFields)   // one named record: the union lane
               with { Stamp = view.Stamp, Pin = new LoadOrderService.ViewPin(resolver, view) };   // stamped and pinned here, off the view actually read
    }

    /// <summary>The read body, answered entirely off ONE captured view, so a freshness rebuild landing mid-read
    /// cannot make a record's reported winner disagree with its own touching list; the <see cref="LoadOrderService.ViewPin"/> rule
    /// is in docs/architecture/read-engine.md.</summary>
    ReadOutcome ResolveRead(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                            FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                            bool resolveNames = false, LinkMemo? linkMemo = null,
                            string? containerHint = ReadEngine.DepthExpandHint,
                            ChildUnionMemo? unionMemo = null,
                            LoadOrderResolver.OverlaySession? batchSession = null,
                            IReadOnlyList<int>? depths = null,
                            IMajorRecordGetter? prefetched = null,
                            IReadOnlyCollection<string>? countFields = null)
    {
        // An explicitly-requested plugin excluded this session is said so, rather than falling through to a
        // misleading "does not define this record".
        if (plugin is not null && view.ExcludedPlugins.TryGetValue(plugin, out var pWhy))
            return ReadOutcome.Fail(fk, $"Plugin '{plugin}' was excluded from this session: {pWhy}");

        // A plugin not in the order at all is its own failure mode: GetRecord returns null for it, and falling
        // through would render a false "does not define this record", which reads as "my write was lost" and
        // invites re-issuing the ops. houseCARL does not read disabled plugins off disk.
        if (plugin is not null && !view.ContainsPlugin(plugin))
        {
            // ExplainAbsence, not AbsenceClause: the latter returns a non-empty string for a typo too, so its
            // length cannot distinguish "a cause was stated" from "a spelling was guessed".
            var cause = view.ExplainAbsence(plugin);
            var why = cause is not null ? " " + cause : view.NameSuggestion(plugin);
            // The write-verify guidance is a fact about the tool, not a guess about the cause, so it is
            // unconditional; only the posture line, which would contradict a stated cause, is conditional.
            var verify = $" To verify a write BEFORE enabling, use the write call's own read-back (readback=true " +
                         $"returns the whole written record). If a prior write into '{plugin}' reported success, the edits " +
                         "DID land — do not re-issue them (re-running list Adds would duplicate entries).";
            var tail = (cause is not null
                ? ""
                : " houseCARL reads load-order truth only and does not open disabled " +
                  "plugins off disk. If this is a freshly written houseCARL patch, it isn't enabled yet: enable it in " +
                  "MO2, then re-read.") + verify;
            return ReadOutcome.Fail(fk,
                $"Plugin '{plugin}' is not in the load order ({view.PluginCount} plugins; names match the plugin FILENAME " +
                "incl. .esp/.esm, case-insensitively)." + why + tail);
        }

        var winner = view.ResolveWinner(fk);
        if (winner is null) return ReadOutcome.Fail(fk, UnresolvedFormId(view, fk));

        var source = plugin ?? winner.Value.WinnerPlugin;
        // A session is an overlay CACHE, and the union opens a body per touching plugin, so a batch that gave one
        // in pays each plugin's mmap once for the whole call. Only what this call opened is disposed here.
        using var ownSession = batchSession is null ? resolver.OpenSession() : null;
        var session = batchSession ?? ownSession!;
        // A body the caller already gathered for THIS row and THIS source is used as it stands; without one this
        // is the per-record whole-overlay seek.
        var rec = prefetched ?? view.GetRecord(session, source, fk);       // excluded-check pinned to the same view the winner came from
        if (rec is null)
        {
            if (plugin is null)
                return ReadOutcome.Fail(fk, $"Winner '{winner.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch — a load-order inconsistency.");
            // An untouched record under a named plugin refuses by naming the actual touchers: a bare "does not
            // define" reads as "my write was lost".
            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
            return ReadOutcome.Fail(fk,
                $"Plugin '{plugin}' does not touch {FormIdToken.Of(fk)} — it has no version of this record. " +
                $"Touched by (load order, winner last): {string.Join(", ", touchers)}.");
        }

        // materialise while the session (overlay) is open; the *parent hop climbs the index's containment map and
        // fetches the containing record's winner body through the same session
        var hop = ContainmentIndex.ReadHop(view, session);
        var record = ReadEngine.ReadFields(rec, fields, depth, containerHint, hop, depths);
        record = AnnotateOwnedChildContent(record, rec, view, session, fk, source, unionMemo, out var childFields, hop, countFields);   // the additive union (or the index-only note), display-only
        if (resolveNames) record = AnnotateLinks(record, view, session, linkMemo ?? new());   // identity of every FormLink token, display-only, on the same open session
        var touching = conflictTree ? view.TouchingPlugins(fk) : null;
        return new ReadOutcome(fk, record, source, winner.Value.WinnerPlugin, winner.Value.OverrideDepth, touching, null)
               { OwnedChildFields = childFields }.WithRuntime(view.RuntimeAddressOf(fk));
    }

    /// <summary>resolve_names: annotate every field that RENDERS a form reference with its target's load-order
    /// identity, hung on <see cref="FieldValue.Link"/> — DISPLAY-ONLY, never touching the round-trip Token.
    /// Type-agnosticism and the unresolved answer are in docs/architecture/read-engine.md.</summary>
    static RecordFields AnnotateLinks(RecordFields rf, LoadOrderResolver.IndexView view,
                                      LoadOrderResolver.OverlaySession session, LinkMemo memo)
    {
        List<FieldValue>? rebuilt = null;
        for (int i = 0; i < rf.Fields.Count; i++)
        {
            var f = rf.Fields[i];
            // Whichever carrier the line RENDERED its reference on: the round-trip token, or the FormID a
            // container element's summary note spelled. One rule, one shape of value.
            var rendered = f.HasValue ? f.Token : f.NoteRef;
            if (rendered is { } tok && FormKey.TryFactory(tok, out var fk) && !fk.IsNull)
            {
                rebuilt ??= new List<FieldValue>(rf.Fields);
                rebuilt[i] = f with { Link = ResolveRefOne(view, session, fk, memo) };
            }
        }
        return rebuilt is null ? rf : rf with { Fields = rebuilt };
    }

    /// <summary>On a read of a field that OWNS CHILD RECORDS, state the ADDITIVE UNION the game assembles there —
    /// every distinct child every touching plugin declares, keyed by FormID. The field's own VALUE stays the read
    /// body's own list, because that is what a write addresses by index.</summary>
    /// <param name="parentOf">The read's own <c>*parent</c> hop, so a row read off a CONTAINING record is judged
    /// against that record and the two spellings of one question cannot disagree.</param>
    static RecordFields AnnotateOwnedChildContent(RecordFields rf, IMajorRecordGetter body,
                                                  LoadOrderResolver.IndexView view,
                                                  LoadOrderResolver.OverlaySession session, FormKey fk, string source,
                                                  ChildUnionMemo? memo,
                                                  out IReadOnlyDictionary<string, ChildUnion?>? annotated,
                                                  Func<IMajorRecordGetter, (IMajorRecordGetter? Parent, string? Why)>? parentOf = null,
                                                  IReadOnlyCollection<string>? countFields = null)
    {
        annotated = null;
        // Group this read's rows by how many '*parent' hops their path opens with: each group is judged against
        // the record its rows were actually read off.
        Dictionary<int, List<int>>? byHops = null;
        for (int i = 0; i < rf.Fields.Count; i++)
        {
            var segs = rf.Fields[i].Path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var (hops, err) = ContainmentIndex.SplitHops(segs, rf.Fields[i].Path);
            if (err is not null) continue;   // a misspelled hop already carries its own refusal note
            (byHops ??= new Dictionary<int, List<int>>()).TryGetValue(hops, out var g);
            (byHops[hops] = g ?? new List<int>()).Add(i);
        }
        if (byHops is null) return rf;

        List<FieldValue>? rebuilt = null;
        Dictionary<string, ChildUnion?>? map = null;
        foreach (var (hops, rows) in byHops)
        {
            // Climb to the record this group's rows were read on; a hop that cannot be taken annotates nothing.
            var on = body; var onKey = fk;
            bool reached = true;
            for (int h = 0; h < hops && reached; h++)
            {
                var (parent, _) = parentOf?.Invoke(on) ?? (null, null);
                if (parent is null) reached = false; else { on = parent; onKey = parent.FormKey; }
            }
            if (!reached) continue;

            // Empty for all but three record types, so this is where the overwhelming majority of reads leave.
            var owning = OwnedChildContent.Fields(on);
            if (owning.Count == 0) continue;

            // Which of the lines THIS read produced are those fields — matched on the path BELOW the hops.
            List<(int Row, string Field)>? hits = null;
            foreach (var i in rows)
            {
                var below = hops == 0 ? rf.Fields[i].Path
                          : string.Join(".", rf.Fields[i].Path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[hops..]);
                if (owning.ContainsKey(below)) (hits ??= new List<(int, string)>()).Add((i, below));
            }
            if (hits is null) continue;

            // Narrowed to the fields this read emitted: the union opens a body per touching plugin.
            var wanted = new Dictionary<string, OwnedChildShape>(hits.Count, StringComparer.Ordinal);
            foreach (var (row, field) in hits)
                // Matched on the row's WHOLE read path, hops and all — the spelling countFields is keyed by.
                if (countFields?.Contains(rf.Fields[row].Path) != true) wanted[field] = owning[field];

            // A hopped group was read off the CONTAINING record's winner body, so that is the subject the union
            // is assembled against; a hopless group is the read's own source.
            var onSource = hops == 0 ? source : view.ResolveWinner(onKey)?.WinnerPlugin;
            if (onSource is null) continue;

            IReadOnlyDictionary<string, ChildUnion>? unions = null;
            if (memo is not null && wanted.Count > 0)
                unions = memo.Union(onKey, () => OwnedChildUnion.Compute(view, session, onKey, onSource, on, wanted));
            // Sole toucher: its own body IS the whole story, and the index-only tier has nothing to say about
            // plugins that are not there.
            var touchers = view.TouchingPlugins(onKey);
            if (unions is null && touchers is not { Count: > 1 }) continue;
            var others = touchers!.Count - 1;

            rebuilt ??= new List<FieldValue>(rf.Fields);
            // The ANNOTATED paths and their unions travel with the outcome, because the render decides its
            // response-level clause off the fields it actually emitted.
            map ??= new Dictionary<string, ChildUnion?>(StringComparer.Ordinal);
            foreach (var (i, field) in hits)
            {
                // A field the union lane was ASKED for must be in the union it computed: a missing key is a fault
                // to throw on, not an index-only note.
                var u = unions is not null && wanted.ContainsKey(field) ? unions[field] : null;
                // These fields are containers and owned records; the other producers of Display fire on [Flags]
                // enum leaves and bytes leaves alone, so there is no annotation here to displace.
                rebuilt[i] = rebuilt[i] with { Display = u is null ? ReadSentences.NotReadNote(others) : ReadSentences.UnionNote(u) };
                map[rebuilt[i].Path] = u;
            }
        }
        if (rebuilt is null) return rf;
        annotated = map;
        return rf with { Fields = rebuilt };
    }

    /// <summary>One CALL's assembled unions, keyed by record, so a formid named twice in one batch pays
    /// once.</summary>
    internal sealed class ChildUnionMemo
    {
        readonly Dictionary<FormKey, IReadOnlyDictionary<string, ChildUnion>?> _byRecord = new();

        internal IReadOnlyDictionary<string, ChildUnion>? Union(
            FormKey fk, Func<IReadOnlyDictionary<string, ChildUnion>?> compute)
        {
            if (!_byRecord.TryGetValue(fk, out var u)) _byRecord[fk] = u = compute();
            return u;
        }
    }

    /// <summary>Why a FormID resolved to nothing, naming which of the three causes it is: the defining plugin was
    /// excluded, it is not in the order, or it IS in the order and defines no such record. The ESL-compaction
    /// clause is stated only where the index says that plugin is light-flagged — pinned by
    /// <c>RuntimeFormIdTests.AMissingRecordInAnEslFlaggedPluginIsToldAboutCompaction</c> and
    /// <c>RecordsRemedyRepairTests.AndDoesNotBlameEslCompactionOnAPluginThatIsNotEslFlagged</c>.</summary>
    internal static string UnresolvedFormId(LoadOrderResolver.IndexView view, FormKey fk,
                                   Dictionary<string, string>? absenceMemo = null)
    {
        var defining = FormIdToken.Plugin(fk.ModKey.FileName.String);
        if (view.ExcludedPlugins.TryGetValue(defining, out var why))
            return $"FormID {FormIdToken.Of(fk)} is not resolvable: its plugin '{defining}' was excluded from this session: {why}";
        if (view.ContainsPlugin(defining))
        {
            var esl = view.IsLightFlagged(defining)
                ? $" '{defining}' IS ESL-flagged, and an ESL-flagged edition compacts its records into 0x800+, so this " +
                  "is commonly a FormID taken from a different (uncompacted) edition of the same mod."
                : "";
            return $"Plugin '{defining}' IS in the load order, but defines no record {fk.ID:X6} — and no other plugin " +
                   $"overrides it either.{esl} List what it actually defines with housecarl_records " +
                   $"plugins={{\"names\": [\"{defining}\"], \"defined_in\": true}}.";
        }
        // One clause, one explainer call, and the spelling hint only where nothing better can be said.
        if (absenceMemo is null || !absenceMemo.TryGetValue(defining, out var tail))
        {
            var absence = view.AbsenceClause(defining, out var cause);
            var hint = cause is null ? " (names match the plugin FILENAME incl. .esp/.esm, case-insensitively)" : "";
            tail = hint + "." + absence;
            if (absenceMemo is not null) absenceMemo[defining] = tail;
        }
        return $"FormID {FormIdToken.Of(fk)} is not present in the load order ({view.PluginCount} plugins): its plugin '{defining}' " +
               $"is not in the order{tail}";
    }

    /// <summary>How deep the conflict diff reads each touching body — its reach and its bound are in
    /// docs/architecture/read-engine.md.</summary>
    internal const int ConflictDiffDepth = 16;

    /// <summary>A header-only summary for one record — the compact one-line-per-match view a cross-plugin scan
    /// uses by default. One winner-body fetch; holds nothing.</summary>
    public RecordSummary ResolveSummary(FormKey fk)
    {
        var resolver = _host.Resolver;
        return ResolveSummary(resolver, resolver.Capture(), fk);   // one capture per summary: winner, depth and fetch from one build
    }

    static RecordSummary ResolveSummary(LoadOrderResolver resolver, LoadOrderResolver.IndexView view, FormKey fk)
    {
        var w = view.ResolveWinner(fk);
        if (w is null) return new RecordSummary(fk, "?", null, "?", 0, $"{FormIdToken.Of(fk)} not in the load order");
        using var session = resolver.OpenSession();
        var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
        if (body is null)
            return new RecordSummary(fk, "?", null, w.Value.WinnerPlugin, w.Value.OverrideDepth,
                $"winner '{w.Value.WinnerPlugin}' did not yield {FormIdToken.Of(fk)} on fetch");
        return new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                 w.Value.WinnerPlugin, w.Value.OverrideDepth, null)
               .WithRuntime(view.RuntimeAddressOf(fk));
    }

    // ---- pinned per-match fills ------------------------------------------------------------------------

    /// <summary>The cross-query detail fill, pinned to the scan's build when the outcome carries one; without the pin
    /// each row re-gates and re-captures.</summary>
    /// <param name="session">The render's one overlay session; <paramref name="prefetched"/> is this row's body when
    /// the caller gathered it in bulk.</param>
    internal ReadOutcome ResolveReadOn(CrossQueryOutcome q, FormKey fk, string? plugin, IReadOnlyList<string>? fields,
                                       bool conflictTree, int depth, bool resolveNames,
                                       LinkMemo? linkMemo,
                                       string? containerHint,
                                       IReadOnlyList<int>? depths,
                                       LoadOrderResolver.OverlaySession? session,
                                       IMajorRecordGetter? prefetched,
                                       IReadOnlyCollection<string>? countFields)
        => q.Pin is { } p
            ? ResolveRead(p.Resolver, p.View, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint,
                          batchSession: session, depths: depths, prefetched: prefetched, countFields: countFields)
              with { Stamp = p.View.Stamp, Pin = p }
            : ResolveRead(fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint, depths, countFields);

    /// <summary>The summary twin of <see cref="ResolveReadOn"/> — the conflicts-only lazy fill.</summary>
    internal RecordSummary ResolveSummaryOn(CrossQueryOutcome q, FormKey fk)
        => q.Pin is { } p ? ResolveSummary(p.Resolver, p.View, fk) : ResolveSummary(fk);

    /// <summary>What a folded tree carries besides its nodes: the winner's identity, and the precise owned-child
    /// tier for the whole tree — empty when the visitor stopped the walk early, since a partial tier would read as
    /// a claim about providers never looked at.</summary>
    internal sealed record TreeFill(string? Type, string? EditorId, IReadOnlyList<ChildDeclarers> ChildDeclarers);

    /// <summary>The conflict-tree fill off a pinned build, so the tree's membership and the response's epoch stamp
    /// name the same build.</summary>
    internal TreeFill? FoldTreePinned(LoadOrderService.ViewPin p, FormKey fk, IReadOnlyList<string>? fields,
                                      Func<string, RecordFields, bool, bool> onNode)
    {
        using var session = p.Resolver.OpenSession();
        return FoldTreeChunkPinned(p, session, new[] { fk }, fields,
                                   (_, _, plugin, read, isWinner) => onNode(plugin, read, isWinner))[0];
    }

    /// <summary>The whole tree materialised — every provider's fields at once, in priority order with the winner
    /// last. For a caller that genuinely needs the providers side by side; the render does not.</summary>
    internal ConflictTreeView? ResolveTreePinned(LoadOrderService.ViewPin p, FormKey fk, IReadOnlyList<string>? fields)
    {
        var nodes = new List<ConflictNodeView>();
        var fill = FoldTreePinned(p, fk, fields, (plugin, read, _) => { nodes.Add(new ConflictNodeView(plugin, read)); return true; });
        if (fill is null) return null;
        nodes.Reverse();                                        // the fold reads winner first; the tree reads winner last
        return new ConflictTreeView(nodes, fill.ChildDeclarers);
    }

    /// <summary>The best-effort display Name of a record body, reflection-generic via Mutagen's
    /// <c>INamedGetter</c> aspect, so it inherits coverage from the model; null for a type with no Name.</summary>
    static string? ReadDisplayName(IMajorRecordGetter body) =>
        body is INamedGetter named && !string.IsNullOrEmpty(named.Name) ? named.Name : null;

    /// <summary>The name-resolution caches one lane carries: a target's identity per FormKey, and the absence tail
    /// per missing plugin name. Both are per-lane, never global — they describe ONE captured build.</summary>
    public sealed class LinkMemo
    {
        /// <summary>Resolved identity per target, so a keyword recurring across a batch resolves once.</summary>
        public Dictionary<FormKey, ResolvedRef> Refs { get; } = new();

        /// <summary>The unresolved-FormID tail per missing plugin, so the absence explainer runs once per plugin
        /// rather than once per dangling FormKey.</summary>
        public Dictionary<string, string> Absences { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve ONE FormKey to its load-order identity off a captured view + open session, memoised so a
    /// target recurring across a batch resolves once.</summary>
    static ResolvedRef ResolveRefOne(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     FormKey fk, LinkMemo memo)
    {
        if (memo.Refs.TryGetValue(fk, out var hit)) return hit;
        ResolvedRef result;
        var w = view.ResolveWinner(fk);
        if (w is null)
            result = EngineImplicit.TryDescribe(fk, out var eiType, out var eiEditorId)
                ? new ResolvedRef(FormIdToken.Of(fk), Resolved: true, Type: eiType, EditorId: eiEditorId, Winner: "<engine>")   // engine-implicit: hardcoded, real, defined by no plugin
                // Valid FormKey, no active plugin defines it; the reason is the three-cause sentence every other
                // lane states.
                : new ResolvedRef(FormIdToken.Of(fk), Resolved: false, Error: UnresolvedFormId(view, fk, memo.Absences));
        else
        {
            var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
            result = body is null
                ? new ResolvedRef(FormIdToken.Of(fk), Resolved: false, Winner: w.Value.WinnerPlugin)   // winner named but the fetch didn't yield it
                : new ResolvedRef(FormIdToken.Of(fk), Resolved: true, Type: RecordNaming.StripOverlay(body.GetType().Name),
                                  EditorId: body.EditorID, Name: ReadDisplayName(body), Winner: w.Value.WinnerPlugin);
        }
        memo.Refs[fk] = result;
        return result;
    }

    /// <summary>Bulk name resolution: a list of FormIDs to their load-order identity in one call over one captured
    /// view, memoised across the batch.</summary>
    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids) => ResolveRefs(formids, out _);

    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids, out OrderStamp epoch)
        => ResolveRefs(formids, null, out epoch, out _);

    /// <summary>The artifact-epoch mismatch refusal — one wording for every consuming lane, naming both epochs and
    /// the two legitimate next moves.</summary>
    internal static string ArtifactEpochMismatch(ArtifactDemand d, string current) =>
        $"artifact '{d.Path}' was captured at epoch={d.Epoch}, but the CURRENT load-order build is epoch={current} — " +
        (LoadOrderResolver.IsCurrentEpochFormat(d.Epoch)
            ? "the load order changed since the artifact was written, so its rows may resolve differently now. "
            : "that epoch was written by an OLDER houseCARL, before the fingerprint formula changed, so the two " +
              "cannot be compared: your load order may be untouched, and this build still cannot tell. ") +
        "Re-run the producing query (with to_file= to re-materialize) against the current build; the old file stays " +
        "readable with your own tools as an honest snapshot of ITS build. There is deliberately no stale-override switch.";

    /// <summary>As above, also handing back the captured build's <paramref name="epoch"/> fingerprint — the batch is
    /// one capture.</summary>
    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids, ArtifactDemand? artifactDemand,
                                                  out OrderStamp epoch, out string? artifactRefusal)
    {
        artifactRefusal = null;
        var resolver = _host.Resolver;
        var view = resolver.Capture();                  // one build for the whole batch
        epoch = view.Stamp;
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            artifactRefusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            return Array.Empty<ResolvedRef>();
        }
        using var session = resolver.OpenSession();
        var memo = new LinkMemo();
        var results = new List<ResolvedRef>(formids.Count);
        foreach (var raw in formids)
        {
            var t = raw?.Trim() ?? "";
            FormKey fk;
            try { fk = view.ParseFormId(t); }
            catch (Exception ex) { results.Add(new ResolvedRef(t, Resolved: false, Error: $"bad FormID: {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); continue; }
            results.Add(ResolveRefOne(view, session, fk, memo));
        }
        return results;
    }

    // ---- pairwise record diff --------------------------------------------------------------------------

    /// <summary>If <paramref name="path"/> is the EXACT file the active order loads for its filename, the plugin name
    /// the order knows it by; else null.</summary>
    internal static string? ActiveNameForPath(LoadOrderResolver.IndexView view, string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim()); } catch { return null; }
        var name = Path.GetFileName(full);
        if (name.Length == 0 || !view.ContainsPlugin(name)) return null;
        // An excluded plugin is still in the name table and the active lane can only refuse it; reading its file
        // directly is the escape hatch, so a path to one must keep taking the off-order lane.
        if (view.ExcludedPlugins.ContainsKey(name)) return null;
        var active = view.PluginPath(name);
        return !string.IsNullOrEmpty(active) && LoadOrderService.SamePluginFile(active, full) ? name : null;
    }

    /// <summary>One side of a housecarl_diff_record comparison: the plugin named, WHERE its version was found,
    /// whether it is in the active order, and the record identity it carries.</summary>
    public sealed record DiffPole(string Plugin, string Where, bool InOrder, string? RecordType, string? EditorId)
    {
        /// <summary>What tells this pole apart from a same-named one on the other arm — the mod folder it was read
        /// out of, or "off-order". Set on the off-order arm only.</summary>
        internal string? Qualifier { get; init; }

        /// <summary>The pole's label for a render that shows both sides, qualified only when the other side
        /// carries the same filename.</summary>
        public string LabelVersus(string? otherPlugin) =>
            Qualifier is { } q && string.Equals(Plugin, otherPlugin, StringComparison.OrdinalIgnoreCase)
                ? $"{Plugin} ({q})" : Plugin;
    }

    // ---- batch ------------------------------------------------------------------------------------------
    /// <summary>Resolve and read many records in one call: one <see cref="ReadOutcome"/> per input, in input
    /// order, and a bad or absent formid is a per-item error that does not fail the batch. Under
    /// <paramref name="plugin"/> every formid is read as that plugin's override, not the load-order winner.</summary>
    /// <summary>Resolve and read many records in one call.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                                                   bool resolveNames, string? plugin,
                                                   string? containerHint,
                                                   IReadOnlyList<int>? depths,
                                                   CancellationToken ct,
                                                   IReadOnlyList<Type>? getterTypes,
                                                   IReadOnlyCollection<string>? countFields)
        => ResolveBatch(formids, fields, conflictTree, depth, resolveNames, plugin, null, out _, out _, containerHint, depths, ct, getterTypes, countFields);

    /// <summary>The artifact-aware overload: <paramref name="artifactDemand"/> is checked against THIS capture's
    /// epoch — the same build that would answer — and a mismatch hands back <paramref name="artifactRefusal"/> and
    /// <paramref name="refusalEpoch"/> with no rows, because a refusal that consulted a build renders stamped
    /// with it.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                                                   bool resolveNames, string? plugin, ArtifactDemand? artifactDemand,
                                                   out string? artifactRefusal, out OrderStamp? refusalEpoch,
                                                   string? containerHint,
                                                   IReadOnlyList<int>? depths,
                                                   CancellationToken ct,
                                                   IReadOnlyList<Type>? getterTypes,
                                                   IReadOnlyCollection<string>? countFields)
    {
        artifactRefusal = null; refusalEpoch = null;
        var resolver = _host.Resolver;           // build/refresh once for the batch
        var view = resolver.Capture();          // one build for every item — the whole batch is one logical operation
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            artifactRefusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new LoadOrderService.ViewPin(resolver, view);
        var linkMemo = resolveNames ? new LinkMemo() : null;   // one link-resolution cache across the whole batch
        var unionMemo = new ChildUnionMemo();                  // the caller NAMED these records: the union lane, one assembly per record
        // One overlay cache for the whole batch.
        using var batchSession = resolver.OpenSession();
        var outcomes = new List<ReadOutcome>(formids.Count);
        // Every FormID is parsed up front so the bodies can be gathered a CHUNK of rows at a time — one
        // enumeration per source plugin — and the scan's body forms come through here, so this lane and the scan
        // render lane cost the same per row.
        var keys = new FormKey[formids.Count];
        var parseErrors = new string?[formids.Count];
        for (int i = 0; i < formids.Count; i++)
        {
            try { keys[i] = view.ParseFormId(formids[i]); }
            catch (Exception ex) { parseErrors[i] = $"bad FormID '{formids[i]}': {ex.Message}"; }
        }
        BodyPrefetch.Chunk? chunk = null;
        int chunkStart = -1;
        for (int i = 0; i < formids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();   // a client that aborted stops the batch inside one record
            if (parseErrors[i] is { } perr) { outcomes.Add(ReadOutcome.Fail(default, perr)); continue; }
            int start = BodyPrefetch.ChunkStart(i);
            if (start != chunkStart)
            {
                chunkStart = start;
                chunk = BodyPrefetch.Gather(view, batchSession, keys, start, Math.Min(start + BodyPrefetch.ChunkRows, keys.Length),
                                            _ => plugin, getterTypes, ct);
            }
            var fk = keys[i];
            var body = chunk?.Body(fk);   // the plugin is walked here, on the first row of the chunk that wants it
            outcomes.Add(ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint, unionMemo, batchSession, depths, body, countFields)
                         with { Stamp = view.Stamp, Pin = pin });   // the batch's one build, stamped and pinned per item
        }
        return outcomes;
    }

    // ---- `records`: the one-pole batch (source=named, wherever the plugin lives) -----------------------

    /// <summary>How a `records` source= pole resolved: active in the order, or an on-disk file outside it.</summary>
    public sealed record PoleInfo(string Plugin, string Where, bool InOrder, bool EpochCoversPole)
    {
        /// <summary>The on-disk locate result for the off-order arm, carried so the consuming lane need not
        /// re-run the locate.</summary>
        internal string? Path { get; init; }

        /// <summary>The layer the off-order copy came from, carried as a fact rather than re-derived from
        /// <see cref="Where"/>.</summary>
        internal string? Layer { get; init; }

        /// <summary>The Data folder of the roots the off-order copy was located under, for its localized strings.</summary>
        internal string? DataDir { get; init; }

        /// <summary>The epoch of the build the arm was judged against, so a load-order change between probe and
        /// dispatch surfaces as a loud retry refusal.</summary>
        public OrderStamp? Stamp { get; init; }

        /// <summary>The FILENAME is in the order even though THIS COPY is not — a shadowed copy addressed by
        /// {file, mod}.</summary>
        public bool NameActive { get; init; }

        /// <summary>That build's fingerprint, read through the stamp.</summary>
        public string? Epoch => Stamp?.Epoch;
    }

    /// <summary>Resolve a `records` source= pole against ONE captured view: active in the order, else located on disk
    /// across the whole install under <paramref name="roots"/>, taken in the view's hold.</summary>
    (PoleInfo? Pole, string? Error) ResolvePoleArm(LoadOrderResolver.IndexView view, Mo2Roots roots, string plugin, string? mod)
    {
        // Judged on the argument as given: the rewrite below turns a path into a bare filename, which would flip
        // a path pole into the mod= lane.
        bool namesMod = !string.IsNullOrWhiteSpace(mod) && !LoadOrderService.LooksLikePath(plugin);

        // A pole addressed by path that IS the active order's file resolves back to its plugin name.
        if (LoadOrderService.LooksLikePath(plugin) && ActiveNameForPath(view, plugin) is { } activeName) plugin = activeName;

        bool activeFilename = view.ContainsPlugin(plugin);
        if (!namesMod && activeFilename)
            return (new PoleInfo(plugin, "active in the load order", InOrder: true, EpochCoversPole: true), null);

        var comp = Mo2LoadOrder.ReadComposition(roots.ProfileDir);
        var loc = LoadOrderService.LocatePluginFileOnDisk(comp, roots, plugin, mod);
        if (loc.Error is not null)
            // A pole found in neither place names both places searched; when the filename IS active, the named
            // mod folder is the only place searched.
            return (null, activeFilename
                ? $"source '{plugin}': {loc.Error} The filename IS active in the load order — drop mod= to read the copy the game loads."
                : $"source '{plugin}' resolves in NEITHER place the one-pole rule searches: it is not ACTIVE in " +
                  $"the load order ({view.PluginCount} plugins), and on disk {loc.Error}");
        if (loc.Ambiguous is not null)
            return (null, $"source '{plugin}' is not active in the load order and its filename is provided by SEVERAL mod " +
                          $"folders on disk: {string.Join(", ", loc.Ambiguous.Select(h => $"'{h.Where}'"))} — disambiguate " +
                          "with source={\"file\": \"" + plugin + "\", \"mod\": \"<mod folder>\"}.");
        // The named copy IS the copy the game loads, so it is read out of the order like any other active pole.
        if (activeFilename && loc.Enabled)
            return (new PoleInfo(plugin, "active in the load order", InOrder: true, EpochCoversPole: true), null);
        var poleWhere = $"OUT-OF-LOAD-ORDER ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})";
        return (new PoleInfo(plugin, poleWhere, InOrder: false, EpochCoversPole: false)
                { Path = loc.Path, Layer = loc.WhereNamesLayer ? loc.Where : null, DataDir = roots.DataDir, NameActive = activeFilename }, null);
    }

    /// <summary>The tool-layer probe: WHICH arm would this source= pole resolve to.</summary>
    public PoleInfo? ProbeSourceArm(string plugin, string? mod, out string? error)
    {
        var (pin, roots) = _host.CapturePinAndRoots(AfterReadPinForGuard);
        var view = pin.View;
        var (pole, err) = ResolvePoleArm(view, roots, plugin, mod);
        error = err;
        return pole is null ? null : pole with { Stamp = view.Stamp };
    }

    /// <summary>The list-driven `records` read under a named source pole: resolve the pole once — active in the
    /// order, else a file on disk in an enabled, disabled or unlisted mod folder — and read every FormID's version
    /// from it off ONE captured build.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatchFromPole(
        IReadOnlyList<string> formids, string plugin, string? mod,
        IReadOnlyList<string>? fields, int depth, bool resolveNames,
        ArtifactDemand? artifactDemand,
        out PoleInfo? pole, out string? refusal, out OrderStamp? refusalEpoch,
        string? containerHint,
        IReadOnlyList<int>? depths,
        CancellationToken ct,
        IReadOnlyList<Type>? getterTypes,
        IReadOnlyCollection<string>? countFields)
    {
        pole = null; refusal = null; refusalEpoch = null;
        var (pin, roots) = _host.CapturePinAndRoots(AfterReadPinForGuard);   // one build and one set of roots for the pole test and every read
        var resolver = pin.Resolver;
        var view = pin.View;
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }

        var (arm, armErr) = ResolvePoleArm(view, roots, plugin, mod);
        if (armErr is not null)
        {
            refusal = armErr;
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        pole = arm;
        plugin = arm!.Plugin;   // a path pole may have resolved back to its active plugin name

        if (arm.InOrder)
        {
            // Active arm: the same per-item reads ResolveBatch(plugin=) does, off the same captured view.
            var linkMemo = resolveNames ? new LinkMemo() : null;
            var unionMemo = new ChildUnionMemo();   // named records again: the union lane
            using var batchSession = resolver.OpenSession();   // and one overlay cache for the batch, as ResolveBatch has
            var outcomes = new List<ReadOutcome>(formids.Count);
            // Parsed up front and gathered a chunk at a time, the same shape ResolveBatch reads by.
            var keys = new FormKey[formids.Count];
            var parseErrors = new string?[formids.Count];
            for (int i = 0; i < formids.Count; i++)
            {
                try { keys[i] = view.ParseFormId(formids[i]); }
                catch (Exception ex) { parseErrors[i] = $"bad FormID '{formids[i]}': {ex.Message}"; }
            }
            BodyPrefetch.Chunk? chunk = null;
            int chunkStart = -1;
            for (int i = 0; i < formids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();   // a client that aborted stops the batch inside one record
                if (parseErrors[i] is { } perr) { outcomes.Add(ReadOutcome.Fail(default, perr)); continue; }
                int start = BodyPrefetch.ChunkStart(i);
                if (start != chunkStart)
                {
                    chunkStart = start;
                    chunk = BodyPrefetch.Gather(view, batchSession, keys, start, Math.Min(start + BodyPrefetch.ChunkRows, keys.Length),
                                                _ => plugin, getterTypes, ct);
                }
                var fk = keys[i];
                var body = chunk?.Body(fk);   // the plugin is walked here, on the first row of the chunk that wants it
                outcomes.Add(ResolveRead(resolver, view, fk, plugin, fields, false, depth, resolveNames, linkMemo, containerHint, unionMemo, batchSession, depths, body, countFields)
                             with { Stamp = view.Stamp, Pin = pin });
            }
            return outcomes;
        }

        // Off-order arm: the locate already ran in ResolvePoleArm, so open the overlay once and pick every
        // requested record in a single enumeration pass.
        var poleWhere = arm.Where;

        // Parse every FormID first (per-item errors keep their input positions), then one enumeration pass.
        var parsed = new List<(int Index, FormKey Fk)>();
        var results = new ReadOutcome?[formids.Count];
        for (int i = 0; i < formids.Count; i++)
        {
            try { parsed.Add((i, view.ParseFormId(formids[i]))); }
            catch (Exception ex) { results[i] = ReadOutcome.Fail(default, $"bad FormID '{formids[i]}': {ex.Message}"); }
        }

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(arm.Path!, string.IsNullOrEmpty(arm.DataDir) ? null : arm.DataDir); }
        catch (Exception ex)
        {
            refusal = $"could not open '{arm.Path}' as a Skyrim plugin: {ex.Message}";
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        try
        {
            var wanted = parsed.Select(p => p.Fk).ToHashSet();
            var found = new Dictionary<FormKey, IMajorRecordGetter>();
            try
            {
                foreach (var r in ov.EnumerateMajorRecords())
                {
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the file walk too
                    if (wanted.Contains(r.FormKey) && !found.ContainsKey(r.FormKey))
                    {
                        found[r.FormKey] = r;
                        if (found.Count == wanted.Count) break;
                    }
                }
            }
            // A cancel is the client's, not a parse fault: naming the file as the cause sends the caller after
            // nothing.
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                refusal = $"file '{plugin}' could not be fully read — a record Mutagen cannot parse: {ex.Message}";
                refusalEpoch = view.Stamp;
                return Array.Empty<ReadOutcome>();
            }

            var linkMemo = resolveNames ? new LinkMemo() : null;
            using var session = resolveNames ? resolver.OpenSession() : null;   // resolve_names annotates against the ACTIVE order
            foreach (var (index, fk) in parsed)
            {
                ct.ThrowIfCancellationRequested();   // a client that aborted stops the read inside one record
                if (!found.TryGetValue(fk, out var rec))
                {
                    // The untouched contract holds on this arm too: name the plugins that DO touch the record, or say
                    // plainly that nothing does.
                    IReadOnlyList<string> touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                    results[index] = ReadOutcome.Fail(fk,
                        $"file '{plugin}' ({poleWhere}) does not define or override {FormIdToken.Of(fk)} — it has no version of this record. " +
                        (touchers.Count > 0
                            ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                            : "No active plugin touches it either."))
                        with { Stamp = view.Stamp, Pin = pin };
                    continue;
                }
                var record = ReadEngine.ReadFields(rec, fields, depth, containerHint, depths: depths);   // materialise while the overlay is open
                if (resolveNames) record = AnnotateLinks(record, view, session!, linkMemo!);
                var winner = view.ResolveWinner(fk);                             // winner CONTEXT where the record also lives in the order
                results[index] = new ReadOutcome(fk, record, plugin, winner?.WinnerPlugin,
                                                 winner?.OverrideDepth ?? 0, null, null)
                                 with { Stamp = view.Stamp, Pin = pin };
            }
            return results.Select(r => r!).ToList();
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ---- the info_order projection form ----------------------------------------------------------------

    /// <summary>One topic's effective-INFO-order row; a non-null Error is a per-item refusal — a bad FormID, an
    /// absent record, or a non-DIAL target named by its actual type.</summary>
    public sealed record InfoOrderRow(string Formid, string? Type, string? EditorId, string? WinnerPlugin,
                                      InfoOrderView? Order, string? Error);

    /// <summary>The form='info_order' batch: per DIAL topic, the effective merged INFO order across every touching
    /// plugin — the game's own walk order — off ONE captured build, epoch-stamped because it reads plugin records
    /// through the index alone.</summary>
    public IReadOnlyList<InfoOrderRow> InfoOrderBatch(IReadOnlyList<string> formids, ArtifactDemand? demand,
                                                      out string? refusal, out OrderStamp? epoch,
                                                      PoleInfo? foldArm, FoldFacts? foldFacts)
    {
        refusal = null;
        var resolver = _host.Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<InfoOrderRow>();
        }
        DialogueFold? fold = null;
        if (foldArm is not null)
        {
            fold = OpenDialogueFold(foldArm, out var foldErr, FoldLabel(foldArm));
            if (foldErr is not null) { refusal = foldErr; return Array.Empty<InfoOrderRow>(); }
            // Where the file would load, decided against THIS build before the merge reads it.
            fold!.PlaceIn(view);
            foldFacts?.Fill(fold);
        }
        using var session = resolver.OpenSession();

        var rows = new List<InfoOrderRow>(formids.Count);
        var dialFks = new List<FormKey>();
        // Per ROW, not per FormKey: a duplicated DIAL key must attach the computed order to every occurrence, and
        // a dictionary keyed on FormKey would leave the earlier duplicates rendering a fabricated failure.
        var dialRows = new List<(int Index, FormKey Fk)>();
        var dialSeen = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { rows.Add(new InfoOrderRow(raw?.Trim() ?? "", null, null, null, null, $"bad FormID '{raw}': {ex.Message}")); continue; }
            var win = view.ResolveWinner(fk);
            if (win is null)
            {
                // A topic only the folded file defines resolves nowhere in the active order, and the fold IS its
                // whole merge — served from the fold, with no winner, rather than refused as absent.
                if (fold?.Topic(fk) is { } foldedOnly)
                {
                    dialRows.Add((rows.Count, fk));
                    rows.Add(new InfoOrderRow(FormIdToken.Of(fk), "DialogTopic", foldedOnly.EditorId, null, null, null));
                    if (dialSeen.Add(fk)) dialFks.Add(fk);
                    continue;
                }
                rows.Add(new InfoOrderRow(FormIdToken.Of(fk), null, null, null, null, UnresolvedFormId(view, fk)));
                continue;
            }
            var body = view.GetRecord(session, win.Value.WinnerPlugin, fk);
            if (body is null)
            {
                rows.Add(new InfoOrderRow(FormIdToken.Of(fk), null, null, win.Value.WinnerPlugin, null,
                                          $"the winner body of {FormIdToken.Of(fk)} could not be read from '{win.Value.WinnerPlugin}'."));
                continue;
            }
            if (body is not Mutagen.Bethesda.Skyrim.IDialogTopicGetter)
            {
                var typeName = RecordNaming.StripOverlay(body.GetType().Name);
                rows.Add(new InfoOrderRow(FormIdToken.Of(fk), typeName, body.EditorID, win.Value.WinnerPlugin, null,
                    $"{FormIdToken.Of(fk)} is a {typeName}, and the info_order form renders the merged INFO sequence of a DIALOGUE TOPIC (DIAL). " +
                    "For a quest's topics, select them by composition: types=[\"DIAL\"] where=[\"Quest = " + FormIdToken.Of(fk) + "\"]."));
                continue;
            }
            dialRows.Add((rows.Count, fk));
            rows.Add(new InfoOrderRow(FormIdToken.Of(fk), RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                      win.Value.WinnerPlugin, null, null));
            if (dialSeen.Add(fk)) dialFks.Add(fk);
        }
        if (dialFks.Count > 0)
        {
            var orders = DialogueValidate.InfoOrders(view, session, dialFks, fold);
            foreach (var (idx, fk) in dialRows)
                if (orders.TryGetValue(fk, out var io)) rows[idx] = rows[idx] with { Order = io };
        }
        return rows;
    }

    /// <summary>What the caller has to say about a fold it asked for, filled by the lane that opened the file, so
    /// the response's statement and the rows under it cannot describe two different files or positions.</summary>
    public sealed class FoldFacts
    {
        public string Plugin { get; private set; } = "";
        public string Label { get; private set; } = "";
        public string Where { get; private set; } = "";

        /// <summary>Where the fold was placed — known only once the file's header has been read.</summary>
        public string Placement { get; private set; } = "";

        /// <summary>The folded file's FILENAME is also active, from another mod folder, so the response may say only
        /// that THIS COPY is not the one the order loads.</summary>
        public bool ShadowsActiveName { get; private set; }

        /// <summary>What the PROBE already knows: the name, the label, where the copy is, and whether the filename is
        /// active — filled before anything is read.</summary>
        internal void FromArm(PoleInfo arm)
        {
            Plugin = arm.Plugin; Label = FoldLabel(arm); Where = arm.Where; ShadowsActiveName = arm.NameActive;
        }

        /// <summary>…and what the opened file adds: where the projection put it.</summary>
        internal void Fill(DialogueFold fold)
        {
            Plugin = fold.Plugin; Label = fold.Label; Where = fold.Where; Placement = fold.Placement;
        }
    }

    /// <summary>The name a fold's rows carry: the filename, unless an ACTIVE plugin already has it — two contributors
    /// under one name leave the reader unable to tell projected lines from live ones.</summary>
    internal static string FoldLabel(PoleInfo arm)
        => arm.NameActive ? $"{arm.Plugin} [off-order copy]" : arm.Plugin;

    /// <summary>Read an already-probed OFF-ORDER pole's DIAL content once, for a dialogue lane to fold at the end
    /// of the order. Every failure is a named refusal, never a fold that silently contributes nothing.</summary>
    internal static DialogueFold? OpenDialogueFold(PoleInfo arm, out string? error, string? label = null,
                                                   bool withRecords = false)
    {
        error = null;
        try
        {
            return withRecords
                ? DialogueFold.Open(arm.Plugin, arm.Where, arm.Path!, arm.DataDir, label)
                : DialogueFold.Read(arm.Plugin, arm.Where, arm.Path!, arm.DataDir, label);
        }
        catch (Exception ex)
        {
            error = $"could not open '{arm.Path}' as a Skyrim plugin: {ex.Message}";
            return null;
        }
    }
}
