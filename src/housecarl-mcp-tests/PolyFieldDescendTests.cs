using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A standalone polymorphic field (not a list element) is descended to its base's arms at pre-flight, and the
/// same request applies through a live arm. Migrated from the poly-field-descend-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class PolyFieldDescendTests
{
    static WriteRequest Set(string recordType, string value, params string[] path) =>
        new() { RecordType = recordType, Path = path, Verb = "Set", Value = value };

    static WriteRequest LevelOnNpcLevel() => Set("Npc", "5", "Configuration", "Level", "Level");
    static WriteRequest LevelMultOnPcLevelMult() => Set("Npc", "1.5", "Configuration", "Level", "LevelMult");

    static Npc FreshNpc() =>
        new SkyrimMod(new ModKey("hc_polydescend", ModType.Plugin), SkyrimRelease.SkyrimSE).Npcs.AddNew();

    // A: Set Npc.Configuration.Level.Level (substruct -> standalone-poly -> NpcLevel arm) passes pre-flight
    [Fact]
    public void AStandalonePolyFieldBehindASubstructDescendsToItsArmLeaf()
        => Assert.Null(TestCorpus.Rulebook.Validate(LevelOnNpcLevel()));

    // B: ...Level.LevelMult reaches the disjoint PcLevelMult arm
    [Fact]
    public void TheSameDescendReachesTheDisjointArm()
        => Assert.Null(TestCorpus.Rulebook.Validate(LevelMultOnPcLevelMult()));

    // C: Npc.Sound.InheritsSoundsFrom (a different poly base, descended at the record root) passes pre-flight
    [Fact]
    public void ADifferentPolyBaseAtTheRecordRootDescends()
        => Assert.Null(TestCorpus.Rulebook.Validate(Set("Npc", "000800:Skyrim.esm", "Sound", "InheritsSoundsFrom")));

    // M: ...VirtualMachineAdapter.ScriptFragments.OnBegin.FragmentName (substruct->poly->substruct->leaf) passes pre-flight
    [Fact]
    public void TheWalkKeepsGoingPastThePolyHop()
        => Assert.Null(TestCorpus.Rulebook.Validate(
            Set("DialogResponses", "Frag", "VirtualMachineAdapter", "ScriptFragments", "OnBegin", "FragmentName")));

    // D: a field on no arm of the descended base still rejects
    // D2: ...and the rejection names the searched arms (descended into the base, not 'not a substruct')
    [Fact]
    public void AFieldOnNoArmRejectsNamingTheArmsItSearched()
    {
        var err = TestCorpus.Rulebook.Validate(Set("Npc", "1", "Configuration", "Level", "Bogus"));
        Assert.NotNull(err);
        Assert.Contains("searched its arms", err);
        Assert.Contains("PcLevelMult", err);
        Assert.DoesNotContain("not a substruct", err);
    }

    // N: descending through a scalar still rejects (the fix did not over-broaden)
    [Fact]
    public void DescendingThroughAScalarStillRejects()
        => Assert.Contains("scalar", TestCorpus.Rulebook.Validate(Set("Npc", "1", "Configuration", "BleedoutOverride", "x")));

    // Apply-1: ApplyVerb descends through the live NpcLevel arm and sets Level=5 (in-memory, in CI)
    [Fact]
    public void ApplyDescendsThroughALiveNpcLevelArm()
    {
        var npc = FreshNpc();
        npc.Configuration.Level = new NpcLevel { Level = 1 };
        WriteEngine.ApplyVerb(npc, LevelOnNpcLevel());
        Assert.Equal(5, Assert.IsType<NpcLevel>(npc.Configuration.Level).Level);
    }

    // Apply-2: ApplyVerb descends through the live PcLevelMult arm and sets LevelMult=1.5 (in-memory, in CI)
    [Fact]
    public void ApplyDescendsThroughALivePcLevelMultArm()
    {
        var npc = FreshNpc();
        npc.Configuration.Level = new PcLevelMult { LevelMult = 0f };
        WriteEngine.ApplyVerb(npc, LevelMultOnPcLevelMult());
        Assert.Equal(1.5f, Assert.IsType<PcLevelMult>(npc.Configuration.Level).LevelMult);
    }
}
