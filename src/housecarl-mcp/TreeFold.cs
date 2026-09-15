using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>
/// The conflict-tree fill for a CHUNK of rows, gathered a plugin at a time (#765).
///
/// <para>A tree row reads every provider of its record, and each of those reads went through
/// <see cref="LoadOrderResolver.IndexView.GetRecord"/>, which finds one record by enumerating its plugin from the
/// top. On a real order the providers are the same handful of large masters row after row, so a hundred rows paid
/// hundreds of whole-plugin walks for bodies one walk each could have answered — measured at ~240 ms a provider
/// against the 86 ms a gathered read costs (#721). The fold now runs in PLUGIN passes over a chunk of rows: the
/// winners first, then every other provider plugin once, each walk answering that plugin's whole share of the
/// chunk.</para>
///
/// <para>Plugin-major is what lets the gather keep the property #722 bought. Row-major with a chunk-wide gather
/// would pin every provider of every row at once — the gigabytes over a worldspace that PR #745 removed. Here the
/// bodies alive at any moment are ONE plugin's share of the chunk, and the diff each row does is still one
/// reference against one provider: the caller is handed each provider's fields as they arrive and releases them
/// before the next plugin is walked. What the chunk holds beyond that is the reference fields of its own rows,
/// which is what <see cref="TreeChunkRows"/> bounds.</para>
/// </summary>
public sealed partial class LoadOrderService
{
    /// <summary>Rows whose provider bodies one fold gathers together. The gather's win rises with the chunk (a
    /// master shared by more rows is walked once for all of them) and so does what the chunk holds (one reference
    /// fields per row, plus one plugin's share of the bodies), so this is the trade. A test lowers it to split a
    /// selection the way a real order's does.</summary>
    internal static int TreeChunkRows = 32;

    /// <summary>How many provider bodies the tree fold has READ in this process, gathered or seeked alike. Counted
    /// for the reason <see cref="LoadOrderResolver.BodySeeks"/> is: what a windowed tree reads rather than renders
    /// is invisible in the answer, and now that the read is a gather it is no longer a seek count.</summary>
    internal static long TreeBodiesRead;

    /// <summary>One provider's fields as the fold reaches them. <paramref name="row"/> indexes the chunk;
    /// <paramref name="node"/> is the provider's position WINNER FIRST (0 is the winner, the last index is the
    /// lowest-priority provider), which is the reverse of the render's own order and the only thing that says where
    /// an out-of-order arrival belongs. Return false to stop this row — no further provider of it is read.</summary>
    internal delegate bool TreeNodeVisitor(int row, int node, string plugin, RecordFields read, bool isWinner);

    /// <summary>Fill the conflict tree of every key in <paramref name="keys"/> off one pinned build, handing each
    /// provider's fields to <paramref name="onNode"/> as it is read. The per-row answer is the same
    /// <see cref="TreeFill"/> the single-row fold returns, and null in the same case: the key is not in the order,
    /// or no provider yielded a body.</summary>
    internal TreeFill?[] FoldTreeChunkPinned(ViewPin p, LoadOrderResolver.OverlaySession session,
                                             IReadOnlyList<FormKey> keys, IReadOnlyList<string>? fields,
                                             TreeNodeVisitor onNode)
    {
        int n = keys.Count;
        var fills = new TreeFill?[n];
        if (n == 0) return fills;
        var view = p.View;
        var hop = ContainmentIndex.ReadHop(view, session);   // '*parent' on fields= — this lane holds the index, so it answers

        // Who provides each row, winner first. Index only: no body is read to learn this, and it is the same
        // ordered list the streamed walk took its providers from.
        var providers = new string[n][];
        for (int r = 0; r < n; r++)
        {
            var t = view.TouchingPlugins(keys[r]);
            if (t is null || t.Count == 0) continue;
            var a = new string[t.Count];
            for (int i = 0; i < t.Count; i++) a[i] = t[t.Count - 1 - i];
            providers[r] = a;
        }

        // Per-row state. The child-bearing fields are the record TYPE's, so the winner body settles them for the
        // whole row; `declares` is then one answer per wanted field per provider, placed by node because the
        // providers no longer arrive in order.
        var owning = new IReadOnlyDictionary<string, OwnedChildShape>?[n];
        var wanted = new List<string>[n];
        var declares = new bool?[n][][];
        var types = new string?[n];
        var editorIds = new string?[n];
        var seekTypes = new Type?[n];
        var stopped = new bool[n];

        RunPass(Groups(0), winnerPass: true);
        RunPass(Groups(1), winnerPass: false);

        for (int r = 0; r < n; r++)
        {
            if (owning[r] is null) continue;                 // no provider yielded a body — the same null the stream gave
            if (stopped[r]) { fills[r] = new TreeFill(types[r], editorIds[r], Array.Empty<ChildDeclarers>()); continue; }
            var wf = wanted[r];
            var list = new List<ChildDeclarers>(wf.Count);
            for (int f = 0; f < wf.Count; f++)
            {
                var declaring = new List<string>();
                var unreadable = new List<string>();
                for (int node = providers[r].Length - 1; node >= 0; node--)   // node 0 is the winner, so this is priority order, winner last
                {
                    var d = declares[r][f][node];
                    if (d == true) declaring.Add(providers[r][node]);
                    else if (d is null) unreadable.Add(providers[r][node]);
                }
                list.Add(new ChildDeclarers(wf[f], owning[r]![wf[f]], declaring, unreadable));
            }
            fills[r] = new TreeFill(types[r], editorIds[r], list);
        }
        return fills;

        // The plugins to walk for node 0 (the winners) or for nodes 1.. (everything else), each with its share of
        // the chunk, in the order a row-major reading first reaches them — so a one-row chunk walks its providers
        // in exactly the order the streamed walk did.
        List<(string Plugin, List<(int Row, int Node)> Hits)> Groups(int fromNode)
        {
            var order = new List<(string, List<(int, int)>)>();
            var at = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < n; r++)
            {
                if (providers[r] is not { } ps) continue;
                int last = fromNode == 0 ? 0 : ps.Length - 1;      // the winner pass is node 0 alone
                for (int node = fromNode; node <= last; node++)
                {
                    if (!at.TryGetValue(ps[node], out var hits))
                    {
                        at[ps[node]] = hits = new List<(int, int)>();
                        order.Add((ps[node], hits));
                    }
                    hits.Add((r, node));
                }
            }
            return order;
        }

        void RunPass(List<(string Plugin, List<(int Row, int Node)> Hits)> groups, bool winnerPass)
        {
            foreach (var (plugin, hits) in groups)
            {
                var want = new HashSet<FormKey>();
                foreach (var (r, _) in hits) if (!stopped[r]) want.Add(keys[r]);
                if (want.Count == 0) continue;
                // The winner pass has no type yet, exactly as the streamed walk fetched its first body blind. After
                // it, each row's type narrows its plugin's walk to the GRUPs that type lives in — the union over
                // the rows this plugin serves, and only when EVERY one of them is known, since a type missing from
                // the list would have its records declared absent by a typed walk that routed the others.
                List<Type>? seek = null;
                if (!winnerPass)
                {
                    seek = new List<Type>();
                    foreach (var (r, _) in hits)
                    {
                        if (stopped[r]) continue;
                        if (seekTypes[r] is not { } t) { seek = null; break; }
                        if (!seek.Contains(t)) seek.Add(t);
                    }
                    if (seek is { Count: 0 }) seek = null;
                }
                var sink = new Dictionary<FormKey, IMajorRecordGetter>(want.Count);
                // A fault reading the PLUGIN leaves the sink empty and every row of it falls back to the per-record
                // fetch below, which raises the same fault from the same place the streamed walk raised it. The
                // gather is an optimisation and must never become a second error path — but an out-of-memory
                // failure is not this plugin's fault and the fallback costs one whole-plugin walk per row, so
                // paying it because memory already ran out makes the failure worse.
                try { view.CollectRecords(session, plugin, want, seek, sink); }
                catch (OutOfMemoryException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { sink.Clear(); }

                foreach (var (r, node) in hits)
                {
                    if (stopped[r]) continue;
                    var fk = keys[r];
                    var body = sink.TryGetValue(fk, out var got)
                             ? got
                             : view.FetchRecord(session, plugin, fk, winnerPass ? null : seekTypes[r]);
                    Interlocked.Increment(ref TreeBodiesRead);
                    if (winnerPass)
                    {
                        owning[r] = OwnedChildContent.Fields(body);
                        wanted[r] = owning[r]!.Count == 0
                            ? new List<string>()
                            : owning[r]!.Keys.Where(f => fields is null || fields.Contains(f, StringComparer.Ordinal))
                                        .OrderBy(f => f, StringComparer.Ordinal).ToList();
                        declares[r] = new bool?[wanted[r].Count][];
                        for (int f = 0; f < wanted[r].Count; f++) declares[r][f] = new bool?[providers[r].Length];
                        seekTypes[r] = WriteEngine.SeekTypeFor(body);
                    }
                    var read = ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop);   // materialise while open
                    if (winnerPass) { types[r] = read.Type; editorIds[r] = read.EditorId; }
                    bool go = onNode(r, node, plugin, read, winnerPass);
                    for (int f = 0; f < wanted[r].Count; f++)
                    {
                        // Null means "could not look", never "declares nothing": a body dropped in silence would
                        // render as "nobody declares content here".
                        declares[r][f][node] = OwnedChildContent.DeclaresChild(body, wanted[r][f]);
                    }
                    if (!go) stopped[r] = true;
                }
                sink.Clear();                                // this plugin's share of the chunk is done with
            }
        }
    }

    /// <summary>Which rows of <paramref name="count"/> fall in the chunk starting at <paramref name="start"/>.</summary>
    internal static int ChunkEnd(int start, int count) => Math.Min(start + TreeChunkRows, count);
}
