using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The read door: a call's keys sorted by plugin, then by Mutagen group, each group walked once and dropped
/// before the next; contract in docs/architecture/read-engine.md.</summary>
public static class OrderedGather
{
    /// <summary>Walk each (winner plugin, group) of <paramref name="keys"/> once, in load order, handing the group's bodies
    /// and the keys it was asked for to <paramref name="perGroup"/>. A key with no winner is not handed over.</summary>
    public static void ForEachGroup(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                    IEnumerable<FormKey> keys,
                                    Action<IReadOnlyDictionary<FormKey, IMajorRecordGetter>, IReadOnlyCollection<FormKey>> perGroup,
                                    Dictionary<string, PluginUnreadableException>? unreadable = null,
                                    CancellationToken ct = default, Func<bool>? stop = null)
    {
        var pairs = new List<(string, FormKey)>();
        foreach (var fk in keys)
            if (view.ResolveWinner(fk) is { } w) pairs.Add((w.WinnerPlugin, fk));
        ForEachGroup(view, session, pairs, (_, bodies, asked) => perGroup(bodies, asked), unreadable, ct, stop);
    }

    /// <summary>The same door over named copies: each (plugin, key) is read from that plugin, not its winner. A group
    /// keeps what its walk yielded before a fault; the fault goes to <paramref name="unreadable"/>, and that plugin's
    /// later groups are handed over unread. <paramref name="stop"/> ends the walk before the next group.</summary>
    public static void ForEachGroup(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                    IEnumerable<(string Plugin, FormKey Key)> pairs,
                                    Action<string, IReadOnlyDictionary<FormKey, IMajorRecordGetter>, IReadOnlyCollection<FormKey>> perGroup,
                                    Dictionary<string, PluginUnreadableException>? unreadable = null,
                                    CancellationToken ct = default, Func<bool>? stop = null)
    {
        foreach (var (plugin, items) in Sort(view, pairs, p => p.Plugin, p => p.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (stop is not null && stop()) return;
            var asked = new HashSet<FormKey>(items.Select(p => p.Key));
            var bodies = new Dictionary<FormKey, IMajorRecordGetter>(asked.Count);
            if (unreadable is null || !unreadable.ContainsKey(plugin))
                if (BodyGather.WalkOnce(view, session, plugin, asked, bodies) is { } fault) unreadable?.TryAdd(plugin, fault);
            perGroup(plugin, bodies, asked);
        }
    }

    /// <summary>The door's order: <paramref name="items"/> bucketed by (plugin, Mutagen group), sorted by load position
    /// then group. Seek types whose groups overlap in one plugin (cells, worldspaces, the references inside them) share
    /// one bucket; a plugin outside the order sorts last, in buckets of its own.</summary>
    public static List<(string Plugin, List<T> Items)> Sort<T>(LoadOrderResolver.IndexView view, IEnumerable<T> items,
                                                               Func<T, string> pluginOf, Func<T, FormKey> keyOf)
    {
        var byType = new Dictionary<(int Idx, string Plugin, Type? Seek), List<T>>();
        foreach (var item in items)
        {
            var plugin = pluginOf(item);
            var (idx, seek) = view.GroupOf(plugin, keyOf(item));
            if (!byType.TryGetValue((idx, plugin, seek), out var l)) byType[(idx, plugin, seek)] = l = new List<T>();
            l.Add(item);
        }
        var buckets = new List<(int Idx, string Group, string Plugin, List<T> Items)>();
        foreach (var plugin in byType.Keys.GroupBy(k => (k.Idx, k.Plugin)))
        {
            var sets = plugin.Select(k => (Groups: GroupsOf(k.Seek), Items: byType[k])).ToList();
            for (int a = 0; a < sets.Count; a++)
                for (int b = a + 1; b < sets.Count; b++)
                    if (sets[a].Groups.Overlaps(sets[b].Groups))
                    {
                        sets[a].Groups.UnionWith(sets[b].Groups);
                        sets[a].Items.AddRange(sets[b].Items);
                        sets.RemoveAt(b);
                        b = a;                                                 // the joined set may now overlap one already passed
                    }
            foreach (var (groups, list) in sets)
                buckets.Add((plugin.Key.Idx, string.Join("+", groups.Order(StringComparer.Ordinal)), plugin.Key.Plugin, list));
        }
        return buckets.OrderBy(g => g.Idx).ThenBy(g => g.Plugin, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(g => g.Group, StringComparer.Ordinal).Select(g => (g.Plugin, g.Items)).ToList();
    }

    /// <summary>The group names a seek type is walked in; a type with no group map (or none known) is a flat walk of its own.</summary>
    static HashSet<string> GroupsOf(Type? seek)
        => seek is not null && MutagenGroups.PropsOf(seek) is { } props
            ? new HashSet<string>(props.Select(p => p.Name))
            : new HashSet<string> { "*" + (seek?.Name ?? "") };
}
