using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The sweep families' <c>plugins=</c> split: which named plugins are in the ACTIVE order, and which are files on
/// disk to sweep OFF-ORDER — the pre-enable verify lane for a patch houseCARL has just written.
///
/// <para>One home, because both swept families take the lane and a second spelling of it is how the two would come
/// to disagree about which names resolve, which are ambiguous, and what the refusal says. The locate itself is
/// <see cref="LoadOrderService.LocatePluginFileOnDisk"/>, the contract every other lane uses; the MO2 composition
/// is read lazily, so a call whose every name is active pays for no folder sweep. A surface running both families
/// over one list hands them one <see cref="SweepOffOrderMemo"/>, so the composition read and the folder sweep
/// happen once and the two answer off the same split rather than off two runs of the same code.</para>
/// </summary>
internal static class SweepOffOrderScope
{
    /// <summary>Why the split refused, and whether the refusal was decided against the caller's captured build —
    /// a membership or locate refusal consulted it and is stamped with its epoch, a blank name consulted nothing
    /// and is not.</summary>
    internal readonly record struct Refusal(string Message, bool Stamped);

    /// <summary>Split <paramref name="plugins"/> against <paramref name="view"/>. Returns the refusal, or null on
    /// success with <paramref name="active"/> and <paramref name="offOrder"/> filled — a blank name, a name found
    /// nowhere, and a name several mod folders provide each refuse before anything is swept.
    /// <para>An optional <paramref name="memo"/> makes the second family of one call reuse the first family's
    /// answer instead of reading the composition and sweeping every mod folder again. It answers only when it was
    /// filled against this same build and this same list; otherwise the split is recomputed and the memo refilled,
    /// so a memo can never hand back a composition the caller's own build did not see.</para></summary>
    internal static Refusal? Split(LoadOrderResolver.IndexView view, IReadOnlyList<string> plugins,
                                   string modsDir, string dataDir, string overwriteDir, string profileDir,
                                   out List<string> active, out List<(string Name, string Path)> offOrder,
                                   SweepOffOrderMemo? memo = null)
    {
        if (memo is { Epoch: not null } m && m.Epoch == view.Epoch && ReferenceEquals(m.Plugins, plugins))
        {
            active = m.Active;
            offOrder = m.OffOrder;
            return m.Refusal;
        }

        var answer = Compute(view, plugins, modsDir, dataDir, overwriteDir, profileDir, out active, out offOrder);
        if (memo is not null)
        {
            memo.Epoch = view.Epoch;
            memo.Plugins = plugins;
            memo.Refusal = answer;
            memo.Active = active;
            memo.OffOrder = offOrder;
        }
        return answer;
    }

    static Refusal? Compute(LoadOrderResolver.IndexView view, IReadOnlyList<string> plugins,
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

/// <summary>ONE CALL's memo of the off-order split, so a surface that hands the same <c>plugins=</c> list to both
/// swept families pays the MO2 composition read and the whole-install folder sweep once rather than twice — and
/// cannot have the two families disagree about which names resolved.
/// <para>It lives for that one call: nothing holds a locate between calls, so a mod folder that changed since the
/// last sweep is still seen. It answers only for the build and the very list it was filled against; anything else
/// recomputes. A family handed no memo resolves on its own, which is what each standalone tool does.</para></summary>
public sealed class SweepOffOrderMemo
{
    internal string? Epoch;
    internal IReadOnlyList<string>? Plugins;
    internal SweepOffOrderScope.Refusal? Refusal;
    internal List<string> Active = new();
    internal List<(string Name, string Path)> OffOrder = new();
}
