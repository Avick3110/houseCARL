using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A nested set's own compose (a Condition's Data arm) survives the wire-to-core mapping, validates, applies and
/// serializes. Migrated from the nullarm-guard probe, part A.</summary>
[Trait("tier", "unit")]
public sealed class NestedComposeMapTests : IDisposable
{
    readonly WritePathRig _rig = new();

    static StructInput ConditionWithData(string dataArm) => new()
    {
        Type = "ConditionFloat",
        Sets = new[] { new NestedSet { Path = "Data", Compose = new StructInput { Type = dataArm } } },
    };

    static WriteRequest AddCondition(StructSpec? spec) =>
        new() { RecordType = "ConstructibleObject", Path = new[] { "Conditions" }, Verb = "Add", Struct = spec };

    static StructSpec? Mapped(string dataArm) => LoadOrderService.MapStruct(ConditionWithData(dataArm), "test", out _);

    // A1: MapStruct propagates a nested compose into Sets[0].Struct
    [Fact]
    public void MapStructCarriesTheNestedComposeIntoTheNestedStruct()
    {
        var spec = LoadOrderService.MapStruct(ConditionWithData("GetActorValueConditionData"), "test", out var err);
        Assert.Null(err);
        Assert.Equal("GetActorValueConditionData", Assert.Single(spec!.Sets!).Struct?.Type);
    }

    // A2: ApplyVerb builds the composed Data arm through the nested compose (in-memory)
    [Fact]
    public void ApplyBuildsTheComposedDataArm()
    {
        var cobj = new SkyrimMod(new ModKey("hc_nullarm_a2", ModType.Plugin), SkyrimRelease.SkyrimSE).ConstructibleObjects.AddNew();
        WriteEngine.ApplyVerb(cobj, AddCondition(Mapped("GetActorValueConditionData")));
        var cond = Assert.IsType<ConditionFloat>(Assert.Single(cobj.Conditions));
        Assert.IsType<GetActorValueConditionData>(cond.Data);
    }

    // A3: pre-flight accepts the nested-composed Add (validates the sub-arm)
    [Fact]
    public void PreflightAcceptsTheNestedComposedAdd()
        => Assert.Null(TestCorpus.Rulebook.Validate(AddCondition(Mapped("GetActorValueConditionData"))));

    // A4: an illegal nested arm rejects naming the legal arms
    [Fact]
    public void AnIllegalNestedArmRejectsNamingIt()
    {
        var err = TestCorpus.Rulebook.Validate(AddCondition(Mapped("BogusConditionData")));
        Assert.Contains("BogusConditionData", err);
        Assert.Contains("GetActorValueConditionData", err);   // a legal arm is listed, not just the word "arm"
    }

    // A5: a nested-composed Condition (Data set) serializes to a valid patch
    [Fact]
    public void ANestedComposedConditionSerializes()
    {
        var path = _rig.Out("hc_nullarm_a5.esp");
        var mod = new SkyrimMod(new ModKey("hc_nullarm_a5", ModType.Plugin), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(mod.ConstructibleObjects.AddNew(), AddCondition(Mapped("GetActorValueConditionData")));
        WriteEngine.WritePatch(mod, new ISkyrimModGetter[] { mod }, path);
        var back = _rig.Open(path).ConstructibleObjects.Single();
        Assert.IsAssignableFrom<IGetActorValueConditionDataGetter>(((IConditionFloatGetter)back.Conditions.Single()).Data);
    }

    public void Dispose() => _rig.Dispose();
}
