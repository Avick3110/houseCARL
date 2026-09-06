using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The scan detail lane's row reader — the one path the four scan renders (text, json, dense, artifact) read a
/// match's body through.
///
/// <para>It exists for two costs the per-row call carried (#582). One overlay SESSION covers the whole render, so a
/// plugin is memory-mapped once for the call instead of once per row. And each row's body is gathered a CHUNK of
/// rows at a time, one enumeration per source plugin however many of that chunk's rows want it, instead of the
/// whole-overlay walk per record that <see cref="LoadOrderResolver.IndexView.GetRecord"/> costs — the same #251
/// shape <see cref="WinnerBodies"/> already uses on the scan's matching half. Per-row that walk is O(records in the
/// winner plugin), which is why a projection of four leaves cost 40 ms a row on an order whose winners live in
/// large masters.</para>
///
/// <para>Chunked rather than whole-set: the map, and the getters it pins, are bounded by the chunk, and a render
/// that stops at max_chars pays for the chunk it reached rather than for every selected row.</para>
///
/// <para>Every row also checks the caller's cancellation token, so a client that aborts stops the render inside one
/// row.</para>
/// </summary>
internal sealed class ScanDetailReader : IDisposable
{
    /// <summary>Rows gathered per chunk. Big enough that a large master is walked tens of times over a whole-order
    /// catalogue rather than tens of thousands, small enough that the pinned getters stay bounded.</summary>
    internal const int ChunkRows = 2000;

    readonly LoadOrderService _svc;
    readonly CrossQueryOutcome _q;
    readonly IReadOnlyList<string>? _fields;
    readonly int _depth;
    readonly bool _resolveNames, _winnerFields;
    readonly string? _containerHint;
    readonly IReadOnlyList<int>? _depths;
    readonly CancellationToken _ct;
    readonly LoadOrderService.LinkMemo? _linkMemo;
    readonly LoadOrderResolver.IndexView? _view;
    readonly LoadOrderResolver.OverlaySession? _session;

    Dictionary<FormKey, IMajorRecordGetter>? _chunk;
    int _chunkStart = -1;

    internal ScanDetailReader(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int depth,
                              bool resolveNames, bool winnerFields, string? containerHint,
                              IReadOnlyList<int>? depths, CancellationToken ct)
    {
        _svc = svc; _q = q; _fields = fields; _depth = depth;
        _resolveNames = resolveNames; _winnerFields = winnerFields;
        _containerHint = containerHint; _depths = depths; _ct = ct;
        _linkMemo = resolveNames ? new LoadOrderService.LinkMemo() : null;
        // Only a pinned outcome can be read this way: the session and the prefetch have to come off the very build
        // the scan matched on, and an unpinned outcome falls through to the plain per-row path unchanged.
        _view = q.Pin?.View;
        _session = q.Pin?.Resolver.OpenSession();
    }

    /// <summary>One link-resolution cache for the whole render, so a target recurring across rows resolves once.</summary>
    internal LoadOrderService.LinkMemo? LinkMemo => _linkMemo;

    /// <summary>Read row <paramref name="i"/> of the scan's key list.</summary>
    internal ReadOutcome Row(int i)
    {
        _ct.ThrowIfCancellationRequested();
        var fk = _q.Keys[i];
        var plugin = SourceAt(i);
        FillChunk(i);
        var body = _chunk is { } c && c.TryGetValue(fk, out var b) ? b : null;
        return _svc.ResolveReadOn(_q, fk, plugin, _fields, false, _depth, _resolveNames, _linkMemo,
                                  _containerHint, _depths, _session, body);
    }

    /// <summary>The plugin whose body this row displays: the scan's own per-match source, or the winner when the
    /// call retargeted display to it. The same reading every render made inline before this reader existed.</summary>
    string? SourceAt(int i)
        => _winnerFields ? null : (_q.Sources is { } src && i < src.Count ? src[i] : null);

    void FillChunk(int i)
    {
        if (_view is not { } view || _session is null) return;   // unpinned: the per-row path answers
        int start = i / ChunkRows * ChunkRows;
        if (start == _chunkStart) return;
        _chunkStart = start;

        var byPlugin = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        int end = Math.Min(start + ChunkRows, _q.Keys.Count);
        for (int r = start; r < end; r++)
        {
            var fk = _q.Keys[r];
            var plugin = SourceAt(r) ?? view.ResolveWinner(fk)?.WinnerPlugin;
            if (plugin is null) continue;                  // unresolvable: the row's own read names the cause
            if (!byPlugin.TryGetValue(plugin, out var set)) byPlugin[plugin] = set = new HashSet<FormKey>();
            set.Add(fk);
        }

        var sink = new Dictionary<FormKey, IMajorRecordGetter>(end - start);
        foreach (var (plugin, wanted) in byPlugin)
        {
            _ct.ThrowIfCancellationRequested();
            // A plugin that cannot be read leaves its rows unfetched here and the row's own read raises the same
            // fault it always did — the prefetch is an optimisation and must never become a second error path.
            try { view.CollectRecords(_session, plugin, wanted, null, sink); }
            catch (Exception) { }
        }
        _chunk = sink;
    }

    public void Dispose() => _session?.Dispose();
}
