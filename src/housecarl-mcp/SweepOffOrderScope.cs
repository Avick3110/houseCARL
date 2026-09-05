using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The sweep families' <c>plugins=</c> split: which named plugins are in the ACTIVE order, and which are files on
/// disk to sweep OFF-ORDER — the pre-enable verify lane for a patch houseCARL has just written.
///
/// <para>One home, because both swept families take the lane and a second spelling of it is how the two would come
/// to disagree about which names resolve, which are ambiguous, and what the refusal says. The locate itself is
/// <see cref="LoadOrderService.LocatePluginFileOnDisk"/>, the contract every other lane uses; the MO2 composition
/// is read lazily, so a call whose every name is active pays for no folder sweep.</para>
/// </summary>
internal static class SweepOffOrderScope
{
    /// <summary>Why the split refused, and whether the refusal was decided against the caller's captured build —
    /// a membership or locate refusal consulted it and is stamped with its epoch, a blank name consulted nothing
    /// and is not.</summary>
    internal readonly record struct Refusal(string Message, bool Stamped);

    /// <summary>Split <paramref name="plugins"/> against <paramref name="view"/>. Returns the refusal, or null on
    /// success with <paramref name="active"/> and <paramref name="offOrder"/> filled — a blank name, a name found
    /// nowhere, and a name several mod folders provide each refuse before anything is swept.</summary>
    internal static Refusal? Split(LoadOrderResolver.IndexView view, IReadOnlyList<string> plugins,
                                   string modsDir, string dataDir, string overwriteDir, string profileDir,
                                   out List<string> active, out List<(string Name, string Path)> offOrder)
    {
        active = new List<string>();
        offOrder = new List<(string Name, string Path)>();
        Mo2Composition? comp = null;
        foreach (var name in plugins)
        {
            var n = name?.Trim() ?? "";
            if (n.Length == 0) return new Refusal(SweepSharedInput.BlankPluginName, Stamped: false);
            if (view.ContainsPlugin(n)) { active.Add(n); continue; }
            comp ??= Mo2LoadOrder.ReadComposition(profileDir);
            var loc = LoadOrderService.LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, n, null);
            if (loc.Error is not null)
                // The did-you-mean rides along: the commonest cause of a name found neither in the order nor on
                // disk is a typo or the wrong extension, and the near-miss is the one thing that fixes it.
                return new Refusal(
                    $"plugin not in the load order: {n} — and no on-disk copy was found either ({loc.Error}).{view.AbsenceClause(n)}", true);
            if (loc.Ambiguous is not null)
                return new Refusal(
                    $"plugin '{n}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                    $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to sweep. " +
                    "Enable the one you mean in MO2, or remove the duplicates.", true);
            offOrder.Add((n, loc.Path!));
        }
        return null;
    }
}
