using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>How a field holds its owned child records — the distinction that decides what is TRUE of it.
///
/// <para>A <see cref="Collection"/> field (a cell's Persistent/Temporary/NavigationMeshes, a topic's Responses, a
/// worldspace's SubCells) holds MANY children, each with its own FormKey, and the game assembles them from every
/// plugin that declares any — so one plugin's list is not the total. A <see cref="Singular"/> field
/// (Cell.Landscape, Worldspace.TopCell) holds ONE child record: several plugins declaring it are OVERRIDING one
/// record, resolved by load order, not contributing to a merge. Saying "not the merged total" about a singular
/// child is simply false, which is why this distinction is structural rather than a wording choice.</para></summary>
public enum OwnedChildShape
{
    /// <summary>The field is not one that owns child records, or its shape could not be determined.</summary>
    None,
    /// <summary>ONE owned child record — declarers override each other (Cell.Landscape, Worldspace.TopCell).</summary>
    Singular,
    /// <summary>MANY owned child records — declarers contribute additively (Persistent, Temporary, Responses, …).</summary>
    Collection,
}

/// <summary>
/// The READ side's view of the fields that own child records — the same question
/// <see cref="WriteEngine.ChildBearingProperties"/> answers for the write surface, asked of a body the read
/// engine just fetched.
///
/// <para><b>Why the read side asks it at all (#342).</b> An owned child record is declared PER PLUGIN, and for a
/// COLLECTION field the game assembles a parent's children from every plugin that declares them — a cell override
/// that touches the record for an unrelated reason (occlusion data, lighting, music) carries no placed references
/// and deletes none either. Reading such a winner's <c>Temporary</c> therefore reports an empty list for a cell
/// the game fills with hundreds of references. Measured: <c>008EB5:Skyrim.esm</c> (Dawnstar exterior) reads
/// Persistent 0 / Temporary 0 at its winner while <c>Skyrim.esm</c>'s own body carries 201 Temporary.</para>
///
/// <para><b>The field set is not a hand list.</b> It is <see cref="WriteEngine.ChildBearingProperties"/> — the
/// reflected, recursive walk the write surface's child-preservation runs on, pinned by write-surface-guard over
/// EVERY concrete record type Mutagen models and cross-checked against the corpus by corpus-hygiene-guard's
/// INV6. So the read side inherits the same coverage by construction: against Mutagen 0.53.1 that is Cell
/// (Landscape, NavigationMeshes, Persistent, Temporary), DialogTopic (Responses) and Worldspace (TopCell,
/// SubCells), and a Mutagen bump that grows one is annotated without an edit here.</para>
///
/// <para>The read engine hands this a getter — an overlay body, not the settable class the write walk reflects
/// over — so the field set maps getter → concrete through the engine's own <see cref="WriteEngine.PrimaryGetter"/>
/// / <see cref="WriteEngine.ConcreteOf"/> pair rather than a second name mapping of its own. That hop is
/// load-bearing and its cost is measured, not assumed: asking the overlay type directly answers correctly for the
/// LIST children and silently drops the SINGULAR ones, which the guard holds in both a Landscape arm and a
/// type-level arm over every concrete child-bearing type.</para>
/// </summary>
public static class OwnedChildContent
{
    /// <summary>The child-bearing fields of <paramref name="body"/>'s type, each with its
    /// <see cref="OwnedChildShape"/> — memoized per runtime type (the
    /// <see cref="WriteEngine.ChildBearingProperties"/> precedent: reflection metadata, constant for the process
    /// lifetime, asked on every record read). EMPTY for every record type but the three that own children, which
    /// is what makes a caller's walk free on the reads that don't touch this shape.</summary>
    public static IReadOnlyDictionary<string, OwnedChildShape> Fields(IMajorRecordGetter body) =>
        _byType.GetOrAdd(body.GetType(), static t =>
        {
            // ChildBearingProperties requires a SETTABLE property (it exists to lift children off a record and put
            // them back), and an overlay body is not the type it was written against. Measured on Mutagen 0.53.1:
            // CellBinaryOverlay answers [NavigationMeshes, Persistent, Temporary] but NOT Landscape — the overlay
            // exposes the LIST children settably and the SINGULAR one read-only. So asking the runtime type
            // directly loses exactly the singular owned children (Cell.Landscape, Worldspace.TopCell — #335's
            // shape) while looking correct on the common ones, which is the worst way for it to be wrong.
            var getter = WriteEngine.PrimaryGetter(t);
            var concrete = getter is null ? null : WriteEngine.ConcreteOf(getter);
            var map = new Dictionary<string, OwnedChildShape>(StringComparer.Ordinal);
            if (concrete is null) return map;
            foreach (var p in WriteEngine.ChildBearingProperties(concrete))
                // The shape is a fact about the PROPERTY, not about any one body's value — so it is answered here,
                // off the type, and is available to a caller that has read no bodies at all.
                map[p.Name] = typeof(IMajorRecordGetter).IsAssignableFrom(p.PropertyType)
                    ? OwnedChildShape.Singular
                    : OwnedChildShape.Collection;
            return map;
        });

    static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, OwnedChildShape>> _byType = new();

    /// <summary>The shape of one field on <paramref name="body"/>'s type, or <see cref="OwnedChildShape.None"/>
    /// when the field owns no children.</summary>
    public static OwnedChildShape ShapeOf(IMajorRecordGetter body, string field) =>
        Fields(body).TryGetValue(field, out var s) ? s : OwnedChildShape.None;

    /// <summary>Does <paramref name="body"/>'s own <paramref name="field"/> DECLARE at least one child record?
    ///
    /// <para><b>Reaching a record, not holding an element.</b> The two are not the same question, and answering
    /// the easy one imports the bug this whole feature exists to kill. <c>Worldspace.SubCells</c> holds
    /// <c>WorldspaceBlock</c>s, whose cells sit two container levels down — so "has a first element" is true of a
    /// worldspace declaring nothing but empty block scaffolding. Measured on Mutagen 0.53.1: a worldspace written
    /// with 2 blocks / 2 sub-blocks / ZERO cells exposes 2 top-level elements and enumerates 0 child records,
    /// while one with 1 block / 1 sub-block / 3 cells exposes 1 element and enumerates 3. An element-level answer
    /// would call the empty scaffold a declarer and rank it above the real one.</para>
    ///
    /// <para>So the walk descends to the first actual record and stops there: a singular owned child is its own
    /// answer; a container defers to <see cref="IMajorRecordGetterEnumerable"/> — Mutagen's OWN containment
    /// enumeration, the same independent yardstick <c>WriteEngine.RestoreChildGroup</c> checks itself against —
    /// which is lazy, so the common case parses one child and returns. FormLinks are cut exactly as the
    /// type-level walk cuts them: a link REFERENCES a record, it does not own one.</para>
    ///
    /// <para>NULL means the field could not be READ — never false. "I could not look" is not evidence of "there
    /// is nothing there" (the #308 rule the read engine's own <see cref="ReadEngine.LeafRead.Unreadable"/> exists
    /// for), and a caller must not report an unreadable body as one that declares nothing.</para>
    ///
    /// <para><b>This reads a BODY, and reading a body is not free</b> — the resolver fetches one by enumerating a
    /// whole overlay, so a caller asking this of every plugin touching a record pays per plugin. Only the lane
    /// that has already fetched those bodies (the conflict tree) asks it; the default read answers the cheaper
    /// question the index alone can settle.</para></summary>
    public static bool? DeclaresChild(IMajorRecordGetter body, string field)
    {
        try
        {
            var p = WriteEngine.ResolveProperty(body.GetType(), field);
            return p is null ? null : ReachesRecord(p.GetValue(body), 0);
        }
        catch { return null; }
    }

    /// <summary>The deepest container nesting the value walk will follow before answering "I could not look".
    /// The type-level walk (<c>WriteEngine.ReachesOwnedRecord</c>) uses the same constant against TYPES, where
    /// the deepest real path is 5; against VALUES the walk is shallower still, because a container hands off to
    /// Mutagen's containment enumeration rather than being stepped through property by property. So this is a
    /// tripwire for a Mutagen shape nobody has seen, not a limit the current model approaches — and it answers
    /// NULL rather than false, because a bound hit is exactly "I could not look".</summary>
    const int MaxDepth = 6;

    static bool? ReachesRecord(object? val, int depth)
    {
        if (val is null) return false;                       // absent optional / empty — declares nothing
        if (depth > MaxDepth) return null;                   // nested deeper than any known shape — unknown, not "no"
        if (val is IFormLinkGetter) return false;            // a reference, not a child (the type walk's own cut)
        if (val is IMajorRecordGetter) return true;          // a singular owned child, present
        if (val is string) return false;
        // Mutagen's own containment walk, and it is LAZY: Any() stops at the first child record rather than
        // counting them, which is what keeps a worldspace from being enumerated to answer a yes/no question.
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
