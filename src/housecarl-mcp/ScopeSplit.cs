using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A <c>plugins=</c> scope split against one index build, with the three sentences every lane the scope
/// reaches answers with: scan the plugins that are there, name the ones that are not.</summary>
sealed record ScopeSplit(IReadOnlyList<string> Present, IReadOnlyList<string> Missing, string? BlankRefusal)
{
    /// <summary>Split the names. A null or blank entry is refused by its index rather than trimmed into a lookup.</summary>
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

    /// <summary>Each missing name with the cause this build can give for it — the untick, the did-you-mean.</summary>
    string Causes(LoadOrderResolver.IndexView view) =>
        string.Join(" ", Missing.Select(m => $"'{m}'.{view.AbsenceClause(m)}"));

    internal string NothingToScanRefusal(LoadOrderResolver.IndexView view) =>
        $"plugins= names nothing the load order carries, so there is nothing to scan: {Causes(view)} "
      + "Drop the name(s), or scope to plugins that are loaded.";

    internal string ServedNote(LoadOrderResolver.IndexView view) =>
        $"note: this answer covers the {Present.Count} named plugin(s) the load order carries, and nothing from the "
      + $"{Missing.Count} it does not: {Causes(view)}";
}
