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
/// containing context attached, for about 0.3 s more over a 3,800-plugin order — 0.31 s to 0.43 s across repeated
/// alternating runs on the ARR 2.0 order (3,801 plugins, 3.69M records), which is that machine's own run-to-run
/// spread; the changelog quotes the low end. There is no per-record-type parent
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
/// ~30 B an entry, about 77 MB measured for the 2,711,713 children of a large order, of which
/// <c>Cell.Temporary</c> is four fifths. Pure data — no bodies, no file handles. The count is reported in band on
/// <c>housecarl_load_order_status</c>, so the cost is declared rather than discovered.</para></summary>
public sealed class ContainmentIndex
{
    /// <summary>The one spelling of the containment step, shared by every surface that takes a path — a
    /// predicate, a field projection, a walk's seed paths and follow. The <c>*</c> sigil is §5.5's rule: the
    /// co-resident space is field names, and <c>Worldspace.Parent</c> is a real field, so a bare <c>parent</c> is
    /// takeable.</summary>
    public const string ParentToken = "*parent";

    /// <summary>True when this path segment IS the containment step.</summary>
    public static bool IsParentStep(string seg) => string.Equals(seg, ParentToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ONE grammar check for a <c>*parent</c> run, shared by every surface that takes a path — the
    /// <c>where=</c> predicate and the <c>project.fields</c> read walk both call this, so an identical mistake
    /// gets an identical sentence instead of a typo hint on one surface and a named refusal on the other.
    /// Returns how many hops lead <paramref name="segs"/>, or the refusal (unprefixed, so each surface wraps it
    /// in its own voice). A hop leads a path by definition — the containing record is a property of the RECORD,
    /// not of a field value — so a <c>*parent</c> anywhere else refuses by name, as does one carrying a
    /// quantifier, one with nothing after it, and any other <c>*</c> token.</summary>
    /// <param name="display">How the caller spelled this side, for the message.</param>
    /// <param name="isLinkLeft">The left side of a <c>-&gt;</c> step, whose all-hops case wants a link, not a value.</param>
    /// <param name="allowBare">A walk's seed path or follow, where a path of nothing but hops IS the edge crossed —
    /// the containing record is the walk's next node, not a value to read.</param>
    public static (int Hops, string? Error) SplitHops(string[] segs, string display, bool isLinkLeft = false, bool allowBare = false)
    {
        int hops = 0;
        while (hops < segs.Length && IsParentStep(segs[hops])) hops++;
        for (int i = hops; i < segs.Length; i++)
        {
            var s = segs[i];
            if (IsParentStep(s))
                return (0, $"'{ParentToken}' is the record that CONTAINS this one, so it can only lead a path — " +
                           $"in '{display}' it follows a field step. Write the hops first ('{ParentToken}.EditorID').");
            int open = s.IndexOf('[');
            if (open > 0 && s.EndsWith("]", StringComparison.Ordinal)
                && string.Equals(s[..open], ParentToken, StringComparison.OrdinalIgnoreCase))
                return (0, $"'{s}' — '{ParentToken}' names ONE containing record, not a list, so it takes no quantifier. Write '{ParentToken}'.");
            if (s.Length > 0 && s[0] == '*' && open != 0)
                return (0, $"'{s}' is not a path token — the tokens are '{ParentToken}' (the containing record) and the quantifiers [*any], [*all], [*none] and [*count] on a list step.");
        }
        if (hops == segs.Length && hops > 0 && !allowBare)
            return (0, isLinkLeft
                ? $"'{display}' is the containing record, which is not a link-bearing field — name one on it ('{display}.Quest->editorid')."
                : $"'{display}' names the containing record, not a value — follow it with a field ('{display}.EditorID').");
        return (hops, null);
    }

    readonly Dictionary<ulong, ulong> _map = new();
    readonly Dictionary<ModKey, int> _modToIdx = new();
    readonly List<ModKey> _idxToMod = new();

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
    /// merge only if the whole plugin enumerated — the same plugin-atomic rule the winner index follows.
    ///
    /// <para>The climb to the nearest record ancestor is UNBOUNDED, deliberately. Mutagen hands a worldspace's
    /// cells up through block and sub-block contexts that carry no record of their own, and the deepest chain
    /// today is two such hops — but a cap here would turn a chain one level deeper than the cap into a staged
    /// nothing, which <c>ParentOf</c> then reports as "no record contains this", a false statement rather than a
    /// missing one. The chain is finite and Mutagen-owned (each context's Parent terminates at the mod), so
    /// walking it to the end makes "no containing record" true by construction and lets a Mutagen bump that nests
    /// one level deeper keep working with no edit here.</para></summary>
    internal static void Stage(IModContext context, List<(FormKey Child, FormKey Parent)> into)
    {
        if (context.Record is not IMajorRecordGetter child) return;
        for (var up = context.Parent; up is not null; up = up.Parent)
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

    /// <summary>The <c>*parent</c> hop a field read takes: this build's containment map, then the containing
    /// record's winner body through the caller's own session, so a read that already holds one plugin open pays
    /// nothing extra. A record nothing contains comes back with the plain sentence saying so and naming the
    /// properties containment runs from — never a null the render has to guess at.</summary>
    public static Func<IMajorRecordGetter, (IMajorRecordGetter? Parent, string? Why)> ReadHop(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session) => child =>
    {
        var pk = view.ParentOf(child.FormKey);
        if (pk is null)
            return (null, $"no record contains this {RecordNaming.StripOverlay(child.GetType().Name)} — containment runs " +
                          $"from these properties only: {ChildBearingSurface()}");
        var winner = view.ResolveWinner(pk.Value);
        if (winner is null)
            return (null, $"the containing record {pk.Value} is not in the active load order");
        var body = view.GetRecord(session, winner.Value.WinnerPlugin, pk.Value);
        return body is null
            ? (null, $"the containing record {pk.Value} would not fetch from its winner '{winner.Value.WinnerPlugin}'")
            : (body, null);
    };

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
