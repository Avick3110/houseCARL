using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The scan detail lane's row reader — the one path the four scan renders (text, json, dense, artifact)
/// read a match's body through, on one overlay session and one <see cref="BodyPrefetch"/> chunk, checking the
/// caller's cancellation token per ROW; contract in docs/architecture/read-engine.md.</summary>
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
    readonly RecordReads.LinkMemo? _linkMemo;
    readonly LoadOrderResolver.IndexView? _view;
    readonly LoadOrderResolver.OverlaySession? _session;

    BodyPrefetch.Chunk? _chunk;
    int _chunkStart = -1;

    internal ScanDetailReader(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int depth,
                              bool resolveNames, bool winnerFields, string? containerHint,
                              IReadOnlyList<int>? depths, CancellationToken ct)
    {
        _svc = svc; _q = q; _fields = fields; _depth = depth;
        _resolveNames = resolveNames; _winnerFields = winnerFields;
        _containerHint = containerHint; _depths = depths; _ct = ct;
        _linkMemo = resolveNames ? new RecordReads.LinkMemo() : null;
        // Only a pinned outcome can be read this way; an unpinned one falls through to the plain per-row path.
        _view = q.Pin?.View;
        _session = q.Pin?.Resolver.OpenSession();
    }

    /// <summary>One link-resolution cache for the whole render, so a target recurring across rows resolves once.</summary>
    internal RecordReads.LinkMemo? LinkMemo => _linkMemo;

    /// <summary>Read row <paramref name="i"/> of the scan's key list.</summary>
    internal ReadOutcome Row(int i)
    {
        _ct.ThrowIfCancellationRequested();
        var fk = _q.Keys[i];
        var plugin = SourceAt(i);
        FillChunk(i);
        var body = _chunk?.Body(fk);   // the plugin is walked here, on the first row of the chunk that wants it
        return _svc.ResolveReadOn(_q, fk, plugin, _fields, false, _depth, _resolveNames, _linkMemo,
                                  _containerHint, _depths, _session, body);
    }

    /// <summary>The plugin whose body this row displays: the scan's own per-match source, or the winner when the
    /// call retargeted display to it.</summary>
    string? SourceAt(int i)
        => _winnerFields ? null : (_q.Sources is { } src && i < src.Count ? src[i] : null);

    void FillChunk(int i)
    {
        if (_view is not { } view || _session is null) return;   // unpinned: the per-row path answers
        int start = BodyPrefetch.ChunkStart(i);
        if (start == _chunkStart) return;
        _chunkStart = start;
        // The scan's own resolved types narrow each plugin's walk to the GRUPs they live in.
        _chunk = BodyPrefetch.Gather(view, _session, _q.Pin!.Resolver.Counters, _q.Keys, start,
                                     Math.Min(start + BodyPrefetch.ChunkRows, _q.Keys.Count),
                                     SourceAt, _q.GetterTypes, _ct);
    }

    public void Dispose() => _session?.Dispose();
}

/// <summary>One scan call's matches as its text render reads them: each row read once, in order, however many times
/// the render is laid (whole first, at the cap, and the floor check's re-renders), with what reading them cost.</summary>
internal sealed class ScanRows : IDisposable
{
    readonly LoadOrderService _svc;
    readonly CrossQueryOutcome _q;
    readonly ScanDetailReader? _reader;
    readonly List<ReadOutcome> _detail = new();
    readonly List<long> _detailMs = new();
    readonly List<RecordSummary> _summary = new();
    readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>A detail reader is opened only when <paramref name="fields"/> asks for bodies.</summary>
    internal ScanRows(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int depth,
                      bool resolveNames, bool winnerFields, string? containerHint, CancellationToken ct)
    {
        _svc = svc;
        _q = q;
        // One session, one link cache, one chunked body prefetch for every rendered match.
        _reader = fields is { Count: > 0 } && q.Error is null && q.Groups is null
            ? new ScanDetailReader(svc, q, fields, depth, resolveNames, winnerFields, containerHint, null, ct)
            : null;
    }

    /// <summary>Row <paramref name="i"/>'s body, read on first ask.</summary>
    internal ReadOutcome Detail(int i)
    {
        while (_detail.Count <= i)
        {
            _clock.Start();
            _detail.Add(_reader!.Row(_detail.Count));
            _clock.Stop();
            _detailMs.Add(_clock.ElapsedMilliseconds);
        }
        return _detail[i];
    }

    /// <summary>How many bodies the call has read so far.</summary>
    internal int BodiesRead => _detail.Count;

    /// <summary>What reading the first <paramref name="rows"/> bodies cost, fixed once read, so every render of the call states one figure.</summary>
    internal long MillisThrough(int rows) => rows == 0 ? 0 : _detailMs[rows - 1];

    /// <summary>Row <paramref name="i"/>'s summary: prefilled by the scan, or filled on first ask, pinned to the scan's build.</summary>
    internal RecordSummary Summary(int i)
    {
        if (_q.Prefilled is not null) return _q.Prefilled[i];
        while (_summary.Count <= i) _summary.Add(_svc.ResolveSummaryOn(_q, _q.Keys[_summary.Count]));
        return _summary[i];
    }

    public void Dispose() => _reader?.Dispose();
}
