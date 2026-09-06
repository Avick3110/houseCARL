using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// The scan detail lane's row reader — the one path the four scan renders (text, json, dense, artifact) read a
/// match's body through.
///
/// <para>It exists for two costs the per-row call carried (#582). One overlay SESSION covers the whole render, so a
/// plugin is memory-mapped once for the call instead of once per row. And each row's body comes from
/// <see cref="BodyPrefetch"/>, a chunk of rows at a time, one enumeration per source plugin however many of that
/// chunk's rows want it, instead of the whole-overlay walk per record that
/// <see cref="LoadOrderResolver.IndexView.GetRecord"/> costs.</para>
///
/// <para>Every row also checks the caller's cancellation token, so a client that aborts stops the render inside one
/// row.</para>
/// </summary>
internal sealed class ScanDetailReader : IDisposable
{
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
        int start = BodyPrefetch.ChunkStart(i);
        if (start == _chunkStart) return;
        _chunkStart = start;
        // The scan's own resolved types narrow each plugin's walk to the GRUPs they live in, which is the whole
        // point of the gather on a master whose placed references outnumber the records wanted.
        _chunk = BodyPrefetch.Gather(view, _session, _q.Keys, start,
                                     Math.Min(start + BodyPrefetch.ChunkRows, _q.Keys.Count),
                                     SourceAt, _q.GetterTypes, _ct);
    }

    public void Dispose() => _session?.Dispose();
}
