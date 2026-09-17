using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The bulk body gather both reading lanes share: a chunk of rows' bodies collected ONE enumeration
/// per source plugin, instead of the whole-plugin seek per record <see cref="LoadOrderResolver.IndexView"/>
/// costs (#582). DEFERRED: a plugin is walked when a row that wants it is actually READ, and that walk then
/// covers every row of the chunk from that plugin.</summary>
internal static class BodyPrefetch
{
    /// <summary>Rows gathered per chunk: enough that a large master is walked tens of times over a whole-order
    /// catalogue rather than tens of thousands, few enough that the pinned getters stay bounded.</summary>
    internal const int ChunkRows = 2000;

    internal static int ChunkStart(int i) => i / ChunkRows * ChunkRows;

    /// <summary>How many record bodies the gather has been asked for in this process; pinned by
    /// RecordsWalkCostTests.ACappedSeedGathersNoBodiesPastItsCap.</summary>
    internal static long KeysWanted;

    /// <summary>The chunk covering rows <paramref name="start"/> (inclusive) to <paramref name="end"/>
    /// (exclusive), ready to walk a plugin when a row asks for it. <paramref name="sourceAt"/> names the plugin
    /// whose body a row displays, or null for the load-order winner; <paramref name="getterTypes"/> narrows each
    /// plugin's walk to the GRUPs the caller's types live in.</summary>
    internal static Chunk Gather(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyList<FormKey> keys, int start, int end, Func<int, string?> sourceAt,
        IReadOnlyList<Type>? getterTypes, CancellationToken ct)
    {
        var gather = new BodyGather(view, session, getterTypes, BodyGather.Absent.Null, onDemand: true, ct: ct);
        var plugins = new Dictionary<FormKey, string>();
        for (int r = start; r < end; r++)
        {
            var fk = keys[r];
            if (fk.IsNull) continue;                       // a row whose FormID did not parse has no body to gather
            var plugin = sourceAt(r) ?? view.ResolveWinner(fk)?.WinnerPlugin;
            if (plugin is null) continue;                  // unresolvable: the row's own read names the cause
            gather.Want(plugin, fk);
            plugins[fk] = plugin;
        }
        Interlocked.Add(ref KeysWanted, plugins.Count);     // what this caller asked for, whether or not a row reads it
        return new Chunk(gather, plugins);
    }

    /// <summary>One chunk's bodies, each source plugin enumerated once and only when a row asks for it.</summary>
    internal sealed class Chunk
    {
        readonly BodyGather _gather;
        readonly Dictionary<FormKey, string> _plugins;

        internal Chunk(BodyGather gather, Dictionary<FormKey, string> plugins)
        { _gather = gather; _plugins = plugins; }

        /// <summary>This row's body, walking its source plugin once for the whole chunk on the first row that wants it.
        /// A body that is not gathered comes back null and the row's own read raises the fault it always did; an
        /// <see cref="OutOfMemoryException"/> from the walk propagates rather than being swallowed (#756).</summary>
        internal IMajorRecordGetter? Body(FormKey fk)
            => _plugins.TryGetValue(fk, out var plugin) ? _gather.Body(plugin, fk) : null;
    }
}
