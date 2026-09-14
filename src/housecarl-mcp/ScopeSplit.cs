using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A <c>plugins=</c> scope split against one index build: the names the order carries and the names it does
/// not. Every lane the scope reaches answers the same way — scan the plugins that are there, name the ones that are
/// not — so the split and its three sentences live in one place rather than once per lane.</summary>
/// <param name="Present">The named plugins this build carries, in the caller's order.</param>
/// <param name="Missing">The named plugins it does not, in the caller's order.</param>
/// <param name="BlankRefusal">Set when an entry was null or blank, which is not a plugin filename at all.</param>
sealed record ScopeSplit(IReadOnlyList<string> Present, IReadOnlyList<string> Missing, string? BlankRefusal)
{
    /// <summary>Split the names. A null or blank entry is refused BY ITS INDEX rather than trimmed into a lookup: the
    /// binder accepts one, and reaching the resolver with it turned bad input into an internal-failure message.</summary>
    internal static ScopeSplit Of(LoadOrderResolver.IndexView view, IReadOnlyList<string> names)
    {
        var present = new List<string>(names.Count);
        var missing = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            var n = (names[i] ?? "").Trim();
            if (n.Length == 0)
                return new ScopeSplit(present, missing,
                    $"plugins.names[{i}] is empty — every entry is a plugin FILENAME (e.g. 'Requiem.esp'). Drop the empty entry.");
            (view.ContainsPlugin(n) ? present : missing).Add(n);
        }
        return new ScopeSplit(present, missing, null);
    }

    /// <summary>Each missing name with the cause this build can give for it — the untick, the did-you-mean. Paid once
    /// per MISSING name, which is the caller's own list, and never on a scope that is wholly present.</summary>
    string Causes(LoadOrderResolver.IndexView view) =>
        string.Join(" ", Missing.Select(m => $"'{m}'.{view.AbsenceClause(m)}"));

    /// <summary>Every named plugin is missing, so there is nothing left to scan and the call is refused.</summary>
    internal string NothingToScanRefusal(LoadOrderResolver.IndexView view) =>
        $"plugins= names nothing the load order carries, so there is nothing to scan: {Causes(view)} "
      + "Drop the name(s), or scope to plugins that are loaded.";

    /// <summary>Some named plugins are loaded, so the scan answers for those and says which were left out and why.</summary>
    internal string ServedNote(LoadOrderResolver.IndexView view) =>
        $"note: this answer covers the {Present.Count} named plugin(s) the load order carries, and nothing from the "
      + $"{Missing.Count} it does not: {Causes(view)}";
}
