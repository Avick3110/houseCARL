using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueInfoOrder — the EFFECTIVE, MERGED INFO order of a dialogue topic (#275)
//  (xEdit INOM/INOA parity: the sequence the game walks top-to-bottom when picking which line plays.)
//
//  WHY THIS EXISTS. A topic's lines are ordered, and the game plays the FIRST INFO whose conditions pass.
//  When two lines both pass, POSITION decides the outcome — so a pure reorder is a behaviour change with no
//  field delta anywhere. No single record holds that order: each plugin's DIAL carries only ITS OWN child
//  list, and the effective sequence is the MERGE of every touching plugin's list in load order.
//
//  THE MODEL (derived from xEdit's TwbGroupRecord.Sort/ProcessDIAL — the community's reference implementation
//  of engine behaviour — and confirmed twice empirically, see below):
//
//    for each touching plugin, in LOAD ORDER:
//      for each INFO in THAT plugin's child list, in ITS order:
//        1. EVICT every copy of that INFO already placed (xEdit's InsertEntry* all call RemoveEntry first, and
//           RemoveEntry drops the master copy AND every override copy) — so exactly one entry per FormKey
//           survives, and THE LAST PLUGIN TO LIST AN INFO OWNS ITS POSITION.
//        2. place it:  PNAM absent               -> TAIL (append)
//                      PNAM present, unresolvable -> HEAD
//                      PNAM resolves to T         -> immediately AFTER T (T placed first if absent, cycle-guarded)
//
//  EMPIRICAL CONFIRMATION (2026-07-27, live load order). HirelingQuestTopic1 (0BCC84:Skyrim.esm): Skyrim.esm
//  lists 8 INFOs, USSEP re-lists 6 (PNAM-chained in vanilla relative order — a no-op), then two plugins each
//  re-list ONLY 01A18E (the "you can't afford me" refusal) with no PNAM. The model evicts it from #0 and
//  appends it, landing it LAST — putting the hire line 01A1CD ahead of it, which is exactly the reported bug
//  (hired at 79 gold because the refusal is no longer reached first). Reproduced independently of the report.
//
//  WHAT THIS CORRECTS. The prior "DIAL-wins-wholesale" model (the winning topic's Responses IS the in-game INFO
//  set, so a line no other plugin re-lists is DROPPED) is FALSE and is superseded here. In the case above the
//  winning topic carries ONE INFO while the game plays EIGHT, and the line that misfired is not in the winner's
//  list at all — it could not have fired if non-relisted lines were dropped. Non-relisting drops NOTHING; it
//  REORDERS. (That model was recorded Aaron-confirmed 2026-06-19; superseded on the evidence above, #275.)
//
//  Q3 BOUNDARY — the one thing this cannot see. "PNAM absent" and "PNAM present but zero" place at OPPOSITE ENDS
//  (tail vs head), and Mutagen does NOT preserve that distinction (measured — see PnamZeroIsDistinguishable). It
//  applies to ANY topic of 2+ lines, contested or not, and nothing in the data marks which line is affected — so
//  it is disclosed ONCE PER REPORT in the validator's standing-limits footer (the established home for a standing
//  caveat), never as a per-topic note that would fire on every topic and stop being read.
//
//  INPUT IS PLAIN DATA (InfoLine), not record getters — deliberately. The caller projects each plugin's child list
//  while it still holds the body (overlay bodies are consume-before-advance), so this merge can run after every
//  overlay is gone, and a batched loader can pull many topics from ONE typed scan per plugin instead of an
//  unindexed whole-overlay lookup per (topic, plugin). It also makes the merge testable without a load order.
//
//  PURE + NEVER THROWS: a deterministic in-memory merge with no I/O. Malformed input (a self-referencing PNAM, a
//  PNAM cycle, a chain past the depth ceiling) DEGRADES to a stated placement and is reported on the view's Note —
//  never an exception, never a silent guess.
// ======================================================================

/// <summary>One INFO as the merge needs it: its identity, the PNAM (previous-line) link it carries — null when the
/// subrecord is absent, which is the common case — and whether its placing copy is flagged deleted. A projection of
/// <c>IDialogResponsesGetter</c> taken while the body is live (see <see cref="DialogueInfoOrder.LinesOf"/>).</summary>
public sealed record InfoLine(FormKey Info, FormKey? PreviousDialog, bool Deleted);

/// <summary>Which PNAM arm decided this line's position AT THE MOMENT IT WAS PLACED. Surfaced so a reader can see
/// WHY a line sits where it does, not just that it does (Q3: the merge is explainable, never a bare list).
///
/// It is a record of the placement DECISION, not a standing claim about the final list: a later plugin can evict and
/// re-place other lines around this one, so an <see cref="AfterTarget"/> line need not still sit immediately after
/// its target once the merge finishes. That is faithful to the model — the engine places in the same order — but do
/// not read the label as a post-merge invariant.</summary>
public enum InfoPlacement
{
    /// <summary>No PNAM — appended at the end of the list as it stood. The overwhelmingly common arm, and the one
    /// that moves a re-listed line to the BOTTOM of a topic.</summary>
    Tail,

    /// <summary>A PNAM that names no reachable INFO — placed at the head. Covers a PNAM pointing at a record no
    /// active plugin defines, one that closes a cycle, one naming its own record, and a chain past the depth
    /// ceiling. (It does NOT cover a zero PNAM: that is indistinguishable from absent — see
    /// <see cref="DialogueInfoOrder.PnamZeroIsDistinguishable"/> — so such a line takes the Tail arm.)</summary>
    Head,

    /// <summary>PNAM resolved — placed immediately after its target.</summary>
    AfterTarget,
}

/// <summary>One INFO's place in the effective order: its <see cref="Index"/> (0-based) in the merged sequence, the
/// <see cref="PlacedBy"/> plugin whose child list LAST carried it (the plugin that owns its position), the
/// <see cref="Placement"/> arm that decided it, and <see cref="OriginIndex"/> — its 0-based index in the DEFINING
/// plugin's own list, or null when a later plugin introduced it. <see cref="Deleted"/> marks an INFO whose placing
/// copy is flagged deleted: it still occupies a slot in the merge (the engine walks it), so it is shown, never
/// silently dropped.
///
/// <see cref="Moved"/> is deliberately NOT "the index changed". Moving one line to the bottom shifts every line
/// after it up by one, so an index comparison marks the whole topic as moved and buries the one line that actually
/// went somewhere. This flags only lines that changed RELATIVE order — the minimal set whose displacement explains
/// the difference (see <see cref="DialogueInfoOrder"/>'s LCS pass).</summary>
public sealed record InfoOrderEntry(
    FormKey Info, int Index, string PlacedBy, InfoPlacement Placement, int? OriginIndex, bool Deleted, bool Moved);

/// <summary>The effective merged INFO order for one topic: the <see cref="Order"/> the game walks top-to-bottom,
/// the <see cref="ContributingPlugins"/> whose child lists fed the merge (load order), the <see cref="Moved"/> lines
/// (worst displacement first), and <see cref="Note"/> — a Q3 caveat when part of the merge DEGRADED on malformed or
/// oversized input (null when it ran clean). The standing PNAM-zero fidelity limit is NOT carried here; it applies
/// to every topic and is disclosed once per report by the validator's footer.
/// <see cref="Contested"/> is the whole point of the view: with one contributing plugin the effective order IS
/// that plugin's list and there is nothing to reconcile.</summary>
public sealed record InfoOrderView(
    IReadOnlyList<InfoOrderEntry> Order,
    IReadOnlyList<string> ContributingPlugins,
    IReadOnlyList<InfoOrderEntry> Moved,
    string? Note)
{
    /// <summary>More than one plugin contributed a child list — the case where the merged order can differ from
    /// any single plugin's own list. Only meaningful when <see cref="Complete"/>: with a contributor missing, a
    /// contested topic can look uncontested.</summary>
    public bool Contested => ContributingPlugins.Count > 1;

    /// <summary>Plugins the load-order index says TOUCH this topic whose child list could not be read, so their
    /// lines are absent from <see cref="Order"/>. Empty in the normal case.</summary>
    public IReadOnlyList<string> UnreadContributors { get; init; } = Array.Empty<string>();

    /// <summary>Every touching plugin's list made it into the merge. When false the order is built from FEWER
    /// lists than the load order has, so neither it nor a "nothing merges here" reading of it is authoritative —
    /// the render must not state either as fact (Q3).</summary>
    public bool Complete => UnreadContributors.Count == 0;
}

public static class DialogueInfoOrder
{
    /// <summary>Whether Mutagen's read surface distinguishes a PNAM subrecord that is ABSENT from one that is
    /// PRESENT-but-zero. The two place at OPPOSITE ENDS (tail vs head), so this is the fidelity ceiling of the whole
    /// merge.
    ///
    /// MEASURED FALSE (2026-07-27, Mutagen as pinned by this build): a PNAM written as an explicit null round-trips
    /// through the binary overlay indistinguishably from one that was never written — both read back as no link. So
    /// houseCARL CANNOT see the "I am first" marker, and a line carrying one is placed at the TAIL where xEdit would
    /// place it at the HEAD. Head and tail coincide only for the first line placed into an empty list, so this can
    /// bite ANY topic of 2+ lines — which is why the disclosure is a standing footer bullet, not a conditional note.
    ///
    /// PINNED BY <c>dialogue-info-order-guard</c> against a real write→read round-trip — asserted from measurement,
    /// never from the model, so a Mutagen bump that gains (or loses) the distinction fails CI instead of silently
    /// changing every computed order.</summary>
    public static bool PnamZeroIsDistinguishable => false;

    /// <summary>Move analysis is O(n·m) over a topic's line count; past this many lines it is skipped and said to be
    /// skipped (a pathological topic must not turn an on-demand validate into a stall). Real topics are far under.</summary>
    const int MaxMoveAnalysisLines = 400;

    /// <summary>Ceiling on PNAM-chain recursion depth. Placing a line whose PNAM target is not yet placed recurses
    /// one frame per hop, so an adversarial or bulk-generated topic of thousands of FORWARD-linked lines could
    /// otherwise exhaust the thread stack — and a StackOverflowException cannot be caught, so it would take the whole
    /// server down, not just this call (the one failure mode the never-throws contract cannot absorb). Past this
    /// depth the line takes the unresolvable arm and the view says the chain was truncated. Real topics carry tens
    /// of lines; this is a backstop, not a working limit.</summary>
    const int MaxChainDepth = 400;

    /// <summary>Project a live topic body's child list into the plain data the merge consumes. Call this while the
    /// body is still valid (overlay bodies are consume-before-advance); the result outlives the overlay.</summary>
    public static IReadOnlyList<InfoLine> LinesOf(IDialogTopicGetter topic)
    {
        if (topic.Responses is not { Count: > 0 } responses) return Array.Empty<InfoLine>();
        var lines = new List<InfoLine>(responses.Count);
        foreach (var r in responses) lines.Add(LineOf(r));
        return lines;
    }

    /// <summary>Project ONE live INFO body into the merge's data. The single home for that projection — the fallback
    /// resolver builds lines too, and a field added here must reach both sites or the two silently disagree.</summary>
    public static InfoLine LineOf(IDialogResponsesGetter info) =>
        new(info.FormKey, info.PreviousDialog.FormKeyNullable, info.IsDeleted);

    /// <summary>Merge every touching plugin's child list into the effective INFO order for one topic.
    /// <paramref name="groups"/> is the per-plugin (name, projected lines) sequence in LOAD ORDER, winner last — a
    /// plugin whose child list is empty contributes nothing and is not counted as contributing.
    /// <paramref name="resolveInfo"/> is the FALLBACK for a PNAM target that appears in none of the groups (it
    /// resolves the target to its winning line + the plugin it came from, so a recursively-placed target is credited
    /// to ITS OWN plugin, never to the one that pulled it in); it may return null, which is the unresolvable arm
    /// (HEAD), never a throw. Targets that DO appear in the groups are served from those, so the fallback — whose
    /// implementation is typically an expensive per-record lookup — is reached only for a genuinely foreign target.
    /// Deterministic and pure — the same inputs always give the same order.</summary>
    public static InfoOrderView Compute(
        IReadOnlyList<(string Plugin, IReadOnlyList<InfoLine> Lines)> groups,
        Func<FormKey, (InfoLine Line, string Plugin)?> resolveInfo,
        IReadOnlyList<string>? unreadContributors = null)
    {
        var state = new MergeState { Fallback = resolveInfo };
        var contributing = new List<string>();

        // The DEFINING plugin's own list is the baseline a "moved" verdict is measured against — the order the
        // topic's author laid down. First contributing plugin = the one that defines the topic (load order).
        IReadOnlyDictionary<FormKey, int>? originIdx = null;

        // Every line any group carries, so a PNAM target within the topic never pays the fallback resolver.
        foreach (var (plugin, lines) in groups)
            foreach (var line in lines)
                state.Known[line.Info] = (line, plugin);

        foreach (var (plugin, lines) in groups)
        {
            if (lines.Count == 0) continue;                       // an override carrying no child list places nothing
            contributing.Add(plugin);

            originIdx ??= lines
                .Select((l, i) => (l.Info, i))
                .GroupBy(p => p.Info)                             // a malformed duplicate keeps its FIRST index
                .ToDictionary(g => g.Key, g => g.First().i);

            foreach (var line in lines)
                Place(state, line, plugin, depth: 0);
        }

        var order = state.Order;

        // Which lines actually changed RELATIVE order (not merely index — see InfoOrderEntry.Moved).
        var movedKeys = originIdx is null || order.Count > MaxMoveAnalysisLines
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

        state.Cycles = CountPnamCycles(order, state.Placed);
        return new InfoOrderView(entries, contributing, moved,
                                 BuildNote(state, order.Count, originIdx is not null, unreadContributors))
            { UnreadContributors = unreadContributors ?? Array.Empty<string>() };
    }

    /// <summary>The DEGRADATION note: what part of this merge did not run cleanly, and on what input. Null when the
    /// merge was fully determined. Each clause names a concrete malformation so the reader can act on it (Q3) —
    /// these are data problems in the plugins, not tool limits.</summary>
    static string? BuildNote(MergeState state, int lineCount, bool haveOrigin,
                             IReadOnlyList<string>? unreadContributors)
    {
        var parts = new List<string>();
        // A plugin the index says TOUCHES this topic whose child list could not be read. Q3: the merge below is
        // built from FEWER lists than the load order actually has, so the order — and any "single plugin, nothing
        // merges" reading of it — is NOT authoritative. Never silently absorbed into a clean-looking result.
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
            parts.Add($"{state.Cycles} PNAM cycle(s) — the line closing each loop is placed as if its link were " +
                      "unresolvable, since no order satisfies a cycle");
        if (state.DepthCapped)
            parts.Add($"a PNAM chain ran past the {MaxChainDepth}-hop ceiling and was truncated — the lines beyond " +
                      "it are placed as if unlinked, so their order is not authoritative");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Place ONE INFO per the model: evict every prior copy, then tail / head / after-target. A PNAM target
    /// not yet placed is placed FIRST (recursively, from the topic's own lines where possible) exactly as xEdit does,
    /// so a chain of PNAM-linked lines lands in chain order regardless of the order the file lists them.
    ///
    /// Every malformed shape degrades to the HEAD (unresolvable) arm rather than throwing: a PNAM naming its own
    /// record, a cycle (the line that closes the loop), and a chain past <see cref="MaxChainDepth"/>. The final
    /// insert index is clamped because the recursion can mutate the list under this frame — the self-reference case
    /// reached a negative-length insert before the explicit guard was added.</summary>
    static void Place(MergeState state, InfoLine line, string plugin, int depth)
    {
        var order = state.Order;
        var fk = line.Info;
        order.Remove(fk);                                        // evict every prior copy — last lister owns position
        state.Placed[fk] = new Placed(plugin, InfoPlacement.Tail, line.Deleted, line.PreviousDialog);

        void Head()
        {
            order.Insert(0, fk);
            state.Placed[fk] = new Placed(plugin, InfoPlacement.Head, line.Deleted, line.PreviousDialog);
        }

        var prev = line.PreviousDialog;

        if (prev is null)                                        // PNAM absent — the common arm
        {
            order.Add(fk);
            return;
        }

        if (prev.Value.IsNull)                                   // PNAM present but zero (unreachable today —
        {                                                        // Mutagen collapses it to absent; kept explicit so
            Head();                                              // the arm exists if that ever changes)
            return;
        }

        if (prev.Value == fk)                                    // a PNAM naming its OWN record — malformed
        {
            state.SelfReferencing.Add(fk);
            Head();
            return;
        }

        int at = order.IndexOf(prev.Value);
        if (at < 0 && depth < MaxChainDepth && state.Stack.Add(fk))    // target not placed yet — place it first
        {
            var target = state.Known.TryGetValue(prev.Value, out var known) ? known : state.Fallback?.Invoke(prev.Value);
            if (target is { } t) Place(state, t.Line, t.Plugin, depth + 1);
            state.Stack.Remove(fk);
            at = order.IndexOf(prev.Value);

            // Under a PNAM CYCLE the recursion above walks back around and places THIS record — so the eviction
            // at the top of this call is stale and inserting now would list the line twice. Evict again; the
            // insert below is the one that stands.
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
            Head();
            return;
        }

        order.Insert(Math.Clamp(at + 1, 0, order.Count), fk);
        state.Placed[fk] = new Placed(plugin, InfoPlacement.AfterTarget, line.Deleted, line.PreviousDialog);
    }

    /// <summary>The lines that changed RELATIVE order between the defining plugin's list and the effective one:
    /// everything outside a longest common subsequence of the two. That is the MINIMAL set whose displacement
    /// explains the difference — move one line to the bottom and this names that one line, where comparing indices
    /// would name every line it shifted past.
    ///
    /// Sibling detector: <c>FieldsDiff</c>'s ORDER-DIFFERS check answers the coarser "did this list get reordered at
    /// all" for an arbitrary two-sided field diff (conflict_tree / diff_record). This one answers "which lines moved"
    /// for an N-plugin merge against its origin. Different granularity and different call sites, deliberately kept
    /// separate — but a change to reorder semantics should consider both.</summary>
    static HashSet<FormKey> RelativeOrderChanges(List<FormKey> effective, IReadOnlyDictionary<FormKey, int> originIdx)
    {
        // Both sequences restricted to the lines they SHARE — a line added by a later plugin never "moved", and a
        // line the defining plugin listed but nothing carries forward isn't in the effective order to move.
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

    /// <summary>Count PNAM cycles as a property of the DATA, over the final placed line set — not as a by-product
    /// of the recursion. The placement-time signal (the cycle guard tripping) only fires when a target is not yet
    /// placed, so a cycle whose members were BOTH already placed by an earlier plugin — e.g. plugin A lists a and b
    /// unlinked, then plugin B re-lists both pointing at each other — went entirely undetected and the report read
    /// clean on unsatisfiable input (PR #293 re-review). Each line has at most ONE PNAM edge, so this is a walk over
    /// a functional graph: follow each unvisited chain and count a cycle when it re-enters the current walk.
    /// Self-edges are excluded — those are reported as self-references, and would otherwise be counted twice.</summary>
    static int CountPnamCycles(IReadOnlyList<FormKey> order, IReadOnlyDictionary<FormKey, Placed> placed)
    {
        const int OnThisWalk = 1, Settled = 2;
        var seen = new Dictionary<FormKey, int>(order.Count);
        int cycles = 0;

        foreach (var start in order)
        {
            if (seen.ContainsKey(start)) continue;
            var walk = new List<FormKey>();
            var cur = start;
            while (true)
            {
                if (seen.TryGetValue(cur, out int st))
                {
                    if (st == OnThisWalk) cycles++;               // re-entered this walk — a genuine loop
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
        return cycles;
    }

    /// <summary>One line's placement outcome — bundled so every exit path of <see cref="Place"/> writes the whole
    /// tuple at once. (Three parallel dictionaries let a new code path update two of three and produce a
    /// silently-wrong entry behind the defaults; this makes that a compile error instead.)</summary>
    readonly record struct Placed(string PlacedBy, InfoPlacement Placement, bool Deleted, FormKey? Pnam);

    /// <summary>The merge's working state, threaded through the recursion as one object rather than as a growing
    /// parameter list.</summary>
    sealed class MergeState
    {
        public readonly List<FormKey> Order = new();
        public readonly Dictionary<FormKey, Placed> Placed = new();
        public readonly Dictionary<FormKey, (InfoLine Line, string Plugin)> Known = new();
        public readonly HashSet<FormKey> Stack = new();
        public readonly HashSet<FormKey> SelfReferencing = new();
        public Func<FormKey, (InfoLine Line, string Plugin)?>? Fallback;
        public int Cycles;
        public bool DepthCapped;
    }
}
