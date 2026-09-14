namespace HousecarlCore;

/// <summary>Where a merged plugin has to load, derived from what the merge already computed: the donors' load-order
/// positions, the output's masters, and the plugins outside the merge that touch donor records. No extra scan.</summary>
/// <summary>Positions here are 1-based places in the active load order, as a report prints them.
/// <paramref name="UnreadBetween"/> counts the plugins in that range the identify pass could not look into, so a
/// sentence about the range can say what it did not see.</summary>
public sealed record MergeSiting(
    string FirstDonor, int FirstPosition, string LastDonor, int LastPosition,
    string? LastMaster, int LastMasterPosition, IReadOnlyList<string> Intervening, int UnreadBetween);

/// <summary>The merged plugin's placement constraint. It must load after its masters, and AT the last donor's position:
/// the merge baked the donors' conflict outcomes in as they stood there, so sitting earlier lets content the donors
/// used to beat win over the merge, and sitting later flips the merge over plugins after the last donor that override
/// the same records. The plugins BETWEEN the first and last donor that also touch donor records are named, because
/// those are the ones whose position relative to the merge changes which version wins.
///
/// <para>The touching set comes from the merge's identify pass, which looks for the records the donors ORIGINATE. An
/// override a donor carries at its master's FormID is not one of those, so a plugin that only contends over such a
/// record is not found here — the sentence a report writes has to claim only that much.</para></summary>
public static class MergeLoadPosition
{
    /// <summary>Donor and plugin positions come in as 0-based indices into the active order (what the resolver hands
    /// out); every position on the returned <see cref="MergeSiting"/> is 1-based. <paramref name="touching"/> is every
    /// plugin outside the merge known to reference or override a record a donor originates — the identify pass's own
    /// output — and <paramref name="unreadable"/> the plugins that pass could not look into at all.</summary>
    public static MergeSiting Derive(
        IReadOnlyList<(string Name, int Order)> donorsByLoadOrder,
        IReadOnlyList<string> masters,
        IEnumerable<string> touching,
        IEnumerable<string> unreadable,
        Func<string, int?> positionOf)
    {
        var first = donorsByLoadOrder[0];
        var last = donorsByLoadOrder[^1];

        string? lastMaster = null;
        int lastMasterPos = -1;
        foreach (var m in masters)
        {
            var p = positionOf(m);
            if (p is null) continue;                                       // a master the order does not carry is the pre-flight's refusal, not this sentence's business
            if (lastMaster is null || p.Value > lastMasterPos) { lastMaster = m; lastMasterPos = p.Value; }
        }

        var donorNames = new HashSet<string>(donorsByLoadOrder.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var between = new List<(string Name, int Order)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in touching)
        {
            if (donorNames.Contains(t) || !seen.Add(t)) continue;
            var p = positionOf(t);
            if (p is null || p.Value <= first.Order || p.Value >= last.Order) continue;
            between.Add((t, p.Value));
        }
        between.Sort((a, b) => a.Order.CompareTo(b.Order));

        // A plugin in the range the pass could not read is not evidence of absence, so it is counted and said.
        int unread = 0;
        var seenUnread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in unreadable)
        {
            if (donorNames.Contains(u) || !seenUnread.Add(u)) continue;
            var p = positionOf(u);
            if (p is not null && p.Value > first.Order && p.Value < last.Order) unread++;
        }

        return new MergeSiting(first.Name, first.Order + 1, last.Name, last.Order + 1,
            lastMaster, lastMasterPos + 1, between.Select(b => b.Name).ToList(), unread);
    }
}
