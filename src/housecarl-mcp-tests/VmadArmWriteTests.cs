using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace HousecarlMcpTests;

/// <summary>
/// The polymorphic-element validator surface (the VMAD write gap): the corpus models <c>ScriptEntry.Properties</c> as
/// the base <c>ScriptProperty</c>, but a real element is one of its arms. Pre-flight admits an arm-only field and a
/// composed arm element, still refuses a field on no arm and a spec type that is no arm, and the engine performs what
/// pre-flight admits. World-free: the test corpus and an Activator in memory.
/// </summary>
[Trait("tier", "unit")]
public sealed class VmadArmWriteTests
{
    static readonly WriteRequest SetArmLeaf = new()
    {
        RecordType = "Activator",
        Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]", "Object" },
        Verb = "Set", Value = "018C91:Skyrim.esm",
    };

    static readonly WriteRequest AddArm = new()
    {
        RecordType = "Activator",
        Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties" },
        Verb = "Add",
        Struct = new StructSpec
        {
            Type = "ScriptObjectProperty",
            Fields = new() { ["Name"] = "VmadGuardProp", ["Flags"] = "Edited", ["Object"] = "00308D:Update.esm", ["Alias"] = "-1" },
        },
    };

    // Probe arm A: a Set through an arm-only field passes pre-flight.
    [Fact]
    public void ASetOnAnArmOnlyFieldPassesPreflight() => Assert.Null(TestCorpus.Rulebook.Validate(SetArmLeaf));

    // Probe arm B: composing a concrete arm element into the base-typed list passes pre-flight.
    [Fact]
    public void ComposingAnArmElementIntoTheBaseTypedListPassesPreflight() => Assert.Null(TestCorpus.Rulebook.Validate(AddArm));

    // Probe arms C and C2: a field on no arm still rejects, and the rejection says the arms were searched.
    [Fact]
    public void AFieldOnNoArmIsRefusedNamingTheArms()
    {
        var err = TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]", "Bogus" },
            Verb = "Set", Value = "1",
        });

        Assert.Contains("arms", err, StringComparison.OrdinalIgnoreCase);
    }

    // Probe arms D and D2: a spec type that is no arm rejects, listing the legal element types.
    [Fact]
    public void ASpecTypeThatIsNoArmIsRefusedListingTheArms()
    {
        var err = TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "Activator",
            Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties" },
            Verb = "Add",
            Struct = new StructSpec { Type = "LeveledItemEntry", Fields = new() },
        });

        Assert.Contains("ScriptObjectProperty", err, StringComparison.Ordinal);
    }

    // Probe arm E: the quest alias-script arm-field path validates through the same surface.
    [Fact]
    public void TheQuestAliasScriptArmFieldPassesPreflight() =>
        Assert.Null(TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "Quest",
            Path = new[] { "VirtualMachineAdapter", "Aliases[0]", "Scripts[0]", "Properties[0]", "Object" },
            Verb = "Set", Value = "00308D:Update.esm",
        }));

    // Probe arm G: a dict of polymorphic elements accepts a composed arm.
    [Fact]
    public void ADictArmComposeIsAccepted() =>
        Assert.Null(TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
            Struct = new StructSpec { Type = "PackageDataBool", Fields = new() },
        }));

    // Probe arm G2: a dict compose whose type is no arm rejects, naming the legal arms.
    [Fact]
    public void ADictComposeOfANonArmTypeIsRefusedNamingTheLegalTypes()
    {
        var err = TestCorpus.Rulebook.Validate(new WriteRequest
        {
            RecordType = "Package", Path = new[] { "Data" }, Verb = "Add", Key = "0",
            Struct = new StructSpec { Type = "Weapon", Fields = new() },
        });

        Assert.Contains("does not match", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Legal element types", err, StringComparison.OrdinalIgnoreCase);
    }

    // Probe arm H: a polymorphic base whose arms are records (GameSetting) classifies Record, not composable.
    [Fact]
    public void ARecordFamilyPolymorphicBaseClassifiesAsRecord()
    {
        var corpus = CorpusRulebook.LoadCorpus(TestCorpus.Path);
        var field = corpus.Types["SkyrimMod"].Fields.First(x => x.Name == "GameSettings");

        Assert.Equal(ElementKind.Record, SchemaClassifier.ClassifyElement(field, corpus));
    }

    // Probe arm F1 (was --source only): the engine's Set of the arm field lands.
    [Fact]
    public void TheEnginesSetOfAnArmFieldLands()
    {
        var act = ScriptedActivator();

        WriteEngine.ApplyVerb(act, SetArmLeaf);

        var p0 = Assert.IsType<ScriptObjectProperty>(act.VirtualMachineAdapter!.Scripts[0].Properties[0]);
        Assert.Equal("018C91:Skyrim.esm", p0.Object.FormKey.ToString());
    }

    // Probe arm F2 (was --source only): the engine's compose-Add of an arm element lands with its fields.
    [Fact]
    public void TheEnginesComposeAddOfAnArmElementLands()
    {
        var act = ScriptedActivator();

        WriteEngine.ApplyVerb(act, AddArm);

        var props = act.VirtualMachineAdapter!.Scripts[0].Properties;
        Assert.Equal(2, props.Count);
        var added = Assert.IsType<ScriptObjectProperty>(props[1]);
        Assert.Equal("VmadGuardProp", added.Name);
        Assert.Equal(-1, added.Alias);
        Assert.Equal("00308D:Update.esm", added.Object.FormKey.ToString());
        Assert.Equal(ScriptProperty.Flag.Edited, added.Flags);
    }

    static Activator ScriptedActivator()
    {
        var act = new Activator(new FormKey(new ModKey("HcVmadArm", ModType.Plugin), 0x800), SkyrimRelease.SkyrimSE)
        {
            VirtualMachineAdapter = new VirtualMachineAdapter(),
        };
        var script = new ScriptEntry { Name = "HcVmadArmScript" };
        script.Properties.Add(new ScriptObjectProperty { Name = "Existing", Flags = ScriptProperty.Flag.Edited });
        act.VirtualMachineAdapter.Scripts.Add(script);
        return act;
    }
}
