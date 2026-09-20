using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>How a field holds its owned child records; the singular-versus-collection split is in docs/architecture/records-owned-child-declarers.md.</summary>
public enum OwnedChildShape
{
    None,
    Singular,
    Collection,
}

/// <summary>The READ side's view of the fields that own child records — the same question <see cref="WriteEngine.ChildBearingProperties"/> answers for the write surface, asked of a body the read engine just fetched.</summary>
public static class OwnedChildContent
{
    /// <summary>The child-bearing fields of <paramref name="body"/>'s type with their shape, memoized per runtime type; empty for every type but the ones that own children.</summary>
    public static IReadOnlyDictionary<string, OwnedChildShape> Fields(IMajorRecordGetter body) =>
        _byType.GetOrAdd(body.GetType(), static t =>
        {
            // ChildBearingProperties needs a SETTABLE property, and an overlay type exposes the list children settably and the singular one read-only.
            var getter = WriteEngine.PrimaryGetter(t);
            var concrete = getter is null ? null : WriteEngine.ConcreteOf(getter);
            var map = new Dictionary<string, OwnedChildShape>(StringComparer.Ordinal);
            if (concrete is null) return map;
            foreach (var p in WriteEngine.ChildBearingProperties(concrete))
                // The shape is a fact about the PROPERTY, not about any one body's value.
                map[p.Name] = typeof(IMajorRecordGetter).IsAssignableFrom(p.PropertyType)
                    ? OwnedChildShape.Singular
                    : OwnedChildShape.Collection;
            return map;
        });

    static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, OwnedChildShape>> _byType = new();

    /// <summary>The child-bearing fields whose child records sit BELOW the field's own elements, so a note carrying both numbers has to say which unit it counts.</summary>
    public static IReadOnlySet<string> NestedFields(IMajorRecordGetter body) =>
        _nestedByType.GetOrAdd(body.GetType(), static t =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var getter = WriteEngine.PrimaryGetter(t);
            var concrete = getter is null ? null : WriteEngine.ConcreteOf(getter);
            if (concrete is null) return (IReadOnlySet<string>)set;
            foreach (var p in WriteEngine.ChildBearingProperties(concrete))
            {
                if (typeof(IMajorRecordGetter).IsAssignableFrom(p.PropertyType)) continue;   // singular: the value IS the child
                var elem = WriteEngine.ElementTypeOf(p.PropertyType);
                // An element that is itself a record makes the list the child list; anything else holds its children deeper down.
                if (elem is null || !typeof(IMajorRecordGetter).IsAssignableFrom(elem)) set.Add(p.Name);
            }
            return (IReadOnlySet<string>)set;
        });

    static readonly ConcurrentDictionary<Type, IReadOnlySet<string>> _nestedByType = new();

    public static OwnedChildShape ShapeOf(IMajorRecordGetter body, string field) =>
        Fields(body).TryGetValue(field, out var s) ? s : OwnedChildShape.None;

    /// <summary>Does <paramref name="body"/>'s own <paramref name="field"/> DECLARE at least one child record? NULL means the field could not be READ, never false; contracts in docs/architecture/records-owned-child-declarers.md.</summary>
    public static bool? DeclaresChild(IMajorRecordGetter body, string field)
    {
        try
        {
            var p = WriteEngine.ResolveProperty(body.GetType(), field);
            return p is null ? null : ReachesRecord(p.GetValue(body), 0);
        }
        catch { return null; }
    }

    /// <summary>The deepest container nesting the value walk follows before answering "I could not look" — a tripwire, and it answers NULL rather than false.</summary>
    const int MaxDepth = 6;

    static bool? ReachesRecord(object? val, int depth)
    {
        if (val is null) return false;                       // absent optional / empty — declares nothing
        if (depth > MaxDepth) return null;                   // nested deeper than any known shape — unknown, not "no"
        if (val is IFormLinkGetter) return false;            // a reference, not a child (the type walk's own cut)
        if (val is IMajorRecordGetter) return true;          // a singular owned child, present
        if (val is string) return false;
        // Mutagen's own containment walk, and it is LAZY: Any() stops at the first child record.
        if (val is IMajorRecordGetterEnumerable en) return en.EnumerateMajorRecords().Any();
        if (val is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                var r = ReachesRecord(item, depth + 1);
                if (r is not false) return r;                // true, or an unknown that must not be reported as "no"
            }
            return false;
        }
        return false;
    }
}
