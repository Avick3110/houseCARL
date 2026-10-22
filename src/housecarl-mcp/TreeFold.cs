using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>The conflict-tree fill for a CHUNK of rows: ONE PLUGIN PASS over the chunk, highest priority first, so
/// each provider plugin is walked exactly once and answers its whole share, and the caller releases each
/// provider's fields before the next plugin is walked. Contracts and pins in docs/architecture/read-engine.md.</summary>
internal sealed partial class RecordReads
{
    /// <summary>Rows whose bodies one comparison-form chunk gathers together — ONE number over both lanes, since
    /// both hold one materialised thing per row of the chunk.</summary>
    internal const int ComparisonChunkRows = 32;

    /// <summary>The largest delta window: windows double from <see cref="ComparisonChunkRows"/> up to this many rows,
    /// then stay at it, so the subject readings a window holds are bounded at any row count.</summary>
    internal const int DeltaWindowCapRows = 2_048;

    /// <summary>The delta window cap a call reads with; <see cref="DeltaWindowCapRows"/>, and a test lowers it, never below one row.</summary>
    internal int DeltaWindowCap { get => _deltaWindowCap; set => _deltaWindowCap = Math.Max(1, value); }
    int _deltaWindowCap = DeltaWindowCapRows;

    /// <summary>One provider's fields as the fold reaches them, the row's WINNER FIRST (node 0 is the top provider,
    /// which a partial form above the winner can be); return false to stop this row.</summary>
    internal delegate bool TreeNodeVisitor(int row, int node, string plugin, RecordFields read, bool isWinner);

    /// <summary>Fill the conflict tree of every key off one pinned build, handing each provider's fields to
    /// <paramref name="onNode"/> as it is read; null per row when no provider yielded a body.</summary>
    internal TreeFill?[] FoldTreeChunkPinned(LoadOrderService.ViewPin p, LoadOrderResolver.OverlaySession session,
                                             IReadOnlyList<FormKey> keys, IReadOnlyList<string>? fields,
                                             TreeNodeVisitor onNode)
    {
        int n = keys.Count;
        var fills = new TreeFill?[n];
        if (n == 0) return fills;
        var view = p.View;
        var hop = ContainmentIndex.ReadHop(view, session);   // '*parent' on fields= — this lane holds the index, so it answers

        // Who provides each row, top first, and which node is the index's winner (a partial form above it loses).
        // Index only: no body is read to learn this.
        var providers = new string[n][];
        var winnerNode = new int[n];
        for (int r = 0; r < n; r++)
        {
            var t = view.TouchingPlugins(keys[r]);
            if (t is null || t.Count == 0) continue;
            var a = new string[t.Count];
            for (int i = 0; i < t.Count; i++) a[i] = t[t.Count - 1 - i];
            providers[r] = a;
            winnerNode[r] = t.Count - 1 - view.WinnerAt(keys[r]);
        }

        // Per-row state. `declares` is one answer per wanted field per provider, placed BY NODE because the
        // providers no longer arrive in order.
        var owning = new IReadOnlyDictionary<string, OwnedChildShape>?[n];
        var wanted = new List<string>[n];
        var declares = new bool?[n][][];
        var types = new string?[n];
        var editorIds = new string?[n];
        var stopped = new bool[n];
        // A row's partial copies read before its winner, held until the winner arrives; none with no partial form.
        var held = new List<(int Node, string Plugin, IMajorRecordGetter Body)>?[n];

        RunPass(Groups());

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
                for (int node = providers[r].Length - 1; node >= 0; node--)   // node 0 is the top provider, so this is priority order
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

        // Every provider plugin of the chunk ONCE, with its whole share, in DESCENDING load order — so every
        // row's winner arrives before any other provider of that row, bar the partial forms above it, and the
        // rest arrive in node order.
        List<(string Plugin, List<(int Row, int Node)> Hits)> Groups()
        {
            var at = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < n; r++)
            {
                if (providers[r] is not { } ps) continue;
                for (int node = 0; node < ps.Length; node++)
                {
                    if (!at.TryGetValue(ps[node], out var hits)) at[ps[node]] = hits = new List<(int, int)>();
                    hits.Add((r, node));
                }
            }
            return at.Select(kv => (kv.Key, kv.Value))
                     .OrderByDescending(g => view.OrderIndexOf(g.Key))
                     .ToList();
        }

        void RunPass(List<(string Plugin, List<(int Row, int Node)> Hits)> groups)
        {
            foreach (var (plugin, hits) in groups)
            {
                var want = new HashSet<FormKey>();
                foreach (var (r, _) in hits) if (!stopped[r]) want.Add(keys[r]);
                if (want.Count == 0) continue;
                var sink = new Dictionary<FormKey, IMajorRecordGetter>(want.Count);
                // The one gather rule, owned by BodyGather.WalkOnce; a fault there leaves the sink empty and every
                // row falls back to the per-record fetch. Which ROW a fault names, and why, is in
                // docs/architecture/read-engine.md.
                if (BodyGather.WalkOnce(view, session, plugin, want, sink) is not null) sink.Clear();

                foreach (var (r, node) in hits)
                {
                    if (stopped[r]) continue;
                    var body = sink.TryGetValue(keys[r], out var got)
                             ? got
                             : view.FetchRecord(session, plugin, keys[r]);
                    Interlocked.Increment(ref p.Resolver.Counters.TreeBodiesRead);
                    // A partial form above the winner waits for it, since the winner is the row's baseline.
                    if (node < winnerNode[r]) { (held[r] ??= new()).Add((node, plugin, body)); continue; }
                    Visit(r, node, plugin, body);
                    if (node == winnerNode[r] && held[r] is { } waiting)
                        foreach (var (hn, hp, hb) in waiting) if (!stopped[r]) Visit(r, hn, hp, hb);
                }
                sink.Clear();                                // this plugin's share of the chunk is done with
            }
        }

        void Visit(int r, int node, string plugin, IMajorRecordGetter body)
        {
            bool isWinner = node == winnerNode[r];
            if (isWinner)
            {
                owning[r] = OwnedChildContent.Fields(body);
                wanted[r] = owning[r]!.Count == 0
                    ? new List<string>()
                    : owning[r]!.Keys.Where(f => fields is null || fields.Contains(f, StringComparer.Ordinal))
                                .OrderBy(f => f, StringComparer.Ordinal).ToList();
                declares[r] = new bool?[wanted[r].Count][];
                for (int f = 0; f < wanted[r].Count; f++) declares[r][f] = new bool?[providers[r].Length];
            }
            var read = ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop);   // materialise while open
            if (isWinner) { types[r] = read.Type; editorIds[r] = read.EditorId; }
            bool go = onNode(r, node, plugin, read, isWinner);
            for (int f = 0; f < wanted[r].Count; f++)
            {
                // Null means "could not look", never "declares nothing".
                declares[r][f][node] = OwnedChildContent.DeclaresChild(body, wanted[r][f]);
            }
            if (!go) stopped[r] = true;
        }
    }

    /// <summary>Which rows of <paramref name="count"/> fall in the chunk starting at <paramref name="start"/>.</summary>
    internal static int ChunkEnd(int start, int count) => Math.Min(start + ComparisonChunkRows, count);
}
