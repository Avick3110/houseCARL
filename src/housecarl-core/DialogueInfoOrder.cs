using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueInfoOrder — the EFFECTIVE, MERGED INFO order of a dialogue topic (xEdit INOM/INOA parity). The model,
// the PNAM-zero axis, why the input is plain data and why the merge never throws are contracts in
// docs/architecture/dialogue.md; xEdit's TwbGroupRecord.Sort/ProcessDIAL is the reference implementation.

/// <summary>One INFO as the merge needs it: identity, PNAM link, and whether its placing copy is deleted.</summary>
public sealed record InfoLine(FormKey Info, FormKey? PreviousDialog, bool Deleted);

/// <summary>Which PNAM arm decided this line's position when it was placed — a decision, not an invariant.</summary>
public enum InfoPlacement
{
    /// <summary>No PNAM — appended at the end; the common arm, and the one that moves a re-list to the BOTTOM.</summary>
    Tail,

    /// <summary>A PNAM present with value ZERO — the "I am first" marker, placed at the head. Not a fault.</summary>
    HeadFirstMarker,

    /// <summary>A PNAM naming no reachable INFO, placed at the head and worth a second look. NOT cycle members,
    /// whose outer frame overwrites the arm — cycles surface through the view's Note instead.</summary>
    HeadUnresolvable,

    /// <summary>PNAM resolved — placed immediately after its target.</summary>
    AfterTarget,
}

/// <summary>One INFO's place in the effective order, and how it got there. A deleted placing copy still occupies
/// a slot. <see cref="Moved"/> is RELATIVE order, not index; contract in docs/architecture/dialogue.md.</summary>
public sealed record InfoOrderEntry(
    FormKey Info, int Index, string PlacedBy, InfoPlacement Placement, int? OriginIndex, bool Deleted, bool Moved);

/// <summary>The effective merged INFO order for one topic, its contributors, its moved lines, its note.</summary>
public sealed record InfoOrderView(
    IReadOnlyList<InfoOrderEntry> Order,
    IReadOnlyList<string> ContributingPlugins,
    IReadOnlyList<InfoOrderEntry> Moved,
    string? Note)
{
    /// <summary>More than one plugin contributed a child list. Only meaningful when <see cref="Complete"/>.</summary>
    public bool Contested => ContributingPlugins.Count > 1;

    /// <summary>Touching plugins whose child list could not be read, so their lines are absent from the order.</summary>
    public IReadOnlyList<string> UnreadContributors { get; init; } = Array.Empty<string>();

    /// <summary>Whether the move analysis RAN; when false, an empty <see cref="Moved"/> means "not computed".</summary>
    public bool MovesComputed { get; init; } = true;

    /// <summary>Whether OriginIndex really is the DEFINING plugin's list; EVERY origin claim is gated on it.</summary>
    public bool BaselineTrusted { get; init; } = true;

    /// <summary>Every touching plugin's list made it in; when false, nothing read off the order is authoritative.</summary>
    public bool Complete => UnreadContributors.Count == 0;

    /// <summary>The OFF-ORDER plugin folded in; every render carrying one must say so and mark its lines.</summary>
    public string? FoldedPlugin { get; init; }

    /// <summary>Where that file was placed and why, stated per topic — it decides which lines the fold evicted.</summary>
    public string? FoldedPlacement { get; init; }

    /// <summary>Did the folded file actually place a line in this topic — "does my patch move this one".</summary>
    public bool FoldContributed => FoldedPlugin is { } p
        && ContributingPlugins.Any(c => c.Equals(p, StringComparison.OrdinalIgnoreCase));
}

public static class DialogueInfoOrder
{
    /// <summary>Whether an ABSENT PNAM subrecord reads back distinctly from a PRESENT-but-zero one — TRUE, and the
    /// merge's fidelity ceiling; pinned by DialogueInfoOrderProbe's PNAM-ZERO-AXIS and WRITER-DROPS-NULL.</summary>
    public static bool PnamZeroIsDistinguishable => true;

    /// <summary>Move analysis is O(n·m); past this many lines it is skipped, and said to be skipped.</summary>
    const int MaxMoveAnalysisLines = 400;

    /// <summary>Ceiling on PNAM-chain recursion depth — one frame per hop, and a stack overflow cannot be caught.</summary>
    const int MaxChainDepth = 400;

    /// <summary>Project a live topic body's child list into the merge's data; call it while the body is valid.</summary>
    public static IReadOnlyList<InfoLine> LinesOf(IDialogTopicGetter topic)
    {
        if (topic.Responses is not { Count: > 0 } responses) return Array.Empty<InfoLine>();
        var lines = new List<InfoLine>(responses.Count);
        foreach (var r in responses) lines.Add(LineOf(r));
        return lines;
    }

    /// <summary>Project ONE live INFO body into the merge's data — the single home for that projection.</summary>
    public static InfoLine LineOf(IDialogResponsesGetter info) =>
        new(info.FormKey, info.PreviousDialog.FormKeyNullable, info.IsDeleted);

    /// <summary>Merge every touching plugin's child list into the effective INFO order for one topic, from the
    /// per-plugin (name, lines) sequence in LOAD ORDER. <paramref name="resolveInfo"/> is the FALLBACK for a PNAM
    /// target in none of the groups; it may return null, the unresolvable arm, never a throw.</summary>
    /// <param name="projectedPlugin">a group that is a PROJECTION and not this topic's definer, so it merges like
    /// any contributor but sets no move baseline; null where the folded file IS the definer.</param>
    public static InfoOrderView Compute(
        IReadOnlyList<(string Plugin, IReadOnlyList<InfoLine> Lines)> groups,
        Func<FormKey, (InfoLine Line, string Plugin)?> resolveInfo,
        IReadOnlyList<string>? unreadContributors = null,
        bool originIsDefiningPlugin = true,
        string? projectedPlugin = null)
    {
        var state = new MergeState { Fallback = resolveInfo };
        var contributing = new List<string>();

        // The DEFINING plugin's own list is the baseline a "moved" verdict is measured against.
        IReadOnlyDictionary<FormKey, int>? originIdx = null;

        // Every line any group carries, so a PNAM target within the topic never pays the fallback resolver.
        foreach (var (plugin, lines) in groups)
            foreach (var line in lines)
                state.Known[line.Info] = (line, plugin);

        foreach (var (plugin, lines) in groups)
        {
            if (lines.Count == 0) continue;                       // an override carrying no child list places nothing
            contributing.Add(plugin);

            // The projection never sets the baseline; everything else merges as a plugin there would.
            if (!plugin.Equals(projectedPlugin, StringComparison.OrdinalIgnoreCase))
                originIdx ??= lines
                    .Select((l, i) => (l.Info, i))
                    .GroupBy(p => p.Info)                         // a malformed duplicate keeps its FIRST index
                    .ToDictionary(g => g.Key, g => g.First().i);

            foreach (var line in lines)
                Place(state, line, plugin, depth: 0);
        }

        var order = state.Order;

        // Skipped where the baseline is untrustworthy: a spurious moved set is stated as fact by the render.
        var movedKeys = originIdx is null || order.Count > MaxMoveAnalysisLines || !originIsDefiningPlugin
            ? new HashSet<FormKey>()
            : RelativeOrderChanges(order, originIdx);

        var entries = new List<InfoOrderEntry>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            var fk = order[i];
            int? origin = originIdx is not null && originIdx.TryGetValue(fk, out int o) ? o : null;
            var p = state.Placed.GetValueOrDefault(fk, new Placed("?", InfoPlacement.Tail, false, null));
            entries.Add(new InfoOrderEntry(fk, i, p.PlacedBy, p.Placement, origin, p.Deleted, movedKeys.Contains(fk)));
        }

        var moved = entries.Where(e => e.Moved)
                           .OrderByDescending(e => Math.Abs(e.Index - (e.OriginIndex ?? e.Index)))
                           .ToList();

        (state.Cycles, state.CycleMembers) = CountPnamCycles(order, state.Placed);
        return new InfoOrderView(entries, contributing, moved,
                                 BuildNote(state, order.Count, originIdx is not null, unreadContributors, originIsDefiningPlugin))
            { UnreadContributors = unreadContributors ?? Array.Empty<string>(),
              BaselineTrusted = originIsDefiningPlugin,
              MovesComputed = originIdx is not null && order.Count <= MaxMoveAnalysisLines && originIsDefiningPlugin };
    }

    /// <summary>The DEGRADATION note: what did not run cleanly, and on what input. Data problems, not tool limits.</summary>
    static string? BuildNote(MergeState state, int lineCount, bool haveOrigin,
                             IReadOnlyList<string>? unreadContributors, bool originIsDefiningPlugin)
    {
        var parts = new List<string>();
        if (haveOrigin && !originIsDefiningPlugin)
            parts.Add("the plugin that DEFINES this topic is among those that could not be read, so the baseline " +
                      "for \"which lines moved\" would be a later plugin's list — move analysis was SKIPPED rather " +
                      "than measured against the wrong order");
        // A touching plugin whose list could not be read: the order is built from FEWER lists than the order has.
        if (unreadContributors is { Count: > 0 })
            parts.Add($"{unreadContributors.Count} plugin(s) that TOUCH this topic could not be read " +
                      $"({string.Join(", ", unreadContributors)}) — their lines are MISSING from the order below, " +
                      "so it is incomplete and any line's position may be wrong; re-run (a plugin moved or locked " +
                      "by MO2/xEdit mid-call is the usual cause)");
        if (haveOrigin && lineCount > MaxMoveAnalysisLines)
            parts.Add($"this topic carries {lineCount} lines, past the {MaxMoveAnalysisLines}-line ceiling for move " +
                      "analysis — the order above is exact, but which lines moved was NOT computed");
        if (state.SelfReferencing.Count > 0)
            parts.Add($"{state.SelfReferencing.Count} line(s) carry a PNAM naming their OWN record " +
                      $"({string.Join(", ", state.SelfReferencing.Take(3))}" +
                      (state.SelfReferencing.Count > 3 ? ", …" : "") +
                      ") — malformed, and placed as if the link were unresolvable");
        if (state.Cycles > 0)
            parts.Add($"{state.Cycles} PNAM cycle(s), among these lines: " +
                      string.Join(", ", state.CycleMembers.Take(4)) +
                      (state.CycleMembers.Count > 4 ? ", …" : "") +
                      " — no order satisfies a cycle, so the loop is broken at whichever of its lines ends up " +
                      "FIRST in the order below, and the positions of the lines INSIDE it are not authoritative " +
                      "(they carry no per-line annotation, which is why they are named here)");
        if (state.DepthCapped)
            parts.Add($"a PNAM chain ran past the {MaxChainDepth}-hop ceiling and was truncated — the lines beyond " +
                      "it are placed as if unlinked, so their order is not authoritative");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Place ONE INFO per the model: evict every prior copy, then tail / head / after-target, placing an
    /// unplaced PNAM target first. The final insert index MUST stay clamped — the recursion mutates the list.</summary>
    static void Place(MergeState state, InfoLine line, string plugin, int depth)
    {
        var order = state.Order;
        var fk = line.Info;
        order.Remove(fk);                                        // evict every prior copy — last lister owns position
        state.Placed[fk] = new Placed(plugin, InfoPlacement.Tail, line.Deleted, line.PreviousDialog);

        void Head(InfoPlacement why)
        {
            order.Insert(0, fk);
            state.Placed[fk] = new Placed(plugin, why, line.Deleted, line.PreviousDialog);
        }

        var prev = line.PreviousDialog;

        if (prev is null)                                        // PNAM absent — the common arm
        {
            order.Add(fk);
            return;
        }

        // PNAM present with value ZERO — the "I am first" marker. LOAD-BEARING: see PnamZeroIsDistinguishable.
        if (prev.Value.IsNull)
        {
            Head(InfoPlacement.HeadFirstMarker);
            return;
        }

        if (prev.Value == fk)                                    // a PNAM naming its OWN record — malformed
        {
            state.SelfReferencing.Add(fk);
            Head(InfoPlacement.HeadUnresolvable);
            return;
        }

        int at = order.IndexOf(prev.Value);
        if (at < 0 && depth < MaxChainDepth && state.Stack.Add(fk))    // target not placed yet — place it first
        {
            var target = state.Known.TryGetValue(prev.Value, out var known) ? known : state.Fallback?.Invoke(prev.Value);
            if (target is { } t) Place(state, t.Line, t.Plugin, depth + 1);
            state.Stack.Remove(fk);
            at = order.IndexOf(prev.Value);

            // Under a cycle the recursion above already placed THIS record, so evict again before inserting.
            int dup = order.IndexOf(fk);
            if (dup >= 0)
            {
                order.RemoveAt(dup);
                if (at > dup) at--;
            }
        }
        else if (at < 0 && depth >= MaxChainDepth)
        {
            state.DepthCapped = true;
        }

        if (at < 0)                                              // unreachable target (dangling, cycle, or truncated)
        {
            Head(InfoPlacement.HeadUnresolvable);
            return;
        }

        order.Insert(Math.Clamp(at + 1, 0, order.Count), fk);
        state.Placed[fk] = new Placed(plugin, InfoPlacement.AfterTarget, line.Deleted, line.PreviousDialog);
    }

    /// <summary>The lines that changed RELATIVE order — everything outside a longest common subsequence of the
    /// defining plugin's list and the effective one. Coarser sibling, kept separate: <c>FieldsDiff</c>.</summary>
    static HashSet<FormKey> RelativeOrderChanges(List<FormKey> effective, IReadOnlyDictionary<FormKey, int> originIdx)
    {
        // Both sequences restricted to the lines they SHARE: neither side's extras can have moved.
        var live = new HashSet<FormKey>(effective);
        var a = originIdx.Where(kv => live.Contains(kv.Key)).OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var b = effective.Where(originIdx.ContainsKey).ToList();

        int n = a.Count, m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var kept = new HashSet<FormKey>();
        for (int i = 0, j = 0; i < n && j < m; )
        {
            if (a[i] == b[j]) { kept.Add(a[i]); i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }

        var moved = new HashSet<FormKey>();
        foreach (var fk in b) if (!kept.Contains(fk)) moved.Add(fk);
        return moved;
    }

    /// <summary>Count PNAM cycles over the final placed set — the placement-time guard misses a cycle whose
    /// members an earlier plugin already placed. Self-edges are excluded and reported as self-references.</summary>
    static (int Count, List<FormKey> Members) CountPnamCycles(
        IReadOnlyList<FormKey> order, IReadOnlyDictionary<FormKey, Placed> placed)
    {
        const int OnThisWalk = 1, Settled = 2;
        var seen = new Dictionary<FormKey, int>(order.Count);
        int cycles = 0;
        var members = new List<FormKey>();

        foreach (var start in order)
        {
            if (seen.ContainsKey(start)) continue;
            var walk = new List<FormKey>();
            var cur = start;
            while (true)
            {
                if (seen.TryGetValue(cur, out int st))
                {
                    if (st == OnThisWalk)
                    {
                        cycles++;                                // re-entered this walk — a genuine loop
                        // Name the loop: no cycle member carries an annotation, so the Note is the only handle.
                        members.AddRange(walk.SkipWhile(k => k != cur));
                    }
                    break;
                }
                seen[cur] = OnThisWalk;
                walk.Add(cur);
                if (!placed.TryGetValue(cur, out var p) || p.Pnam is not { } next) break;   // no link — chain ends
                if (next == cur) break;                          // self-edge — reported as a self-reference
                if (!placed.ContainsKey(next)) break;             // dangling — reported by its Head placement
                cur = next;
            }
            foreach (var n in walk) seen[n] = Settled;
        }
        return (cycles, members);
    }

    /// <summary>One line's placement outcome, bundled so every exit path of <see cref="Place"/> writes it whole.</summary>
    readonly record struct Placed(string PlacedBy, InfoPlacement Placement, bool Deleted, FormKey? Pnam);

    /// <summary>The merge's working state, threaded through the recursion as one object.</summary>
    sealed class MergeState
    {
        public readonly List<FormKey> Order = new();
        public readonly Dictionary<FormKey, Placed> Placed = new();
        public readonly Dictionary<FormKey, (InfoLine Line, string Plugin)> Known = new();
        public readonly HashSet<FormKey> Stack = new();
        public readonly HashSet<FormKey> SelfReferencing = new();
        public Func<FormKey, (InfoLine Line, string Plugin)?>? Fallback;
        public int Cycles;
        public List<FormKey> CycleMembers = new();
        public bool DepthCapped;
    }
}
