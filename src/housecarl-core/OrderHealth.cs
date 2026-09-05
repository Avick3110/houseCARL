namespace HousecarlCore;

/// <summary>Whether the index build an answer was stamped with had LOST plugins to a load failure, keyed by that
/// build's epoch.
///
/// <para>A plugin that becomes unopenable or unparseable mid-session drops out of the next index build, and every
/// read after it answers normally off the narrowed order — shallower override depth, fewer touchers, sometimes a
/// different winner. The epoch changes, but a legitimate reorder changes it too, so within one response a
/// failure-degraded order and a legitimately reordered one look the same (#353). The marker says which.</para>
///
/// <para>Keyed by the epoch string rather than plumbed as a second field beside every stamp: the epoch already
/// fingerprints the build INCLUDING its excluded set (<see cref="LoadOrderResolver"/>'s ComputeEpoch folds it in
/// deliberately), so "was this build degraded" is a function of the epoch and nothing else. Every response layer
/// that writes a stamp can therefore look the note up at the one shared writer, instead of 48 stamp sites across
/// seven outcome types each having to remember to carry a sibling field — a lane that forgot would go back to the
/// silence the marker exists to end.</para>
///
/// <para>Only DEGRADED builds are recorded, so a healthy session stores nothing and every lane stays silent. The
/// table is bounded and evicts oldest-first; a build 32 rebuilds old is no longer being answered from.</para></summary>
public static class OrderHealth
{
    /// <summary>How many degraded builds are remembered at once.</summary>
    const int Capacity = 32;

    /// <summary>How many plugin names the sentence lists before it counts the rest — the note stays one readable
    /// sentence, and the COUNT is always exact even when the names are not all there.</summary>
    const int NamesShown = 10;

    /// <summary>One degraded build as a response states it: the count the text head line shows and the sentence the
    /// json lanes carry.</summary>
    readonly record struct Degraded(int Count, string Note);

    static readonly object Gate = new();
    static readonly Dictionary<string, Degraded> Notes = new(StringComparer.Ordinal);
    static readonly Queue<string> Order = new();

    /// <summary>Remember that this build lost <paramref name="excludedPlugins"/> to a load failure. Called once per
    /// index build, from the snapshot constructor, so no build can reach a response without having been recorded.
    /// A healthy build records nothing.</summary>
    public static void Record(string epoch, IReadOnlyCollection<string> excludedPlugins)
    {
        if (excludedPlugins.Count == 0) return;
        var entry = new Degraded(excludedPlugins.Count, Sentence(excludedPlugins));
        lock (Gate)
        {
            if (!Notes.TryAdd(epoch, entry)) return;     // same build seen again — same fact, nothing to update
            Order.Enqueue(epoch);
            while (Order.Count > Capacity) Notes.Remove(Order.Dequeue());
        }
    }

    /// <summary>The one sentence a response carries for this build, or null when the build was healthy or the epoch
    /// names no build at all (a pre-capture refusal). Every stamp site asks; only a degraded build answers.</summary>
    public static string? NoteFor(string? epoch) => Lookup(epoch)?.Note;

    /// <summary>The short clause a TEXT head line appends beside <c>epoch=</c>, or an empty string when the build was
    /// healthy. The full sentence goes on the json lanes; the head line says the count, so a reader scanning it sees
    /// the order is short of plugins without the sentence taking over the line.</summary>
    public static string ClauseFor(string? epoch) =>
        Lookup(epoch) is { } d ? $" · {d.Count} plugin(s) excluded (load failure)" : "";

    static Degraded? Lookup(string? epoch)
    {
        if (epoch is null) return null;
        lock (Gate) return Notes.TryGetValue(epoch, out var d) ? d : null;
    }

    /// <summary>What the caller needs to know in one sentence: how many plugins are missing, which ones, that this
    /// is a FAILURE rather than something they did, and where the reason is.</summary>
    static string Sentence(IReadOnlyCollection<string> excludedPlugins)
    {
        var names = excludedPlugins.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", names.Take(NamesShown));
        if (names.Count > NamesShown) shown += $", and {names.Count - NamesShown} more";
        return $"{names.Count} plugin(s) could not be loaded for this build and are absent from the order this " +
               $"answer describes ({shown}) — this is a load FAILURE, not a change you made; " +
               "housecarl_load_order_status gives the reason.";
    }
}
