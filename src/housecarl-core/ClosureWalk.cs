using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

// ======================================================================
//  ClosureWalk — the generic link walk (SPEC §3, components in §3.1).
//
//  WHAT MAKES IT GENERIC: expansion is Mutagen's own EnumerateFormLinks, so the set of
//  links followed is the set the record model declares — by construction, never a
//  per-type hand list. Everything domain-specific arrives as DATA: which fields seed the
//  walk, which record classes are excluded and how hard, which plugins the walk is
//  standalone-izing away from. No appearance vocabulary appears in this file, and none
//  may be added to it: a walk that knows what a head part is has stopped being a walk.
//
//  SENTENCE-FREE: every refusal is a typed WalkRefusal carrying its data (the key, the
//  chain that reached it, the caps, the arms consulted). The render composes the prose
//  from the shared sentence source — core owning refusal text is what #337 undid.
//
//  PROVENANCE IS PER NODE, NOT PER WALK. Each reached node records WHICH source arm
//  produced its body (SourceChain's first-hit answer) and the full chain of keys that
//  pulled it in. A chain-level "these sources were consulted" summary would be a
//  different, weaker claim: with an ordered universe the interesting fact is that THIS
//  record came from the override and THAT one fell through to the defining plugin.
//
//  CYCLES ARE RECORDED, NOT MERELY SKIPPED (SPEC §3.1). A visited-set walk that simply
//  drops a repeat link cannot tell a diamond (two paths to one record — ordinary and
//  uninteresting) from a genuine cycle (a record that reaches itself). The first is
//  noise; the second is a fact about the data worth reporting. They are distinguished
//  here by ANCESTRY: a link whose target is already on the current node's pull chain is
//  a cycle; any other repeat is a re-convergence and is silently deduped.
// ======================================================================

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

/// <summary>The expand-vs-keep rule (SPEC §3.1's scope predicate). A reached record either EXPANDS (the walk
/// enters it and it joins the reached set) or is KEPT as a boundary link (recorded, not entered).</summary>
public sealed record WalkScope(Func<FormKey, bool> ShouldExpand)
{
    /// <summary>The standalone-ization predicate: expand a record iff it is defined in one of the plugins being
    /// moved away from, OR it does not resolve in the active order (it would become a missing master). Everything
    /// else — vanilla, active shared resources — stays a link the artifact masters normally.
    /// <para>Named here rather than inlined at the call site because it is the rule the ACT consumer is built on,
    /// and a walk used for reading wants a different one.</para></summary>
    public static WalkScope StandaloneFrom(IReadOnlySet<ModKey> boundPlugins, Func<FormKey, bool> resolvesActively)
        => new(fk => boundPlugins.Contains(fk.ModKey) || !resolvesActively(fk));
}

/// <summary>One record the walk reached and will expand, with everything the report and the ACT consumer need.
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
/// them let one response say a link was "kept and mastered normally" while the strip list showed the same link
/// removed (review round 1). A render that cannot distinguish them cannot be honest about either.</para></summary>
public sealed record WalkBoundary(FormKey Key, string PulledBy, string Why, bool Excluded = false);

/// <summary>A genuine cycle: <paramref name="Path"/> is the pull chain from the seed to the node that closed it,
/// and <paramref name="Back"/> is the key it pointed back at — which is somewhere on that path.</summary>
public sealed record WalkCycle(IReadOnlyList<FormKey> Path, FormKey Back, string PulledBy);

/// <summary>Why a walk refused. Typed, so the render owns the words.</summary>
public enum WalkRefusalKind
{
    /// <summary>The seed set resolved to nothing — a walk that would copy nothing at all (Q3: silently succeeding
    /// here is how an ACT consumer blanks its target).</summary>
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
    /// the remedy is different: the path is right and the LANE is wrong (shape ruling (a), 2026-08-15).</summary>
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

/// <summary>The walk's outcome. A refusal carries NOTHING usable — the ACT posture (SPEC §3.1): a write that
/// breaches a bound refuses loud rather than truncating, because a silently partial copy is a broken artifact.</summary>
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

public static class ClosureWalk
{
    /// <summary>Default node cap. A real subtree is small; a walk past this is a runaway, and the ACT posture is to
    /// refuse with the chain rather than truncate (SPEC §3.1). Caller-overridable and always NAMED in the refusal.</summary>
    public const int DefaultNodeCap = 128;

    /// <summary>Default depth cap. Bounded and named for the same reason; a cycle cannot run away (ancestry stops
    /// it) but a deep legitimate chain still should not surprise anyone.</summary>
    public const int DefaultDepthCap = 32;

    /// <summary>Resolve the caller's seed PATHS against a seed record into seed links — the one place the walk
    /// touches named fields, and it does so by reflection over the record model, never a hand list.
    /// <para>An unknown path is a REFUSAL, not zero links: seed paths arrive as skill data, and a typo that
    /// silently seeded nothing would make the walk succeed having copied nothing — the exact shape that blanks an
    /// ACT consumer's target. Top-level property names only for now; a dotted path is a stated gap rather than a
    /// silent miss (it refuses as unknown, naming what was tried).</para></summary>
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

            object? val;
            try { val = prop.GetValue(seed); }
            catch (Exception ex)
            {
                return WalkResult.Fail(new WalkRefusal(
                    WalkRefusalKind.UnknownSeedPath, seed.FormKey, "", Array.Empty<FormKey>(),
                    $"'{path}' could not be read on {RecordNaming.StripOverlay(type.Name)}: {ex.Message}"));
            }
            if (val is null) continue;

            if (val is IFormLinkGetter single)
            {
                if (single.FormKeyNullable is { } fk && !fk.IsNull) seeds.Add(new WalkSeed(fk, path, $"{seedType}.{path}"));
                continue;
            }
            if (val is IEnumerable list and not string)
            {
                // A list is supported only when its ELEMENTS are links. A list of link-BEARING elements
                // (RankPlacement, PerkPlacement, ContainerEntry…) is link-bearing but is not a link list, and
                // walking it silently contributed zero seeds while the caller believed it had named a field —
                // the same silent-zero class an unknown path already refuses for. Refused by shape, with the lane
                // that does support it named (shape ruling (a), Aaron-go 2026-08-15).
                // The judgement is on the DECLARED element type, not on the elements present. An empty
                // ExtendedList<RankPlacement> and an empty ExtendedList<IFormLinkGetter<IHeadPartGetter>> are
                // indistinguishable by inspection, so a content-based test would call the first one supported
                // whenever the donor happened to carry no factions — and then empty the target's list at attach
                // time. Shape questions get shape answers.
                if (ListElementType(val.GetType()) is not { } el)
                    return WalkResult.Fail(new WalkRefusal(
                        WalkRefusalKind.UnsupportedSeedShape, seed.FormKey, "", Array.Empty<FormKey>(),
                        $"'{path}' on {seedType} is a collection whose element type cannot be read"));
                if (!typeof(IFormLinkGetter).IsAssignableFrom(el))
                    return WalkResult.Fail(new WalkRefusal(
                        WalkRefusalKind.UnsupportedSeedShape, seed.FormKey, "", Array.Empty<FormKey>(),
                        $"'{path}' on {seedType} is a list of link-BEARING entries ({RecordNaming.StripOverlay(el.Name)}), " +
                        "not a list of record links"));
                foreach (var e in list)
                    if (e is IFormLinkGetter l && l.FormKeyNullable is { } lk && !lk.IsNull)
                        seeds.Add(new WalkSeed(lk, path, $"{seedType}.{path}"));
                // An EMPTY link list is supported and is not an error: the source carries none, and the ACT
                // consumer's job is then to clear the target's rather than to leave a mixture behind.
                continue;
            }
            // A named path that is not link-bearing at all is a caller error of the same class as a typo: it
            // contributes nothing and the caller believes it contributed something.
            return WalkResult.Fail(new WalkRefusal(
                WalkRefusalKind.UnknownSeedPath, seed.FormKey, "", Array.Empty<FormKey>(),
                $"'{path}' on {RecordNaming.StripOverlay(type.Name)} carries no record links, so it cannot seed a walk"));
        }
        return null;
    }

    /// <summary>The declared element type of a collection, or null when it declares none this walk can read. Used
    /// to judge a seed path's SHAPE without depending on what the source happens to carry — see ResolveSeeds for
    /// why an empty list makes the content-based test unsound.</summary>
    public static Type? ListElementType(Type listType) => listType.GetInterfaces()
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        .Select(i => i.GetGenericArguments()[0])
        .FirstOrDefault();

    /// <summary>Walk forward from the seeds, resolving every link against the ordered source universe and applying
    /// the scope predicate and exclusions. Returns the reached set (to be internalized by the ACT consumer), the
    /// boundary links kept as-is, and any genuine cycles — or a typed refusal with nothing usable.</summary>
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
        var cycles = new List<WalkCycle>();
        var seen = new HashSet<FormKey>();
        // parent[k] = the key that pulled k in. The pull CHAIN is rebuilt from this, so every refusal can show the
        // whole path rather than one hop — which is what makes a cap refusal actionable.
        var parent = new Dictionary<FormKey, FormKey>();
        var excl = exclusions.ToDictionary(e => e.TypeName, e => e, StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<(FormKey Key, string PulledBy, int Depth)>();
        // A seed has no parent BY DEFINITION — it is where a chain starts. Recording one for a key that is both a
        // seed and reachable from another seed made ChainTo() disagree with the node's own PulledBy in a cap
        // refusal (review round 1): the node reported "pulled in by Npc.HeadParts" under a chain claiming it came
        // via another head part. Held explicitly so the parent map can refuse to overwrite a start.
        var seedKeys = new HashSet<FormKey>(seeds.Select(s => s.Key));
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

        bool IsAncestor(FormKey candidate, FormKey of)
        {
            var cur = of;
            var guard = new HashSet<FormKey>();
            while (guard.Add(cur))
            {
                if (cur == candidate) return true;
                if (!parent.TryGetValue(cur, out var p)) return false;
                cur = p;
            }
            return false;
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
            if (hit.Body is not IFormLinkContainerGetter flc) continue;
            foreach (var link in flc.EnumerateFormLinks())
            {
                var target = link.FormKey;
                if (target.IsNull) continue;
                // A repeat link is either a CYCLE (the target is on this node's own pull chain) or an ordinary
                // re-convergence (a diamond). Only the first is a fact worth reporting; conflating them would
                // bury real cycles under every shared texture set.
                if (seen.Contains(target))
                {
                    if (IsAncestor(target, key))
                        cycles.Add(new WalkCycle(ChainTo(key), target, label));
                    continue;
                }
                if (!parent.ContainsKey(target) && !seedKeys.Contains(target)) parent[target] = key;
                queue.Enqueue((target, label, depth + 1));
            }
        }

        return new WalkResult(true, null, reached, kept, cycles);
    }
}
