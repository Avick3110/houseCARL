using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The live winner's BODY for each of a known set of records (#251).
///
/// <para>A scan that decides its match on the winner has the candidate FormKeys in hand before it needs any winner
/// body, and a plugin's winners are a contiguous fact about that plugin's overlay. So the bodies are gathered by
/// PLUGIN — one enumeration per distinct winner plugin, however many of its records are wanted — rather than by
/// record, which is one whole-overlay walk EACH and turns a broad audit into O(candidates x overlay). The order
/// bounds the total work: every plugin is walked at most once.</para>
///
/// <para>Nothing is held past the caller's session: the bodies are backed by the overlays that session has open,
/// exactly as <see cref="LoadOrderResolver.IndexView.GetRecord"/>'s are, and the map itself is one entry per
/// CANDIDATE — never one per record in the order.</para>
/// </summary>
public static class WinnerBodies
{
    /// <summary>The winner body of each candidate, keyed by FormKey. A candidate whose winner cannot be resolved,
    /// or whose winner plugin does not yield it, is simply ABSENT — the caller decides what an unfetchable winner
    /// means, because "the index named a winner that did not re-resolve" is a fact it has to report, not one this
    /// helper may swallow. <paramref name="getterTypes"/> is the caller's own type scope when it has one, which
    /// narrows each plugin's walk to the GRUPs those types live in.</summary>
    public static Dictionary<FormKey, IMajorRecordGetter> For(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyCollection<FormKey> candidates, IReadOnlyList<Type>? getterTypes)
    {
        var bodies = new Dictionary<FormKey, IMajorRecordGetter>(candidates.Count);
        if (candidates.Count == 0) return bodies;

        var byPlugin = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in candidates)
        {
            if (view.ResolveWinner(fk) is not { } w) continue;
            if (!byPlugin.TryGetValue(w.WinnerPlugin, out var set)) byPlugin[w.WinnerPlugin] = set = new HashSet<FormKey>();
            set.Add(fk);
        }
        foreach (var (plugin, wanted) in byPlugin)
            // A winner plugin that will not OPEN leaves its candidates absent rather than ending the scan, which is
            // what the per-record fetch this replaces did: each such candidate is then the caller's own
            // "the index named a winner that did not re-resolve" row, and the rest of the scan still answers.
            try { view.CollectRecords(session, plugin, wanted, getterTypes, bodies); }
            catch { /* absent; the caller reports each candidate */ }
        return bodies;
    }
}
