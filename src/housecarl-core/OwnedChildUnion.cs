using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>One plugin's contribution to a field's additive union: how many child records ITS body declares there.
/// A child two plugins both declare is counted by both — the counts are contributions, not a partition, which is
/// why <see cref="ChildUnion.Total"/> is carried rather than summed from these.</summary>
public sealed record ChildUnionDeclarer(string Plugin, int Count);

/// <summary>What the game assembles for ONE child-bearing field of ONE record, across every plugin that touches it.
///
/// <para>For a <see cref="OwnedChildShape.Collection"/> field the answer is a UNION keyed by FormID:
/// <see cref="Members"/> is every distinct child the order declares, in load order of first declaration, each
/// counted once however many plugins declare it. For a <see cref="OwnedChildShape.Singular"/> field there is no
/// union — the declarers override one record — so <see cref="Members"/> is the one live child and
/// <see cref="LivePlugin"/> is the highest plugin declaring it.</para>
///
/// <para><b>What it claims is DECLARATION, not liveness.</b> A child in this set is one some plugin declares for
/// this parent. Whether that child's own winner is deleted or initially disabled is a fact about the child record,
/// readable by reading it, and asserting it here would cost one fetch per member.</para></summary>
public sealed record ChildUnion(
    string Field,
    OwnedChildShape Shape,
    IReadOnlyList<FormKey> Members,
    int OwnCount,
    IReadOnlyList<ChildUnionDeclarer> Declarers,
    IReadOnlyList<string> Unreadable,
    string? LivePlugin)
{
    /// <summary>Distinct child records the order declares for this field.</summary>
    public int Total => Members.Count;

    /// <summary>Does the body the read was taken from carry less than the whole? The #342 case in one predicate:
    /// the winner touched the parent for an unrelated reason and its own list reads short or empty.</summary>
    public bool OwnIsPartial => OwnCount < Total;
}

/// <summary>
/// The additive union a child-bearing field really holds (#342 / #487).
///
/// <para>An owned child record is declared PER PLUGIN, and for a collection field the game assembles the parent's
/// children from every plugin that declares any. A cell override that exists to add occlusion data carries no
/// placed references and deletes none, so reading its <c>Temporary</c> reports an empty cell the game fills with
/// hundreds — the read model's one silent wrong answer. This computes what the engine assembles instead: the
/// FormID-keyed union over every touching plugin's own body, so a child two overrides both declare is counted
/// once rather than twice.</para>
///
/// <para><b>Cost.</b> One body per touching plugin, seeked by the record's own type
/// (<see cref="LoadOrderResolver.IndexView.GetRecord"/> takes it, #354) so finding a cell in a worldspace plugin
/// does not step over the placed references that outnumber it. Nothing is held: the bodies are read through the
/// caller's open session and only FormKeys and counts survive the call.</para>
/// </summary>
public static class OwnedChildUnion
{
    /// <summary>The additive union of each named child-bearing field of <paramref name="fk"/>, or NULL when the
    /// record has one toucher — its own body is then the whole story and there is nothing to assemble.
    /// <paramref name="subjectBody"/> is the body the read was taken from (the winner, or a <c>plugin=</c>-scoped
    /// copy); it is reused rather than re-fetched, and it is what <see cref="ChildUnion.OwnCount"/> is about.</summary>
    public static IReadOnlyDictionary<string, ChildUnion>? Compute(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, FormKey fk,
        string subjectPlugin, IMajorRecordGetter subjectBody, IReadOnlyDictionary<string, OwnedChildShape> fields)
    {
        if (fields.Count == 0) return null;
        var touching = view.TouchingPlugins(fk);
        if (touching is null || touching.Count <= 1) return null;

        var getterType = WriteEngine.PrimaryGetter(subjectBody.GetType());
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

        // Priority order, low to high: the union's member order is first-declaration order, and the SINGULAR
        // shape's live copy is simply the last declarer standing.
        foreach (var plugin in touching)
        {
            var body = string.Equals(plugin, subjectPlugin, StringComparison.OrdinalIgnoreCase)
                ? subjectBody
                : view.GetRecord(session, plugin, fk, getterType);
            foreach (var f in fields.Keys)
            {
                // Null is "could not look", never "declares nothing" — a provider counted into the negative would
                // turn an unreadable body into evidence the field is empty (#308's rule, one level down).
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

        var result = new Dictionary<string, ChildUnion>(fields.Count, StringComparer.Ordinal);
        foreach (var (f, shape) in fields)
            result[f] = new ChildUnion(f, shape,
                                       shape == OwnedChildShape.Singular ? liveKeys[f] : members[f],
                                       own[f], declarers[f], unreadable[f], live[f]);
        return result;
    }

    /// <summary>The child records ONE body declares in ONE field, or NULL when the field could not be read.
    /// <para>It collects the FIRST record level and stops there: a child's own children belong to the child's own
    /// fields. That matters for <c>Worldspace.SubCells</c>, whose cells sit two container levels down and hold
    /// placed references of their own — Mutagen's untyped containment enumeration would sweep those in and report
    /// a worldspace as declaring every reference in the game.</para></summary>
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

    /// <summary>The deepest container nesting the value walk will follow before answering "I could not look" —
    /// the same tripwire constant, for the same reason, as <see cref="OwnedChildContent"/>'s.</summary>
    const int MaxDepth = 6;

    static bool Collect(object? val, Type? childType, List<FormKey> sink, int depth)
    {
        if (val is null) return true;                            // absent optional / empty — declares nothing
        if (depth > MaxDepth) return false;                      // nested deeper than any known shape — unknown, not "no"
        if (val is IFormLinkGetter) return true;                 // a reference, not a child (the type walk's own cut)
        if (val is IMajorRecordGetter rec) { sink.Add(rec.FormKey); return true; }
        if (val is string) return true;
        // A non-record container that knows its own containment (a worldspace block): ask Mutagen for the child
        // TYPE this field owns, so the walk stops at the cells rather than descending into their contents.
        if (val is IMajorRecordGetterEnumerable en && childType is not null)
        {
            foreach (var child in en.EnumerateMajorRecords(childType, throwIfUnknown: false)) sink.Add(child.FormKey);
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
