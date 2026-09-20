using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ClosureWalk — the generic link walk; expansion is Mutagen's own EnumerateFormLinks and everything domain-specific arrives as data. Contracts in docs/architecture/select-and-walk.md.

/// <summary>How hard an exclusion bites when the walk reaches a matching record.</summary>
public enum ExclusionSeverity
{
    /// <summary>Prune: do not expand it, record it as a boundary. The walk continues.</summary>
    Stop,
    /// <summary>The whole walk fails loud, for subtrees that are not internalizable at all.</summary>
    Refuse,
}

/// <summary>One exclusion, matched on the record's TYPE name — serializable data a skill can hand over a wire.</summary>
public sealed record WalkExclusion(string TypeName, ExclusionSeverity Severity, string Reason);

/// <summary>The expand-vs-keep rule: a reached record either EXPANDS or is KEPT as a boundary link.</summary>
public sealed record WalkScope(Func<FormKey, bool> ShouldExpand)
{
    /// <summary>The standalone-ization predicate: expand a record iff it is defined in a plugin being moved away from, or it does not resolve in the active order.</summary>
    public static WalkScope StandaloneFrom(IReadOnlySet<ModKey> boundPlugins, Func<FormKey, bool> resolvesActively)
        => new(fk => boundPlugins.Contains(fk.ModKey) || !resolvesActively(fk));
}

/// <summary>One record the walk reached and will expand, with its per-node provenance arm and the full pull chain from a seed.</summary>
public sealed record WalkNode(
    FormKey Key, IMajorRecordGetter Body, string TypeName, string? EditorId,
    int ArmIndex, string ArmSpelling,
    IReadOnlyList<FormKey> Chain, string PulledBy, int Depth);

/// <summary>A link the walk deliberately did NOT enter; <paramref name="Excluded"/> tells a scope boundary from an exclusion boundary.</summary>
public sealed record WalkBoundary(FormKey Key, string PulledBy, string Why, bool Excluded = false);

/// <summary>A genuine cycle: <paramref name="Path"/> is the loop itself and <paramref name="Back"/> its first key, so the path both starts at it and returns to it.</summary>
public sealed record WalkCycle(IReadOnlyList<FormKey> Path, FormKey Back, string PulledBy);

/// <summary>Why a walk refused. Typed, so the render owns the words.</summary>
public enum WalkRefusalKind
{
    /// <summary>The seed set resolved to nothing — a walk that would copy nothing at all.</summary>
    NoSeeds,
    /// <summary>A seed path the caller named does not exist on the seed record.</summary>
    UnknownSeedPath,
    /// <summary>More records than the node cap. Carries the last pull and its chain.</summary>
    NodeCap,
    /// <summary>Deeper than the depth cap. Carries the same.</summary>
    DepthCap,
    /// <summary>No source in the universe could produce a record the walk must expand.</summary>
    SourceMiss,
    /// <summary>A source HAS the record but could not read it (SourceChain's fault — never a silent fallthrough).</summary>
    SourceFault,
    /// <summary>A <see cref="ExclusionSeverity.Refuse"/> exclusion matched.</summary>
    Excluded,
    /// <summary>A seed path names a real, link-BEARING field whose shape seed_paths does not support — the path is right and the lane is wrong.</summary>
    UnsupportedSeedShape,
}

/// <summary>A refusal as DATA. Every field a render might need is here; which ones matter depends on the kind.</summary>
public sealed record WalkRefusal(
    WalkRefusalKind Kind,
    FormKey Key,
    string PulledBy,
    IReadOnlyList<FormKey> Chain,
    string Detail,
    SourceMiss? Miss = null,
    SourceFault? Fault = null,
    WalkExclusion? Exclusion = null,
    int Cap = 0);

/// <summary>The walk's outcome; a refusal carries nothing usable rather than a silently partial copy.</summary>
public sealed record WalkResult(
    bool Success,
    WalkRefusal? Refusal,
    IReadOnlyList<WalkNode> Reached,
    IReadOnlyList<WalkBoundary> Kept,
    IReadOnlyList<WalkCycle> Cycles)
{
    public static WalkResult Fail(WalkRefusal r) => new(false, r,
        Array.Empty<WalkNode>(), Array.Empty<WalkBoundary>(), Array.Empty<WalkCycle>());
}

/// <summary>One seed link, the field path it came off, and the provenance label "&lt;SeedRecordType&gt;.&lt;Path&gt;" built where the seed record is in hand.</summary>
public sealed record WalkSeed(FormKey Key, string Path, string Label);

/// <summary>What shape a seed path is, decided ONCE and consumed everywhere.</summary>
public enum SeedShapeKind
{
    Link,
    LinkList,
    Unsupported,
}

/// <summary>One seed path's shape verdict; <paramref name="Reason"/> is set only for <see cref="SeedShapeKind.Unsupported"/>.</summary>
public sealed record SeedShape(SeedShapeKind Kind, Type? ElementType = null, string? Reason = null);

public static class ClosureWalk
{
    /// <summary>THE seed-shape judgement, consumed by every site that needs it; it reads the DECLARED type, and null and empty are states of a shape, not shapes.</summary>
    public static SeedShape ClassifySeed(PropertyInfo prop)
    {
        var t = prop.PropertyType;
        if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return new SeedShape(SeedShapeKind.Link);

        if (t != typeof(string))
        {
            var element = t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0])
                .FirstOrDefault();
            if (element is not null)
            {
                if (typeof(IFormLinkGetter).IsAssignableFrom(element))
                    return new SeedShape(SeedShapeKind.LinkList, element);
                return new SeedShape(SeedShapeKind.Unsupported, element,
                    $"'{prop.Name}' is a list of {RecordNaming.StripOverlay(element.Name)} entries, which are not record links");
            }
        }

        return new SeedShape(SeedShapeKind.Unsupported, null,
            $"'{prop.Name}' is {RecordNaming.StripOverlay(t.Name)}, which is neither a record link nor a list of record links");
    }

    /// <summary>Default node cap; a walk past it refuses with the chain rather than truncating, and the cap is named in the refusal.</summary>
    public const int DefaultNodeCap = 128;

    /// <summary>Default depth cap, bounded and named for the same reason.</summary>
    public const int DefaultDepthCap = 32;

    /// <summary>Resolve the caller's seed PATHS against a seed record into seed links, by reflection over the record model; an unknown path is a refusal, not zero links.</summary>
    public static WalkResult? ResolveSeeds(
        IMajorRecordGetter seed, IReadOnlyList<string> paths, out List<WalkSeed> seeds)
    {
        seeds = new List<WalkSeed>();
        var type = seed.GetType();
        var seedType = RecordNaming.StripOverlay(type.Name);
        foreach (var path in paths)
        {
            var prop = type.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || prop.GetIndexParameters().Length != 0)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.UnknownSeedPath, seed.FormKey, "", Array.Empty<FormKey>(),
                    $"'{path}' is not a field on {RecordNaming.StripOverlay(type.Name)}"));

            // The shape is decided FIRST, off the declared type, so a null cannot be mistaken for an absence of shape.
            var shape = ClassifySeed(prop);
            if (shape.Kind == SeedShapeKind.Unsupported)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.UnsupportedSeedShape, seed.FormKey, "", Array.Empty<FormKey>(),
                    shape.Reason + $" on {seedType}"));

            object? val;
            try { val = prop.GetValue(seed); }
            catch (Exception ex)
            {
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.UnknownSeedPath, seed.FormKey, "", Array.Empty<FormKey>(),
                    $"'{path}' could not be read on {RecordNaming.StripOverlay(type.Name)}: {ex.Message}"));
            }
            if (val is null) continue;

            if (shape.Kind == SeedShapeKind.Link)
            {
                if (val is IFormLinkGetter single && single.FormKeyNullable is { } fk && !fk.IsNull)
                    seeds.Add(new WalkSeed(fk, path, $"{seedType}.{path}"));
                continue;
            }
            if (val is IEnumerable list and not string)
            {
                // A list is supported only when its ELEMENTS are links; a list of link-bearing elements is refused by shape.
                foreach (var e in list)
                    if (e is IFormLinkGetter l && l.FormKeyNullable is { } lk && !lk.IsNull)
                        seeds.Add(new WalkSeed(lk, path, $"{seedType}.{path}"));
                continue;
            }
            // The shape said LinkList and the value read as neither null nor enumerable — surfaced rather than guessed.
            return WalkResult.Fail(new WalkRefusal(
                WalkRefusalKind.UnsupportedSeedShape, seed.FormKey, "", Array.Empty<FormKey>(),
                $"'{path}' on {seedType} is declared a list of record links but did not read as one"));
        }
        return null;
    }

    /// <summary>Walk forward from the seeds against the ordered source universe, returning the reached set, the boundary links kept, and any genuine cycles — or a typed refusal.</summary>
    public static WalkResult Run(
        IReadOnlyList<WalkSeed> seeds,
        SourceChain sources,
        WalkScope scope,
        IReadOnlyList<WalkExclusion> exclusions,
        int nodeCap = DefaultNodeCap,
        int depthCap = DefaultDepthCap)
    {
        if (seeds.Count == 0)
            return WalkResult.Fail(new WalkRefusal(
                WalkRefusalKind.NoSeeds, default, "", Array.Empty<FormKey>(),
                "the seed fields carry no record links"));

        var reached = new List<WalkNode>();
        var kept = new List<WalkBoundary>();
        var seen = new HashSet<FormKey>();
        // The walked graph as EDGES plus a label per node; cycles are found from this after the walk, not during it.
        var edges = new Dictionary<FormKey, List<FormKey>>();
        var labels = new Dictionary<FormKey, string>();
        // parent[k] = the key that pulled k in; the pull chain is rebuilt from this, so a refusal shows the whole path.
        var parent = new Dictionary<FormKey, FormKey>();
        var excl = exclusions.ToDictionary(e => e.TypeName, e => e, StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<(FormKey Key, string PulledBy, int Depth)>();
        foreach (var s in seeds) queue.Enqueue((s.Key, s.Label, 0));

        List<FormKey> ChainTo(FormKey k)
        {
            var chain = new List<FormKey>();
            var cur = k;
            var guard = new HashSet<FormKey>();
            while (guard.Add(cur))
            {
                chain.Add(cur);
                if (!parent.TryGetValue(cur, out var p)) break;
                cur = p;
            }
            chain.Reverse();
            return chain;
        }

        while (queue.Count > 0)
        {
            var (key, pulledBy, depth) = queue.Dequeue();
            if (key.IsNull || !seen.Add(key)) continue;

            if (!scope.ShouldExpand(key))
            {
                kept.Add(new WalkBoundary(key, pulledBy, "resolves outside the scope predicate — kept as a link"));
                continue;
            }

            if (depth > depthCap)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.DepthCap, key, pulledBy, ChainTo(key),
                    $"the walk went deeper than {depthCap} hops", Cap: depthCap));

            if (reached.Count >= nodeCap)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.NodeCap, key, pulledBy, ChainTo(key),
                    $"the walk reached more than {nodeCap} records", Cap: nodeCap));

            var fetched = sources.Fetch(key, pulledBy);
            if (fetched.Fault is { } fault)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.SourceFault, key, pulledBy, ChainTo(key),
                    fault.Cause, Fault: fault));
            if (fetched.Hit is not { } hit)
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.SourceMiss, key, pulledBy, ChainTo(key),
                    "no source in the universe produced it", Miss: sources.Miss(key, pulledBy)));

            var typeName = RecordNaming.StripOverlay(hit.Body.GetType().Name);
            if (excl.TryGetValue(typeName, out var rule))
            {
                if (rule.Severity == ExclusionSeverity.Refuse)
                    return WalkResult.Fail(new WalkRefusal(
                        WalkRefusalKind.Excluded, key, pulledBy, ChainTo(key), rule.Reason, Exclusion: rule));
                kept.Add(new WalkBoundary(key, pulledBy, $"excluded ({typeName}): {rule.Reason}", Excluded: true));
                continue;
            }

            reached.Add(new WalkNode(
                key, hit.Body, typeName, hit.Body.EditorID,
                hit.ArmIndex, hit.Arm.Spelling,
                ChainTo(key), pulledBy, depth));

            var label = $"{typeName} {FormIdToken.Of(key)} ({hit.Body.EditorID ?? "<no editorid>"})";
            labels[key] = label;
            var outgoing = new List<FormKey>();
            edges[key] = outgoing;
            if (hit.Body is not IFormLinkContainerGetter flc) continue;
            foreach (var link in flc.EnumerateFormLinks())
            {
                var target = link.FormKey;
                if (target.IsNull) continue;
                outgoing.Add(target);
                if (seen.Contains(target)) continue;
                if (!parent.ContainsKey(target)) parent[target] = key;
                queue.Enqueue((target, label, depth + 1));
            }
        }

        return new WalkResult(true, null, reached, kept, FindCycles(edges, labels));
    }

    /// <summary>Every cycle in the walked graph, found from the recorded edges once the walk is done; the finding itself is <see cref="GraphCycles"/>.</summary>
    static List<WalkCycle> FindCycles(
        Dictionary<FormKey, List<FormKey>> edges, Dictionary<FormKey, string> labels)
        // Uncapped on purpose: the copy walk refuses at its own node cap rather than truncating.
        => GraphCycles.Find(edges, int.MaxValue, out _)
                      .Select(path => new WalkCycle(
                          path, path[0],
                          labels.TryGetValue(path[^1], out var lb) ? lb : FormIdToken.Of(path[^1])))
                      .ToList();
}
