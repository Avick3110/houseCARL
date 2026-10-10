using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The child-to-parent map: the second edge kind, beside the form link, and what the <c>*parent</c> path step reads; contracts in docs/architecture/select-and-walk.md.</summary>
public sealed class ContainmentIndex
{
    /// <summary>The one spelling of the containment step, shared by every surface that takes a path.</summary>
    public const string ParentToken = "*parent";

    public static bool IsParentStep(string seg) => string.Equals(seg, ParentToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ONE grammar check for a <c>*parent</c> run, shared by every surface that takes a path; returns how many hops lead <paramref name="segs"/>, or the refusal unprefixed.</summary>
    /// <param name="isLinkLeft">The left side of a <c>-&gt;</c> step, whose all-hops case wants a link, not a value.</param>
    /// <param name="allowBare">A walk's seed path or follow, where a path of nothing but hops IS the edge crossed.</param>
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

    public int Count => _map.Count;

    /// <summary>The containing record of <paramref name="child"/>, or null when this build recorded none.</summary>
    public FormKey? ParentOf(FormKey child)
    {
        if (!_modToIdx.TryGetValue(child.ModKey, out int i)) return null;
        if (!_map.TryGetValue(((ulong)(uint)i << 32) | child.ID, out var packed)) return null;
        return new FormKey(_idxToMod[(int)(packed >> 32)], (uint)packed);
    }

    /// <summary>Stage one plugin's containment edges off the context walk, returned only if the whole plugin enumerated; the climb to the nearest record ancestor is unbounded.</summary>
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

    /// <summary>The <c>*parent</c> hop a field read takes: this build's containment map, then the containing record's winner body through the caller's own session.</summary>
    /// <param name="held">Containing records the caller already gathered in bulk; a miss there is fetched one at a time.</param>
    public static Func<IMajorRecordGetter, (IMajorRecordGetter? Parent, string? Why)> ReadHop(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        Func<FormKey, IMajorRecordGetter?>? held = null) => child =>
    {
        var pk = view.ParentOf(child.FormKey);
        if (pk is null)
            return (null, $"no record contains this {RecordNaming.StripOverlay(child.GetType().Name)} — containment runs " +
                          $"from these properties only: {ChildBearingSurface()}");
        var winner = view.ResolveWinner(pk.Value);
        if (winner is null)
            return (null, $"the containing record {FormIdToken.Of(pk.Value)} is not in the active load order");
        var body = held?.Invoke(pk.Value) ?? FetchContainer(view, session, winner.Value.WinnerPlugin, pk.Value, child.GetType());
        return body is null
            ? (null, $"the containing record {FormIdToken.Of(pk.Value)} would not fetch from its winner '{winner.Value.WinnerPlugin}'")
            : (body, null);
    };

    /// <summary>One containing record's body, sought only in the groups a record of <paramref name="childType"/> can sit under (#1147).</summary>
    static IMajorRecordGetter? FetchContainer(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                              string plugin, FormKey parent, Type childType)
        => view.GetRecord(session, plugin, parent, ContainerGetter(childType));

    /// <summary>The one getter type a <paramref name="childType"/> record's containing record can have; null when it can be more than one, or none.</summary>
    public static Type? ContainerGetter(Type childType) => ContainerGetters(childType, 1) is { Count: 1 } t ? t[0] : null;

    /// <summary>The winner bodies of the containing records <paramref name="parents"/> names, each paired with the
    /// type of the child it was climbed from and how many hops up: one walk per winner plugin, in the container
    /// types' groups only. A record whose winner would not open is absent, its plugin named in <paramref name="unreadable"/>.</summary>
    public static Dictionary<FormKey, IMajorRecordGetter> GatherContainers(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IEnumerable<(FormKey Parent, Type ChildType, int Hops)> parents,
        out Dictionary<string, PluginUnreadableException> unreadable, CancellationToken ct = default)
    {
        var keys = new HashSet<FormKey>();
        var types = new List<Type>();
        foreach (var (pk, childType, hops) in parents)
        {
            keys.Add(pk);
            foreach (var t in ContainerGetters(childType, hops))
                if (!types.Contains(t)) types.Add(t);
        }
        return WinnerBodies.For(view, session, keys, types.Count > 0 ? types : null, out unreadable, ct);
    }

    /// <summary>The getter types of the records that can contain a <paramref name="child"/> record
    /// <paramref name="hops"/> steps up, read off <see cref="WriteEngine.ChildBearingProperties"/>; empty when none can.</summary>
    public static IReadOnlyList<Type> ContainerGetters(Type child, int hops) =>
        _containerGetters.GetOrAdd((child, hops), static k =>
        {
            var concrete = WriteEngine.PrimaryGetter(k.Child) is { } g ? WriteEngine.ConcreteOf(g) : null;
            IEnumerable<Type> at = concrete is null ? Array.Empty<Type>() : new[] { concrete };
            for (int i = 0; i < k.Hops; i++) at = at.SelectMany(ContainersOf).Distinct().ToList();
            return at.Select(WriteEngine.PrimaryGetter).OfType<Type>().Distinct().ToList();
        });
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Child, int Hops), IReadOnlyList<Type>> _containerGetters = new();

    /// <summary>The concrete record classes with a child-bearing property that reaches <paramref name="concreteChild"/>.</summary>
    static IEnumerable<Type> ContainersOf(Type concreteChild) =>
        ConcreteRecordClasses().Where(t => WriteEngine.ChildBearingProperties(t)
            .Any(p => WriteEngine.ReachesRecordType(p.PropertyType, concreteChild, new HashSet<Type>(), 0)));

    /// <summary>Every concrete record class Mutagen models for Skyrim.</summary>
    static IReadOnlyList<Type> ConcreteRecordClasses() => _concrete ??=
        typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                        && typeof(IMajorRecord).IsAssignableFrom(t))
            .ToList();
    static IReadOnlyList<Type>? _concrete;

    /// <summary>The child-bearing property surface, spelled <c>Type.Property</c>, derived from <see cref="WriteEngine.ChildBearingProperties"/> over every concrete record type Mutagen models.</summary>
    public static string ChildBearingSurface() => _surface ??= string.Join(", ",
        ConcreteRecordClasses()
            .SelectMany(t => WriteEngine.ChildBearingProperties(t).Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(s => s, StringComparer.Ordinal));
    static string? _surface;
}
