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
//  (tail vs head), and whether Mutagen preserves that distinction is a property of the binary overlay, not of this
//  code. <see cref="DialogueInfoOrder.PnamZeroIsDistinguishable"/> is pinned by the guard probe against a real
//  round-trip; when it is false this class says so on the view's Note rather than quietly picking an end.
//
//  PURE + NEVER THROWS over a topic walk: the caller supplies the per-plugin bodies and a resolver closure; this
//  is a deterministic in-memory merge with no I/O of its own.
// ======================================================================

/// <summary>Where an INFO's placement rule put it — the PNAM arm that decided its position. Surfaced so a reader
/// can see WHY a line sits where it does, not just that it does (Q3: the merge is explainable, never a bare list).</summary>
public enum InfoPlacement
{
    /// <summary>No PNAM — appended at the end of the list as it stood. The overwhelmingly common arm, and the one
    /// that moves a re-listed line to the BOTTOM of a topic.</summary>
    Tail,

    /// <summary>A PNAM that names no reachable INFO — placed at the head. Covers both a zero PNAM (the "I am first"
    /// marker) and a PNAM pointing at a record no active plugin defines.</summary>
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
/// went somewhere. This flags only lines that changed RELATIVE order — the minimal set whose removal explains the
/// difference (see <see cref="DialogueInfoOrder"/>'s LCS pass).</summary>
public sealed record InfoOrderEntry(
    FormKey Info, int Index, string PlacedBy, InfoPlacement Placement, int? OriginIndex, bool Deleted, bool Moved);

/// <summary>The effective merged INFO order for one topic: the <see cref="Order"/> the game walks top-to-bottom,
/// the <see cref="ContributingPlugins"/> whose child lists fed the merge (load order), and <see cref="Note"/> —
/// a Q3 caveat when something about the merge could NOT be determined (null when the merge is fully determined).
/// <see cref="Contested"/> is the whole point of the view: with one contributing plugin the effective order IS
/// that plugin's list and there is nothing to reconcile.</summary>
public sealed record InfoOrderView(
    IReadOnlyList<InfoOrderEntry> Order,
    IReadOnlyList<string> ContributingPlugins,
    IReadOnlyList<InfoOrderEntry> Moved,
    string? Note)
{
    /// <summary>More than one plugin contributed a child list — the case where the merged order can differ from
    /// any single plugin's own list.</summary>
    public bool Contested => ContributingPlugins.Count > 1;
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
    /// place it at the HEAD. That is disclosed on <see cref="InfoOrderView.Note"/> wherever it could change a
    /// reading, never quietly absorbed (Q3).
    ///
    /// PINNED BY <c>dialogue-info-order-guard</c> against a real write→read round-trip — asserted from measurement,
    /// never from the model, so a Mutagen bump that gains (or loses) the distinction fails CI instead of silently
    /// changing every computed order.</summary>
    public static bool PnamZeroIsDistinguishable => false;

    /// <summary>Move analysis is O(n·m) over a topic's line count; past this many lines it is skipped and said to be
    /// skipped (a pathological topic must not turn an on-demand validate into a stall). Real topics are far under.</summary>
    const int MaxMoveAnalysisLines = 400;

    /// <summary>Merge every touching plugin's child list into the effective INFO order for one topic.
    /// <paramref name="groups"/> is the per-plugin (name, topic body) sequence in LOAD ORDER, winner last — a
    /// plugin whose child list is empty contributes nothing and is not counted as contributing.
    /// <paramref name="resolveInfo"/> resolves a PNAM target to its winning body AND the plugin that body came from
    /// (so a recursively-placed target is credited to ITS OWN plugin, never to the one that happened to pull it in);
    /// it may return null, which is the unresolvable arm (HEAD), never a throw.
    /// Deterministic and pure — the same inputs always give the same order.</summary>
    public static InfoOrderView Compute(
        IReadOnlyList<(string Plugin, IDialogTopicGetter Topic)> groups,
        Func<FormKey, (IDialogResponsesGetter Body, string Plugin)?> resolveInfo)
    {
        var order = new List<FormKey>();                         // the merged sequence, rebuilt as plugins apply
        var placedBy = new Dictionary<FormKey, string>();
        var placement = new Dictionary<FormKey, InfoPlacement>();
        var deleted = new Dictionary<FormKey, bool>();
        var contributing = new List<string>();
        string? note = null;

        // The DEFINING plugin's own list is the baseline a "moved" verdict is measured against — the order the
        // topic's author laid down. First contributing plugin = the one that defines the topic (load order).
        IReadOnlyDictionary<FormKey, int>? originIdx = null;

        foreach (var (plugin, topic) in groups)
        {
            var children = topic.Responses;
            if (children is null || children.Count == 0) continue;    // an override carrying no child list places nothing
            contributing.Add(plugin);

            originIdx ??= children
                .Select((c, i) => (c.FormKey, i))
                .GroupBy(p => p.FormKey)                              // a malformed duplicate keeps its FIRST index
                .ToDictionary(g => g.Key, g => g.First().i);

            foreach (var info in children)
                Place(info, plugin, order, placedBy, placement, deleted, resolveInfo, new HashSet<FormKey>());
        }

        // Which lines actually changed RELATIVE order (not merely index — see InfoOrderEntry.Moved).
        var movedKeys = originIdx is null || order.Count > MaxMoveAnalysisLines
            ? new HashSet<FormKey>()
            : RelativeOrderChanges(order, originIdx);
        if (originIdx is not null && order.Count > MaxMoveAnalysisLines)
            note = $"this topic carries {order.Count} lines, past the {MaxMoveAnalysisLines}-line ceiling for move " +
                   "analysis — the order above is exact, but which lines moved was NOT computed";

        var entries = new List<InfoOrderEntry>(order.Count);
        for (int i = 0; i < order.Count; i++)
        {
            var fk = order[i];
            int? origin = originIdx is not null && originIdx.TryGetValue(fk, out int o) ? o : null;
            entries.Add(new InfoOrderEntry(
                fk, i, placedBy.GetValueOrDefault(fk, "?"), placement.GetValueOrDefault(fk, InfoPlacement.Tail),
                origin, deleted.GetValueOrDefault(fk, false), movedKeys.Contains(fk)));
        }

        var moved = entries.Where(e => e.Moved)
                           .OrderByDescending(e => Math.Abs(e.Index - (e.OriginIndex ?? e.Index)))
                           .ToList();

        // The one thing the merge cannot see. Disclosed where it could change a reading — i.e. where lines moved
        // and the reader is about to act on that — rather than on every topic (a caveat shown everywhere is noise,
        // and noise is how a real caveat stops being read).
        if (note is null && !PnamZeroIsDistinguishable && moved.Count > 0)
            note = "a line whose PNAM was written as an explicit \"first\" marker is indistinguishable here from one " +
                   "carrying no PNAM at all, so such a line is placed LAST where the game would place it FIRST. Rare " +
                   "(xEdit writes that marker when it fills PNAM), but if a moved line above looks wrong, confirm it " +
                   "against xEdit's INOA row for this topic";

        return new InfoOrderView(entries, contributing, moved, note);
    }

    /// <summary>The lines that changed RELATIVE order between the defining plugin's list and the effective one:
    /// everything outside a longest common subsequence of the two. That is the MINIMAL set whose displacement
    /// explains the difference — move one line to the bottom and this names that one line, where comparing indices
    /// would name every line it shifted past. Ties are broken toward keeping earlier lines anchored, so the reported
    /// mover is the line that travelled, not its neighbours.</summary>
    static HashSet<FormKey> RelativeOrderChanges(List<FormKey> effective, IReadOnlyDictionary<FormKey, int> originIdx)
    {
        // Both sequences restricted to the lines they SHARE — a line added by a later plugin never "moved", and a
        // line the defining plugin listed but nothing carries forward isn't in the effective order to move.
        var a = originIdx.Where(kv => effective.Contains(kv.Key)).OrderBy(kv => kv.Value)
                         .Select(kv => kv.Key).ToList();
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

        var moved = new HashSet<FormKey>(b.Count);
        foreach (var fk in b) if (!kept.Contains(fk)) moved.Add(fk);
        return moved;
    }

    /// <summary>Place ONE INFO per the model: evict every prior copy, then tail / head / after-target. A PNAM target
    /// not yet placed is placed FIRST (recursively, off its winning body) exactly as xEdit does, so a chain of
    /// PNAM-linked lines lands in chain order regardless of the order the file lists them.
    /// <paramref name="stack"/> is the cycle guard — a PNAM cycle degrades to a TAIL placement for the record that
    /// closes the loop rather than recursing forever (xEdit reports the cycle and aborts the insert; we place it and
    /// keep going, because a partial order beats no order and the record still has to appear somewhere).</summary>
    static void Place(
        IDialogResponsesGetter info, string plugin,
        List<FormKey> order, Dictionary<FormKey, string> placedBy,
        Dictionary<FormKey, InfoPlacement> placement, Dictionary<FormKey, bool> deleted,
        Func<FormKey, (IDialogResponsesGetter Body, string Plugin)?> resolveInfo, HashSet<FormKey> stack)
    {
        var fk = info.FormKey;
        order.Remove(fk);                                        // evict every prior copy — last lister owns position
        placedBy[fk] = plugin;
        deleted[fk] = info.IsDeleted;

        var prev = info.PreviousDialog.FormKeyNullable;

        if (prev is null)                                        // PNAM absent — the common arm
        {
            order.Add(fk);
            placement[fk] = InfoPlacement.Tail;
            return;
        }

        if (prev.Value.IsNull)                                   // PNAM present but zero — the "I am first" marker
        {
            order.Insert(0, fk);
            placement[fk] = InfoPlacement.Head;
            return;
        }

        int at = order.IndexOf(prev.Value);
        if (at < 0 && stack.Add(fk))                             // target not placed yet — place it first
        {
            if (resolveInfo(prev.Value) is { } target)
                Place(target.Body, target.Plugin, order, placedBy, placement, deleted, resolveInfo, stack);
            stack.Remove(fk);
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

        if (at < 0)                                              // unreachable target (dangling, or a PNAM cycle)
        {
            order.Insert(0, fk);
            placement[fk] = InfoPlacement.Head;
            return;
        }

        order.Insert(at + 1, fk);
        placement[fk] = InfoPlacement.AfterTarget;
    }
}
