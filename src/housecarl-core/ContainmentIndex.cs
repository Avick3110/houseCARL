using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The child → parent map: the second edge kind, beside the form link. A DIAL owns its INFOs, a CELL its placed
/// references and navmeshes, a WRLD its cells — group nesting, not a link, so <c>references=</c> cannot see it and
/// the child body names nothing (only a NavigationMesh does, at <c>Data.Parent</c>). This is what the
/// <c>*parent</c> path step reads.
///
/// <para><b>Captured at index build, from Mutagen's own containment walk.</b>
/// <see cref="LoadOrderResolver"/> already enumerates every plugin once; swapping that walk from the flat
/// <c>EnumerateMajorRecords</c> to <c>EnumerateMajorRecordContexts</c> yields the identical record set with the
/// containing context attached, for about 0.4 s more over a 3,800-plugin order. There is no per-record-type parent
/// map here and none is possible: the parent arrives from Mutagen, so a Mutagen bump that adds a child-bearing
/// container grows <c>*parent</c>'s reach with no edit — the first cornerstone applied to an edge kind.</para>
///
/// <para><b>Later plugin wins, like everything else.</b> Across a real order thousands of children end up under a
/// different parent than the first plugin put them under (a placed reference moved between cells), so the map is
/// merged per plugin in priority order and the last declaration stands — the same rule the winner index follows.
/// A plugin that throws part-way through its walk merges nothing, so a half-read plugin never leaves partial
/// containment behind.</para>
///
/// <para><b>Packed.</b> Entries are ulong → ulong ((interned mod index &lt;&lt; 32) | FormID), not FormKey → FormKey:
/// ~44 B an entry, about 77 MB for the 2.7M children of a large order, of which <c>Cell.Temporary</c> is four
/// fifths. Pure data — no bodies, no file handles.</para></summary>
public sealed class ContainmentIndex
{
    readonly Dictionary<ulong, ulong> _map = new();
    readonly Dictionary<ModKey, int> _modToIdx = new();
    readonly List<ModKey> _idxToMod = new();

    /// <summary>How far a group/block context is climbed to reach the nearest record ancestor. Mutagen hands a
    /// worldspace's cells up through block and sub-block contexts that carry no record of their own; the deepest
    /// real chain is two hops. A chain that holds no record inside the bound stages no edge, so <c>*parent</c>
    /// says it has none rather than guessing at one.</summary>
    const int MaxGroupHops = 6;

    /// <summary>Distinct children with a recorded parent.</summary>
    public int Count => _map.Count;

    /// <summary>The containing record of <paramref name="child"/>, or null when this build recorded none — the
    /// record is top-level, or its context chain held no record ancestor.</summary>
    public FormKey? ParentOf(FormKey child)
    {
        if (!_modToIdx.TryGetValue(child.ModKey, out int i)) return null;
        if (!_map.TryGetValue(((ulong)(uint)i << 32) | child.ID, out var packed)) return null;
        return new FormKey(_idxToMod[(int)(packed >> 32)], (uint)packed);
    }

    /// <summary>Stage one plugin's containment edges, read off the context walk. Returns the pairs for the caller to
    /// merge only if the whole plugin enumerated — the same plugin-atomic rule the winner index follows.</summary>
    internal static void Stage(IModContext context, List<(FormKey Child, FormKey Parent)> into)
    {
        if (context.Record is not IMajorRecordGetter child) return;
        var up = context.Parent;
        for (int hops = 0; up is not null && hops <= MaxGroupHops; hops++, up = up.Parent)
            if (up.Record is IMajorRecordGetter ancestor) { into.Add((child.FormKey, ancestor.FormKey)); return; }
    }

    /// <summary>Merge one fully-enumerated plugin's edges, later-wins.</summary>
    internal void Merge(IReadOnlyList<(FormKey Child, FormKey Parent)> edges)
    {
        foreach (var (child, parent) in edges) _map[Pack(child)] = Pack(parent);
    }

    ulong Pack(FormKey k)
    {
        if (!_modToIdx.TryGetValue(k.ModKey, out int i))
        {
            _modToIdx[k.ModKey] = i = _idxToMod.Count;
            _idxToMod.Add(k.ModKey);
        }
        return ((ulong)(uint)i << 32) | k.ID;
    }

    /// <summary>The child-bearing property surface, spelled <c>Type.Property</c> — what a <c>*parent</c> refusal
    /// names as the reason a record has no containing record. Derived from
    /// <see cref="WriteEngine.ChildBearingProperties"/> over every concrete record type Mutagen models, the same
    /// source the write surface's child preservation runs on, so it is never a hand list.</summary>
    public static string ChildBearingSurface() => _surface ??= string.Join(", ",
        typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                        && typeof(IMajorRecord).IsAssignableFrom(t))
            .SelectMany(t => WriteEngine.ChildBearingProperties(t).Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(s => s, StringComparer.Ordinal));
    static string? _surface;
}
