namespace HousecarlCore;

/// <summary>One index build's fingerprint AND the plugins that build lost to a load failure, as a single value.
///
/// <para>A plugin that becomes unopenable or unparseable mid-session drops out of the next index build, and every
/// read after it answers normally off the narrowed order — shallower override depth, fewer touchers, sometimes a
/// different winner. The epoch changes, but a legitimate reorder changes it too, so within one response a
/// failure-degraded order and a legitimately reordered one look the same (#353). The marker says which.</para>
///
/// <para>The two facts travel together rather than being re-derived from the epoch through a side table: a response
/// stamped with a build is holding the build's excluded set already, and a table lookup can MISS — which renders as
/// a clean bill of health, the silence this marker exists to end. Carrying them as one value also means an outcome
/// cannot hold an epoch without holding its health: there is no second field for a lane to forget.</para>
///
/// <para>The health is a SIBLING of the epoch, never inside it: the epoch is opaque and compared only for equality,
/// so folding health into the string would leave two builds that differ only in health comparing as merely
/// "different" — today's ambiguity re-spelled.</para></summary>
public sealed record OrderStamp(string Epoch, IReadOnlyList<string> ExcludedPlugins)
{
    /// <summary>Did this build LOSE plugins to a load failure? False for a healthy order, and every lane stays
    /// silent on one.</summary>
    public bool Degraded => ExcludedPlugins.Count > 0;

    /// <summary>The one sentence the json lanes carry, or null on a healthy build.</summary>
    public string? Note => Degraded ? OrderDegraded.Sentence(ExcludedPlugins) : null;

    /// <summary>The short clause a TEXT head line appends beside <c>epoch=</c>, or "" on a healthy build.</summary>
    public string Clause => OrderDegraded.Clause(ExcludedPlugins.Count);

    /// <summary>The stamp for a build, with its excluded roster sorted once so every response spells it the same.
    /// </summary>
    public static OrderStamp For(string epoch, IEnumerable<string> excludedPlugins) =>
        new(epoch, excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
}

/// <summary>The two spellings of the degraded-order marker, so the json note and the text clause cannot drift.
/// Both take the excluded set the answer is holding — never a lookup that can come back empty.</summary>
public static class OrderDegraded
{
    /// <summary>How many plugin names the sentence lists before it counts the rest — the note stays one readable
    /// sentence, and the COUNT is always exact even when the names are not all there.</summary>
    const int NamesShown = 10;

    /// <summary>What the caller needs to know in one sentence: how many plugins are missing, which ones, that this
    /// is a FAILURE rather than something they did, and where the reason is.</summary>
    public static string Sentence(IReadOnlyCollection<string> excludedPlugins)
    {
        var names = excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", names.Take(NamesShown));
        if (excludedPlugins.Count > NamesShown) shown += $", and {excludedPlugins.Count - NamesShown} more";
        return $"{excludedPlugins.Count} plugin(s) could not be loaded for this build and are absent from the order " +
               $"this answer describes ({shown}) — this is a load FAILURE, not a change you made; " +
               "housecarl_load_order_status gives the reason.";
    }

    /// <summary>The text head line's clause for a build that lost <paramref name="count"/> plugins, or "" for a
    /// healthy one. Says the count, so a reader scanning the head sees the order is short of plugins without the
    /// sentence taking over the line.</summary>
    public static string Clause(int count) => count > 0 ? $" · {count} plugin(s) excluded (load failure)" : "";
}
