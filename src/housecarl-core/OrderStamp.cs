namespace HousecarlCore;

/// <summary>One index build's fingerprint AND the plugins that build lost to a load failure, as a single value; contract in docs/architecture/output-and-artifacts.md.</summary>
public sealed record OrderStamp(string Epoch, IReadOnlyList<string> ExcludedPlugins)
{
    /// <summary>Did this build LOSE plugins to a load failure? Every lane stays silent on a healthy order.</summary>
    public bool Degraded => ExcludedPlugins.Count > 0;

    /// <summary>The one sentence the json lanes carry, or null on a healthy build.</summary>
    public string? Note => Degraded ? OrderDegraded.Sentence(ExcludedPlugins) : null;

    /// <summary>The short clause a TEXT head line appends beside <c>epoch=</c>, or "" on a healthy build.</summary>
    public string Clause => OrderDegraded.Clause(ExcludedPlugins.Count);

    /// <summary>The stamp for a build, with its excluded roster sorted once so every response spells it the same.</summary>
    public static OrderStamp For(string epoch, IEnumerable<string> excludedPlugins) =>
        new(epoch, excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
}

/// <summary>The two spellings of the degraded-order marker, so the json note and the text clause cannot drift.</summary>
public static class OrderDegraded
{
    /// <summary>How many plugin names the sentence lists before it counts the rest; the count stays exact either way.</summary>
    const int NamesShown = 10;

    /// <summary>The one sentence: how many plugins are missing, which ones, that a LOAD FAILED rather than the order being rearranged, and where the per-plugin reason is.</summary>
    public static string Sentence(IReadOnlyCollection<string> excludedPlugins)
    {
        var names = excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", names.Take(NamesShown));
        if (excludedPlugins.Count > NamesShown) shown += $", and {excludedPlugins.Count - NamesShown} more";
        return $"{excludedPlugins.Count} plugin(s) could not be loaded for this build and are absent from the order " +
               $"this answer describes ({shown}) — this is a load FAILURE, not a reorder; " +
               "housecarl_load_order_status gives the reason for each.";
    }

    /// <summary>The text head line's clause for a build that lost <paramref name="count"/> plugins, or "" for a healthy one.</summary>
    public static string Clause(int count) => count > 0 ? $" · {count} plugin(s) excluded (load failure)" : "";
}
