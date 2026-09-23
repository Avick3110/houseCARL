using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>At the depth floor a <c>Conditions[].Data</c> arm opens one bounded level to its params and keeps its
/// <c>[Type]</c> summary; a plain substruct still stops there (#258). Migrated from <c>condition-arm-expand-guard</c>.</summary>
[Trait("tier", "unit")]
public sealed class ConditionArmExpandGuardTests
{
    static MagicEffect ConditionedEffect()
    {
        var mod = new SkyrimMod(new ModKey("hc_condarm", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });
        return mgef;
    }

    // PARAMS-SURFACE: depth=3 shows Data.ActorValue.
    [Fact]
    public void AtDepthThreeTheArmShowsItsParam()
    {
        var f = ReadEngine.ReadFields(ConditionedEffect(), new[] { "Conditions" }, 3).Fields
            .Single(x => x.Path == "Conditions[0].Data.ActorValue");
        Assert.Equal("Conjuration", f.Token);
    }

    // SUMMARY-KEPT: the arm [Type] summary is still present beside its params.
    [Fact]
    public void AtDepthThreeTheArmKeepsItsTypeSummary()
    {
        var f = ReadEngine.ReadFields(ConditionedEffect(), new[] { "Conditions" }, 3).Fields
            .Single(x => x.Path == "Conditions[0].Data" && !x.HasValue);
        Assert.Contains("GetActorValueConditionData", f.Note);
    }

    // BOUNDED: the arm opens exactly one level, nothing under Data.ActorValue.
    [Fact]
    public void TheArmOpensExactlyOneLevel()
    {
        var fields = ReadEngine.ReadFields(ConditionedEffect(), new[] { "Conditions" }, 3).Fields;
        Assert.Contains(fields, x => x.Path == "Conditions[0].Data.ActorValue");
        Assert.DoesNotContain(fields, x => x.Path.StartsWith("Conditions[0].Data.ActorValue.", StringComparison.Ordinal));
    }

    // DEPTH-NOT-LOWERED: depth=2 still shows no arm params.
    [Fact]
    public void AtDepthTwoTheArmShowsNoParams()
    {
        var fields = ReadEngine.ReadFields(ConditionedEffect(), new[] { "Conditions" }, 2).Fields;
        Assert.DoesNotContain(fields, x => x.Path.StartsWith("Conditions[0].Data.", StringComparison.Ordinal));
    }

    // FLOOR-UNCHANGED-OFF-ARM: an NPC Perks substruct still stops at the floor.
    [Fact]
    public void APlainSubstructStillStopsAtTheFloor()
    {
        var mod = new SkyrimMod(new ModKey("hc_condarm", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        var pp = new PerkPlacement { Rank = 1 };
        pp.Perk.SetTo(FormKey.Factory("03AF81:Skyrim.esm"));
        npc.Perks = new() { pp };
        var fields = ReadEngine.ReadFields(npc, new[] { "Perks" }, 2).Fields;
        Assert.Contains(fields, x => x.Path == "Perks[0]");
        Assert.DoesNotContain(fields, x => x.Path == "Perks[0].Perk");
    }
}
