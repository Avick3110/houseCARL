using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The READ side's view of the fields that own child records — the same question
/// <see cref="WriteEngine.ChildBearingProperties"/> answers for the write surface, asked of a body the read
/// engine just fetched.
///
/// <para><b>Why the read side asks it at all (#342).</b> An owned child record is declared PER PLUGIN and the
/// game assembles a parent's children from every plugin that declares them — a cell override that touches the
/// record for an unrelated reason (occlusion data, lighting, music) carries no placed references and deletes
/// none either. Reading such a winner's <c>Temporary</c> therefore reports an empty list for a cell the game
/// fills with hundreds of references. Measured: <c>008EB5:Skyrim.esm</c> (Dawnstar exterior) reads
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
/// over — so <see cref="FieldNames"/> maps getter → concrete through the engine's own
/// <see cref="WriteEngine.PrimaryGetter"/> / <see cref="WriteEngine.ConcreteOf"/> pair rather than a second
/// name mapping of its own. That hop is load-bearing and its cost is measured, not assumed: asking the overlay
/// type directly answers correctly for the LIST children and silently drops the SINGULAR ones (see the comment
/// on the walk), which the guard's Landscape arm holds.</para>
/// </summary>
public static class OwnedChildContent
{
    /// <summary>The names of <paramref name="body"/>'s fields that own child records, memoized per runtime type
    /// (the <see cref="WriteEngine.ChildBearingProperties"/> precedent — reflection metadata, constant for the
    /// process lifetime, and this is asked on every record read). EMPTY for every record type but the three that
    /// own children, which is what makes the caller's walk free on the reads that don't touch this shape.</summary>
    public static IReadOnlyList<string> FieldNames(IMajorRecordGetter body) =>
        _byType.GetOrAdd(body.GetType(), static t =>
        {
            // ChildBearingProperties requires a SETTABLE property (it exists to lift children off a record and put
            // them back), and an overlay body is not the type it was written against. Measured on Mutagen 0.53.1:
            // CellBinaryOverlay answers [NavigationMeshes, Persistent, Temporary] but NOT Landscape — the overlay
            // exposes the LIST children settably and the SINGULAR one read-only. So asking the runtime type
            // directly loses exactly the singular owned children (Cell.Landscape, Worldspace.TopCell — #335's
            // shape) while looking correct on the common ones, which is the worst way for it to be wrong. The
            // getter is mapped to the concrete settable class first, through the engine's own mapper.
            var getter = WriteEngine.PrimaryGetter(t);
            var concrete = getter is null ? null : WriteEngine.ConcreteOf(getter);
            return concrete is null
                ? Array.Empty<string>()
                : WriteEngine.ChildBearingProperties(concrete).Select(p => p.Name).ToArray();
        });

    static readonly ConcurrentDictionary<Type, IReadOnlyList<string>> _byType = new();

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
    /// for), and a caller must not report an unreadable body as one that declares nothing.</para></summary>
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
