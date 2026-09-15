namespace HousecarlCore;

/// <summary>Where a merged plugin has to load, derived from what the merge already computed: the donors' load-order
/// positions and the output's masters. No extra scan.
///
/// <para>Positions here are 1-based places in the active load order, as a report prints them.
/// <paramref name="MasterAfterLastDonor"/> is the order the advice cannot fully satisfy: a master that is not flagged
/// ESM can sit after the last donor, and the output cannot be both after it and at the donors' position.</para></summary>
public sealed record MergeSiting(
    string FirstDonor, int FirstPosition, string LastDonor, int LastPosition,
    string? LastMaster, int LastMasterPosition, bool MasterAfterLastDonor);

/// <summary>The merged plugin's placement constraint. It must load after its masters, and AT the last donor's position:
/// the merge baked the donors' conflict outcomes in as they stood there, so sitting earlier lets content the donors
/// used to beat win over the merge, and sitting later puts the merge over plugins that used to beat the donors.
///
/// <para>It names no other plugins. What the merge knows about plugins outside it — the identify pass's referencers
/// and overriders — is about the records the donors ORIGINATE, and every one of those is renumbered into the output's
/// own FormID space, where nothing outside the merge shares an id with it; such a plugin is orphaned by the swap, which
/// the report's own warning says, not outranked by where the output sits. The records whose winner the position really
/// decides are the overrides the donors carry at their MASTERS' FormIDs, which no pass in a merge enumerates — so the
/// constraint is stated and the roster is not guessed at.</para></summary>
public static class MergeLoadPosition
{
    /// <summary>Donor positions come in as 0-based indices into the active order (what the resolver hands out); every
    /// position on the returned <see cref="MergeSiting"/> is 1-based. <paramref name="positionOf"/> answers for a
    /// master; a master the active order does not carry is the pre-flight's refusal, not this sentence's business.</summary>
    public static MergeSiting Derive(
        IReadOnlyList<(string Name, int Order)> donorsByLoadOrder,
        IReadOnlyList<string> masters,
        Func<string, int?> positionOf)
    {
        var first = donorsByLoadOrder[0];
        var last = donorsByLoadOrder[^1];

        string? lastMaster = null;
        int lastMasterPos = -1;
        foreach (var m in masters)
        {
            var p = positionOf(m);
            if (p is null) continue;
            if (lastMaster is null || p.Value > lastMasterPos) { lastMaster = m; lastMasterPos = p.Value; }
        }

        return new MergeSiting(first.Name, first.Order + 1, last.Name, last.Order + 1,
            lastMaster, lastMasterPos + 1, lastMaster is not null && lastMasterPos > last.Order);
    }
}
