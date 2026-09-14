namespace HousecarlCore;

/// <summary>Where a merged plugin has to load, derived from what the merge already computed: the donors' load-order
/// positions, the output's masters, and the plugins outside the merge that touch donor records. No extra scan.</summary>
public sealed record MergeSiting(
    string FirstDonor, int FirstPosition, string LastDonor, int LastPosition,
    string? LastMaster, int LastMasterPosition, IReadOnlyList<string> Intervening);

/// <summary>The merged plugin's placement constraint. It must load after its masters, and at or after the LAST donor's
/// position: the merge baked the donors' conflict outcomes in at that position, and anything that used to lose to a
/// donor there would win over the merge if the merge sat earlier. The plugins BETWEEN the first and last donor that
/// also touch donor records are named, because those are the ones whose position relative to the merge changes which
/// version wins.</summary>
public static class MergeLoadPosition
{
    /// <summary>Positions are 1-based places in the ACTIVE load order. <paramref name="touching"/> is every plugin
    /// outside the merge known to reference or override a donor record — the identify pass's own output.</summary>
    public static MergeSiting Derive(
        IReadOnlyList<(string Name, int Order)> donorsByLoadOrder,
        IReadOnlyList<string> masters,
        IEnumerable<string> touching,
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

        return new MergeSiting(first.Name, first.Order + 1, last.Name, last.Order + 1,
            lastMaster, lastMasterPos + 1, between.Select(b => b.Name).ToList());
    }
}
