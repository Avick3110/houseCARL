namespace HousecarlCore;

/// <summary>Where a merged plugin has to load: the donors' 1-based load-order positions, the last master's, and
/// whether that master sits after the last donor; contract in docs/architecture/write-path.md.</summary>
public sealed record MergeSiting(
    string FirstDonor, int FirstPosition, string LastDonor, int LastPosition,
    string? LastMaster, int LastMasterPosition, bool MasterAfterLastDonor);

/// <summary>The merged plugin's placement constraint: after its masters and AT the last donor's position, naming no
/// other plugins; contract in docs/architecture/write-path.md.</summary>
public static class MergeLoadPosition
{
    /// <summary>Derive the siting from 0-based donor indices, the output's masters, and a lookup for a master's place.</summary>
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
