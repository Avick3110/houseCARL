using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The sweep families' <c>plugins=</c> split: which named plugins are in the ACTIVE order, and which are
/// files on disk to sweep OFF-ORDER — the pre-enable verify lane for a patch houseCARL has just written. One home,
/// because both swept families take the lane; the MO2 composition is read lazily.</summary>
internal static class SweepOffOrderScope
{
    /// <summary>Why the split refused; <c>Stamped</c> when the refusal consulted the caller's build (a blank name did not).</summary>
    internal readonly record struct Refusal(string Message, bool Stamped);

    /// <summary>Split <paramref name="plugins"/> against <paramref name="view"/>: the refusal, or null with
    /// <paramref name="active"/> and <paramref name="offOrder"/> filled. A blank name, a name found nowhere, and a
    /// name several mod folders provide each refuse before anything is swept. The composition, read only when a name
    /// is off-order, is the call's one read through <paramref name="memo"/>.</summary>
    internal static Refusal? Split(LoadOrderResolver.IndexView view, IReadOnlyList<string> plugins,
                                   Mo2Roots roots,
                                   out List<string> active, out List<(string Name, string Path)> offOrder,
                                   SweepOffOrderMemo memo)
    {
        if (memo.Epoch is not null && memo.Epoch == view.Epoch && memo.Roots == roots && ReferenceEquals(memo.Plugins, plugins))
        {
            active = memo.Active;
            offOrder = memo.OffOrder;
            return memo.Refusal;
        }

        var answer = Compute(view, plugins, roots, out active, out offOrder, memo.CompositionReader(roots.ProfileDir));
        memo.Epoch = view.Epoch;
        memo.Roots = roots;
        memo.Plugins = plugins;
        memo.Refusal = answer;
        memo.Active = active;
        memo.OffOrder = offOrder;
        return answer;
    }

    static Refusal? Compute(LoadOrderResolver.IndexView view, IReadOnlyList<string> plugins,
                            Mo2Roots roots,
                            out List<string> active, out List<(string Name, string Path)> offOrder,
                            Func<Mo2Composition> readComposition)
    {
        active = new List<string>();
        offOrder = new List<(string Name, string Path)>();
        Mo2Composition? comp = null;
        foreach (var name in plugins)
        {
            var n = name?.Trim() ?? "";
            if (n.Length == 0) return new Refusal(SweepSharedInput.BlankPluginName, Stamped: false);
            if (view.ContainsPlugin(n)) { active.Add(n); continue; }
            comp ??= readComposition();
            var loc = LoadOrderService.LocatePluginFileOnDisk(comp, roots, n, null);
            if (loc.Error is not null)
                // The did-you-mean rides along: a name found neither in the order nor on disk is usually a typo.
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

/// <summary>ONE CALL's shared state for the swept families, so a merged check pays the profile's composition read and
/// the off-order folder sweep once. The composition is parsed at most once per profile folder; the split answers only
/// for the build, the roots and the very list it was filled against, and anything else recomputes. A family handed no
/// memo makes its own.</summary>
public sealed class SweepOffOrderMemo
{
    internal string? Epoch;
    internal Mo2Roots? Roots;
    internal IReadOnlyList<string>? Plugins;
    internal SweepOffOrderScope.Refusal? Refusal;
    internal List<string> Active = new();
    internal List<(string Name, string Path)> OffOrder = new();

    string? _compositionProfile;
    Lazy<Mo2Composition>? _composition;

    /// <summary>A reader for <paramref name="profileDir"/>'s composition that parses it at most once for this call,
    /// and only when first invoked; a different folder is read afresh. A failed read fails the same way each time.</summary>
    internal Func<Mo2Composition> CompositionReader(string profileDir)
    {
        if (_composition is null || !string.Equals(_compositionProfile, profileDir, StringComparison.Ordinal))
        {
            _compositionProfile = profileDir;
            _composition = new Lazy<Mo2Composition>(() => Mo2LoadOrder.ReadComposition(profileDir), LazyThreadSafetyMode.None);
        }
        var composition = _composition;
        return () => composition.Value;
    }
}
