using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// The cycle question, asked of a walked graph's EDGES rather than of its traversal: a visited set cannot tell a
// diamond from a genuine cycle, and neither can the traversal tree. Shared by every walk that keeps its edges.

/// <summary>Cycle finding over a walked link graph.</summary>
public static class GraphCycles
{
    /// <summary>One loop per BACK EDGE in the recorded edge set, keys in order, closing from the last key back to the
    /// first; one cycle per (from, to) pair, and only nodes with recorded edges can be on one. NONE means acyclic; a
    /// count is a LOWER BOUND on the distinct loops, and the loops returned are not a complete list of the records
    /// LYING on one — an edge into an already finished node is skipped.</summary>
    /// <param name="capped">True when <paramref name="limit"/> stopped the search rather than the search finishing.</param>
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

            // Explicit stack rather than recursion, so the walk's shape never sits on the CLR's stack.
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
                        // The whole search stops here, not just the collecting.
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
