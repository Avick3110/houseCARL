using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The live winner's BODY for each of a known set of records — <see cref="BodyGather"/> with the winner
/// resolution a scan does in front of it, gathered by PLUGIN rather than by record. The caller hands in one CHUNK
/// at a time, and nothing is held past its session; contract in docs/architecture/read-engine.md.</summary>
public static class WinnerBodies
{
    /// <summary>The winner body of each candidate, keyed by FormKey; a candidate whose winner cannot be resolved or
    /// fetched is ABSENT, and an unreadable winner plugin is named once in <paramref name="unreadable"/> with its
    /// cause. <paramref name="ct"/> is checked between plugin walks.</summary>
    public static Dictionary<FormKey, IMajorRecordGetter> For(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyCollection<FormKey> candidates, IReadOnlyList<Type>? getterTypes,
        out Dictionary<string, PluginUnreadableException> unreadable,
        CancellationToken ct = default)
    {
        unreadable = new Dictionary<string, PluginUnreadableException>(StringComparer.OrdinalIgnoreCase);
        var bodies = new Dictionary<FormKey, IMajorRecordGetter>(candidates.Count);
        if (candidates.Count == 0) return bodies;

        var gather = new BodyGather(view, session, getterTypes, ct: ct);
        foreach (var fk in candidates)
            if (view.ResolveWinner(fk) is { } w) gather.Want(w.WinnerPlugin, fk);
        gather.Gather();
        foreach (var (plugin, fault) in gather.Faults) unreadable[plugin] = fault;   // declaration order, which is the caller's own
        gather.CopyInto(bodies);
        return bodies;
    }
}
