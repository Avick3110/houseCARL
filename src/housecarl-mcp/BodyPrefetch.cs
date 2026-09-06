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
/// <para>A plugin is walked when a row that wants it is actually READ, not when the chunk is opened, and that walk
/// then covers every row of the chunk from that plugin. The render stops at max_chars mid-chunk, so gathering the
/// whole chunk up front would enumerate plugins for rows nobody sees — at <see cref="ChunkRows"/> against a default
/// 500-row window the chunk IS the whole selection, and a render cut at forty rows would pay for all five hundred.
/// Deferred, the cost is the plugins the rendered rows came from, which is never more than the per-row seek this
/// replaced.</para>
/// </summary>
internal static class BodyPrefetch
{
    /// <summary>Rows gathered per chunk. Big enough that a large master is walked tens of times over a whole-order
    /// catalogue rather than tens of thousands, small enough that the pinned getters stay bounded.</summary>
    internal const int ChunkRows = 2000;

    /// <summary>The first row of the chunk row <paramref name="i"/> falls in.</summary>
    internal static int ChunkStart(int i) => i / ChunkRows * ChunkRows;

    /// <summary>How many record bodies the gather has been asked for in this process — the keys it registers for a
    /// plugin walk. Counted for the reason <see cref="LoadOrderResolver.BodySeeks"/> is: whether a caller gathered
    /// only what it can use, or a whole frontier it then threw away, is invisible in the answer and only the cost
    /// differs, so this is what a test can hold that claim to.</summary>
    internal static long KeysWanted;

    /// <summary>The chunk covering rows <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive):
    /// which plugin each row's body comes from, and each plugin's whole share of the chunk, ready to be walked when
    /// a row asks for it. <paramref name="sourceAt"/> names the plugin whose body a row displays, or null for the
    /// load-order winner; <paramref name="getterTypes"/> is the caller's own type scope when it has one, which
    /// narrows each plugin's walk to the GRUPs those types live in.</summary>
    internal static Chunk Gather(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyList<FormKey> keys, int start, int end, Func<int, string?> sourceAt,
        IReadOnlyList<Type>? getterTypes, CancellationToken ct)
    {
        var byPlugin = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        var plugins = new Dictionary<FormKey, string>();
        for (int r = start; r < end; r++)
        {
            var fk = keys[r];
            if (fk.IsNull) continue;                       // a row whose FormID did not parse has no body to gather
            var plugin = sourceAt(r) ?? view.ResolveWinner(fk)?.WinnerPlugin;
            if (plugin is null) continue;                  // unresolvable: the row's own read names the cause
            if (!byPlugin.TryGetValue(plugin, out var set)) byPlugin[plugin] = set = new HashSet<FormKey>();
            set.Add(fk);
            plugins[fk] = plugin;
        }
        Interlocked.Add(ref KeysWanted, plugins.Count);     // what this caller asked for, whether or not a row reads it
        return new Chunk(view, session, byPlugin, plugins, getterTypes, ct);
    }

    /// <summary>One chunk's bodies, each source plugin enumerated once and only when a row of the chunk asks for
    /// it.</summary>
    internal sealed class Chunk
    {
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly Dictionary<string, HashSet<FormKey>> _wanted;
        readonly Dictionary<FormKey, string> _plugins;
        readonly IReadOnlyList<Type>? _getterTypes;
        readonly CancellationToken _ct;
        readonly Dictionary<FormKey, IMajorRecordGetter> _bodies = new();
        readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);

        internal Chunk(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                       Dictionary<string, HashSet<FormKey>> wanted, Dictionary<FormKey, string> plugins,
                       IReadOnlyList<Type>? getterTypes, CancellationToken ct)
        {
            _view = view; _session = session; _wanted = wanted; _plugins = plugins;
            _getterTypes = getterTypes; _ct = ct;
        }

        /// <summary>This row's body, walking its source plugin once for the whole chunk on the first row that wants
        /// it.
        /// <para>A body that is not gathered — an unresolvable key, a plugin that cannot be read — comes back null
        /// and the row's own read raises the fault it always did: the prefetch is an optimisation and must never
        /// become a second error path.</para></summary>
        internal IMajorRecordGetter? Body(FormKey fk)
        {
            if (_bodies.TryGetValue(fk, out var have)) return have;
            if (!_plugins.TryGetValue(fk, out var plugin) || !_walked.Add(plugin)) return null;
            _ct.ThrowIfCancellationRequested();
            try { _view.CollectRecords(_session, plugin, _wanted[plugin], _getterTypes, _bodies); }
            catch (Exception) { }
            return _bodies.TryGetValue(fk, out var got) ? got : null;
        }
    }
}
