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

    /// <summary>How many provider bodies the tree fold has READ — pinned by
    /// <c>RecordsRenderCostTests.ATreeGathersItsProviderBodiesPerPluginNotPerRow</c>.</summary>
    internal static long TreeBodiesRead;

    /// <summary>One provider's fields as the fold reaches them, <paramref name="node"/> WINNER FIRST; return false
    /// to stop this row.</summary>
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

        // Who provides each row, winner first. Index only: no body is read to learn this.
        var providers = new string[n][];
        for (int r = 0; r < n; r++)
        {
            var t = view.TouchingPlugins(keys[r]);
            if (t is null || t.Count == 0) continue;
            var a = new string[t.Count];
            for (int i = 0; i < t.Count; i++) a[i] = t[t.Count - 1 - i];
            providers[r] = a;
        }

        // Per-row state. `declares` is one answer per wanted field per provider, placed BY NODE because the
        // providers no longer arrive in order.
        var owning = new IReadOnlyDictionary<string, OwnedChildShape>?[n];
        var wanted = new List<string>[n];
        var declares = new bool?[n][][];
        var types = new string?[n];
        var editorIds = new string?[n];
        var seekTypes = new Type?[n];
        var stopped = new bool[n];

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

        // Every provider plugin of the chunk ONCE, with its whole share, in DESCENDING load order — so every
        // row's winner arrives before any other provider of that row and the rest arrive in node order.
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
                // A row's type narrows its plugin's walk to the GRUPs that type lives in — the union over the rows
                // this plugin serves, and only when EVERY one is known.
                List<Type>? seek = new List<Type>();
                foreach (var (r, _) in hits)
                {
                    if (stopped[r]) continue;
                    if (seekTypes[r] is not { } t) { seek = null; break; }
                    if (!seek.Contains(t)) seek.Add(t);
                }
                if (seek is { Count: 0 }) seek = null;
                var sink = new Dictionary<FormKey, IMajorRecordGetter>(want.Count);
                // The one gather rule, owned by BodyGather.WalkOnce; a fault there leaves the sink empty and every
                // row falls back to the per-record fetch. Which ROW a fault names, and why, is in
                // docs/architecture/read-engine.md.
                if (BodyGather.WalkOnce(view, session, plugin, want, seek, sink) is not null) sink.Clear();

                foreach (var (r, node) in hits)
                {
                    if (stopped[r]) continue;
                    bool isWinner = node == 0;
                    var fk = keys[r];
                    var body = sink.TryGetValue(fk, out var got)
                             ? got
                             : view.FetchRecord(session, plugin, fk, isWinner ? null : seekTypes[r]);
                    Interlocked.Increment(ref TreeBodiesRead);
                    if (isWinner)
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
                    if (isWinner) { types[r] = read.Type; editorIds[r] = read.EditorId; }
                    bool go = onNode(r, node, plugin, read, isWinner);
                    for (int f = 0; f < wanted[r].Count; f++)
                    {
                        // Null means "could not look", never "declares nothing".
                        declares[r][f][node] = OwnedChildContent.DeclaresChild(body, wanted[r][f]);
                    }
                    if (!go) stopped[r] = true;
                }
                sink.Clear();                                // this plugin's share of the chunk is done with
            }
        }
    }

    /// <summary>Which rows of <paramref name="count"/> fall in the chunk starting at <paramref name="start"/>.</summary>
    internal static int ChunkEnd(int start, int count) => Math.Min(start + ComparisonChunkRows, count);
}
