namespace HousecarlCore;

/// <summary>One index build's fingerprint AND the plugins that build lost to a load failure, as a single value; contract in docs/architecture/output-and-artifacts.md.</summary>
public sealed record OrderStamp(string Epoch, IReadOnlyList<string> ExcludedPlugins)
{
    /// <summary>Did this build LOSE plugins to a load failure? Every lane stays silent on a healthy order.</summary>
    public bool Degraded => ExcludedPlugins.Count > 0;

    /// <summary>The one sentence the json lanes carry, or null on a healthy build.</summary>
    public string? Note => Degraded ? OrderDegraded.Sentence(ExcludedPlugins) : null;

    /// <summary>The short clause a TEXT head line appends beside <c>epoch=</c>, pointing to where the reasons are, or "" on a healthy build.</summary>
    public string Clause => OrderDegraded.Clause(ExcludedPlugins, pointToStatus: true);

    /// <summary>The stamp for a build, with its excluded roster sorted once so every response spells it the same.</summary>
    public static OrderStamp For(string epoch, IEnumerable<string> excludedPlugins) =>
        new(epoch, OrderDegraded.Sorted(excludedPlugins));
}

/// <summary>The two spellings of the degraded-order marker, so the json note and the text clause cannot drift.</summary>
public static class OrderDegraded
{
    /// <summary>How many plugin names the sentence lists before it counts the rest; the count stays exact either way.</summary>
    const int NamesShown = 10;

    /// <summary>How many plugin names the one-line text clause lists before it counts the rest.</summary>
    const int ClauseNamesShown = 3;

    /// <summary>A roster in the one order every response spells it in; <see cref="OrderStamp.For"/> applies it once.</summary>
    public static IReadOnlyList<string> Sorted(IEnumerable<string> excludedPlugins) =>
        excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The one sentence: how many plugins are missing, which ones, that a LOAD FAILED rather than the order being rearranged, and where the per-plugin reason is. Takes a <see cref="Sorted"/> roster.</summary>
    public static string Sentence(IReadOnlyList<string> sorted) =>
        $"{sorted.Count} plugin(s) could not be loaded for this build and are absent from the order " +
        $"this answer describes ({Shown(sorted, NamesShown, n => $", and {n} more")}) — this is a load FAILURE, not a reorder; " +
        "housecarl_load_order_status gives the reason for each.";

    /// <summary>The text head line's clause naming the plugins a build lost, or "" for a healthy one; the pointer to the reasons only where the response does not print them itself. Takes a <see cref="Sorted"/> roster.</summary>
    public static string Clause(IReadOnlyList<string> sorted, bool pointToStatus) =>
        sorted.Count == 0 ? "" :
        $" · {sorted.Count} plugin(s) excluded (load failure): {Shown(sorted, ClauseNamesShown, n => $" +{n} more")}" +
        (pointToStatus ? " — reason in housecarl_load_order_status" : "");

    /// <summary>The first <paramref name="cap"/> names of a roster, then <paramref name="more"/> of how many are left.</summary>
    internal static string Shown(IReadOnlyList<string> names, int cap, Func<int, string> more) =>
        string.Join(", ", names.Take(cap)) + (names.Count > cap ? more(names.Count - cap) : "");
}
