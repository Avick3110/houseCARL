using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>A whole struct leaf Set by composing it from parts: accepted for a struct or an arm leaf, refused for the
/// wrong type or a bad field, guided toward compose when no compose is given, left as a plain value on a coercible
/// struct, not opened for a struct with no parameterless constructor, and landed on disk, including by a dotted path
/// into an absent struct. Migrated from the nested-create-guard probe (SUBSTRUCT-SET arms).</summary>
[Trait("tier", "integration")]
public sealed class SubstructComposeSetTests : IDisposable
{
    NestedCreateRig? _rig;
    NestedCreateRig W => _rig ??= new NestedCreateRig();

    static string? Check(WriteRequest req) => TestCorpus.Rulebook.Validate(req);

    static WriteRequest ComposeSet(string type, string path, string structType, Dictionary<string, string>? fields = null) => new()
    {
        RecordType = type, Path = path.Split('.'), Verb = "Set",
        Struct = new StructSpec { Type = structType, Fields = fields },
    };

    static Dictionary<string, string> Face(string nose) => new() { ["Nose"] = nose, ["Eyes"] = "0", ["Mouth"] = "0" };

    // SUBSTRUCT-SET-COMPOSE-OK: Set NPC_.FaceParts from a NpcFaceParts compose is accepted.
    [Fact]
    public void AStructLeafComposedFromPartsIsAccepted()
        => Assert.Null(Check(ComposeSet("Npc", "FaceParts", "NpcFaceParts", Face("32"))));

    // SUBSTRUCT-SET-ARM-COMPOSE-OK: SceneAdapter.ScriptFragments composed as SceneScriptFragments is accepted.
    [Fact]
    public void AnArmLeafComposedFromPartsIsAccepted()
        => Assert.Null(Check(ComposeSet("SceneAdapter", "ScriptFragments", "SceneScriptFragments")));

    // SUBSTRUCT-SET-COMPOSE-BADTYPE: a Weapon compose on FaceParts refuses 'does not match', naming NpcFaceParts.
    [Fact]
    public void AComposeOfTheWrongTypeIsRefusedNamingTheLeafsType()
    {
        var reject = Check(ComposeSet("Npc", "FaceParts", "Weapon"));
        Assert.NotNull(reject);
        Assert.Contains("does not match", reject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NpcFaceParts", reject, StringComparison.Ordinal);
    }

    // SUBSTRUCT-SET-COMPOSE-BADFIELD: Nose='notauint' refuses naming Nose.
    [Fact]
    public void AComposeWithABadFieldIsRefusedNamingTheField()
    {
        var reject = Check(ComposeSet("Npc", "FaceParts", "NpcFaceParts", new() { ["Nose"] = "notauint" }));
        Assert.NotNull(reject);
        Assert.Contains("Nose", reject, StringComparison.Ordinal);
    }

    // SUBSTRUCT-SET-NOSPEC: Set FaceParts with a plain value names 'compose spec', not 'requires a value'.
    [Fact]
    public void AStructLeafSetWithAPlainValueNamesTheCompose()
    {
        var reject = Check(WritePathRig.Req("Npc", "FaceParts", "Set", "0"));
        Assert.NotNull(reject);
        Assert.Contains("compose spec", reject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requires a value", reject, StringComparison.OrdinalIgnoreCase);
    }

    // SUBSTRUCT-SET-COERCIBLE-UNCHANGED: APerkEffect.ButtonLabel (TranslatedString) still takes a plain value.
    [Fact]
    public void ACoercibleStructLeafStillTakesAPlainValue()
        => Assert.Null(Check(WritePathRig.Req("APerkEffect", "ButtonLabel", "Set", "Activate")));

    // SUBSTRUCT-SET-ARRAY2D-REJECTED: CellMaxHeightData.HeightMap (no parameterless ctor) is not opened to compose.
    [Fact]
    public void AStructWithNoParameterlessConstructorIsNotComposed()
        => Assert.NotNull(Check(ComposeSet("CellMaxHeightData", "HeightMap", "ReadOnlyArray2d<Byte>")));

    // SUBSTRUCT-SET-COMPOSE-E2E: a composed NpcFaceParts(Nose=32) lands on NPC_.FaceParts on disk.
    [Fact]
    public void AComposedStructLeafIsWritten()
    {
        var (o, path) = W.Create("HcNcSubComp.esp", Spec("Npc", "HcNcSubCompNpc", ComposeSet("Npc", "FaceParts", "NpcFaceParts", Face("32"))));
        Assert.True(o.Success, o.Error);
        Assert.Equal(32u, W.Open(path).Npcs.Single(n => n.FormKey == o.Created[0].FormKey).FaceParts?.Nose);
    }

    // SUBSTRUCT-SET-DOTTED-VIVIFY: Set FaceParts.Nose on an absent struct creates it and lands Nose=32.
    [Fact]
    public void ADottedSetIntoAnAbsentStructCreatesIt()
    {
        var (o, path) = W.Create("HcNcSubDotted.esp", Spec("Npc", "HcNcSubDottedNpc", WritePathRig.Req("Npc", "FaceParts.Nose", "Set", "32")));
        Assert.True(o.Success, o.Error);
        Assert.Equal(32u, W.Open(path).Npcs.Single(n => n.FormKey == o.Created[0].FormKey).FaceParts?.Nose);
    }

    // SUBSTRUCT-SET-ARM-COMPOSE-E2E: a composed SceneScriptFragments lands on Scene.VMAD.ScriptFragments on disk.
    [Fact]
    public void AComposedArmLeafIsWritten()
    {
        var (o, path) = W.Create("HcNcSubArm.esp", Spec("Scene", "HcNcSubArmScene",
            ComposeSet("Scene", "VirtualMachineAdapter.ScriptFragments", "SceneScriptFragments", new() { ["FileName"] = "HcScene", ["ExtraBindDataVersion"] = "2" })));
        Assert.True(o.Success, o.Error);
        Assert.NotNull(W.Open(path).Scenes.Single(s => s.FormKey == o.Created[0].FormKey).VirtualMachineAdapter?.ScriptFragments);
    }

    // SUBSTRUCT-SET-COERCIBLE-COMPOSE-MSG: a compose on ButtonLabel names 'plain value', not 'requires a value'.
    [Fact]
    public void AComposeOnACoercibleStructLeafNamesThePlainValue()
    {
        var reject = Check(ComposeSet("APerkEffect", "ButtonLabel", "TranslatedString"));
        Assert.NotNull(reject);
        Assert.Contains("plain value", reject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requires a value", reject, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _rig?.Dispose();
}
