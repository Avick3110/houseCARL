using System.Collections.Concurrent;
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

    /// <summary>How much child content <paramref name="body"/>'s own <paramref name="field"/> DECLARES: the
    /// element count for a list (0 = declared nothing), 1/0 for a singular owned child (Cell.Landscape,
    /// Worldspace.TopCell — present or absent, the singular analogue of a list's count).
    ///
    /// <para>NULL means the field could not be READ on this body — never 0. "I could not look" is not evidence of
    /// "there is nothing there" (the #308 rule the read engine's own
    /// <see cref="ReadEngine.LeafRead.Unreadable"/> exists for), and a caller comparing counts must not treat an
    /// unreadable body as one that declares nothing.</para></summary>
    public static int? DeclaredCount(IMajorRecordGetter body, string field)
    {
        try
        {
            var p = WriteEngine.ResolveProperty(body.GetType(), field);
            if (p is null) return null;
            return p.GetValue(body) switch
            {
                null => 0,                                                        // absent optional — declares nothing
                IMajorRecordGetter => 1,                                          // a singular owned child, present
                System.Collections.IEnumerable e => e.Cast<object?>().Count(),    // a list/dict of them
                _ => null,                                                        // some other shape — don't guess (Q3)
            };
        }
        catch { return null; }
    }
}
