using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The read door: each plugin's group walked once per call; contract in docs/architecture/read-engine.md.</summary>
public static class OrderedGather
{
    /// <summary>Walk each (winner plugin, group) of <paramref name="keys"/> once, in load order, handing over its bodies and asked keys.</summary>
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

    /// <summary>The same door over named copies: each (plugin, key) is read from that plugin, not its winner.
    /// <paramref name="faulted"/> is handed each group whose walk faulted, or, when <paramref name="unreadable"/> is passed, that a plugin which will not open left unread, with that group's fault.</summary>
    public static void ForEachGroup(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                    IEnumerable<(string Plugin, FormKey Key)> pairs,
                                    Action<string, IReadOnlyDictionary<FormKey, IMajorRecordGetter>, IReadOnlyCollection<FormKey>> perGroup,
                                    Dictionary<string, PluginUnreadableException>? unreadable = null,
                                    CancellationToken ct = default, Func<bool>? stop = null,
                                    Action<string, IReadOnlyCollection<FormKey>, PluginUnreadableException>? faulted = null)
    {
        foreach (var (plugin, items) in Sort(view, pairs, p => p.Plugin, p => p.Key))
        {
            ct.ThrowIfCancellationRequested();
            if (stop is not null && stop()) return;
            var asked = new HashSet<FormKey>(items.Select(p => p.Key));
            var bodies = new Dictionary<FormKey, IMajorRecordGetter>(asked.Count);
            // A plugin that will not open is skipped from then on; a broken group leaves its other groups to be walked.
            if (unreadable is not null && unreadable.TryGetValue(plugin, out var prior) && prior is not PluginUnscannableException)
                faulted?.Invoke(plugin, asked, prior);
            else if (BodyGather.WalkOnce(view, session, plugin, asked, bodies) is { } fault)
            {
                unreadable?.TryAdd(plugin, fault);
                faulted?.Invoke(plugin, asked, fault);
            }
            perGroup(plugin, bodies, asked);
        }
    }

    /// <summary>The door's order: <paramref name="items"/> bucketed by (plugin, Mutagen group), by load position then group.</summary>
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
            MutagenGroups.JoinOverlapping(sets);
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
