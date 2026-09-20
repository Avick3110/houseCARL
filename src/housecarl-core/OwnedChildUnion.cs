using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>One plugin's contribution to a field's additive union; the counts are contributions, not a partition.</summary>
public sealed record ChildUnionDeclarer(string Plugin, int Count);

/// <summary>What the game assembles for ONE child-bearing field of ONE record, across every plugin that touches it; it claims DECLARATION, not liveness (docs/architecture/records-owned-child-declarers.md).</summary>
public sealed record ChildUnion(
    string Field,
    OwnedChildShape Shape,
    IReadOnlyList<FormKey> Members,
    int OwnCount,
    IReadOnlyList<ChildUnionDeclarer> Declarers,
    IReadOnlyList<string> Unreadable,
    string? LivePlugin,
    bool Nested)
{
    /// <summary>Distinct child records the order declares for this field.</summary>
    public int Total => Members.Count;

    /// <summary>Do <see cref="OwnCount"/> and the field's rendered value count the same unit? False on a nested field, where the value counts blocks and these count the cells under them.</summary>
    public bool CountsTheRenderedUnit => !Nested;

}

/// <summary>The additive union a child-bearing field really holds (#342 / #487): the FormID-keyed union over every touching plugin's own body, read through the caller's session and holding nothing after the call.</summary>
public static class OwnedChildUnion
{
    /// <summary>The additive union of each named child-bearing field of <paramref name="fk"/>, or NULL when the record has one toucher; <paramref name="subjectBody"/> is reused rather than re-fetched.</summary>
    public static IReadOnlyDictionary<string, ChildUnion>? Compute(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, FormKey fk,
        string subjectPlugin, IMajorRecordGetter subjectBody, IReadOnlyDictionary<string, OwnedChildShape> fields)
    {
        if (fields.Count == 0) return null;
        var touching = view.TouchingPlugins(fk);
        if (touching is null || touching.Count <= 1) return null;

        var getterType = WriteEngine.SeekTypeFor(subjectBody);
        var members = new Dictionary<string, List<FormKey>>(StringComparer.Ordinal);
        var seen = new Dictionary<string, HashSet<FormKey>>(StringComparer.Ordinal);
        var declarers = new Dictionary<string, List<ChildUnionDeclarer>>(StringComparer.Ordinal);
        var unreadable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var live = new Dictionary<string, string?>(StringComparer.Ordinal);
        var liveKeys = new Dictionary<string, IReadOnlyList<FormKey>>(StringComparer.Ordinal);
        var own = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in fields.Keys)
        {
            members[f] = new List<FormKey>(); seen[f] = new HashSet<FormKey>();
            declarers[f] = new List<ChildUnionDeclarer>(); unreadable[f] = new List<string>();
            live[f] = null; liveKeys[f] = Array.Empty<FormKey>(); own[f] = 0;
        }

        // Priority order, low to high: member order is first-declaration order, and a singular field's live copy is the last declarer standing.
        foreach (var plugin in touching)
        {
            // A sibling plugin that will not OPEN must not fault the read; it becomes an unreadable the note names.
            IMajorRecordGetter? body;
            if (string.Equals(plugin, subjectPlugin, StringComparison.OrdinalIgnoreCase)) body = subjectBody;
            else
                try { body = view.GetRecord(session, plugin, fk, getterType); }
                catch { body = null; }
            foreach (var f in fields.Keys)
            {
                // Null is "could not look", never "declares nothing" (#308's rule, one level down).
                var keys = body is null ? null : ChildKeys(body, f);
                if (keys is null) { unreadable[f].Add(plugin); continue; }
                if (keys.Count == 0) continue;
                declarers[f].Add(new ChildUnionDeclarer(plugin, keys.Count));
                live[f] = plugin;
                liveKeys[f] = keys;
                if (ReferenceEquals(body, subjectBody)) own[f] = keys.Count;
                foreach (var k in keys)
                    if (seen[f].Add(k)) members[f].Add(k);
            }
        }

        // The unit the counts are in, against the unit the field's value renders in — asked once off the subject's type.
        var nested = OwnedChildContent.NestedFields(subjectBody);
        var result = new Dictionary<string, ChildUnion>(fields.Count, StringComparer.Ordinal);
        foreach (var (f, shape) in fields)
            result[f] = new ChildUnion(f, shape,
                                       shape == OwnedChildShape.Singular ? liveKeys[f] : members[f],
                                       own[f], declarers[f], unreadable[f], live[f], nested.Contains(f));
        return result;
    }

    /// <summary>The child records ONE body declares in ONE field, or NULL when the field could not be read; it collects the FIRST record level and stops there.</summary>
    public static IReadOnlyList<FormKey>? ChildKeys(IMajorRecordGetter body, string field)
    {
        try
        {
            var p = WriteEngine.ResolveProperty(body.GetType(), field);
            if (p is null) return null;
            var keys = new List<FormKey>();
            return Collect(p.GetValue(body), WriteEngine.OwnedRecordTypeOf(p.PropertyType), keys, 0) ? keys : null;
        }
        catch { return null; }
    }

    /// <summary>The deepest container nesting the value walk follows before answering "I could not look".</summary>
    const int MaxDepth = 6;

    static bool Collect(object? val, Type? childType, List<FormKey> sink, int depth)
    {
        if (val is null) return true;                            // absent optional / empty — declares nothing
        if (depth > MaxDepth) return false;                      // nested deeper than any known shape — unknown, not "no"
        if (val is IFormLinkGetter) return true;                 // a reference, not a child (the type walk's own cut)
        if (val is IMajorRecordGetter rec) { sink.Add(rec.FormKey); return true; }
        if (val is string) return true;
        // A non-record container that knows its own containment: ask Mutagen for the child TYPE this field owns, so the walk stops at the cells.
        if (val is IMajorRecordGetterEnumerable en && childType is not null)
        {
            int before = sink.Count;
            foreach (var child in en.EnumerateMajorRecords(childType, throwIfUnknown: false)) sink.Add(child.FormKey);
            // An unroutable typed enumeration yields an EMPTY sequence rather than throwing, so an empty typed walk over a container that holds records at all is a miss, not a negative.
            if (sink.Count == before && en.EnumerateMajorRecords().Any()) return false;
            return true;
        }
        if (val is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
                if (!Collect(item, childType, sink, depth + 1)) return false;
            return true;
        }
        return false;                                            // a shape this walk does not know — "could not look"
    }
}
