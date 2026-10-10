using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The bulk body gather both reading lanes share: a chunk of rows' bodies collected ONE enumeration
/// per source plugin, instead of the whole-plugin seek per record (#582). DEFERRED: a plugin is walked when a
/// row that wants it is actually READ, and that walk covers every row of the chunk from that plugin.</summary>
internal static class BodyPrefetch
{
    /// <summary>Rows gathered per chunk: few enough that the pinned getters stay bounded.</summary>
    internal const int ChunkRows = 2000;

    internal static int ChunkStart(int i) => i / ChunkRows * ChunkRows;

    /// <summary>The chunk covering rows <paramref name="start"/> to <paramref name="end"/>, ready to walk a plugin
    /// when a row asks. <paramref name="sourceAt"/> names the plugin whose body a row displays, or null for the
    /// winner; <paramref name="getterTypes"/> narrows each walk to the GRUPs the caller's types live in.</summary>
    internal static Chunk Gather(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, CostCounters counters,
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
        Interlocked.Add(ref counters.KeysWanted, plugins.Count);     // what this caller asked for, whether or not a row reads it
        return new Chunk(gather, plugins, view, session, ct);
    }

    /// <summary>One chunk's bodies, each source plugin enumerated once and only when a row asks for it.</summary>
    internal sealed class Chunk
    {
        readonly BodyGather _gather;
        readonly Dictionary<FormKey, string> _plugins;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly CancellationToken _ct;
        Dictionary<FormKey, IMajorRecordGetter>? _parents;

        internal Chunk(BodyGather gather, Dictionary<FormKey, string> plugins,
                       LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, CancellationToken ct)
        { _gather = gather; _plugins = plugins; _view = view; _session = session; _ct = ct; }

        /// <summary>The containing record <paramref name="parent"/> of one of this chunk's rows: the first '*parent'
        /// hop a row reads gathers every row's containing record, one walk per winner plugin (#1147); null when the
        /// gather does not hold it, and the hop then fetches it alone.</summary>
        internal IMajorRecordGetter? Parent(FormKey parent)
        {
            if (_parents is null)
            {
                var wanted = new List<(FormKey Parent, Type ChildType, int Hops)>();
                foreach (var (fk, plugin) in _plugins)
                    if (_view.ParentOf(fk) is { } pk && _gather.Body(plugin, fk) is { } body) wanted.Add((pk, body.GetType(), 1));
                _parents = ContainmentIndex.GatherContainers(_view, _session, wanted, out _, _ct);
            }
            return _parents.GetValueOrDefault(parent);
        }

        /// <summary>This row's body, walking its source plugin once for the whole chunk. A body that is not gathered
        /// comes back null and the row's own read raises the fault it always did; an
        /// <see cref="OutOfMemoryException"/> from the walk propagates rather than being swallowed (#756).</summary>
        internal IMajorRecordGetter? Body(FormKey fk)
            => _plugins.TryGetValue(fk, out var plugin) ? _gather.Body(plugin, fk) : null;
    }
}
