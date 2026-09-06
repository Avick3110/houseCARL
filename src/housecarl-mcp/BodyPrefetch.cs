using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The bulk body gather both reading lanes share: a chunk of rows' bodies collected ONE enumeration per source
/// plugin, instead of the whole-plugin seek per record that <see cref="LoadOrderResolver.IndexView.GetRecord"/>
/// costs (#582, the same #251 shape <see cref="WinnerBodies"/> uses on the scan's matching half).
///
/// <para>Per row that seek is O(records in the source plugin), which is why a projection of four leaves cost 40 ms a
/// row on an order whose winners live in large masters. One place, so the scan detail lane
/// (<see cref="ScanDetailReader"/>) and the batch lane (<see cref="LoadOrderService.ResolveBatch"/>) cannot drift on
/// what a rendered row costs — the bound in <see cref="RenderBudget"/> is one number over both.</para>
///
/// <para>Chunked rather than whole-set: the map, and the getters it pins, are bounded by the chunk, so a render that
/// stops at max_chars pays for the chunk it reached rather than for every selected row.</para>
/// </summary>
internal static class BodyPrefetch
{
    /// <summary>Rows gathered per chunk. Big enough that a large master is walked tens of times over a whole-order
    /// catalogue rather than tens of thousands, small enough that the pinned getters stay bounded.</summary>
    internal const int ChunkRows = 2000;

    /// <summary>The first row of the chunk row <paramref name="i"/> falls in.</summary>
    internal static int ChunkStart(int i) => i / ChunkRows * ChunkRows;

    /// <summary>The bodies for rows <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive),
    /// keyed by FormKey. <paramref name="sourceAt"/> names the plugin whose body a row displays, or null for the
    /// load-order winner; <paramref name="getterTypes"/> is the caller's own type scope when it has one, which
    /// narrows each plugin's walk to the GRUPs those types live in.
    /// <para>A row whose body is not gathered here — an unresolvable key, a plugin that cannot be read — is simply
    /// absent, and the row's own read raises the fault it always did: the prefetch is an optimisation and must
    /// never become a second error path.</para></summary>
    internal static Dictionary<FormKey, IMajorRecordGetter> Gather(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyList<FormKey> keys, int start, int end, Func<int, string?> sourceAt,
        IReadOnlyList<Type>? getterTypes, CancellationToken ct)
    {
        var byPlugin = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        for (int r = start; r < end; r++)
        {
            var fk = keys[r];
            if (fk.IsNull) continue;                       // a row whose FormID did not parse has no body to gather
            var plugin = sourceAt(r) ?? view.ResolveWinner(fk)?.WinnerPlugin;
            if (plugin is null) continue;                  // unresolvable: the row's own read names the cause
            if (!byPlugin.TryGetValue(plugin, out var set)) byPlugin[plugin] = set = new HashSet<FormKey>();
            set.Add(fk);
        }

        var sink = new Dictionary<FormKey, IMajorRecordGetter>(end - start);
        foreach (var (plugin, wanted) in byPlugin)
        {
            ct.ThrowIfCancellationRequested();
            try { view.CollectRecords(session, plugin, wanted, getterTypes, sink); }
            catch (Exception) { }
        }
        return sink;
    }
}
