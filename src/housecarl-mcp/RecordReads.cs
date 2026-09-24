using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>Resolve + read one record: the WINNER's body by default, or a named <paramref name="plugin"/>'s
    /// override; with <paramref name="conflictTree"/> also the ordered touching-plugin list. Every failure is a
    /// recoverable NAMED error, never a silent empty result; contracts in docs/architecture/read-engine.md.</summary>
    public ReadOutcome ResolveRead(FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                   bool resolveNames = false, LinkMemo? linkMemo = null,
                                   string? containerHint = ReadEngine.DepthExpandHint,
                                   IReadOnlyList<int>? depths = null,
                                   IReadOnlyCollection<string>? countFields = null)
    {
        var resolver = Resolver;
        var view = resolver.Capture();
        return ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint,
                           new ChildUnionMemo(), depths: depths, countFields: countFields)   // one named record: the union lane
               with { Stamp = view.Stamp, Pin = new ViewPin(resolver, view) };   // stamped and pinned here, off the view actually read
    }

    /// <summary>The read body, answered entirely off ONE captured view, so a freshness rebuild landing mid-read
    /// cannot make a record's reported winner disagree with its own touching list; the <see cref="ViewPin"/> rule
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
    static string UnresolvedFormId(LoadOrderResolver.IndexView view, FormKey fk,
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
        var resolver = Resolver;
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

    /// <summary>A pinned (resolver, view) pair, carried on the outcome so the render-time fills a response makes
    /// read the build the outcome's epoch names. Pure data, no handles.</summary>
    internal sealed record ViewPin(LoadOrderResolver Resolver, LoadOrderResolver.IndexView View);

    /// <summary>The cross-query detail fill, pinned to the scan's build when the outcome carries one; without the pin
    /// each row re-gates and re-captures.</summary>
    /// <param name="session">The render's one overlay session; <paramref name="prefetched"/> is this row's body when
    /// the caller gathered it in bulk.</param>
    internal ReadOutcome ResolveReadOn(CrossQueryOutcome q, FormKey fk, string? plugin, IReadOnlyList<string>? fields,
                                       bool conflictTree, int depth = 1, bool resolveNames = false,
                                       LinkMemo? linkMemo = null,
                                       string? containerHint = ReadEngine.DepthExpandHint,
                                       IReadOnlyList<int>? depths = null,
                                       LoadOrderResolver.OverlaySession? session = null,
                                       IMajorRecordGetter? prefetched = null,
                                       IReadOnlyCollection<string>? countFields = null)
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
    internal TreeFill? FoldTreePinned(ViewPin p, FormKey fk, IReadOnlyList<string>? fields,
                                      Func<string, RecordFields, bool, bool> onNode)
    {
        using var session = p.Resolver.OpenSession();
        return FoldTreeChunkPinned(p, session, new[] { fk }, fields,
                                   (_, _, plugin, read, isWinner) => onNode(plugin, read, isWinner))[0];
    }

    /// <summary>The whole tree materialised — every provider's fields at once, in priority order with the winner
    /// last. For a caller that genuinely needs the providers side by side; the render does not.</summary>
    internal ConflictTreeView? ResolveTreePinned(ViewPin p, FormKey fk, IReadOnlyList<string>? fields)
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
        var resolver = Resolver;
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
    static string? ActiveNameForPath(LoadOrderResolver.IndexView view, string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim()); } catch { return null; }
        var name = Path.GetFileName(full);
        if (name.Length == 0 || !view.ContainsPlugin(name)) return null;
        // An excluded plugin is still in the name table and the active lane can only refuse it; reading its file
        // directly is the escape hatch, so a path to one must keep taking the off-order lane.
        if (view.ExcludedPlugins.ContainsKey(name)) return null;
        var active = view.PluginPath(name);
        return !string.IsNullOrEmpty(active) && SamePluginFile(active, full) ? name : null;
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
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                                   bool resolveNames = false, string? plugin = null,
                                                   string? containerHint = ReadEngine.DepthExpandHint,
                                                   IReadOnlyList<int>? depths = null,
                                                   CancellationToken ct = default,
                                                   IReadOnlyList<Type>? getterTypes = null,
                                                   IReadOnlyCollection<string>? countFields = null)
        => ResolveBatch(formids, fields, conflictTree, depth, resolveNames, plugin, null, out _, out _, containerHint, depths, ct, getterTypes, countFields);

    /// <summary>The artifact-aware overload: <paramref name="artifactDemand"/> is checked against THIS capture's
    /// epoch — the same build that would answer — and a mismatch hands back <paramref name="artifactRefusal"/> and
    /// <paramref name="refusalEpoch"/> with no rows, because a refusal that consulted a build renders stamped
    /// with it.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                                                   bool resolveNames, string? plugin, ArtifactDemand? artifactDemand,
                                                   out string? artifactRefusal, out OrderStamp? refusalEpoch,
                                                   string? containerHint = ReadEngine.DepthExpandHint,
                                                   IReadOnlyList<int>? depths = null,
                                                   CancellationToken ct = default,
                                                   IReadOnlyList<Type>? getterTypes = null,
                                                   IReadOnlyCollection<string>? countFields = null)
    {
        artifactRefusal = null; refusalEpoch = null;
        var resolver = Resolver;                // build/refresh once for the batch
        var view = resolver.Capture();          // one build for every item — the whole batch is one logical operation
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            artifactRefusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);
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
    /// across the whole install.</summary>
    (PoleInfo? Pole, string? Error) ResolvePoleArm(LoadOrderResolver.IndexView view, string plugin, string? mod)
    {
        // Judged on the argument as given: the rewrite below turns a path into a bare filename, which would flip
        // a path pole into the mod= lane.
        bool namesMod = !string.IsNullOrWhiteSpace(mod) && !LooksLikePath(plugin);

        // A pole addressed by path that IS the active order's file resolves back to its plugin name.
        if (LooksLikePath(plugin) && ActiveNameForPath(view, plugin) is { } activeName) plugin = activeName;

        bool activeFilename = view.ContainsPlugin(plugin);
        if (!namesMod && activeFilename)
            return (new PoleInfo(plugin, "active in the load order", InOrder: true, EpochCoversPole: true), null);

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex)
        {
            return (null, activeFilename
                ? $"source '{plugin}' names mod folder '{mod!.Trim()}', and the MO2 roots couldn't be derived to read that folder's copy: {ex.Message}"
                : $"source '{plugin}' is not active in the load order, and the MO2 roots couldn't be derived to search for it on disk: {ex.Message}");
        }
        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, mod);
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
                { Path = loc.Path, Layer = loc.WhereNamesLayer ? loc.Where : null, NameActive = activeFilename }, null);
    }

    /// <summary>The tool-layer probe: WHICH arm would this source= pole resolve to.</summary>
    public PoleInfo? ProbeSourceArm(string plugin, string? mod, out string? error)
    {
        var view = Resolver.Capture();
        var (pole, err) = ResolvePoleArm(view, plugin, mod);
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
        string? containerHint = ReadEngine.DepthExpandHint,
        IReadOnlyList<int>? depths = null,
        CancellationToken ct = default,
        IReadOnlyList<Type>? getterTypes = null,
        IReadOnlyCollection<string>? countFields = null)
    {
        pole = null; refusal = null; refusalEpoch = null;
        var resolver = Resolver;
        var view = resolver.Capture();          // one build for the pole test and every read
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);

        var (arm, armErr) = ResolvePoleArm(view, plugin, mod);
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
        string dataDirForOverlay;
        try { lock (_gate) { EnsurePathsDerived(); dataDirForOverlay = _dataDir; } }
        catch (Exception ex)
        {
            refusal = $"the MO2 roots couldn't be derived to open '{plugin}': {ex.Message}";
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
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
        try { ov = LoadOrderResolver.OpenOverlay(arm.Path!, string.IsNullOrEmpty(dataDirForOverlay) ? null : dataDirForOverlay); }
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

    // ---- comparison poles and the delta/tree batches --------------------------------------------------

    /// <summary>Which pole a source= or versus= expression names.</summary>
    public enum PoleKind { Winner, Named, PreviousProvider, Overlay }

    /// <summary>A parsed pole expression, from the wire spelling into this engine value.</summary>
    public sealed record PoleSpec(PoleKind Kind, string? Plugin = null, string? Mod = null, string? OverlayState = null,
                                  SkyPatcherDraft.Plan? Draft = null)
    {
        public static readonly PoleSpec Winner = new(PoleKind.Winner);
        /// <summary>The arm statement a render leads with when the pole is uniform across the batch.</summary>
        public string Label => Kind switch
        {
            PoleKind.Winner => "winner",
            PoleKind.PreviousProvider => "previous_provider (the provider immediately below the subject, per record)",
            PoleKind.Overlay => $"skypatcher overlay ({OverlayState})" + (Draft is null ? "" : $", with {Draft.Arm}"),
            _ => Plugin ?? "?",
        };
    }

    /// <summary>One record's delta: subject pole versus reference pole, compared by <see
    /// cref="FieldsDiff"/>.</summary>
    public sealed record DeltaRow(string Formid, DiffPole? Subject, DiffPole? Reference, FieldsDiff.Result? Diff,
                                  IReadOnlyList<string>? StackAbove, string? Note, string? Error);

    /// <summary>The project=delta batch: every pole of every record resolves against ONE captured build, so a
    /// comparison can never span two.</summary>
    public IReadOnlyList<DeltaRow> DeltaBatch(
        IReadOnlyList<string> formids, PoleSpec subject, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? subjectArm, out string? referenceArm, out bool epochCoversAll,
        out string? refusal, out OrderStamp? epoch, SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        subjectArm = null; referenceArm = null; epochCoversAll = true; refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();          // one build for every pole of every record
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<DeltaRow>();
        }
        using var session = resolver.OpenSession();

        // Pre-parse the keys so an off-order pole's cache materializes only the requested records.
        var parsed = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wanted = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsed.Add((raw, fk0, null)); wanted.Add(fk0); }
            catch (Exception ex) { parsed.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        // Resolve the uniform arms once; winner and overlay are per-record but uniform in statement.
        var sGather = new PoleGather();
        var rGather = new PoleGather();
        var sReader = MakePoleReader(view, session, subject, fields, wanted, out subjectArm, out var sCovers, out var sErr, out var sOffOrder, overlayWarnings, sGather);
        if (sErr is not null) { refusal = "source: " + sErr; return Array.Empty<DeltaRow>(); }
        var rReader = MakePoleReader(view, session, reference, fields, wanted, out referenceArm, out var rCovers, out var rErr, out _, overlayWarnings, rGather);
        if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<DeltaRow>(); }
        epochCoversAll = sCovers && rCovers;

        // previous_provider is measured from the SUBJECT's position in the active touching stack, which an
        // off-order subject holds in no record. That is a fact about the arm, not about any record.
        if (reference.Kind == PoleKind.PreviousProvider && sOffOrder is not null)
        {
            refusal = $"versus: the subject is the off-order file '{sOffOrder.Plugin}' ({sOffOrder.Where}), which holds no position in the " +
                      "active touching stack previous_provider is measured in. Name an active plugin as source=, or compare against a named versus= plugin.";
            return Array.Empty<DeltaRow>();
        }

        var rows = new List<DeltaRow>(formids.Count);
        // A chunk of rows at a time, so each pole walks a plugin once for the whole chunk.
        for (int start = 0; start < parsed.Count; start = ChunkEnd(start, parsed.Count))
        {
            int end = ChunkEnd(start, parsed.Count);
            var chunkKeys = new List<FormKey>(end - start);
            for (int i = start; i < end; i++) if (parsed[i].Fk is { } k) chunkKeys.Add(k);
            sGather.Open(view, session, chunkKeys, _ => null);
            // previous_provider is measured FROM the subject, so the reference's declaration needs the plugin the
            // subject resolved to.
            var subjects = new string?[chunkKeys.Count];
            for (int j = 0; j < chunkKeys.Count; j++) subjects[j] = sGather.PluginOf?.Invoke(chunkKeys[j], null);
            rGather.Open(view, session, chunkKeys, j => subjects[j]);

            for (int i = start; i < end; i++)
            {
                var (raw, fkOpt, parseError) = parsed[i];
                if (parseError is not null) { rows.Add(new DeltaRow(raw?.Trim() ?? "", null, null, null, null, null, parseError)); continue; }
                var fk = fkOpt!.Value;

                var s = sReader(fk, null);
                if (s.Error is not null) { rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, null, null, null, null, "subject: " + s.Error)); continue; }
                // previous_provider is measured from the SUBJECT, so the reference reader is handed the subject's
                // resolved plugin for this record.
                var r = rReader(fk, s.Pole!.Plugin);
                if (r.Error is not null) { rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, r.Pole, null, r.StackAbove, null, "versus: " + r.Error)); continue; }

                string? note = string.Equals(s.Pole.Plugin, r.Pole!.Plugin, StringComparison.OrdinalIgnoreCase) && s.Pole.Where == r.Pole.Where
                    ? "the two poles resolved to the SAME provider — the diff is trivially empty by construction"
                    : null;
                // Two copies of one filename on opposite arms: the delta line names the off-order side's mod
                // folder, or the reader cannot tell which side a value came from.
                var diff = FieldsDiff.Compare(s.Fields!, r.Fields!, referenceLabel: r.Pole.LabelVersus(s.Pole.Plugin));
                rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, r.Pole, diff, r.StackAbove, note, null));
            }
        }
        return rows;
    }

    /// <summary>A pole reader's per-record result: the deep-read fields plus the pole identity for the render, or
    /// a per-item error. <see cref="StackAbove"/> names what outranks the subject.</summary>
    internal sealed record PoleReading(RecordFields? Fields, DiffPole? Pole, IReadOnlyList<string>? StackAbove, string? Error);

    internal delegate PoleReading PoleReader(FormKey fk, string? subjectPlugin);

    /// <summary>Build the per-record reader for one pole against the shared captured view and session; uniform arm
    /// resolution happens here once and per-record work stays in the returned reader.</summary>
    PoleReader MakePoleReader(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                              PoleSpec spec, IReadOnlyList<string>? fields, IReadOnlyCollection<FormKey>? wanted,
                              out string? armStatement, out bool covers, out string? error, out PoleInfo? offOrderArm,
                              SkyPatcherOverlay.WarningSink? overlayWarnings = null, PoleGather? gather = null)
    {
        error = null; covers = true; offOrderArm = null;
        // '*parent' on fields=: every in-order arm reads through this captured view and open session.
        var hop = ContainmentIndex.ReadHop(view, session);
        switch (spec.Kind)
        {
            case PoleKind.Winner:
                armStatement = "winner";
                if (gather is not null) gather.PluginOf = (fk, _) => view.ResolveWinner(fk)?.WinnerPlugin;
                return (fk, _) =>
                {
                    var w = view.ResolveWinner(fk);
                    if (w is null)
                        return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                    var body = gather is { Live: true } ? gather.Body(w.Value.WinnerPlugin, fk)
                                                        : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the winner body of {FormIdToken.Of(fk)} could not be read from '{w.Value.WinnerPlugin}'.");
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                           new DiffPole(w.Value.WinnerPlugin, "winner (active order)", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                };

            case PoleKind.PreviousProvider:
                // Subject-relative: resolved per record against the touching list, anchored on the plugin the
                // subject resolved to. Always active-order, since the touching list is the order's.
                armStatement = spec.Label;
                if (gather is not null) gather.PluginOf = (fk, subjectPlugin) =>
                {
                    if (subjectPlugin is null) return null;
                    var t = view.TouchingPlugins(fk);
                    if (t is null) return null;
                    for (int i = 1; i < t.Count; i++)                    // i starts at 1: the bottom provider has nothing beneath it
                        if (string.Equals(t[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) return t[i - 1];
                    return null;
                };
                return (fk, subjectPlugin) =>
                {
                    var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                    if (touchers.Count == 0)
                        return new PoleReading(null, null, null, $"no active plugin touches {FormIdToken.Of(fk)} — there is no provider stack to measure previous_provider in.");
                    int idx = -1;
                    for (int i = 0; i < touchers.Count; i++)
                        if (string.Equals(touchers[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                    if (idx < 0)   // the subject doesn't touch the record at all
                        return new PoleReading(null, null, null,
                            $"the subject '{subjectPlugin}' does not touch {FormIdToken.Of(fk)}, so it has no position to measure previous_provider from. " +
                            $"Touched by (active order, winner last): {string.Join(", ", touchers)}.");
                    if (idx == 0)  // the subject IS the origin; never a silent empty diff
                        return new PoleReading(null, null, null,
                            $"no previous provider — '{subjectPlugin}' DEFINES {FormIdToken.Of(fk)} (bottom of the touching list); there is nothing beneath it to compare against.");
                    var refPlugin = touchers[idx - 1];
                    var body = gather is { Live: true } ? gather.Body(refPlugin, fk) : view.GetRecord(session, refPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the previous provider '{refPlugin}' of {FormIdToken.Of(fk)} could not be read.");
                    // Mid-stack subject: what sits above is surfaced as neutral fact, never advice.
                    IReadOnlyList<string>? above = idx < touchers.Count - 1 ? touchers.Skip(idx + 1).ToList() : null;
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                           new DiffPole(refPlugin, $"previous provider (immediately below '{subjectPlugin}')", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), above, null);
                };

            case PoleKind.Overlay:
                return MakeOverlayPoleReader(view, session, spec, fields, out armStatement, out covers, out error, overlayWarnings, gather);

            default:   // Named — the one-pole rule: active in the order, else an on-disk file.
                var (arm, armErr) = ResolvePoleArm(view, spec.Plugin!, spec.Mod);
                if (armErr is not null) { armStatement = null; error = armErr; return (_, _) => new PoleReading(null, null, null, armErr); }
                armStatement = $"{arm!.Plugin} — {arm.Where}";
                if (arm.InOrder)
                {
                    if (view.ExcludedPlugins.TryGetValue(arm.Plugin, out var why))
                    {
                        var exclMsg = $"'{arm.Plugin}' was excluded from this session ({why}) — its records aren't resolvable.";
                        error = exclMsg;
                        return (_, _) => new PoleReading(null, null, null, exclMsg);
                    }
                    if (gather is not null) gather.PluginOf = (_, _) => arm.Plugin;
                    return (fk, _) =>
                    {
                        var body = gather is { Live: true } ? gather.Body(arm.Plugin, fk) : view.GetRecord(session, arm.Plugin, fk);
                        if (body is null)
                        {
                            // Name the actual touchers, never a silent absence.
                            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                            return new PoleReading(null, null, null,
                                $"'{arm.Plugin}' does not define or override {FormIdToken.Of(fk)} — it has no version of this record. " +
                                (touchers.Count > 0
                                    ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                    : "No active plugin touches it either."));
                        }
                        return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                               new DiffPole(arm.Plugin, arm.Where, true,
                                                            RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                    };
                }
                // Off-order arm: open the overlay lazily once; per-record lookups sweep it on first use and
                // memoise every record seen, so one enumeration pass serves the whole batch.
                covers = false;   // the file's content sits outside the epoch fingerprint
                offOrderArm = arm;
                var lazy = new OffOrderPoleCache(this, arm, fields, wanted);
                return (fk, _) =>
                {
                    var (rec, oerr) = lazy.Find(fk);
                    if (oerr is not null) return new PoleReading(null, null, null, oerr);
                    if (rec is null)
                    {
                        var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                        return new PoleReading(null, null, null,
                            $"file '{arm.Plugin}' ({arm.Where}) does not define or override {FormIdToken.Of(fk)} — it has no version of this record. " +
                            (touchers.Count > 0
                                ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                : "No active plugin touches it either."));
                    }
                    return new PoleReading(rec, new DiffPole(arm.Plugin, arm.Where, false, rec.Type, rec.EditorId)
                                                { Qualifier = arm.Layer ?? "off-order" }, null, null);
                };
        }
    }

    /// <summary>The SkyPatcher-overlay pole.</summary>
    PoleReader MakeOverlayPoleReader(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     PoleSpec spec, IReadOnlyList<string>? fields,
                                     out string? armStatement, out bool covers, out string? error,
                                     SkyPatcherOverlay.WarningSink? overlayWarnings = null, PoleGather? gather = null)
    {
        error = null;
        var hop = ContainmentIndex.ReadHop(view, session);   // both overlay arms read through the order's own index
        var state = (spec.OverlayState ?? "post").Trim().ToLowerInvariant();
        if (state is not ("pre" or "post"))
        {
            armStatement = null; covers = true;
            error = $"overlay state '{spec.OverlayState}' is not recognized — use \"pre\" (the winner before the INI layer) or \"post\" (after it; the default).";
            var msg = error;
            return (_, _) => new PoleReading(null, null, null, msg);
        }

        if (state == "pre")
        {
            covers = true;
            armStatement = "skypatcher overlay (pre) — the plain load-order winner, before the INI layer";
            if (gather is not null) gather.PluginOf = (fk, _) => view.ResolveWinner(fk)?.WinnerPlugin;
            return (fk, _) =>
            {
                var w = view.ResolveWinner(fk);
                if (w is null) return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                var body = gather is { Live: true } ? gather.Body(w.Value.WinnerPlugin, fk)
                                                     : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                if (body is null) return new PoleReading(null, null, null, $"the winner body of {FormIdToken.Of(fk)} could not be read from '{w.Value.WinnerPlugin}'.");
                return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                       new DiffPole(w.Value.WinnerPlugin, "skypatcher overlay (pre) = winner", true,
                                                    RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
            };
        }

        covers = false;   // the INI layer's files are outside the index fingerprint (a draft INI likewise)
        armStatement = "skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays"
                     + (spec.Draft is null ? "" : $", with {spec.Draft.Arm}");
        // The replay context is built lazily once for the whole batch.
        SkyPatcherReplay? replay = null;
        // Per-key memo: the scratch mod is shared across the reader's lifetime, so a repeated key's second replay
        // would re-apply every INI line onto the already-mutated copy.
        var postMemo = new Dictionary<FormKey, PoleReading>();
        string? setupError = null;
        void Setup()
        {
            if (replay is not null || setupError is not null) return;
            try
            {
                replay = OpenSkyPatcherReplay(view, session, out var draftRefusal, spec.Draft, overlayWarnings);
                if (draftRefusal is not null) setupError = draftRefusal;
            }
            catch (Exception ex)
            {
                setupError = $"the SkyPatcher layer could not be discovered for the overlay pole: {ex.Message}";
            }
        }
        // A draft is folded up front rather than on the first record: whether it can be folded at all is a fact
        // about the whole call, so it refuses by name here.
        if (spec.Draft is not null)
        {
            Setup();
            if (setupError is not null) { error = setupError; return (_, _) => new PoleReading(null, null, null, setupError); }
        }
        return (fk, _) =>
        {
            if (setupError is not null) return new PoleReading(null, null, null, setupError);
            if (postMemo.TryGetValue(fk, out var memoized)) return memoized;
            Setup();
            if (setupError is not null) return new PoleReading(null, null, null, setupError);
            var r = replay!.Replay(fk);
            CollectOverlayWarnings(r.Folders, overlayWarnings);
            if (r.Error is not null)
            {
                // An unpatchable type is an answer, not a failure: the layer cannot touch it, so post IS pre.
                if (r.Unpatchable)
                {
                    var w = view.ResolveWinner(fk);
                    var body = w is null ? null : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    if (body is not null)
                        return postMemo[fk] = new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                               new DiffPole(w!.Value.WinnerPlugin,
                                                            "skypatcher overlay (post) = winner — type not SkyPatcher-patchable, the layer cannot touch it",
                                                            true, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                }
                return postMemo[fk] = new PoleReading(null, null, null, r.Error);
            }
            int applied = r.Folders.Where(f => f.Result is not null).Sum(f => f.Result!.Applied.Count);
            var post = ReadEngine.ReadFields(r.Copy!, fields, ConflictDiffDepth, parentOf: hop);
            return postMemo[fk] = new PoleReading(post,
                new DiffPole(r.WinnerPlugin!, $"skypatcher overlay (post) — {applied} op(s) applied onto the winner", true,
                             post.Type, r.EditorId), null, null);
        };
    }

    /// <summary>The off-order pole's lazy single-pass cache: opens the file's overlay on first lookup and sweeps it
    /// once, materialising every wanted record's deep fields as a value snapshot.</summary>
    sealed class OffOrderPoleCache
    {
        readonly LoadOrderService _svc;
        readonly PoleInfo _arm;
        readonly IReadOnlyList<string>? _fields;
        readonly HashSet<FormKey>? _wanted;   // materialize only the requested keys, never the whole file
        Dictionary<FormKey, RecordFields>? _all;
        string? _error;

        public OffOrderPoleCache(LoadOrderService svc, PoleInfo arm, IReadOnlyList<string>? fields,
                                 IReadOnlyCollection<FormKey>? wanted)
        { _svc = svc; _arm = arm; _fields = fields; _wanted = wanted is null ? null : new HashSet<FormKey>(wanted); }

        public (RecordFields? Fields, string? Error) Find(FormKey fk)
        {
            if (_error is not null) return (null, _error);
            if (_all is null && Sweep() is { } err) { _error = err; return (null, err); }
            return (_all!.TryGetValue(fk, out var rec) ? rec : null, null);
        }

        string? Sweep()
        {
            string dataDir;
            try { lock (_svc._gate) { _svc.EnsurePathsDerived(); dataDir = _svc._dataDir; } }
            catch (Exception ex) { return $"the MO2 roots couldn't be derived to open '{_arm.Plugin}': {ex.Message}"; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(_arm.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex) { return $"could not open '{_arm.Path}' as a Skyrim plugin: {ex.Message}"; }
            try
            {
                var all = new Dictionary<FormKey, RecordFields>();
                foreach (var r in ov.EnumerateMajorRecords())
                {
                    if (_wanted is not null && !_wanted.Contains(r.FormKey)) continue;   // one pass, only what was asked
                    if (!all.ContainsKey(r.FormKey))
                        all[r.FormKey] = ReadEngine.ReadFields(r, _fields, ConflictDiffDepth);
                    if (_wanted is not null && all.Count == _wanted.Count) break;
                }
                _all = all;
                return null;
            }
            catch (Exception ex) { return $"file '{_arm.Plugin}' could not be fully read — a record Mutagen cannot parse: {ex.Message}"; }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>The list-driven `records` read under the SkyPatcher-overlay post source: each record's winner is
    /// replayed through the discovered INI layer and the replayed body is what the projection reads.</summary>
    public IReadOnlyList<ReadOutcome> OverlayPostBatch(
        IReadOnlyList<string> formids, IReadOnlyList<string>? fields, int depth, bool resolveNames,
        ArtifactDemand? demand, out string? refusal, out OrderStamp? refusalEpoch, out OrderStamp? epoch,
        string? containerHint = ReadEngine.DepthExpandHint,
        IReadOnlyList<int>? depths = null,
        CancellationToken ct = default,
        SkyPatcherDraft.Plan? draft = null,
        SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        refusal = null; refusalEpoch = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);
        using var session = resolver.OpenSession();

        SkyPatcherReplay? replay;
        string? draftRefusal;
        try
        {
            replay = OpenSkyPatcherReplay(view, session, out draftRefusal, draft, overlayWarnings);
        }
        catch (Exception ex)
        {
            refusal = $"the SkyPatcher layer could not be discovered for the overlay source: {ex.Message}";
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        if (replay is null) { refusal = draftRefusal; refusalEpoch = view.Stamp; return Array.Empty<ReadOutcome>(); }

        // Per-batch replay memo: the scratch mod is shared, so a duplicated key's second replay would run every
        // INI line onto the already-mutated copy. One replay per key.
        var replayMemo = new Dictionary<FormKey, ReadOutcome>();
        LinkMemo? overlayLinkMemo = null;   // resolve_names cache, one per batch
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            ct.ThrowIfCancellationRequested();   // a client that aborted stops the replay inside one record
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            if (replayMemo.TryGetValue(fk, out var memoized)) { outcomes.Add(memoized); continue; }
            var winner = view.ResolveWinner(fk);
            if (winner is null)
            {
                var miss = ReadOutcome.Fail(fk, UnresolvedFormId(view, fk))
                           with { Stamp = view.Stamp, Pin = pin };
                replayMemo[fk] = miss; outcomes.Add(miss);
                continue;
            }
            var r = replay.Replay(fk);
            CollectOverlayWarnings(r.Folders, overlayWarnings);
            IMajorRecordGetter? bodyToRead = r.Copy;
            if (r.Error is not null)
            {
                if (r.Unpatchable)
                    bodyToRead = view.GetRecord(session, winner.Value.WinnerPlugin, fk);   // post IS pre for an unpatchable type
                if (bodyToRead is null)
                {
                    var fail = ReadOutcome.Fail(fk, r.Error) with { Stamp = view.Stamp, Pin = pin };
                    replayMemo[fk] = fail; outcomes.Add(fail);
                    continue;
                }
            }
            var record = ReadEngine.ReadFields(bodyToRead!, fields, depth, containerHint, ContainmentIndex.ReadHop(view, session), depths);
            if (resolveNames) record = AnnotateLinks(record, view, session, overlayLinkMemo ??= new LinkMemo());
            var ok = (new ReadOutcome(fk, record, winner.Value.WinnerPlugin, winner.Value.WinnerPlugin,
                                      winner.Value.OverrideDepth, null, null)
                      with { Stamp = view.Stamp, Pin = pin }).WithRuntime(view.RuntimeAddressOf(fk));
            replayMemo[fk] = ok; outcomes.Add(ok);
        }
        return outcomes;
    }

    /// <summary>Carry one replay's warnings into the caller's sink, deduplicated; each already names its own file and line.</summary>
    static void CollectOverlayWarnings(IReadOnlyList<SkyPatcherFolderOutcome> folders, SkyPatcherOverlay.WarningSink? sink)
    {
        if (sink is null) return;
        foreach (var f in folders)
            foreach (var w in f.Result?.Warnings ?? Array.Empty<string>())
                sink.Add(w);
    }

    /// <summary>One provider's node in a project=tree row: its position plus its delta against the row's reference
    /// pole. Empty deltas together with Complete means genuinely identical to the reference.</summary>
    public sealed record TreeNodeDelta(string Plugin, bool IsWinner, bool IsReference,
                                       IReadOnlyList<string> Deltas, int AgreedCount, bool Complete, string? Error);

    /// <summary>One record's project=tree row: every provider in priority order, winner last, each diffed against the
    /// reference pole; a non-null Error is a per-item refusal.</summary>
    public sealed record TreeRow(string Formid, string? Type, string? EditorId,
                                 IReadOnlyList<string> Touchers, string? ReferencePlugin,
                                 IReadOnlyList<TreeNodeDelta> Nodes, string? Error,
                                 IReadOnlyList<ChildDeclarers> ChildDeclarers);

    /// <summary>The project=tree batch: per record, the full provider stack (touching list, winner last) with each
    /// provider diffed against the reference pole — the winner by default, or a named plugin under the one-pole
    /// rule, with untouched records refused by naming the touchers. One captured build for everything.</summary>
    public IReadOnlyList<TreeRow> TreeBatch(
        IReadOnlyList<string> formids, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? referenceArm, out bool epochCoversAll, out string? refusal, out OrderStamp? epoch,
        SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        referenceArm = null; epochCoversAll = true; refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<TreeRow>();
        }
        using var session = resolver.OpenSession();

        // A winner reference reads each node off the tree itself; a named reference goes through the same pole
        // reader the delta form uses. Pre-parsed for the same reason as DeltaBatch.
        var parsedT = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wantedT = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsedT.Add((raw, fk0, null)); wantedT.Add(fk0); }
            catch (Exception ex) { parsedT.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        // A named or overlay-pre versus= pole reads one body per row out of ONE plugin, opened on the same chunk
        // boundaries the fold uses below.
        var refGather = new PoleGather();
        PoleReader? refReader = null;
        if (reference.Kind is not PoleKind.Winner)
        {
            refReader = MakePoleReader(view, session, reference, fields, wantedT, out referenceArm, out var rCovers, out var rErr, out _, overlayWarnings, refGather);
            if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<TreeRow>(); }
            epochCoversAll = rCovers;
        }
        else referenceArm = "winner";

        // Every row that answers from the INDEX alone is settled here, so the fold's chunks hold only rows that
        // will actually read bodies.
        var rows = new TreeRow[parsedT.Count];
        var liveRow = new List<int>();
        var liveKey = new List<FormKey>();
        var liveTouchers = new List<IReadOnlyList<string>>();
        for (int i = 0; i < parsedT.Count; i++)
        {
            var (raw, fkOpt, parseError) = parsedT[i];
            if (parseError is not null)
            {
                rows[i] = new TreeRow(raw?.Trim() ?? "", null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(), parseError, Array.Empty<ChildDeclarers>());
                continue;
            }
            var fk0 = fkOpt!.Value;
            var t = view.TouchingPlugins(fk0) ?? Array.Empty<string>();
            if (t.Count == 0)
            {
                rows[i] = new TreeRow(FormIdToken.Of(fk0), null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(),
                                      UnresolvedFormId(view, fk0), Array.Empty<ChildDeclarers>());
                continue;
            }
            liveRow.Add(i); liveKey.Add(fk0); liveTouchers.Add(t);
        }

        // A chunk of rows at a time, so each provider plugin is walked once for the whole chunk.
        for (int start = 0; start < liveRow.Count; start = ChunkEnd(start, liveRow.Count))
        {
            int end = ChunkEnd(start, liveRow.Count), c = end - start;
            var keys = new FormKey[c];
            var refFields = new RecordFields?[c];
            var refPlugin = new string[c];
            var refPole = new DiffPole?[c];
            var refIsActiveProvider = new bool[c];
            var refLabel = new string[c];
            var versusError = new string?[c];
            var nodes = new TreeNodeDelta?[c][];
            for (int j = 0; j < c; j++)
            {
                keys[j] = liveKey[start + j];
                refPlugin[j] = ""; refLabel[j] = "";
                nodes[j] = new TreeNodeDelta?[liveTouchers[start + j].Count];
            }
            // The versus= pole does not depend on the fold, so the whole chunk's references are resolved here and
            // the gather dropped before a single provider is walked; read inside the fold instead, that plugin's
            // chunk share would stay alive beside every other plugin's.
            if (refReader is not null)
            {
                refGather.Open(view, session, keys, _ => null);   // one walk of the versus plugin for the chunk
                for (int j = 0; j < c; j++)
                {
                    var rr = refReader(keys[j], null);
                    if (rr.Error is not null) { versusError[j] = "versus: " + rr.Error; continue; }
                    refFields[j] = rr.Fields; refPlugin[j] = rr.Pole!.Plugin; refPole[j] = rr.Pole;
                }
                refGather.Release();
            }

            var fills = FoldTreeChunkPinned(new ViewPin(resolver, view), session, keys, fields,
                (j, node, plugin, read, isWinner) =>
                {
                    var touchers = liveTouchers[start + j];
                    if (isWinner)
                    {
                        if (refReader is null) { refFields[j] = read; refPlugin[j] = plugin; }
                        // A refused versus= stops the row at the winner, so the row still carries the type and
                        // editorid the winner body just gave it and no nodes.
                        else if (versusError[j] is not null) return false;
                        // A node IS the reference only when the reference resolved IN the order: an off-order pole is
                        // never one of the active providers.
                        refIsActiveProvider[j] = refPole[j] is null || refPole[j]!.InOrder;
                        refLabel[j] = refPole[j] is not null && touchers.Any(t => string.Equals(t, refPlugin[j], StringComparison.OrdinalIgnoreCase))
                                    ? refPole[j]!.LabelVersus(refPlugin[j]) : refPlugin[j];
                    }
                    bool isRef = refReader is null ? isWinner
                               : refIsActiveProvider[j] && string.Equals(plugin, refPlugin[j], StringComparison.OrdinalIgnoreCase);
                    if (isRef) { nodes[j][node] = new TreeNodeDelta(plugin, isWinner, true, Array.Empty<string>(), 0, true, null); return true; }
                    var d = FieldsDiff.Compare(read, refFields[j]!, referenceLabel: refLabel[j]);
                    nodes[j][node] = new TreeNodeDelta(plugin, isWinner, false, d.Deltas, d.AgreedCount, d.Complete, null);
                    return true;
                });

            for (int j = 0; j < c; j++)
            {
                int i = liveRow[start + j];
                var fk = keys[j];
                var touchers = liveTouchers[start + j];
                // The versus refusal is checked FIRST: it stops the row at the winner, so it leaves no nodes, and
                // the empty-nodes row below would otherwise name the wrong cause.
                if (versusError[j] is not null)
                {
                    rows[i] = new TreeRow(FormIdToken.Of(fk), fills[j]?.Type, fills[j]?.EditorId,
                                          touchers, null, Array.Empty<TreeNodeDelta>(), versusError[j],
                                          Array.Empty<ChildDeclarers>());
                    continue;
                }
                var ordered = new List<TreeNodeDelta>(nodes[j].Length);
                for (int node = nodes[j].Length - 1; node >= 0; node--)   // the fold read winner first; the row reads winner last
                    if (nodes[j][node] is { } nd) ordered.Add(nd);
                if (fills[j] is null || ordered.Count == 0)
                {
                    rows[i] = new TreeRow(FormIdToken.Of(fk), null, null, touchers, null, Array.Empty<TreeNodeDelta>(),
                                          $"the provider bodies of {FormIdToken.Of(fk)} could not be read.", Array.Empty<ChildDeclarers>());
                    continue;
                }
                rows[i] = new TreeRow(FormIdToken.Of(fk), fills[j]!.Type, fills[j]!.EditorId,
                                      touchers, refLabel[j], ordered, null, fills[j]!.ChildDeclarers);
            }
        }
        return rows;
    }

    // ---- the traversal construct (walk=) ---------------------------------------------------------------

    /// <summary>The most record bodies one forward walk held at once — pinned, with the line above, by
    /// <c>RecordsWalkCostTests.AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem</c>.</summary>
    internal static int WalkBodyHighWater;

    /// <summary>How many record bodies the last forward walk was STILL holding when it returned — zero on every
    /// walk, the refusal and the no-frontier returns included.</summary>
    internal static int WalkBodiesHeldAtReturn;

    /// <summary>How many record bodies one forward-walk gather pass reads before it releases them;
    /// <see cref="BodyPrefetch.ChunkRows"/>, and a test lowers it to split a hop.</summary>
    internal static int WalkPassRows = BodyPrefetch.ChunkRows;

    /// <summary>Everything a walk takes from one reached node: its identity and its links, as values — what a key
    /// is remembered by once its body is gone, so a node two seeds both reach is still ONE read per call.</summary>
    sealed class WalkNodeFact
    {
        public bool Resolved;
        public string? Type;
        public string? EditorId;
        public List<FormKey>? Links;
        /// <summary>Set when Mutagen could not parse the node's content, so its links never read — the same fact the
        /// scan lanes account as an unscannable record.</summary>
        public string? Unscannable;
    }

    static readonly List<FormKey> EmptyKeys = new();

    /// <summary>Whether a fault reading one record is a PARSE of that record's content — Mutagen's own exceptions
    /// and the argument/format failures its lazy span reads raise. Anything else is the CALL's and goes on up.</summary>
    static bool IsWalkRecordFault(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is OperationCanceledException or OutOfMemoryException) return false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Namespace is { } ns && ns.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return true;
            if (e is ArgumentException or FormatException or IndexOutOfRangeException or OverflowException or InvalidCastException) return true;
        }
        return false;
    }

    /// <summary>How a walk reports a record whose content would not parse — the scan lanes' own account.</summary>
    static string WalkUnscannableNote(string fault)
        => $"could not be scanned (Mutagen could not parse its content) and was not entered — {fault}";

    static string WalkFaultOf(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>What the NPC template report needs from one node — values, never a getter, so reading a chain pins
    /// no record group's bytes.</summary>
    readonly record struct WalkTemplateFact(string TypeName, string? EditorId, FormKey Template, bool HasTemplate,
                                            NpcConfiguration.TemplateFlag Flags, bool IsNpc, bool IsLeveled,
                                            string? Unscannable = null);

    /// <summary>One record the walk reached: its identity, its provenance (<see cref="PulledBy"/>) and whether the
    /// walk entered it or recorded it as a boundary, with a boundary's reason in <see cref="Note"/>.</summary>
    public sealed record WalkNodeRow(string Key, string? Type, string? EditorId, int Depth,
                                     string PulledBy, string Status, string? Note);

    /// <summary>The NPC_ TemplateFlags typed interpreter — deliberately the only such interpreter until a gap
    /// report demands a second.</summary>
    public sealed record NpcTemplateCategory(string Category, bool InheritedAtSeed,
                                             string? ProviderKey, string? ProviderEditorId, string? Note);

    /// <summary>One seed's walk: the reached nodes in BFS order with provenance; the cycles found in the graph it
    /// walked, ONE PER CLOSING LINK, so the count is a lower bound on the distinct loops and none means none
    /// (<see cref="GraphCycles.Find"/>); the truncation note when a cap cut the walk; and, for an NPC_ seed under
    /// follow="Template", the per-category inheritance report.</summary>
    public sealed record WalkSeedResult(string Seed, string? Type, string? EditorId,
                                        IReadOnlyList<WalkNodeRow> Nodes, IReadOnlyList<string> Cycles,
                                        string? TruncationNote, IReadOnlyList<NpcTemplateCategory>? TemplateReport,
                                        string? Error, bool CyclesCapped = false);

    /// <summary>How many loops one seed's cycle search collects.</summary>
    public const int WalkCycleCap = 200;

    /// <summary>One seed's walk in progress: the rows it has proved and the frontier it has still to enter.</summary>
    sealed class WalkSeedState
    {
        public FormKey Key;
        public WalkTemplateFact? SeedTemplateFact;
        public string? EditorId;
        public string Type = "";
        public string Label = "";
        public List<WalkNodeRow> Nodes = new();
        /// <summary>The walked graph as edges, parent to target, per node this seed entered — one entry per link
        /// CROSSED, which is why it goes at <see cref="Settle"/> rather than at return.</summary>
        public Dictionary<FormKey, List<FormKey>> Edges = new();
        /// <summary>This seed's cycles, found once at <see cref="Settle"/>.</summary>
        public IReadOnlyList<string>? Cycles;
        /// <summary>Whether <see cref="WalkCycleCap"/> stopped the search.</summary>
        public bool CyclesCapped;
        /// <summary>Whether this walk will be READ for its cycles; a reading form records no edges and settles
        /// none.</summary>
        public bool WantCycles;
        public string? Truncation;
        /// <summary>Set when the seed itself gave the walk nothing to start from — its content would not parse.</summary>
        public string? Error;
        public HashSet<FormKey> Visited = new();
        public Queue<(FormKey Key, int Depth, string PulledBy)> Frontier = new();

        /// <summary>Record one walked edge; every link off an entered node is recorded, cycle or not.</summary>
        public void Edge(FormKey from, FormKey to)
        {
            if (!WantCycles || to.IsNull) return;
            if (!Edges.TryGetValue(from, out var outgoing)) Edges[from] = outgoing = new List<FormKey>();
            outgoing.Add(to);
        }

        /// <summary>This seed is finished: find its cycles and let its edge set go, where the visited set and the
        /// frontier are dropped, rather than staying resident until the last seed in the batch finishes.</summary>
        public void Settle()
        {
            Cycles ??= WantCycles ? CyclesFound() : Array.Empty<string>();
            Edges = new();
        }

        /// <summary>This seed's cycles, each stated as its loop of records — the last hop closes it.</summary>
        IReadOnlyList<string> CyclesFound()
        {
            var found = GraphCycles.Find(Edges, WalkCycleCap, out var capped);
            CyclesCapped = capped;
            if (found.Count == 0) return Array.Empty<string>();
            var editorIds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [FormIdToken.Of(Key)] = EditorId,
            };
            foreach (var n in Nodes) editorIds[n.Key] = n.EditorId;
            string Label(FormKey k)
            {
                var token = FormIdToken.Of(k);
                var editorId = editorIds.TryGetValue(token, out var e) ? e : null;
                return $"{token} ({editorId ?? "<no editorid>"})";
            }
            return found.Select(path => string.Join(" -> ", path.Select(Label)) + " -> " + Label(path[0])).ToList();
        }
    }

    /// <summary>The forward walk over the winner link graph, per seed off ONE captured build.</summary>
    public IReadOnlyList<WalkSeedResult> WalkForwardBatch(
        IReadOnlyList<string> seeds, IReadOnlyList<string>? seedPaths, string? follow,
        int depth, int maxNodes, IReadOnlyList<(string Match, bool Refuse)> exclusions,
        ArtifactDemand? demand, out string? refusal, out OrderStamp? epoch, CancellationToken ct = default,
        bool wantCycles = false)
    {
        refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<WalkSeedResult>();
        }
        using var session = resolver.OpenSession();

        string[]? followSegs = null;
        bool closure = string.IsNullOrWhiteSpace(follow) || follow!.Trim() == "*";
        if (!closure)
        {
            followSegs = follow!.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (followSegs.Length == 0) { refusal = $"walk.follow '{follow}' is not a usable field path."; return Array.Empty<WalkSeedResult>(); }
        }
        bool templateFollow = followSegs is { Length: 1 } && followSegs[0].Equals("Template", StringComparison.OrdinalIgnoreCase);

        var bodyCache = new Dictionary<FormKey, IMajorRecordGetter?>();
        WalkBodyHighWater = 0;
        WalkBodiesHeldAtReturn = 0;
        IMajorRecordGetter? Fetch(FormKey k)
        {
            if (bodyCache.TryGetValue(k, out var c)) return c;
            IMajorRecordGetter? g = view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;
            bodyCache[k] = g;
            return g;
        }
        // The same read WITHOUT the cache — for the template chain, whose nodes are read once and dropped.
        IMajorRecordGetter? FetchTransient(FormKey k)
            => bodyCache.TryGetValue(k, out var c) ? c
             : view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;

        // One node's template facts, memoised BY VALUE: nothing is pinned between seeds, and the walk fills this
        // as it reads, so the report only reads a chain node the walk did not reach itself.
        var templateFacts = new Dictionary<FormKey, WalkTemplateFact?>();
        WalkTemplateFact? TemplateFactOf(FormKey k)
        {
            if (templateFacts.TryGetValue(k, out var have)) return have;
            var body = FetchTransient(k);
            WalkTemplateFact? fact = body is null ? null : FactOf(body);
            templateFacts[k] = fact;
            return fact;
        }
        // The template link and flags are lazily parsed subrecords too, so this read carries the same per-record
        // guard the link read does: a body that will not parse comes back NAMED.
        static WalkTemplateFact FactOf(IMajorRecordGetter b)
        {
            var typeName = RecordNaming.StripOverlay(b.GetType().Name);
            var npc = b as INpcGetter;
            try
            {
                var t = npc?.Template;
                return new WalkTemplateFact(typeName, b.EditorID,
                                            t is null || t.IsNull ? default : t.FormKey, t is not null && !t.IsNull,
                                            npc?.Configuration.TemplateFlags ?? default, npc is not null,
                                            b is ILeveledNpcGetter);
            }
            catch (Exception ex) when (IsWalkRecordFault(ex))
            {
                return new WalkTemplateFact(typeName, null, default, false, default, npc is not null, false,
                                            WalkFaultOf(ex));
            }
        }
        // The hop's bodies, one enumeration per source plugin. A key the gather does not return stays UNCACHED, so
        // Fetch still raises whatever the per-record read raises: the gather is an optimisation, not an error path.
        void Prefetch(IReadOnlyList<FormKey> keys)
        {
            var wanted = new List<FormKey>();
            var seen = new HashSet<FormKey>();
            foreach (var k in keys)
                if (!k.IsNull && !bodyCache.ContainsKey(k) && seen.Add(k)) wanted.Add(k);
            for (int i = 0; i < wanted.Count; i += BodyPrefetch.ChunkRows)
            {
                int end = Math.Min(i + BodyPrefetch.ChunkRows, wanted.Count);
                var chunk = BodyPrefetch.Gather(view, session, wanted, i, end, _ => null, null, ct);
                // The walk asks for every key it gathered, so the chunk's deferred per-plugin walk is forced here.
                for (int k = i; k < end; k++)
                    if (chunk.Body(wanted[k]) is { } body) bodyCache[wanted[k]] = body;
            }
        }
        static string TypeOf(IMajorRecordGetter b) => RecordNaming.StripOverlay(b.GetType().Name);
        List<FormKey> LinksOf(IMajorRecordGetter body, string[]? segs, out string? note)
        {
            note = null;
            if (segs is null)
            {
                var seen = new HashSet<FormKey>();
                var list = new List<FormKey>();
                if (body is Mutagen.Bethesda.Plugins.Records.IFormLinkContainerGetter flc)
                    foreach (var link in flc.EnumerateFormLinks())
                        if (!link.FormKey.IsNull && seen.Add(link.FormKey)) list.Add(link.FormKey);
                return list;
            }
            // The '*parent' containment step: hop to the record that CONTAINS this one, then read the rest of the
            // path there. A path that is nothing but hops IS the edge, and the same grammar where= enforces.
            var (hops, gerr) = ContainmentIndex.SplitHops(segs, string.Join(".", segs), allowBare: true);
            if (gerr is not null) { note = $"({gerr})"; return new List<FormKey>(); }
            for (int i = 0; i < hops; i++)
            {
                var pk = view.ParentOf(body.FormKey);
                if (pk is null)
                {
                    note = $"(no record contains this {TypeOf(body)} — containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()})";
                    return new List<FormKey>();
                }
                if (i == hops - 1 && hops == segs.Length) return new List<FormKey> { pk.Value };
                IMajorRecordGetter? up;
                // A fault reading the CONTAINING record is that record's, and the note names it — never this node's.
                try { up = Fetch(pk.Value); }
                catch (Exception ex) when (IsWalkRecordFault(ex))
                { note = $"(the containing record {FormIdToken.Of(pk.Value)} {WalkUnscannableNote(WalkFaultOf(ex))})"; return new List<FormKey>(); }
                if (up is null) { note = $"(the containing record {FormIdToken.Of(pk.Value)} would not fetch)"; return new List<FormKey>(); }
                body = up;
            }

            var (links, n) = ReadEngine.CollectLinksAt(body, hops == 0 ? segs : segs[hops..]);
            note = n;
            return links ?? new List<FormKey>();
        }

        // What each reached key yielded, by value, so a key two seeds both reach is READ once per call.
        var nodeFacts = seeds.Count > 1 ? new Dictionary<FormKey, WalkNodeFact>() : null;

        // The node's identity and, unless it is at the depth cap or an excluded class, its links.
        WalkNodeFact FactFor(FormKey k, bool atCap)
        {
            var fact = nodeFacts is not null && nodeFacts.TryGetValue(k, out var f) ? f : null;
            if (fact is not null && (!fact.Resolved || atCap || fact.Links is not null || fact.Unscannable is not null || Excluded(fact.Type))) return fact;

            var body = Fetch(k);
            fact = body is null
                 ? new WalkNodeFact { Resolved = false }
                 : new WalkNodeFact { Resolved = true, Type = TypeOf(body), EditorId = body.EditorID };
            // On a template walk the reached nodes ARE the chain nodes, so the report takes its facts off the body
            // in hand here rather than re-reading every chain node from disk.
            if (templateFollow && body is not null)
            {
                var tf = FactOf(body);
                templateFacts.TryAdd(k, tf);
                if (tf.Unscannable is { } tfault) fact.Unscannable = tfault;
            }
            // PER-RECORD FAULT ISOLATION, the twin of the scan lanes': reading a node's links parses its content
            // lazily, so one record Mutagen cannot parse is a boundary and the walk goes on.
            if (body is not null && !atCap && !Excluded(fact.Type) && fact.Unscannable is null)
                try { fact.Links = LinksOf(body, followSegs, out _); }
                catch (Exception ex) when (IsWalkRecordFault(ex)) { fact.Unscannable = WalkFaultOf(ex); }
            if (nodeFacts is not null) nodeFacts[k] = fact;
            return fact;
        }
        bool Excluded(string? type)
            => type is not null && exclusions.Any(x => x.Match.Equals(type, StringComparison.OrdinalIgnoreCase));

        // Is this queued item's row already answerable from the memo? Then its body is not worth a gather slot.
        bool Memoised(FormKey k, int hop)
            => nodeFacts is not null && nodeFacts.TryGetValue(k, out var f)
               && (!f.Resolved || hop >= depth || f.Links is not null || f.Unscannable is not null || Excluded(f.Type));

        // The seeds: parsed, then gathered together, then started on their first hop — in SLICES of a pass, for
        // the reason the hops are, since a walk can be seeded from a spilled artifact holding thousands of IDs.
        var rows = new WalkSeedResult?[seeds.Count];
        var states = new WalkSeedState?[seeds.Count];
        var seedKeys = new FormKey[seeds.Count];
        for (int s0 = 0; s0 < seeds.Count; s0 += WalkPassRows)
        {
        int s1 = Math.Min(s0 + WalkPassRows, seeds.Count);
        var seedGather = new List<FormKey>(s1 - s0);
        for (int i = s0; i < s1; i++)
        {
            try { seedKeys[i] = view.ParseFormId(seeds[i]); seedGather.Add(seedKeys[i]); }
            catch (Exception ex) { rows[i] = new WalkSeedResult(seeds[i]?.Trim() ?? "", null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null, $"bad FormID '{seeds[i]}': {ex.Message}"); }
        }
        Prefetch(seedGather);

        for (int i = s0; i < s1; i++)
        {
            if (rows[i] is not null) continue;
            ct.ThrowIfCancellationRequested();
            var seedFk = seedKeys[i];
            var seedBody = Fetch(seedFk);
            if (seedBody is null)
            {
                // Fetch returns null for two conditions needing different sentences: no winner at all, or a named
                // winner whose body did not come back on fetch.
                var seedWin = view.ResolveWinner(seedFk);
                rows[i] = new WalkSeedResult(FormIdToken.Of(seedFk), null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null,
                    seedWin is null
                        ? UnresolvedFormId(view, seedFk) + " Nothing to walk from."
                        : $"the winner body of {FormIdToken.Of(seedFk)} could not be read from '{seedWin.Value.WinnerPlugin}' — nothing to walk from.");
                continue;
            }
            var seedType = TypeOf(seedBody);
            // The seed's own facts parse its content too, so a seed that will not parse carries its fault.
            var seedFact = templateFollow && seedBody is INpcGetter ? FactOf(seedBody) : (WalkTemplateFact?)null;
            string? seedFault = seedFact?.Unscannable;
            var st = new WalkSeedState
            {
                Key = seedFk,
                // The template report takes the seed's facts, not its body: a getter kept per seed would pin one
                // record group per seed.
                SeedTemplateFact = seedFault is null ? seedFact : null,
                EditorId = seedBody.EditorID,
                Type = seedType,
                Label = $"{seedType} {FormIdToken.Of(seedFk)} ({seedBody.EditorID ?? "<no editorid>"})",
                Visited = new HashSet<FormKey> { seedFk },
                WantCycles = wantCycles,
            };
            // The seed's facts go in the shared memo too, for the seed that sits on another seed's chain.
            if (st.SeedTemplateFact is { } sf) templateFacts.TryAdd(seedFk, sf);

            // Reading the seed's own links parses its content, so a seed Mutagen cannot parse says so rather than
            // raising out of the call; every path still answers for ITSELF.
            List<FormKey> SeedLinks(string[]? segs, out string? note)
            {
                note = null;
                try { return LinksOf(seedBody, segs, out note); }
                catch (Exception ex) when (IsWalkRecordFault(ex))
                {
                    var fault = WalkFaultOf(ex);
                    seedFault ??= fault;
                    note = WalkUnscannableNote(fault);
                    return new List<FormKey>();
                }
            }

            // First hop: seed_paths (each path's links) or every link on the seed.
            if (seedPaths is { Count: > 0 })
            {
                foreach (var p in seedPaths)
                {
                    var segs = p.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (segs.Length == 0) continue;
                    var links = SeedLinks(segs, out var note);
                    if (links.Count == 0 && note is not null)
                        st.Nodes.Add(new WalkNodeRow($"(seed path '{p}')", null, null, 0, st.Label, "no links", note));   // a wrong path fails loudly in the rows
                    foreach (var l in links) { st.Edge(seedFk, l); st.Frontier.Enqueue((l, 1, $"{st.Label}.{p}")); }
                }
            }
            else
            {
                foreach (var l in SeedLinks(null, out _)) { st.Edge(seedFk, l); st.Frontier.Enqueue((l, 1, st.Label)); }
            }
            // The seed itself would not parse.
            if (seedFault is { } fault)
            {
                if (st.Frontier.Count == 0 && st.Nodes.Count == 0)
                    st.Error = $"{FormIdToken.Of(seedFk)} {WalkUnscannableNote(fault)}. Nothing to walk from.";
                if (nodeFacts is not null)
                    nodeFacts[seedFk] = new WalkNodeFact
                    {
                        Resolved = true, Type = seedType, EditorId = st.EditorId, Unscannable = fault,
                    };
            }
            states[i] = st;
        }
        // The slice's seed bodies have given up their identity and their first-hop links; they go now.
        if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
        bodyCache.Clear();
        }

        // The hops: every seed advances one hop together, so the hop's bodies are ONE gather, and a node at the depth
        // cap is recorded and not entered.
        var atLevel = new int[states.Length];
        for (int d = 1; d <= depth; d++)
        {
            ct.ThrowIfCancellationRequested();
            // How many queued items belong to THIS hop, snapshotted before anything is enqueued for the next one.
            bool pending = false;
            for (int i = 0; i < states.Length; i++)
            {
                atLevel[i] = states[i] is { } s0 ? s0.Frontier.Count : 0;
                if (atLevel[i] > 0) pending = true;
            }
            if (!pending) break;

            while (true)
            {
            ct.ThrowIfCancellationRequested();
            // The gather is bounded by what each seed can still RECORD, not by the size of its frontier.
            var frontier = new List<FormKey>();
            var gatherSeen = new HashSet<FormKey>();
            var take = new int[states.Length];
            bool more = false;
            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                if (s is null || atLevel[i] == 0) continue;
                more = true;
                int room = maxNodes - s.Nodes.Count;
                if (room <= 0) { take[i] = atLevel[i]; continue; }        // at its cap: its turn below records the cut
                int took = 0, seenItems = 0;
                foreach (var q in s.Frontier)
                {
                    if (seenItems >= atLevel[i] || frontier.Count >= WalkPassRows) break;
                    seenItems++;
                    // A key this seed already visited is dropped at dequeue, so gathering it would spend a slot
                    // on a body no row ever shows.
                    if (q.Key.IsNull || s.Visited.Contains(q.Key) || bodyCache.ContainsKey(q.Key)
                        || Memoised(q.Key, q.Depth) || !gatherSeen.Add(q.Key)) continue;
                    frontier.Add(q.Key);
                    if (++took >= room) break;
                }
                take[i] = seenItems;
            }
            if (!more) break;
            if (frontier.Count > 0) Prefetch(frontier);

            for (int si = 0; si < states.Length; si++)
            {
                var st = states[si];
                if (st is null || take[si] == 0) continue;
                ct.ThrowIfCancellationRequested();
                int thisPass = take[si];
                atLevel[si] -= thisPass;
                for (int q = 0; q < thisPass; q++)
                {
                    var (key, hop, pulledBy) = st.Frontier.Dequeue();
                    if (key.IsNull) continue;
                    // A revisit is deduped and nothing more; the edge that reached it is already recorded.
                    if (!st.Visited.Add(key)) continue;
                    if (st.Nodes.Count >= maxNodes)
                    {
                        st.Truncation = $"walk truncated: the {maxNodes}-node cap was reached — what is listed IS reached and proved; raise walk.max_nodes to walk further.";
                        st.Frontier.Clear();
                        atLevel[si] = 0;
                        break;
                    }
                    bool atCap = hop >= depth;
                    var fact = FactFor(key, atCap);
                    if (!fact.Resolved)
                    {
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), null, null, hop, pulledBy, "kept",
                                                     "unresolved — no active plugin defines this target (a missing endpoint)"));
                        continue;
                    }
                    var type = fact.Type!;
                    var excl = exclusions.FirstOrDefault(x => x.Match.Equals(type, StringComparison.OrdinalIgnoreCase));
                    if (excl.Match is not null)
                    {
                        // A refuse ends the whole call.
                        if (excl.Refuse)
                        {
                            refusal = $"the walk reached a {type} ({FormIdToken.Of(key)}, via {pulledBy}) — a node class this call excludes with severity 'refuse'. Nothing is returned for this call.";
                            // A refusal returns nothing, so the pass in hand is dead: release it here.
                            if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
                            bodyCache.Clear();
                            WalkBodiesHeldAtReturn = 0;
                            return Array.Empty<WalkSeedResult>();
                        }
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy, "kept", $"excluded ({type}, severity stop) — recorded as a boundary, not entered"));
                        continue;
                    }
                    // A node Mutagen could not parse: named, kept as a boundary, and the walk continues.
                    if (fact.Unscannable is { } unscannable)
                    {
                        st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy, "kept",
                                                     WalkUnscannableNote(unscannable)));
                        // A node at the depth cap is a cut chain whatever else is true of it.
                        if (atCap)
                            st.Truncation ??= $"walk reached its depth cap ({depth}) on at least one chain — nodes at the cap are recorded, not entered; raise walk.depth to walk deeper.";
                        continue;
                    }
                    st.Nodes.Add(new WalkNodeRow(FormIdToken.Of(key), type, fact.EditorId, hop, pulledBy,
                                                 atCap ? "kept" : "expanded",
                                                 atCap ? $"at the walk.depth cap ({depth}) — not entered" : null));
                    if (atCap)
                    {
                        st.Truncation ??= $"walk reached its depth cap ({depth}) on at least one chain — nodes at the cap are recorded, not entered; raise walk.depth to walk deeper.";
                        continue;
                    }
                    var label = $"{type} {FormIdToken.Of(key)} ({fact.EditorId ?? "<no editorid>"})";
                    foreach (var l in fact.Links ?? EmptyKeys)
                        if (!l.IsNull) { st.Edge(key, l); st.Frontier.Enqueue((l, hop + 1, label)); }
                }
                // An empty frontier means this seed is finished, so the bookkeeping goes back now.
                if (st.Frontier.Count == 0) { st.Visited = new(); st.Frontier = new(); st.Settle(); }
            }
            // The pass is over: the bodies it gathered have given up their identity and their links, so they go
            // now rather than at the end of the call.
            if (bodyCache.Count > WalkBodyHighWater) WalkBodyHighWater = bodyCache.Count;
            bodyCache.Clear();
            }
        }

        var results = new List<WalkSeedResult>(seeds.Count);
        for (int i = 0; i < seeds.Count; i++)
        {
            if (states[i] is not { } st) { results.Add(rows[i]!); continue; }
            IReadOnlyList<NpcTemplateCategory>? templateReport = null;
            if (st.SeedTemplateFact is { } seedFact) templateReport = NpcTemplateReport(TemplateFactOf, seedFact, st.Key);
            // A seed that never ran a pass — no links off it at all — has not settled yet; one that did settled there.
            st.Settle();
            results.Add(new WalkSeedResult(FormIdToken.Of(st.Key), st.Type, st.EditorId, st.Nodes, st.Cycles!, st.Truncation, templateReport, st.Error, st.CyclesCapped));
        }
        WalkBodiesHeldAtReturn = bodyCache.Count;
        return results;
    }

    /// <summary>The NPC_ TemplateFlags interpreter: a SET flag means the category is inherited and the seed's own
    /// local data is masked, and the provider is the first record down the chain whose flag is CLEAR. A chain ending
    /// in a leveled actor resolves at runtime; a broken link is reported, never guessed.</summary>
    static IReadOnlyList<NpcTemplateCategory> NpcTemplateReport(Func<FormKey, WalkTemplateFact?> factOf,
                                                                WalkTemplateFact seed, FormKey seedFk)
    {
        var report = new List<NpcTemplateCategory>();
        foreach (NpcConfiguration.TemplateFlag flag in Enum.GetValues(typeof(NpcConfiguration.TemplateFlag)))
        {
            var name = flag.ToString();
            if (!seed.Flags.HasFlag(flag))
            {
                report.Add(new NpcTemplateCategory(name, false, FormIdToken.Of(seedFk), seed.EditorId,
                                                   "local data ACTIVE (flag clear)"));
                continue;
            }
            // Walk down: the provider is the first node NOT forwarding this category.
            var cur = seed;
            string? note = null; string? provKey = null; string? provEid = null;
            var hops = new HashSet<FormKey> { seedFk };
            while (true)
            {
                if (!cur.HasTemplate) { note = "flag SET but the template link is empty — the category inherits from nothing (worth a look)"; break; }
                var nextKey = cur.Template;
                if (!hops.Add(nextKey)) { note = $"template chain CYCLES at {nextKey} — no provider is reachable"; break; }
                if (factOf(nextKey) is not { } next) { note = $"template target {nextKey} is unresolved — the chain is broken here"; break; }
                // A node that resolves but will not parse is a different answer from a broken chain.
                if (next.Unscannable is { } bad) { note = $"template target {nextKey} {WalkUnscannableNote(bad)}"; break; }
                if (next.IsLeveled)
                { provKey = FormIdToken.Of(nextKey); provEid = next.EditorId; note = "a LEVELED actor — the concrete provider is rolled at runtime"; break; }
                if (!next.IsNpc)
                { note = $"template target {nextKey} is a {next.TypeName}, not an NPC or leveled actor"; break; }
                if (!next.Flags.HasFlag(flag))
                { provKey = FormIdToken.Of(nextKey); provEid = next.EditorId; break; }
                cur = next;
            }
            report.Add(new NpcTemplateCategory(name, true, provKey, provEid, note));
        }
        return report;
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
                                                      PoleInfo? foldArm = null, FoldFacts? foldFacts = null)
    {
        refusal = null;
        var resolver = Resolver;
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
    internal DialogueFold? OpenDialogueFold(PoleInfo arm, out string? error, string? label = null,
                                            bool withRecords = false)
    {
        error = null;
        string dataDir;
        try { lock (_gate) { EnsurePathsDerived(); dataDir = _dataDir; } }
        catch (Exception ex)
        {
            error = $"the MO2 roots couldn't be derived to open '{arm.Plugin}': {ex.Message}";
            return null;
        }
        try
        {
            return withRecords
                ? DialogueFold.Open(arm.Plugin, arm.Where, arm.Path!, dataDir, label)
                : DialogueFold.Read(arm.Plugin, arm.Where, arm.Path!, dataDir, label);
        }
        catch (Exception ex)
        {
            error = $"could not open '{arm.Path}' as a Skyrim plugin: {ex.Message}";
            return null;
        }
    }

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
                      limit, definedIn, groupBy, offset, whereSource, artifactDemands, referencesNone: referencesNone, ct: ct);

    /// <summary>The formids-by-scan composition: <paramref name="formidSet"/> intersects the selection with an
    /// explicit identity set.</summary>

    /// <summary>The set-valued-types overload: each entry resolves through the same
    /// <see cref="ResolveTypeFilter"/>, and the scan streams the union of the resolved type groups.</summary>
    public CrossQueryOutcome CrossQuery(IReadOnlyList<string>? typeSet, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn = false, string? groupBy = null, int offset = 0, string? whereSource = null,
                                        IReadOnlyList<ArtifactDemand>? artifactDemands = null,
                                        IReadOnlyList<FormKey>? formidSet = null,
                                        LoadOrderResolver.IndexView? pinnedView = null,
                                        IReadOnlyList<FormKey>? referencesNone = null,
                                        CancellationToken ct = default)
    {
        var resolver = Resolver;
        // The caller's own build when its FormID door already captured one, so the tokens it parsed and the
        // records this scan matches come from ONE build.
        var view = pinnedView ?? resolver.Capture();
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
        try { types = ResolveTypeFilterSet(hasType ? typeSet : null); }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }   // unknown type

        if (predicate is not null && hasType && QuantifierShapeRefusal(typeSet!, predicate) is { } qerr)
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
                    predicate.NeedsContainment ? fk => view.ParentOf(fk) : null);
                var seenSet = new HashSet<FormKey>();
                // The universe's bodies are gathered a CHUNK at a time, one enumeration per winner plugin, instead of
                // the whole-overlay seek per record.
                const int SetGatherChunk = 10_000;
                var setPending = new List<FormKey>(SetGatherChunk);
                var faultedSetWinners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool setStopped = false;
                foreach (var fk in formidSet!)
                {
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan between records, and the gather stops it between plugin walks
                    if (!seenSet.Add(fk)) continue;
                    if (view.ResolveWinner(fk) is null) continue;             // not in the order — a clean non-match for a scan; per-item errors belong to the formids= list lane
                    setPending.Add(fk);
                    if (setPending.Count == SetGatherChunk && !DrainSet()) { setStopped = true; break; }
                }
                if (!setStopped && setPending.Count > 0) DrainSet();

                // Gather one chunk's winner bodies and scan its keys in the universe's own order; false stops.
                bool DrainSet()
                {
                    var bodies = WinnerBodies.For(view, sess, setPending, null, out var faults, ct);
                    // A winner plugin that would not open is a whole-plugin coverage gap, named once.
                    foreach (var (plugin, fault) in faults)
                        if (faultedSetWinners.Add(plugin)) unreadablePlugins.Add(fault);
                    bool go = true;
                    foreach (var fk in setPending)
                    {
                        ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                        var w = view.ResolveWinner(fk);
                        if (w is null) continue;
                        try
                        {
                            if (!bodies.TryGetValue(fk, out var body))
                            {
                                unscannable++;
                                if (unscannableSamples.Count < 3)
                                    unscannableSamples.Add(faults.TryGetValue(w.Value.WinnerPlugin, out var f)
                                        ? $"{FormIdToken.Of(fk)} — {f.GetType().Name}: {f.Message}"
                                        : $"{FormIdToken.Of(fk)} — winner '{w.Value.WinnerPlugin}' did not yield the record on fetch");
                                continue;
                            }
                            if (conflictsOnly && (view.TouchingPlugins(fk)?.Count ?? 0) <= 1) continue;
                            if (DeletedRecordRule.HasNoLiveBody(body)
                                && (refSet is not null || predicate is { NeedsLiveBody: true })) continue;
                            if (!string.IsNullOrEmpty(editoridContains)
                                && (body.EditorID is null || body.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                                continue;
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
                            total++;
                            if (groups is not null)
                            {
                                var gk = groupBy == "type" ? RecordNaming.StripOverlay(body.GetType().Name)
                                       : groupBy == "defined_in" ? FormIdToken.Plugin(fk.ModKey.FileName.String)
                                       : w.Value.WinnerPlugin;
                                groups[gk] = groups.GetValueOrDefault(gk) + 1;
                            }
                            else if (total > offset && keys.Count < limit)
                            {
                                keys.Add(fk);
                                sources.Add(null);                            // the winner body is what matched and displays
                                matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);
                                prefilled?.Add(new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                                                 w.Value.WinnerPlugin, w.Value.OverrideDepth, null)
                                               .WithRuntime(view.RuntimeAddressOf(fk)));
                            }
                        }
                        catch (Exception ex)
                        {
                            unscannable++;
                            if (unscannableSamples.Count < 3)
                                unscannableSamples.Add($"{FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message}");
                        }
                    }
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
                predicate.NeedsContainment ? fk => view.ParentOf(fk) : null);
            // where_source=winner needs one body per CANDIDATE, and fetching them one at a time is a whole-overlay
            // walk each.
            const int WinnerGatherChunk = 10_000;
            // The chunk's rows, filtered by everything that needs no body, held only until the chunk drains.
            var pending = whereWinnerActive
                ? new List<(FormKey fk, int depth, IMajorRecordGetter body, string source, string winner)>(WinnerGatherChunk)
                : null;
            var faultedWinners = whereWinnerActive ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            try
            {
                // Carry the source plugin per record so the render shows the body the scan filtered: plugins= gives
                // the scoped plugin's filename, type= gives null, meaning the winner.
                IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string? source)> stream =
                    hasPlugins ? view.RecordsIn(plugins!, types).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)x.source))  // the scoped plugin's own body
                               : view.WinnerRecordsOfType(types!, unreadablePlugins).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)null));    // the load-order winner's body
                bool stopped = false;
                foreach (var (fk, depth, body, source) in stream)
                {
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the scan inside one record
                    if (setFilter is not null && !setFilter.Contains(fk)) continue;   // the identity intersection, cheapest first
                    if (conflictsOnly && depth <= 1) continue;
                    // defined_in= keeps only records whose origin FormKey is a scoped plugin — a definition, not an
                    // override this plugin merely touches. A FormKey test needing no body, so it runs before the try.
                    if (definedIn && !scopedModKeys!.Contains(fk.ModKey)) continue;
                    if (!whereWinnerActive)
                    {
                        if (!ScanRow(fk, depth, body, source)) { stopped = true; break; }
                        continue;
                    }
                    // where_source=winner de-dups up front: the winner verdict is FormKey-intrinsic, so any scoped
                    // copy gives the same answer. The scoped path instead de-dups AFTER the filters, in ScanRow.
                    if (!seen.Add(fk)) continue;
                    // A record the order gives no winner at all is a clean non-match, exactly as the per-record
                    // fetch treated it — never an unscannable row naming a winner there is none of.
                    if (view.ResolveWinner(fk) is not { } w) continue;
                    pending!.Add((fk, depth, body, source!, w.WinnerPlugin));
                    if (pending.Count == WinnerGatherChunk && !DrainChunk()) { stopped = true; break; }
                }
                if (!stopped && whereWinnerActive && pending!.Count > 0) DrainChunk();

                // Fetch one chunk's winner bodies and scan its rows, in stream order; false stops the scan.
                bool DrainChunk()
                {
                    var needed = new List<FormKey>(pending!.Count);
                    foreach (var p in pending)
                        if (!string.Equals(p.winner, p.source, StringComparison.OrdinalIgnoreCase)) needed.Add(p.fk);
                    var bodies = WinnerBodies.For(view, winnerSession!, needed, types, out var faults);
                    // A winner plugin that would not open is a whole-plugin coverage gap, named once in the
                    // response rather than only sampled three rows deep.
                    foreach (var (plugin, fault) in faults)
                        if (faultedWinners!.Add(plugin)) unreadablePlugins.Add(fault);
                    bool go = true;
                    foreach (var p in pending)
                    {
                        IMajorRecordGetter filterBody;
                        if (string.Equals(p.winner, p.source, StringComparison.OrdinalIgnoreCase)) filterBody = p.body;
                        else if (bodies.TryGetValue(p.fk, out var wb)) filterBody = wb;
                        else
                        {
                            unscannable++;
                            if (unscannableSamples.Count < 3)
                                unscannableSamples.Add(faults.TryGetValue(p.winner, out var f)
                                    ? $"{p.fk} — {f.Message}"
                                    : $"{p.fk} — winner '{p.winner}' did not yield the record on winner-source re-fetch");
                            continue;
                        }
                        if (!ScanRow(p.fk, p.depth, filterBody, p.source)) { go = false; break; }
                    }
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
                        if (DeletedRecordRule.HasNoLiveBody(filterBody)
                            && (refSet is not null || predicate is { NeedsLiveBody: true })) return true;
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (filterBody.EditorID is null || filterBody.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            return true;
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
                        // De-dup, since a key can recur across scoped plugins.
                        if (!whereWinnerActive && !seen.Add(fk)) return true;
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
                            // The winner comes off the SAME view the scan runs on; type and editorid come from
                            // the body that MATCHED.
                            prefilled!.Add(new RecordSummary(fk, RecordNaming.StripOverlay(filterBody.GetType().Name), filterBody.EditorID,
                                                             view.ResolveWinner(fk)?.WinnerPlugin ?? "?", depth, null)
                                           .WithRuntime(view.RuntimeAddressOf(fk)));
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
            // Summaries here would each need a winner-body fetch; leaving them to the renderer means a big limit
            // with a small max_chars does not fetch bodies it will never show.
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
              + "(Mutagen could not parse their content, or — under where_source=winner — a winner body the index named did not re-resolve on fetch; another plugin's copy of the same FormKey can still match): "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "")
              + $". Inspect one with {ToolNames.Records} formids=[the FormID] (per-field fault isolation applies).";
        // Records the scan DID filter, but only after reading around content Mutagen refused.
        if (lenientKeys.Count > 0)
            scanNote = scanNote is null ? LenientNote(lenientKeys.Count, lenientSamples)
                                        : scanNote + " " + LenientNote(lenientKeys.Count, lenientSamples);
        // Whole-plugin coverage gap: the scan carried on past a plugin it could not open, so the answer covers
        // the rest of the order but not that plugin's winners.
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
        // Capped means matches exist BEYOND the returned window; the matches offset= skipped were asked to be.
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote,
                                     matched, groupRows, groupBy, definedIn ? string.Join(", ", plugins!) : null, offset,
                                     whereWinner, whereSourceNote)
               { Stamp = view.Stamp, Pin = new ViewPin(resolver, view), GetterTypes = types,
                 ReverseIndexNote = reverseNote,
                 UnreadPlugins = unreadablePlugins.Select(u => u.PluginName).ToList() };
    }

    /// <summary>The schema's answer to a quantifier on a step that is not a list: a refusal naming the step's real
    /// cardinality, or null.</summary>
    string? QuantifierShapeRefusal(IReadOnlyList<string> typeTokens, FieldPredicateSet predicate)
    {
        var schemas = new List<TypeSchema>();
        foreach (var token in typeTokens)
            foreach (var ts in Rulebook.RecordTypesNamed(token))
                if (!schemas.Contains(ts)) schemas.Add(ts);
        if (schemas.Count == 0) return null;

        foreach (var step in predicate.QuantifiedSteps)
        {
            // A '->' right side is rooted at the link TARGET's type, so the scanned type's schema has no say.
            if (!step.OnScannedType) continue;
            var whatItIs = new List<string>();
            bool unanswered = false;
            foreach (var ts in schemas)
            {
                var card = Rulebook.StepCardinality(ts, step.Path, step.Index);
                // The schema cannot say for this type, so this STEP goes to the runtime accounting.
                if (card is null) { unanswered = true; break; }
                if (card == "list") { whatItIs.Clear(); break; }
                whatItIs.Add($"a {card} on {ts.Name}");
            }
            if (unanswered || whatItIs.Count == 0) continue;
            return $"predicate '{step.Text}': '{step.Path[step.Index]}{step.Token}' quantifies a step that is not a list — " +
                   $"it is {string.Join(", ", whatItIs.Take(3))}{(whatItIs.Count > 3 ? $", and {whatItIs.Count - 3} more" : "")}. " +
                   "Drop the quantifier, or point it at a list-valued field.";
        }
        return null;
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
        LoadOrderResolver.IndexView? pinnedView = null,
        IReadOnlyList<FormKey>? referencesNone = null,
        CancellationToken ct = default)
    {
        var resolver = Resolver;
        var view = pinnedView ?? resolver.Capture();   // the caller's door build when it captured one — see CrossQuery

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
        try { types = ResolveTypeFilterSet(typeSet); }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }

        if (predicate is not null && typeSet is { Count: > 0 } && QuantifierShapeRefusal(typeSet, predicate) is { } qerr)
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

        string dataDir;
        try { lock (_gate) { EnsurePathsDerived(); dataDir = _dataDir; } }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"the MO2 roots couldn't be derived to open '{pole.Plugin}': {ex.Message}") with { Stamp = view.Stamp }; }
        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(pole.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
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
                    if (DeletedRecordRule.HasNoLiveBody(rec)
                        && (refSet is not null || predicate is { NeedsLiveBody: true })) continue;
                    if (!string.IsNullOrEmpty(editoridContains)
                        && (rec.EditorID is null || rec.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
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
                        prefilled.Add(new RecordSummary(fk, RecordNaming.StripOverlay(rec.GetType().Name), rec.EditorID,
                                                        w?.WinnerPlugin ?? "(not in the active order)", w?.OverrideDepth ?? 0, null)
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
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote, matched, groupRows, groupBy,
                                     definedIn ? pole.Plugin : null, offset, false, null)
               { Stamp = view.Stamp, Pin = new ViewPin(resolver, view) };
    }

    // ---- effect-chain resolver -------------------------------------------------------------------------

    /// <summary>Resolve which SPEL/ENCH/ALCH/SCRL/INGR apply a MagicEffect, with each effect entry's magnitude, area
    /// and duration.</summary>
    public EffectChainResult ResolveEffectChain(FormKey mgef, IReadOnlyList<string>? typesNarrow, int limit)
    {
        IReadOnlyList<Type> scope;
        if (typesNarrow is { Count: > 0 })
        {
            var picked = new List<Type>();
            foreach (var ts in typesNarrow)
            {
                IReadOnlyList<Type> resolved;
                try { resolved = ResolveTypeFilter(ts.Trim()); }              // unknown type → named error, as on the scan
                catch (ArgumentException ex) { return EffectChainResult.Fail(ex.Message); }
                foreach (var t in resolved)
                {
                    if (!EffectChain.CarrierTypes.Contains(t))
                        return EffectChainResult.Fail(
                            $"type '{ts}' is not effect-bearing — the chain form scans only Spell/ObjectEffect/Ingestible/Scroll/Ingredient " +
                            "(SPEL/ENCH/ALCH/SCRL/INGR), the records that carry an Effects list. Drop it or pass one of those.");
                    if (!picked.Contains(t)) picked.Add(t);
                }
            }
            scope = picked;
        }
        else scope = EffectChain.CarrierTypes;

        return EffectChain.Resolve(Resolver, mgef, scope, limit);
    }

    // ---- corpus-backed type resolution (signature "WEAP" / catalog name "Weapon" → getter Type(s)) -------

    Dictionary<string, List<Type>>? _typeLookup;
    Dictionary<string, List<Type>> TypeLookup => _typeLookup ??= BuildTypeLookup();

    /// <summary>Build the type-string to getter-Type map from the corpus, keyed by both catalog name and signature, with a many-to-one signature and an abstract-group base name each accumulating their variants. A corpus type that will not load is skipped and surfaces as "unknown type" at query time, never as a silently wrong one.</summary>
    static Dictionary<string, List<Type>> BuildTypeLookup()
    {
        var lookup = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, Type t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<Type>();
            if (!list.Contains(t)) list.Add(t);
        }
        var corpus = CorpusRulebook.LoadCorpus();
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "record") continue;
            var t = Type.GetType(ts.GetterInterfaceAssemblyQualified);
            if (t is null) continue;
            Add(ts.Name, t);
            Add(ts.Signature, t);
        }
        // The arms come off the polymorphic base's own corpus entry: derived, not hand-wired (the coverage cornerstone).
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "polymorphic-base" || ts.Arms is not { Count: > 0 } arms) continue;
            foreach (var armName in arms)
                if (corpus.Types.TryGetValue(armName, out var arm) && arm.Kind == "record"
                    && Type.GetType(arm.GetterInterfaceAssemblyQualified) is { } at)
                    Add(ts.Name, at);
        }
        return lookup;
    }

    /// <summary>A user type SET to its getter Types: each entry's resolution unioned in order and deduped, through the same <see cref="ResolveTypeFilter"/> the singular form uses. Null for an absent or empty set.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types) => ResolveTypeFilterSet(types, out _);

    /// <summary>The display names a type SET resolves to — the same spelling a matched body's type renders as. Null for an absent set and for an unknown entry, which the calling surfaces have already refused by name.</summary>
    public IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<string>? types)
    {
        try { return TypeDisplayNames(ResolveTypeFilterSet(types)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Seat every type the scan NAMED in a group_by=type census at zero, so a requested type with no records reads as a 0 row rather than being absent from the table. No-op for the other count keys.</summary>
    static void SeedRequestedTypes(Dictionary<string, int>? groups, string? groupBy, IReadOnlyList<Type>? types)
    {
        if (groups is null || groupBy != "type") return;
        foreach (var name in TypeDisplayNames(types) ?? Array.Empty<string>()) groups.TryAdd(name, 0);
    }

    /// <summary>The same names off the already-resolved getter Types.</summary>
    static IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<Type>? types) =>
        types is { Count: > 0 } ? types.Select(t => RecordNaming.StripGetterInterface(t.Name)).Distinct(StringComparer.Ordinal).ToList() : null;

    /// <summary>The same resolution, also spelling each entry with the arms it expanded to — the label a response needs to name the types sharing one listing, since an entry like <c>GMST</c> names none of them itself.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types, out string? armLabel)
    {
        armLabel = null;
        if (types is not { Count: > 0 }) return null;
        var union = new List<Type>();
        var spelled = new List<string>(types.Count);
        foreach (var ts in types)
        {
            var entry = (ts ?? "").Trim();
            var arms = ResolveTypeFilter(entry);
            foreach (var t in arms)
                if (!union.Contains(t)) union.Add(t);
            spelled.Add(SweepScope.SpellTypeEntry(entry, arms));
        }
        armLabel = string.Join(", ", spelled);
        return union;
    }

    /// <summary>A user type string to its getter Types, throwing and naming the bad input. A BLANK entry is refused as blank here, in one place, never quoted back as an unknown type; an empty or absent SET is a different thing and is handled by the callers.</summary>
    IReadOnlyList<Type> ResolveTypeFilter(string type)
    {
        if (type.Trim().Length == 0)
            throw new ArgumentException(
                "a blank record type — pass a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon'), " +
                "or omit the parameter to leave the types unnarrowed.");
        if (TypeLookup.TryGetValue(type.Trim(), out var types)) return types;
        throw new ArgumentException(
            $"unknown record type '{type}'. Expected a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon').");
    }
}
