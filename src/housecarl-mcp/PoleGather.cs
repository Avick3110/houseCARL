using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>
/// One comparison pole's bodies for a WINDOW of delta rows or a chunk of tree rows, gathered a group at a time (#765).
///
/// <para>A <c>project.form='delta'</c> row reads one body per pole, and each read went through
/// <see cref="LoadOrderResolver.IndexView.GetRecord"/> — a walk of that plugin from the top for one record. On a
/// real order the poles of row after row land in the same handful of large masters, so a hundred rows paid two
/// hundred whole-plugin walks for bodies a few could have answered.</para>
///
/// <para>Which plugin a pole reads a row from is an INDEX fact — the winner, the named arm, the provider below the
/// subject — so it is known before any body is, and the window's whole declaration can be made up front and
/// gathered through <see cref="BodyGather"/>. An arm that reads no in-order body (an off-order file, the SkyPatcher
/// post replay) leaves <see cref="PluginOf"/> null and its rows read exactly as they did.</para>
/// </summary>
internal sealed class PoleGather
{
    /// <summary>The plugin this pole reads a row's body from, from the index alone: the FormKey and, for a
    /// subject-relative pole, the plugin the subject resolved to for that row. Null when the arm reads no in-order
    /// body, which leaves every row of it on its own read.</summary>
    internal Func<FormKey, string?, string?>? PluginOf;

    /// <summary>The arm's one-time work (the off-order sweep, the SkyPatcher replay's open), run before the metered chunks.</summary>
    internal Action? Prime;

    /// <summary>Whether this pole holds a version of the record, for an arm with no in-order plugin (the post replay, an off-order file); null leaves it to <see cref="PluginOf"/>.</summary>
    internal Func<FormKey, bool>? Holds;

    BodyGather? _gather;

    /// <summary>Whether this chunk is gathered, so a caller reads through it rather than seeking per record.</summary>
    internal bool Live => _gather is not null;

    /// <summary>Declare and gather the tree's chunk. Eager, not deferred: the fold reads every row of its batch.
    /// <para>The gathered bodies are held until <see cref="Release"/>, so what a pole holds is its own chunk's rows —
    /// <see cref="RecordReads.ComparisonChunkRows"/> of them. A delta reads through <see cref="Each"/> instead, which
    /// holds one group's bodies at a time and keeps only the window's readings.</para></summary>
    internal void Open(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                       IReadOnlyList<(FormKey Key, string? Subject)> rows)
    {
        _gather = null;
        if (PluginOf is null || rows.Count == 0) return;
        var g = new BodyGather(view, session);
        foreach (var (key, subject) in rows)
            if (PluginOf(key, subject) is { } plugin) g.Want(plugin, key);
        g.Gather();
        _gather = g;
    }

    /// <summary>The same, for a pole whose rows are not measured from a subject.</summary>
    internal void Open(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, IReadOnlyList<FormKey> keys)
        => Open(view, session, keys.Select(k => (k, (string?)null)).ToList());

    /// <summary>Read a delta window a group at a time: <paramref name="read"/> runs on each row while only its group's
    /// bodies are held, then on the rows this pole reads no in-order body for.</summary>
    internal void Each(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                       IReadOnlyList<(int Row, FormKey Key, string? Subject)> rows, Action<int> read)
    {
        var g = new BodyGather(view, session);
        var rowsOf = new Dictionary<FormKey, List<int>>();
        var rest = new List<int>();
        foreach (var (row, key, subject) in rows)
        {
            if (PluginOf?.Invoke(key, subject) is not { } plugin) { rest.Add(row); continue; }
            g.Want(plugin, key);
            if (!rowsOf.TryGetValue(key, out var l)) rowsOf[key] = l = new List<int>();
            l.Add(row);
        }
        _gather = g;
        try { g.Gather(asked => { foreach (var key in asked) rowsOf[key].ForEach(read); }); }
        finally { _gather = null; }
        rest.ForEach(read);
    }

    /// <summary>Drop the chunk's gathered bodies. A caller that has finished READING through this gather while the
    /// call goes on doing other reads calls it, so the chunk's share is not held alongside them.</summary>
    internal void Release() => _gather = null;

    /// <summary>This row's body. Null means the plugin does not hold the record — the same answer
    /// <see cref="LoadOrderResolver.IndexView.GetRecord"/> gives, and the same one a plugin whose walk faulted
    /// gives, because <see cref="BodyGather"/> falls back to that seek for it.</summary>
    internal IMajorRecordGetter? Body(string plugin, FormKey fk) => _gather!.Body(plugin, fk);
}
