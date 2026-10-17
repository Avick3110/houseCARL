using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Which top-level groups of a plugin hold each record type, derived from Mutagen's own getter interfaces:
/// a group holds every major record its records contain, through nested records and lists (cells in blocks, placed
/// references in cells, responses in topics). Nothing here is written per type.</summary>
public static class MutagenGroups
{
    static readonly Lazy<(PropertyInfo Prop, HashSet<Type> Held)[]> Groups = new(Build);
    static readonly ConcurrentDictionary<Type, PropertyInfo[]?> Memo = new();
    static readonly Lazy<Dictionary<PropertyInfo, Func<ISkyrimModGetter, object?>>> Getters =
        new(() => Groups.Value.ToDictionary(g => g.Prop, g => g.Prop.GetMethod!.CreateDelegate<Func<ISkyrimModGetter, object?>>()));

    /// <summary>The group <paramref name="prop"/> names on <paramref name="mod"/>, read through a compiled getter so a
    /// fault inside it arrives as itself, not wrapped by reflection.</summary>
    public static object? Read(PropertyInfo prop, ISkyrimModGetter mod) => Getters.Value[prop](mod);

    /// <summary>The group <paramref name="prop"/> names on <paramref name="mod"/>, or null when the file lacks it: Mutagen
    /// stands in an empty, writable group for a missing one, and an overlay's own groups are read-only.</summary>
    public static IMajorRecordGetterEnumerable? Present(PropertyInfo prop, ISkyrimModGetter mod)
        => Read(prop, mod) is IMajorRecordGetterEnumerable group and not IMajorRecordEnumerable ? group : null;

    /// <summary>The <see cref="ISkyrimModGetter"/> group properties that hold <paramref name="seekType"/>, in declaration
    /// order; null when no group holds it, or every group does (the base record interface): the caller walks flat.</summary>
    public static PropertyInfo[]? PropsOf(Type seekType) => Memo.GetOrAdd(seekType, static t =>
    {
        var props = Groups.Value.Where(g => g.Held.Any(h => t.IsAssignableFrom(h) || h.IsAssignableFrom(t)))
                                .Select(g => g.Prop).ToArray();
        return props.Length == 0 || props.Length == Groups.Value.Length ? null : props;
    });

    /// <summary>The seek types among <paramref name="types"/> whose groups overlap, joined into sets of two or more that
    /// can share one read of those groups. A type alone in its groups, or held by none, is left out.</summary>
    public static List<List<Type>> SharedGroups(IEnumerable<Type> types)
    {
        var sets = types.Where(t => PropsOf(t) is not null)
                        .Select(t => (Props: new HashSet<PropertyInfo>(PropsOf(t)!), Types: new List<Type> { t })).ToList();
        JoinOverlapping(sets);
        return sets.Where(s => s.Types.Count > 1).Select(s => s.Types).ToList();
    }

    /// <summary>Join the sets whose keys overlap, in place, until no two do; a joined set keeps the first one's place.</summary>
    public static void JoinOverlapping<TKey, TItem>(List<(HashSet<TKey> Keys, List<TItem> Items)> sets)
    {
        for (int a = 0; a < sets.Count; a++)
            for (int b = a + 1; b < sets.Count; b++)
                if (sets[a].Keys.Overlaps(sets[b].Keys))
                {
                    sets[a].Keys.UnionWith(sets[b].Keys);
                    sets[a].Items.AddRange(sets[b].Items);
                    sets.RemoveAt(b);
                    b = a;                                                 // the joined set may now overlap one already passed
                }
    }

    static (PropertyInfo, HashSet<Type>)[] Build()
        => typeof(ISkyrimModGetter).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType.IsGenericType)
            .OrderBy(p => p.MetadataToken).Select(p => (p, Reach(p.PropertyType.GetGenericArguments()[0]))).ToArray();

    /// <summary>Every major-record getter type reachable from <paramref name="root"/> through properties holding a Skyrim
    /// getter object or a list of them. Form links are generic and never followed: they reference, they do not contain.</summary>
    static HashSet<Type> Reach(Type root)
    {
        var held = new HashSet<Type>();
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t)) continue;
            if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) held.Add(t);
            foreach (var iface in t.GetInterfaces().Prepend(t))
                foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (Contained(prop.PropertyType) is { } c) stack.Push(c);
        }
        return held;
    }

    /// <summary>The Skyrim getter interface a property holds, directly or as list elements, or null. It goes by Mutagen's
    /// naming (a non-generic I…Getter); a type it missed would only send that type's gather to the flat walk, slower
    /// but the same answer, and the switch-agreement test would fail first.</summary>
    static Type? Contained(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) t = t.GetGenericArguments()[0];
        return t.IsInterface && !t.IsGenericType && t.Namespace == typeof(ISkyrimModGetter).Namespace
               && t.Name.EndsWith("Getter", StringComparison.Ordinal) ? t : null;
    }
}
