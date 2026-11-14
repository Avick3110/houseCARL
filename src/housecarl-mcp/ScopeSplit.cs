using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A <c>plugins=</c> scope split against one index build, with the three sentences every lane the scope
/// reaches answers with: scan the plugins that are there, name the ones that are not.</summary>
sealed record ScopeSplit(IReadOnlyList<string> Present, IReadOnlyList<string> Missing, string? BlankRefusal, bool OwnNamed = false)
{
    /// <summary>Split the names. A null or blank entry is refused by its index rather than trimmed into a lookup. A
    /// name equal to <paramref name="own"/> (an off-order file's own filename) is neither present nor missing;
    /// <see cref="OwnNamed"/> says it was there.</summary>
    internal static ScopeSplit Of(LoadOrderResolver.IndexView view, IReadOnlyList<string> names, string? own = null)
    {
        var present = new List<string>(names.Count);
        var missing = new List<string>();
        bool ownNamed = false;
        for (int i = 0; i < names.Count; i++)
        {
            var n = (names[i] ?? "").Trim();
            if (n.Length == 0)
                return new ScopeSplit(present, missing,
                    $"plugins.names[{i}] is empty — every entry is a plugin FILENAME (e.g. 'Requiem.esp'). Drop the empty entry.", ownNamed);
            if (own is not null && string.Equals(n, own, StringComparison.OrdinalIgnoreCase)) { ownNamed = true; continue; }
            (view.ContainsPlugin(n) ? present : missing).Add(n);
        }
        return new ScopeSplit(present, missing, null, ownNamed);
    }

    /// <summary>Each missing name with the cause this build can give for it — the untick, the did-you-mean.</summary>
    string Causes(LoadOrderResolver.IndexView view) =>
        string.Join(" ", Missing.Select(m => $"'{m}'.{view.AbsenceClause(m)}"));

    internal string NothingToScanRefusal(LoadOrderResolver.IndexView view) =>
        $"plugins= names nothing the load order carries, so there is nothing to scan: {Causes(view)} "
      + "Drop the name(s), or scope to plugins that are loaded.";

    internal string ServedNote(LoadOrderResolver.IndexView view) => "note: " + MissingNote(view);

    /// <summary>The note beside an off-order file's own name, which keeps the whole file: the missing names, named.</summary>
    internal string OwnFileNote(LoadOrderResolver.IndexView view, string ownName) =>
        $"note: plugins= names the file's own name ('{ownName}'), so the scope keeps every record the file holds "
      + "and the call's other filters still narrow it; "
      + $"{Missing.Count} other name(s) name nothing the load order carries: {Causes(view)}";

    /// <summary>The served note without its "note: " lead, for a lane that writes its own.</summary>
    internal string MissingNote(LoadOrderResolver.IndexView view) =>
        $"this answer covers the {Present.Count} named plugin(s) the load order carries, and nothing from the "
      + $"{Missing.Count} it does not: {Causes(view)}";
}
