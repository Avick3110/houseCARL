using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>
/// One comparison pole's bodies for a CHUNK of delta rows, gathered a plugin at a time (#765).
///
/// <para>A <c>project.form='delta'</c> row reads one body per pole, and each read went through
/// <see cref="LoadOrderResolver.IndexView.GetRecord"/> — a walk of that plugin from the top for one record. On a
/// real order the poles of row after row land in the same handful of large masters, so a hundred rows paid two
/// hundred whole-plugin walks for bodies a few could have answered.</para>
///
/// <para>Which plugin a pole reads a row from is an INDEX fact — the winner, the named arm, the provider below the
/// subject — so it is known before any body is, and the chunk's whole declaration can be made up front and
/// gathered through <see cref="BodyGather"/>. An arm that reads no in-order body (an off-order file, the SkyPatcher
/// post replay) leaves <see cref="PluginOf"/> null and its rows read exactly as they did.</para>
/// </summary>
internal sealed class PoleGather
{
    /// <summary>The plugin this pole reads a row's body from, from the index alone: the FormKey and, for a
    /// subject-relative pole, the plugin the subject resolved to for that row. Null when the arm reads no in-order
    /// body, which leaves every row of it on its own read.</summary>
    internal Func<FormKey, string?, string?>? PluginOf;

    BodyGather? _gather;

    /// <summary>Whether this chunk is gathered, so a caller reads through it rather than seeking per record.</summary>
    internal bool Live => _gather is not null;

    /// <summary>Declare and gather the chunk. Eager, not deferred: a delta reads every row of its batch, so nothing
    /// here is speculative — unlike a scan render, which stops at max_chars mid-chunk.
    /// <para>The gathered bodies are held until the NEXT chunk replaces this one, so what a pole holds is its own
    /// chunk's rows — <see cref="RecordReads.ComparisonChunkRows"/> of them — rather than the one body the
    /// per-record read held. That is the retention the chunk size is the bound on.</para></summary>
    internal void Open(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                       IReadOnlyList<FormKey> keys, Func<int, string?> subjectAt)
    {
        _gather = null;
        if (PluginOf is null || keys.Count == 0) return;
        var g = new BodyGather(view, session);
        for (int j = 0; j < keys.Count; j++)
            if (PluginOf(keys[j], subjectAt(j)) is { } plugin) g.Want(plugin, keys[j]);
        g.Gather();
        _gather = g;
    }

    /// <summary>Drop the chunk's gathered bodies. A caller that has finished READING through this gather while the
    /// call goes on doing other reads calls it, so the chunk's share is not held alongside them.</summary>
    internal void Release() => _gather = null;

    /// <summary>This row's body. Null means the plugin does not hold the record — the same answer
    /// <see cref="LoadOrderResolver.IndexView.GetRecord"/> gives, and the same one a plugin whose walk faulted
    /// gives, because <see cref="BodyGather"/> falls back to that seek for it.</summary>
    internal IMajorRecordGetter? Body(string plugin, FormKey fk) => _gather!.Body(plugin, fk);
}
