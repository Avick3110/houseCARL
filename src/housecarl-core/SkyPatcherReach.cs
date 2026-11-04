using System.Runtime.CompilerServices;

namespace HousecarlCore;

/// <summary>One patch line classified against one record type: its filters and operations, whether a key is unknown, and its reach shape.</summary>
public sealed record SkyPatcherClassifiedLine(
    SkyPatcherRecordCatalog RecordCatalog,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Filters,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Ops,
    bool UnknownKey,
    SkyPatcherLineShape Shape)
{
    static readonly ConditionalWeakTable<SkyPatcherLine, SkyPatcherClassifiedLine> Cache = new();

    /// <summary>Classify every key of <paramref name="line"/> once per record type; the answer is kept beside the parsed line, which the parse cache already shares across calls.</summary>
    public static SkyPatcherClassifiedLine Of(SkyPatcherLine line, SkyPatcherCatalog catalog, SkyPatcherRecordCatalog recordCatalog)
    {
        if (Cache.TryGetValue(line, out var hit) && ReferenceEquals(hit.RecordCatalog, recordCatalog)) return hit;
        var filters = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        var ops = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        bool unknown = false;
        foreach (var seg in line.Segments)
        {
            var cls = catalog.Classify(recordCatalog, seg.Key);
            switch (cls.Role)
            {
                case SkyPatcherKeyRole.Filter: filters.Add((seg, cls)); break;
                case SkyPatcherKeyRole.Operation: ops.Add((seg, cls)); break;
                default: unknown = true; break;
            }
        }
        var made = new SkyPatcherClassifiedLine(recordCatalog, filters, ops, unknown, SkyPatcherLineShape.Of(filters, recordCatalog));
        Cache.AddOrUpdate(line, made);
        return made;
    }
}

/// <summary>The reach of one classified SkyPatcher line, read off the catalog's filter roles and the patcher's differences; every consumer that asks which records a line can touch reads this shape. Contract in docs/architecture/skypatcher-layer.md.</summary>
public sealed record SkyPatcherLineShape(
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Gates,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Direct,
    IReadOnlyList<IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)>> FinderGroups,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Narrowers,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> DirectNarrowers,
    IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> Ignored,
    bool FilterlessPatchesNothing)
{
    /// <summary>The line finds records beyond its direct list.</summary>
    public bool HasFinderReach => FinderGroups.Count > 0;

    /// <summary>No direct list and no finder: every record passes, unless the patcher patches nothing then.</summary>
    public bool Filterless => Direct.Count == 0 && FinderGroups.Count == 0;

    /// <summary>A filterless line in a patcher that then patches every record.</summary>
    public bool PatchesEveryRecord => Filterless && !FilterlessPatchesNothing;

    /// <summary>The line can touch only the records its one direct list names: no gate, finder, narrower or ignored key beside it.</summary>
    public bool OnlyDirect => Direct.Count == 1 && Gates.Count == 0 && FinderGroups.Count == 0 && Narrowers.Count == 0 && Ignored.Count == 0;

    /// <summary>Whether something can stop the direct list: a gate, or a narrower the patcher applies to it.</summary>
    public bool DirectConditional => Gates.Count > 0 || DirectNarrowers.Count > 0;

    /// <summary>Sort a line's classified filters into their roles: one finder group per filter family (its AND and Or keys together), narrowers apart, the patcher's direct-list narrowers and unread keys named.</summary>
    public static SkyPatcherLineShape Of(IReadOnlyList<(SkyPatcherSegment seg, SkyPatcherKeyClass cls)> filters, SkyPatcherRecordCatalog recordCatalog)
    {
        var gates = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        var direct = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        var groups = new List<List<(SkyPatcherSegment, SkyPatcherKeyClass)>>();
        var groupOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var narrowers = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        var directNarrowers = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        var ignored = new List<(SkyPatcherSegment, SkyPatcherKeyClass)>();
        foreach (var f in filters)
        {
            var cls = f.cls;
            if (recordCatalog.Ignores.Contains(cls.Filter!.Name, StringComparer.OrdinalIgnoreCase)) { ignored.Add(f); continue; }
            switch (cls.FilterRole)
            {
                case SkyPatcherFilterRole.Gate: gates.Add(f); break;
                case SkyPatcherFilterRole.Direct: direct.Add(f); break;
                case SkyPatcherFilterRole.Narrower:
                    narrowers.Add(f);
                    if (recordCatalog.DirectNarrowedBy.Contains(cls.CatalogKey, StringComparer.OrdinalIgnoreCase)) directNarrowers.Add(f);
                    break;
                default:
                    if (!groupOf.TryGetValue(cls.Filter.Name, out var g)) { groupOf[cls.Filter.Name] = g = groups.Count; groups.Add(new()); }
                    groups[g].Add(f);
                    break;
            }
        }
        return new SkyPatcherLineShape(gates, direct, groups, narrowers, directNarrowers, ignored, recordCatalog.FilterlessPatchesNothing);
    }
}
