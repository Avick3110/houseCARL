using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// GraphCycles — the cycle question, asked of a walked graph's EDGES rather than of its traversal.
//
// Every walk here dedupes on a visited set, and a visited set cannot tell a diamond (two paths to one
// record) from a genuine cycle (a record that reaches itself). Neither can the traversal TREE: a mutual
// reference between two SIBLINGS is a real cycle whose nodes are never each other's tree ancestors. So
// the walk records its edges and asks this afterwards, once, over the whole graph.
//
// Shared by every walk that keeps its edges, so one answer to "is this a cycle" serves all of them.
//
// What it answers is "are there loops here, and what does one look like", not "how many distinct loops are
// there" — the latter is exponential to enumerate. Find's summary states the bound; every render of its
// result has to carry that bound too rather than call the number a total.

/// <summary>Cycle finding over a walked link graph.</summary>
public static class GraphCycles
{
    /// <summary>One loop per BACK EDGE in the recorded edge set. Each result is the loop itself, keys in order: it
    /// starts at the record pointed back at and ends at the record whose link closed the loop, so the closing hop is
    /// from the last key to the first.
    /// <para>A depth-first pass colouring nodes unvisited / on-stack / finished: an edge into an ON-STACK node is a
    /// back edge, which is exactly "this record reaches itself".</para>
    /// <para>What the caller may claim from this: NONE means the graph is acyclic, because a graph with a loop always
    /// has a back edge. A count is a LOWER BOUND on the number of distinct loops, and the loops returned are not a
    /// complete list of the records lying on one — an edge into an already FINISHED node is skipped, so a second loop
    /// running through a record this pass has explored is not enumerated separately. (A -> B, A -> C, B -> A, C -> B
    /// carries two loops and reports the one back edge B -> A.) Enumerating every simple cycle is exponential in the
    /// graph, which is not a cost a read may pay; the claim is worded to what this costs instead.</para>
    /// <para>Only a node with recorded edges can be ON a cycle — a boundary the walk kept was never expanded, has no
    /// outgoing edges, and so closes nothing. Edges into those are skipped rather than treated as dead ends.</para>
    /// <para>One cycle per (from, to) pair: a record linking the same target twice is one cycle stated twice, not
    /// two facts.</para></summary>
    /// <param name="limit">Stop collecting after this many loops and say so through <paramref name="capped"/>. A
    /// strongly connected region of n nodes carries up to n-squared back edges, each holding a path of up to n keys,
    /// so an uncapped collection is quadratic in the region and not linear in the walk's node cap — an OOM on a read.
    /// The cap costs the claim nothing: the count was already a lower bound.</param>
    /// <param name="capped">True when <paramref name="limit"/> stopped the search, so the caller can say so rather
    /// than pass a stopped search off as a finished one.</param>
    public static List<IReadOnlyList<FormKey>> Find(IReadOnlyDictionary<FormKey, List<FormKey>> edges,
                                                    int limit, out bool capped)
    {
        const int Unvisited = 0, OnStack = 1, Finished = 2;
        capped = false;
        var cycles = new List<IReadOnlyList<FormKey>>();
        if (limit <= 0) { capped = edges.Count > 0; return cycles; }
        var state = new Dictionary<FormKey, int>();
        var path = new List<FormKey>();
        var reported = new HashSet<(FormKey From, FormKey To)>();

        foreach (var root in edges.Keys)
        {
            if (state.TryGetValue(root, out var rootState) && rootState != Unvisited) continue;

            // Explicit stack rather than recursion: the node cap bounds the graph, but a 128-deep chain is still
            // no reason to put the walk's shape on the CLR's stack.
            var work = new Stack<(FormKey Key, int Index)>();
            state[root] = OnStack; path.Add(root); work.Push((root, 0));

            while (work.Count > 0)
            {
                var (key, index) = work.Pop();
                var outgoing = edges.TryGetValue(key, out var o) ? o : null;
                if (outgoing is null || index >= outgoing.Count)
                {
                    state[key] = Finished;
                    path.RemoveAt(path.Count - 1);      // finished nodes are always the deepest still on the path
                    continue;
                }
                work.Push((key, index + 1));

                var next = outgoing[index];
                if (!edges.ContainsKey(next)) continue;  // a kept boundary — expanded nothing, so it closes nothing
                var nextState = state.TryGetValue(next, out var s) ? s : Unvisited;
                if (nextState == OnStack)
                {
                    if (reported.Add((key, next)))
                    {
                        var at = path.IndexOf(next);
                        cycles.Add(path.Skip(at).ToList());
                        // The whole search stops here, not just the collecting: walking on would pay the quadratic
                        // cost to find loops nothing will keep.
                        if (cycles.Count >= limit) { capped = true; return cycles; }
                    }
                    continue;
                }
                if (nextState == Finished) continue;     // an ordinary diamond: already explored, not on this path
                state[next] = OnStack; path.Add(next); work.Push((next, 0));
            }
        }
        return cycles;
    }
}
