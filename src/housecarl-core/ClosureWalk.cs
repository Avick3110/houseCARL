using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ClosureWalk — the generic link walk.
//
// Expansion is Mutagen's own EnumerateFormLinks, so the links followed are the ones the record
// model declares, never a per-type hand list. Everything domain-specific arrives as DATA: which
// fields seed the walk, which record classes are excluded and how hard, which plugins the walk is
// standalone-izing away from. No record-type vocabulary belongs in this file.
//
// Every refusal is a typed WalkRefusal carrying its data (the key, the chain that reached it, the
// caps, the arms consulted); the render owns the prose.
//
// Provenance is PER NODE: each reached node records which source arm produced its body and the
// full chain of keys that pulled it in. A per-walk "these sources were consulted" summary would be
// a weaker claim — with an ordered universe the fact worth having is that THIS record came from
// the override and THAT one fell through to the defining plugin.
//
// Cycles are recorded, not merely skipped. A visited-set walk cannot tell a diamond (two paths to
// one record) from a genuine cycle (a record that reaches itself). They are told apart by a
// POST-WALK pass over the recorded edge set: a depth-first colouring where an edge into an
// on-stack node is a back edge. It runs after the walk because the traversal builds a TREE, and
// "is an ancestor in that tree" is a weaker question than "reaches itself in the graph" — a mutual
// reference between two SIBLINGS is a real cycle no ancestry test can see. Any other repeat is a
// re-convergence and is deduped.

/// <summary>How hard an exclusion bites when the walk reaches a matching record.</summary>
public enum ExclusionSeverity
{
    /// <summary>Prune: do not expand it, record it as a boundary. The walk continues.</summary>
    Stop,
    /// <summary>The whole walk fails loud. For subtrees that are not internalizable at all — the copy
    /// consumer's Race entry is the standing example, and it is DATA the caller supplies, not a rule here.</summary>
    Refuse,
}

/// <summary>One exclusion, matched on the record's TYPE name (the caller's own vocabulary, e.g. "Race").
/// Type-keyed rather than predicate-keyed so the whole exclusion set is serializable data a skill can hand over a
/// wire, which is the point of "domain knowledge as data".</summary>
public sealed record WalkExclusion(string TypeName, ExclusionSeverity Severity, string Reason);

/// <summary>The expand-vs-keep rule. A reached record either EXPANDS (the walk enters it and it joins the reached
/// set) or is KEPT as a boundary link (recorded, not entered).</summary>
public sealed record WalkScope(Func<FormKey, bool> ShouldExpand)
{
    /// <summary>The standalone-ization predicate: expand a record iff it is defined in one of the plugins being
    /// moved away from, OR it does not resolve in the active order (it would become a missing master). Everything
    /// else — vanilla, active shared resources — stays a link the artifact masters normally.
    /// <para>Named here rather than inlined because it is the rule the write consumer is built on; a walk used for
    /// reading wants a different one.</para></summary>
    public static WalkScope StandaloneFrom(IReadOnlySet<ModKey> boundPlugins, Func<FormKey, bool> resolvesActively)
        => new(fk => boundPlugins.Contains(fk.ModKey) || !resolvesActively(fk));
}

/// <summary>One record the walk reached and will expand, with everything the report and the write consumer need.
/// <para><paramref name="ArmIndex"/>/<paramref name="ArmSpelling"/> are the per-node provenance: which element of
/// the ordered source universe actually produced THIS body. <paramref name="Chain"/> is the full pull path from a
/// seed, keys first-to-last — not a single parent label, because the cap refusal has to show how the walk got
/// somewhere, and one hop cannot show that.</para></summary>
public sealed record WalkNode(
    FormKey Key, IMajorRecordGetter Body, string TypeName, string? EditorId,
    int ArmIndex, string ArmSpelling,
    IReadOnlyList<FormKey> Chain, string PulledBy, int Depth);

/// <summary>A link the walk deliberately did NOT enter: it resolves outside the scope predicate, or an exclusion
/// pruned it. Recorded so "kept" is a stated outcome rather than an absence.
/// <para><paramref name="Excluded"/> tells the two APART, and it is not cosmetic. A scope boundary resolves OUTSIDE
/// the source universe and masters normally; an exclusion boundary is INSIDE it and still points at it. Collapsing
/// them lets one response call a link "kept and mastered normally" while the strip list shows it removed.</para></summary>
public sealed record WalkBoundary(FormKey Key, string PulledBy, string Why, bool Excluded = false);

/// <summary>A genuine cycle: <paramref name="Path"/> is the loop itself — the records from the one pointed back at
/// through to the one holding the closing link — and <paramref name="Back"/> is that first key, so the path both
/// starts at it and returns to it. <paramref name="PulledBy"/> labels the record whose link closed the loop.</summary>
public sealed record WalkCycle(IReadOnlyList<FormKey> Path, FormKey Back, string PulledBy);

/// <summary>Why a walk refused. Typed, so the render owns the words.</summary>
public enum WalkRefusalKind
{
    /// <summary>The seed set resolved to nothing — a walk that would copy nothing at all. Succeeding silently here
    /// is how a write consumer blanks its target.</summary>
    NoSeeds,
    /// <summary>A seed path the caller named does not exist on the seed record — a typo in skill data must fail
    /// loud, never contribute zero links quietly.</summary>
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
    /// <summary>A seed path names a real, link-BEARING field whose shape seed_paths does not support — a list whose
    /// ELEMENTS carry links inside them rather than being links. Separate from <see cref="UnknownSeedPath"/> because
    /// the remedy is different: the path is right and the LANE is wrong.</summary>
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

/// <summary>The walk's outcome. A refusal carries NOTHING usable: a write that breaches a bound refuses loud rather
/// than truncating, because a silently partial copy is a broken artifact.</summary>
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

/// <summary>One seed link, the field path it came off, and the ready-made provenance label for it — the format is
/// "&lt;SeedRecordType&gt;.&lt;Path&gt;". The label is built where the seed RECORD is in hand
/// (<see cref="ClosureWalk.ResolveSeeds"/>), so the walk never needs to reach back for the seed's type.</summary>
public sealed record WalkSeed(FormKey Key, string Path, string Label);

/// <summary>What shape a seed path is, decided ONCE and consumed everywhere.</summary>
public enum SeedShapeKind
{
    /// <summary>A record link. Carrying nothing is a legal state of this shape, not a different shape.</summary>
    Link,
    /// <summary>A list whose ELEMENTS are record links. Empty — and null — are legal states of this shape.</summary>
    LinkList,
    /// <summary>Anything else, with the reason spelled for the caller.</summary>
    Unsupported,
}

/// <summary>One seed path's shape verdict. <paramref name="Reason"/> is set only for
/// <see cref="SeedShapeKind.Unsupported"/> and is the sentence-free half of the refusal.</summary>
public sealed record SeedShape(SeedShapeKind Kind, Type? ElementType = null, string? Reason = null);

public static class ClosureWalk
{
    /// <summary>
    /// THE seed-shape judgement — one function, consumed by every site that needs it (seed resolution, the attach
    /// lane, and both refusal renders). No site keeps a private opinion.
    ///
    /// <para><b>It reads the DECLARED type and never the value.</b> Mutagen's record model declares these list
    /// properties NON-nullable while the binary overlay hands back <c>null</c> when the subrecord is absent, so the
    /// value and the model disagree and only the model answers "what shape is this field". A value-based test calls
    /// a supported field unsupported whenever the source happens to carry none of it.</para>
    ///
    /// <para><b>Null and empty are STATES of a shape, not shapes.</b> A link that is unset is still a link; a list
    /// that is null is still a list of links. What a consumer does about "carrying nothing" is its own business, but
    /// it never has to re-derive what the field IS.</para>
    /// </summary>
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
                // Named for what the entries ARE, not for what they might contain: calling every rejected element
                // "link-bearing" is false for e.g. a TintLayer and makes the refusal misdescribe the field.
                return new SeedShape(SeedShapeKind.Unsupported, element,
                    $"'{prop.Name}' is a list of {RecordNaming.StripOverlay(element.Name)} entries, which are not record links");
            }
        }

        return new SeedShape(SeedShapeKind.Unsupported, null,
            $"'{prop.Name}' is {RecordNaming.StripOverlay(t.Name)}, which is neither a record link nor a list of record links");
    }

    /// <summary>Default node cap. A real subtree is small; a walk past this is a runaway, and it refuses with the
    /// chain rather than truncating. Caller-overridable and always NAMED in the refusal.</summary>
    public const int DefaultNodeCap = 128;

    /// <summary>Default depth cap. Bounded and named for the same reason; a cycle cannot run away (ancestry stops
    /// it) but a deep legitimate chain still should not surprise anyone.</summary>
    public const int DefaultDepthCap = 32;

    /// <summary>Resolve the caller's seed PATHS against a seed record into seed links — the one place the walk
    /// touches named fields, and it does so by reflection over the record model, never a hand list.
    /// <para>An unknown path is a REFUSAL, not zero links: seed paths arrive as skill data, and a typo that silently
    /// seeded nothing would make the walk succeed having copied nothing, blanking a write consumer's target.
    /// Top-level property names only; a dotted path refuses as unknown, naming what was tried, rather than missing
    /// silently.</para></summary>
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

            // THE SHAPE IS DECIDED FIRST, off the declared type — before the value is read, so a null cannot be
            // mistaken for an absence of shape. Reading the value first and skipping it when null never reaches the
            // shape test, which lets one lane pass a field silently while the other refuses it.
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
            // Carrying nothing is a legal state of a supported shape, so it contributes no seeds and is not an
            // error here. Whether that means "clear the target's" is the consumer's call, not the walk's.
            if (val is null) continue;

            if (shape.Kind == SeedShapeKind.Link)
            {
                if (val is IFormLinkGetter single && single.FormKeyNullable is { } fk && !fk.IsNull)
                    seeds.Add(new WalkSeed(fk, path, $"{seedType}.{path}"));
                continue;
            }
            if (val is IEnumerable list and not string)
            {
                // A list is supported only when its ELEMENTS are links. A list of link-BEARING elements
                // (RankPlacement, PerkPlacement, ContainerEntry…) is not a link list, and walking it would
                // contribute zero seeds while the caller believed they had named a field — so it is refused by
                // shape, with the lane that does support it named.
                foreach (var e in list)
                    if (e is IFormLinkGetter l && l.FormKeyNullable is { } lk && !lk.IsNull)
                        seeds.Add(new WalkSeed(lk, path, $"{seedType}.{path}"));
                continue;
            }
            // The shape said LinkList and the value is neither null nor enumerable — the model and the runtime
            // disagree in a way ClassifySeed cannot see. Surfaced rather than guessed.
            return WalkResult.Fail(new WalkRefusal(
                WalkRefusalKind.UnsupportedSeedShape, seed.FormKey, "", Array.Empty<FormKey>(),
                $"'{path}' on {seedType} is declared a list of record links but did not read as one"));
        }
        return null;
    }

    /// <summary>Walk forward from the seeds, resolving every link against the ordered source universe and applying
    /// the scope predicate and exclusions. Returns the reached set (for the consumer to internalize), the boundary
    /// links kept as-is, and any genuine cycles — or a typed refusal with nothing usable.</summary>
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
        // The walked graph as EDGES, recorded per expanded node, plus a label per node for the readback. Cycles are
        // found from this after the walk rather than during it: the BFS `parent` map is a TREE, and "reaches itself
        // in the graph" is not the same question as "is an ancestor in the tree". A mutual reference between two
        // SIBLINGS (seed -> X, seed -> Y, X -> Y, Y -> X) is a real cycle whose nodes are never each other's tree
        // ancestors, so an ancestry test would call it a diamond. The node cap bounds this set, so a full pass is
        // cheap.
        var edges = new Dictionary<FormKey, List<FormKey>>();
        var labels = new Dictionary<FormKey, string>();
        // parent[k] = the key that pulled k in. The pull CHAIN is rebuilt from this, so every refusal can show the
        // whole path rather than one hop — which is what makes a cap refusal actionable.
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

            var label = $"{typeName} {key} ({hit.Body.EditorID ?? "<no editorid>"})";
            labels[key] = label;
            var outgoing = new List<FormKey>();
            edges[key] = outgoing;
            if (hit.Body is not IFormLinkContainerGetter flc) continue;
            foreach (var link in flc.EnumerateFormLinks())
            {
                var target = link.FormKey;
                if (target.IsNull) continue;
                // Every edge is recorded, cycle or not — telling a cycle from an ordinary re-convergence (a
                // diamond) is the post-walk pass's job now, and it needs the whole graph to do it.
                outgoing.Add(target);
                if (seen.Contains(target)) continue;
                // No seed guard here, deliberately: `seen` fills at DEQUEUE and every seed is dequeued before any
                // child expands, so a link back to a seed always takes the `seen` branch above and never reaches
                // this line. One added here would be dead code.
                if (!parent.ContainsKey(target)) parent[target] = key;
                queue.Enqueue((target, label, depth + 1));
            }
        }

        return new WalkResult(true, null, reached, kept, FindCycles(edges, labels));
    }

    /// <summary>Every cycle in the walked graph, found from the recorded edges once the walk is done.
    /// <para>A depth-first pass colouring nodes unvisited / on-stack / finished: an edge into an ON-STACK node is a
    /// back edge, which is exactly "this record reaches itself", and the stack from that node down to the one
    /// holding the edge IS the cycle. This asks the graph rather than the traversal tree, so a mutual reference
    /// between siblings reports.</para>
    /// <para>Only nodes with recorded edges can be ON the cycle — a boundary the walk kept was never expanded, has
    /// no outgoing edges, and so cannot close one. Edges to those are skipped rather than treated as dead ends.</para></summary>
    static List<WalkCycle> FindCycles(
        Dictionary<FormKey, List<FormKey>> edges, Dictionary<FormKey, string> labels)
    {
        const int Unvisited = 0, OnStack = 1, Finished = 2;
        var cycles = new List<WalkCycle>();
        var state = new Dictionary<FormKey, int>();
        var path = new List<FormKey>();
        var reported = new HashSet<(FormKey From, FormKey To)>();

        foreach (var root in edges.Keys)
        {
            if (state.TryGetValue(root, out var rootState) && rootState != Unvisited) continue;

            // Explicit stack rather than recursion: the node cap bounds the graph, but a 128-deep chain is still
            // no reason to put the walk's shape on the CLR's stack.
            var work = new Stack<(FormKey Key, int Index)>();
            state[root] = OnStack; path.Add(root); work.Push((root, 0));

            while (work.Count > 0)
            {
                var (key, index) = work.Pop();
                var outgoing = edges.TryGetValue(key, out var o) ? o : null;
                if (outgoing is null || index >= outgoing.Count)
                {
                    state[key] = Finished;
                    path.RemoveAt(path.Count - 1);      // finished nodes are always the deepest still on the path
                    continue;
                }
                work.Push((key, index + 1));

                var next = outgoing[index];
                if (!edges.ContainsKey(next)) continue;  // a kept boundary — expanded nothing, so it closes nothing
                var nextState = state.TryGetValue(next, out var s) ? s : Unvisited;
                if (nextState == OnStack)
                {
                    // A back edge. Reported once per (from, to) pair, because a record linking the same target
                    // twice is one cycle stated twice, not two facts.
                    if (reported.Add((key, next)))
                    {
                        var at = path.IndexOf(next);
                        cycles.Add(new WalkCycle(
                            path.Skip(at).ToList(), next,
                            labels.TryGetValue(key, out var lb) ? lb : key.ToString()));
                    }
                    continue;
                }
                if (nextState == Finished) continue;     // an ordinary diamond: already explored, not on this path
                state[next] = OnStack; path.Add(next); work.Push((next, 0));
            }
        }
        return cycles;
    }
}
