using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The generic closure walk (<c>ClosureWalk.ResolveSeeds</c> / <c>ClosureWalk.Run</c>) over in-memory records,
/// apart from MO2: seeds as data, scope, provenance per node, cycles told apart from diamonds, exclusions, caps and
/// source failures. The graph: an NPC seeding HeadParts (hpA → hpB → hpC → hpD, each with a texture set, hpD
/// re-converging late on the shared one), WornArmor (armo → arma → armo, a cycle) and HairColor (an out-of-scope
/// master's record).</summary>
[Trait("tier", "unit")]
public sealed class ClosureWalkGraphTests
{
    static readonly ModKey Fix = new("WalkFix", ModType.Plugin);
    static readonly ModKey Outside = new("Outside", ModType.Master);
    static readonly FormKey Txst = new(Fix, 0x800);
    static readonly FormKey HpA = new(Fix, 0x801);
    static readonly FormKey HpB = new(Fix, 0x802);
    static readonly FormKey Armo = new(Fix, 0x803);
    static readonly FormKey Arma = new(Fix, 0x804);
    static readonly FormKey NpcKey = new(Fix, 0x805);
    static readonly FormKey Txst2 = new(Fix, 0x807);
    static readonly FormKey HpC = new(Fix, 0x808);
    static readonly FormKey HpD = new(Fix, 0x809);
    static readonly FormKey ClfmOut = new(Outside, 0x900);

    readonly Npc _npc;
    readonly Dictionary<FormKey, IMajorRecordGetter> _bodies;
    readonly WalkScope _scope = WalkScope.StandaloneFrom(new HashSet<ModKey> { Fix }, fk => fk.ModKey == Outside);
    static readonly WalkExclusion[] NoExcl = Array.Empty<WalkExclusion>();

    public ClosureWalkGraphTests()
    {
        var mod = new SkyrimMod(Fix, SkyrimRelease.SkyrimSE);
        mod.TextureSets.Add(new TextureSet(Txst, SkyrimRelease.SkyrimSE) { EditorID = "SharedTex" });
        mod.TextureSets.Add(new TextureSet(Txst2, SkyrimRelease.SkyrimSE) { EditorID = "DeepTex" });
        var hpD = new HeadPart(HpD, SkyrimRelease.SkyrimSE) { EditorID = "HpD" };
        hpD.TextureSet.SetTo(Txst);
        mod.HeadParts.Add(hpD);
        var hpC = new HeadPart(HpC, SkyrimRelease.SkyrimSE) { EditorID = "HpC" };
        hpC.TextureSet.SetTo(Txst2);
        hpC.ExtraParts.Add(HpD);
        mod.HeadParts.Add(hpC);
        var hpB = new HeadPart(HpB, SkyrimRelease.SkyrimSE) { EditorID = "HpB" };
        hpB.TextureSet.SetTo(Txst);
        hpB.ExtraParts.Add(HpC);
        mod.HeadParts.Add(hpB);
        var hpA = new HeadPart(HpA, SkyrimRelease.SkyrimSE) { EditorID = "HpA" };
        hpA.TextureSet.SetTo(Txst);
        hpA.ExtraParts.Add(HpB);
        mod.HeadParts.Add(hpA);
        var armo = new Armor(Armo, SkyrimRelease.SkyrimSE) { EditorID = "Armo" };
        armo.Armature.Add(Arma);
        mod.Armors.Add(armo);
        var arma = new ArmorAddon(Arma, SkyrimRelease.SkyrimSE) { EditorID = "Arma" };
        arma.Race.SetTo(Armo);
        mod.ArmorAddons.Add(arma);
        _npc = new Npc(NpcKey, SkyrimRelease.SkyrimSE) { EditorID = "Seed" };
        _npc.HeadParts.Add(HpA);
        _npc.WornArmor.SetTo(Armo);
        _npc.HairColor.SetTo(ClfmOut);
        mod.Npcs.Add(_npc);
        _bodies = mod.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (IMajorRecordGetter)r);
    }

    List<WalkSeed> Seeds()
    {
        Assert.Null(ClosureWalk.ResolveSeeds(_npc, new[] { "HeadParts", "WornArmor", "HairColor" }, out var seeds));
        return seeds;
    }

    SourceChain OneArm() => SourceChain.Single(new SourceArm("WalkFix.esp", SourceArmKind.File, "file 'WalkFix.esp'",
        fk => _bodies.TryGetValue(fk, out var b) ? b : null));

    WalkResult Walk(WalkExclusion[]? excl = null, int? nodeCap = null, int? depthCap = null)
        => ClosureWalk.Run(Seeds(), OneArm(), _scope, excl ?? NoExcl,
            nodeCap ?? ClosureWalk.DefaultNodeCap, depthCap ?? ClosureWalk.DefaultDepthCap);

    // seed paths resolve to their links, each carrying its own provenance label ('Npc.HeadParts')
    [Fact]
    public void SeedPathsResolveToTheirLinksEachLabelledWithItsPath()
    {
        var seeds = Seeds();
        Assert.Equal(3, seeds.Count);
        Assert.Contains(seeds, s => s.Label == "Npc.HeadParts");
    }

    // a TYPO'd seed path REFUSES, naming the path that was wrong
    [Fact]
    public void ATypoedSeedPathRefusesNamingThePath()
    {
        var typo = ClosureWalk.ResolveSeeds(_npc, new[] { "HeadParts", "HeadPart" }, out _);
        Assert.Equal(WalkRefusalKind.UnknownSeedPath, typo?.Refusal?.Kind);
        Assert.Contains("HeadPart", typo?.Refusal?.Detail);
    }

    // a seed path that carries no record links REFUSES too, naming the path
    [Fact]
    public void ASeedPathCarryingNoLinksRefuses()
    {
        var r = ClosureWalk.ResolveSeeds(_npc, new[] { "Height" }, out _);
        Assert.False(r?.Success ?? true);
        Assert.Contains("Height", r?.Refusal?.Detail);
    }

    // reaches the whole in-scope closure through EnumerateFormLinks, no per-type list
    [Fact]
    public void TheWalkReachesTheWholeInScopeClosure()
    {
        var r = Walk();
        Assert.True(r.Success, r.Refusal?.Detail);
        Assert.Equal(new[] { HpA, HpB, HpC, HpD, Txst, Txst2, Armo, Arma }.ToHashSet(), r.Reached.Select(n => n.Key).ToHashSet());
    }

    // an out-of-scope link is KEPT as a named boundary, not silently absent
    [Fact]
    public void AnOutOfScopeLinkIsKeptAsABoundary()
        => Assert.Equal(ClfmOut, Assert.Single(Walk().Kept).Key);

    // PROVENANCE: every reached node names WHICH source arm produced its body
    [Fact]
    public void EveryReachedNodeNamesItsSourceArm()
        => Assert.All(Walk().Reached, n => { Assert.Equal("WalkFix.esp", n.ArmSpelling); Assert.Equal(0, n.ArmIndex); });

    // each node carries its FULL pull chain from the seed link onward, not one hop, and its depth
    [Fact]
    public void ADeepNodeCarriesItsFullChainAndDepth()
    {
        var deep = Walk().Reached.First(n => n.Key == Txst2);
        Assert.Equal(new[] { HpA, HpB, HpC, Txst2 }, deep.Chain.ToArray());
        Assert.Equal(3, deep.Depth);
    }

    // provenance is per NODE: the record only the second arm has is attributed to it, every other node to arm 0
    [Fact]
    public void ProvenanceIsPerNodeAcrossTwoArms()
    {
        var split = new SourceChain(new[]
        {
            new SourceArm("Override.esp", SourceArmKind.File, "file 'Override.esp'",
                fk => fk == Txst ? null : (_bodies.TryGetValue(fk, out var b) ? b : null)),
            new SourceArm("Defining.esp", SourceArmKind.File, "file 'Defining.esp'",
                fk => _bodies.TryGetValue(fk, out var b) ? b : null),
        });
        var rs = ClosureWalk.Run(Seeds(), split, _scope, NoExcl);
        Assert.True(rs.Success, rs.Refusal?.Detail);
        Assert.Equal("Defining.esp", rs.Reached.Single(n => n.Key == Txst).ArmSpelling);
        Assert.All(rs.Reached.Where(n => n.Key != Txst), n => Assert.Equal("Override.esp", n.ArmSpelling));
    }

    // exactly ONE cycle reported, neither diamond is one; it names the key pointed back at and its path holds the loop
    [Fact]
    public void TheCycleIsReportedAndTheDiamondsAreNot()
    {
        var c = Assert.Single(Walk().Cycles);
        Assert.Equal(Armo, c.Back);
        Assert.Contains(Arma, c.Path);
        Assert.Contains(Armo, c.Path);
    }

    // the diamond's shared record is reached ONCE
    [Fact]
    public void TheDiamondsSharedRecordIsReachedOnce()
        => Assert.Single(Walk().Reached, n => n.Key == Txst);

    // a MUTUAL reference between two siblings is REPORTED as a cycle, not passed over as a diamond
    [Fact]
    public void AMutualReferenceBetweenSiblingsIsACycle()
    {
        var sibA = new FormKey(Fix, 0x8A0);
        var sibX = new FormKey(Fix, 0x8A1);
        var sibY = new FormKey(Fix, 0x8A2);
        var mod = new SkyrimMod(Fix, SkyrimRelease.SkyrimSE);
        var x = new HeadPart(sibX, SkyrimRelease.SkyrimSE) { EditorID = "SibX" };
        var y = new HeadPart(sibY, SkyrimRelease.SkyrimSE) { EditorID = "SibY" };
        x.ExtraParts.Add(sibY);
        y.ExtraParts.Add(sibX);
        var a = new HeadPart(sibA, SkyrimRelease.SkyrimSE) { EditorID = "SibSeed" };
        a.ExtraParts.Add(sibX);
        a.ExtraParts.Add(sibY);
        mod.HeadParts.Add(a); mod.HeadParts.Add(x); mod.HeadParts.Add(y);
        var cache = mod.ToImmutableLinkCache();
        var arm = new SourceChain(new[] { new SourceArm("Sib.esp", SourceArmKind.File, "the sibling fixture",
            fk => cache.TryResolve(fk, out var b) ? b : null) });

        var r = ClosureWalk.Run(new[] { new WalkSeed(sibA, "HeadParts", "Npc.HeadParts") }, arm,
            WalkScope.StandaloneFrom(new HashSet<ModKey> { Fix }, _ => true), NoExcl);

        Assert.True(r.Success, r.Refusal?.Detail);
        Assert.Contains(r.Cycles, c => (c.Back == sibX || c.Back == sibY) && c.Path.Contains(sibX) && c.Path.Contains(sibY));
    }

    // a STOP exclusion does not fail the walk; the excluded type is not expanded and is a named EXCLUSION boundary
    [Fact]
    public void AStopExclusionPrunesTheTypeAndKeepsItAsAnExclusionBoundary()
    {
        var r = Walk(new[] { new WalkExclusion("HeadPart", ExclusionSeverity.Stop, "pruned for the test") });
        Assert.True(r.Success, r.Refusal?.Detail);
        Assert.NotEmpty(r.Reached);
        Assert.All(r.Reached, n => Assert.NotEqual("HeadPart", n.TypeName));
        var kept = Assert.Single(r.Kept, b => b.Key == HpA);
        Assert.Contains("excluded", kept.Why);
        Assert.True(kept.Excluded);
    }

    // the OTHER seeds keep walking (continue, not break) while the subtree below the pruned record is not reached
    [Fact]
    public void AStopPrunesOneSubtreeNotTheQueue()
    {
        var r = Walk(new[] { new WalkExclusion("HeadPart", ExclusionSeverity.Stop, "pruned for the test") });
        Assert.Contains(r.Reached, n => n.Key == Armo);
        Assert.Contains(r.Reached, n => n.Key == Arma);
        Assert.DoesNotContain(r.Reached, n => n.Key == Txst);
    }

    // a REFUSE exclusion fails the whole walk, carrying the CALLER's reason, and yields nothing usable
    [Fact]
    public void ARefuseExclusionFailsTheWalkWithTheCallersReasonAndNothingUsable()
    {
        var r = Walk(new[] { new WalkExclusion("Armor", ExclusionSeverity.Refuse, "an Armor is not internalizable here") });
        Assert.False(r.Success);
        Assert.Equal(WalkRefusalKind.Excluded, r.Refusal?.Kind);
        Assert.Equal("an Armor is not internalizable here", r.Refusal?.Exclusion?.Reason);
        Assert.Empty(r.Reached);
        Assert.Empty(r.Kept);
    }

    // a node-cap breach REFUSES, naming the cap, the LAST PULL and its FULL CHAIN, with nothing usable back
    [Fact]
    public void ANodeCapBreachRefusesWithTheLastPullAndItsChain()
    {
        var r = Walk(nodeCap: 2);
        Assert.False(r.Success);
        Assert.Equal(WalkRefusalKind.NodeCap, r.Refusal?.Kind);
        Assert.Equal(2, r.Refusal?.Cap);
        Assert.Equal(Fix, r.Refusal?.Key.ModKey);
        Assert.False(string.IsNullOrEmpty(r.Refusal?.PulledBy));
        Assert.True(r.Refusal?.Chain.Count >= 2, $"chain {r.Refusal?.Chain.Count}");
        Assert.Equal(r.Refusal?.Key, r.Refusal?.Chain[^1]);
        Assert.Empty(r.Reached);
        Assert.Empty(r.Kept);
        Assert.Empty(r.Cycles);
    }

    // a depth-cap breach refuses as its own named kind, with its chain too
    [Fact]
    public void ADepthCapBreachRefusesAsItsOwnKindWithItsChain()
    {
        var r = Walk(depthCap: 1);
        Assert.Equal(WalkRefusalKind.DepthCap, r.Refusal?.Kind);
        Assert.Equal(1, r.Refusal?.Cap);
        Assert.True(r.Refusal?.Chain.Count >= 2, $"chain {r.Refusal?.Chain.Count}");
    }

    // a record no source has REFUSES as a miss, carrying every source consulted
    [Fact]
    public void ARecordNoSourceHasRefusesAsAMiss()
    {
        var empty = SourceChain.Single(new SourceArm("Empty.esp", SourceArmKind.File, "file 'Empty.esp'", _ => null));
        var r = ClosureWalk.Run(Seeds(), empty, _scope, NoExcl);
        Assert.Equal(WalkRefusalKind.SourceMiss, r.Refusal?.Kind);
        Assert.Equal(1, r.Refusal?.Miss?.Consulted.Count);
    }

    // an UNREADABLE record refuses as a FAULT, a different kind from a miss, naming the source
    [Fact]
    public void AnUnreadableRecordRefusesAsAFaultNamingTheSource()
    {
        var bad = SourceChain.Single(new SourceArm("Bad.esp", SourceArmKind.File, "file 'Bad.esp'",
            _ => throw new InvalidOperationException("a record Mutagen cannot parse")));
        var r = ClosureWalk.Run(Seeds(), bad, _scope, NoExcl);
        Assert.Equal(WalkRefusalKind.SourceFault, r.Refusal?.Kind);
        Assert.Equal("Bad.esp", r.Refusal?.Fault?.Arm.Spelling);
    }

    // a walk with NO seed links refuses
    [Fact]
    public void AWalkWithNoSeedsRefuses()
        => Assert.Equal(WalkRefusalKind.NoSeeds,
            ClosureWalk.Run(Array.Empty<WalkSeed>(), OneArm(), _scope, NoExcl).Refusal?.Kind);
}
