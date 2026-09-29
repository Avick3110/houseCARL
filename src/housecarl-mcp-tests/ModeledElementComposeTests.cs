using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>Collection verbs over modeled elements: a record element is sent to the create axis, a compose's ctor args
/// are checked for shape and count, a modeled element has no Merge or remove-by-value, a Package.Data entry and a
/// SetAtIndex compose from parts and land, and a polymorphic base cannot be composed by its own name. Migrated from the
/// nested-create-guard probe (G6, G4, G7, GAP3, GAP4 and G8 arms).</summary>
[Trait("tier", "integration")]
public sealed class ModeledElementComposeTests : IDisposable
{
    NestedCreateRig? _rig;
    NestedCreateRig W => _rig ??= new NestedCreateRig();

    static string? Check(WriteRequest req) => TestCorpus.Rulebook.Validate(req);

    static WriteRequest Compose(string type, string path, string verb, string structType, string? key = null,
        Dictionary<string, string>? fields = null, string[]? ctorArgs = null) => new()
    {
        RecordType = type, Path = path.Split('.'), Verb = verb, Key = key,
        Struct = new StructSpec { Type = structType, Fields = fields, CtorArgs = ctorArgs },
    };

    static string Refuses(WriteRequest req, params string[] words)
    {
        var reject = Check(req);
        Assert.NotNull(reject);
        foreach (var w in words) Assert.Contains(w, reject, StringComparison.OrdinalIgnoreCase);
        return reject!;
    }

    static void Accepts(WriteRequest req) => Assert.Null(Check(req));

    static bool DataZeroIsTrue(ISkyrimModGetter mod, FormKey pack)
        => mod.Packages.Single(p => p.FormKey == pack).Data.TryGetValue((sbyte)0, out var d) && d is IPackageDataBoolGetter b && b.Data;

    const string RecordAxis = "created on its own (the record axis)";

    // G6-REJ-RECORD-ADD: an Add on DialogTopic.Responses refuses, naming housecarl_create as a whole word and the field.
    [Fact]
    public void ARecordElementAddIsSentToTheCreateTool()
    {
        var reject = Refuses(Compose("DialogTopic", "Responses", "Add", "DialogResponses"), RecordAxis);
        Assert.True(ToolNameMatch.ReferencedAtBoundary(reject, "housecarl_create"), reject);
        Assert.Contains("Responses", reject, StringComparison.Ordinal);
    }

    // G6-REJ-RECORD-REPLACEALL: a ReplaceAll on a record-element list refuses too.
    [Fact]
    public void ARecordElementReplaceAllIsRefused()
        => Refuses(new WriteRequest { RecordType = "DialogTopic", Path = new[] { "Responses" }, Verb = "ReplaceAll", Values = new[] { "012345:Skyrim.esm" } }, RecordAxis);

    // G6-OK-REMOVE-BYINDEX: a record-element Remove by index is accepted.
    [Fact]
    public void ARecordElementRemoveByIndexIsAccepted()
        => Accepts(WritePathRig.Req("DialogTopic", "Responses", "Remove", key: "0"));

    // G6-OK-STRUCT-UNCHANGED: a struct-element Add (Faction.Ranks) still composes.
    [Fact]
    public void AStructElementAddStillComposes()
        => Accepts(Compose("Faction", "Ranks", "Add", "Rank"));

    // G6-REJ-E2E: a flat DialogTopic create whose own op Adds to Responses refuses with no file.
    [Fact]
    public void ARecordElementAddIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejG6.esp", Spec("DialogTopic", "HcNcG6Topic", Compose("DialogTopic", "Responses", "Add", "DialogResponses")));
        Assert.Contains(RecordAxis, err, StringComparison.OrdinalIgnoreCase);
    }

    // G4-REJ-CTORARG-SHAPE: a malformed ctor arg refuses naming 'ctor arg #0' and the value.
    [Fact]
    public void AMalformedCtorArgIsRefusedByName()
        => Refuses(Compose("MagicEffect", "Archetype", "Set", "MagicEffectArchetype", ctorArgs: new[] { "notatypeenum" }), "ctor arg #0", "notatypeenum");

    // G4-REJ-CTORARG-ARITY: three ctor args refuse 'no constructor taking 3 arg(s)'.
    [Fact]
    public void AWrongCtorArgCountIsRefused()
        => Refuses(Compose("MagicEffect", "Archetype", "Set", "MagicEffectArchetype", ctorArgs: new[] { "a", "b", "c" }), "no constructor taking 3 arg(s)");

    // G4-OK-CTORARG: a valid ctor arg 'ValueModifier' is accepted.
    [Fact]
    public void AValidCtorArgIsAccepted()
        => Accepts(Compose("MagicEffect", "Archetype", "Set", "MagicEffectArchetype", ctorArgs: new[] { "ValueModifier" }));

    // G4-OK-NOCTORARGS: a compose with no ctor args is accepted.
    [Fact]
    public void AComposeWithoutCtorArgsIsAccepted()
        => Accepts(Compose("MagicEffect", "Archetype", "Set", "MagicEffectLightArchetype"));

    // G4-REJ-CTORARG-E2E: a malformed ctor arg refuses end to end with no file ('ctor arg #0').
    [Fact]
    public void AMalformedCtorArgIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejG4E2E.esp", Spec("MagicEffect", "HcNcG4Mgef",
            Compose("MagicEffect", "Archetype", "Set", "MagicEffectArchetype", ctorArgs: new[] { "notatypeenum" })));
        Assert.Contains("ctor arg #0", err, StringComparison.OrdinalIgnoreCase);
    }

    // G7-REJ-DICTMERGE: a Package.Data Merge refuses 'holds modeled elements' + 'Merge', offering no list verb.
    [Fact]
    public void AModeledDictMergeIsRefusedWithoutListVerbs()
    {
        var reject = Refuses(new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Merge", Entries = new() { ["0"] = "x" } },
            "holds modeled elements");
        Assert.Contains("Merge", reject, StringComparison.Ordinal);
        Assert.DoesNotContain("InsertAtIndex", reject, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAtIndex", reject, StringComparison.Ordinal);
    }

    // G7-REJ-COMPOSABLE-REMOVE-BYVALUE: a Faction.Ranks Remove-by-value refuses 'BY INDEX' + 'not by value'.
    [Fact]
    public void AStructElementRemoveByValueIsRefused()
        => Refuses(WritePathRig.Req("Faction", "Ranks", "Remove", "x"), "BY INDEX", "not by value");

    // G7-REJ-RECORD-REMOVE-BYVALUE: a DialogTopic.Responses Remove-by-value refuses the same way.
    [Fact]
    public void ARecordElementRemoveByValueIsRefused()
        => Refuses(WritePathRig.Req("DialogTopic", "Responses", "Remove", "x"), "BY INDEX", "not by value");

    // G7-OK-COMPOSABLE-REMOVE-BYINDEX: a Faction.Ranks Remove by index is accepted.
    [Fact]
    public void AStructElementRemoveByIndexIsAccepted()
        => Accepts(WritePathRig.Req("Faction", "Ranks", "Remove", key: "0"));

    // G7-OK-DICTREMOVE-BYKEY: a Package.Data Remove by key is accepted.
    [Fact]
    public void AModeledDictRemoveByKeyIsAccepted()
        => Accepts(WritePathRig.Req("Package", "Data", "Remove", key: "0"));

    // GAP3-OK-DICTADD-COMPOSE: a Package.Data Add with a PackageDataBool compose is accepted.
    [Fact]
    public void APackageDataAddComposeIsAccepted()
        => Accepts(Compose("Package", "Data", "Add", "PackageDataBool", "0", new() { ["Data"] = "true" }));

    // GAP3-OK-DICTSET-COMPOSE: a Package.Data Set with a compose is accepted.
    [Fact]
    public void APackageDataSetComposeIsAccepted()
        => Accepts(Compose("Package", "Data", "Set", "PackageDataBool", "0", new() { ["Data"] = "true" }));

    // GAP3-REJ-BADARM: a Weapon compose on Package.Data refuses 'does not match'; the legal list omits 'APackageData'.
    [Fact]
    public void ANonArmComposeOnPackageDataIsRefusedWithoutOfferingTheBase()
    {
        var reject = Refuses(Compose("Package", "Data", "Add", "Weapon", "0"), "does not match");
        var at = reject.IndexOf("Legal element types:", StringComparison.Ordinal);
        Assert.True(at >= 0, reject);
        Assert.DoesNotContain("APackageData", reject[at..], StringComparison.Ordinal);
    }

    // GAP3-OK-LIST-UNCHANGED: a Faction.Conditions ConditionFloat Add still composes.
    [Fact]
    public void AnArmElementListAddStillComposes()
        => Accepts(Compose("Faction", "Conditions", "Add", "ConditionFloat"));

    // GAP4-OK-SETIDX-COMPOSE: a SetAtIndex with an arm compose is accepted.
    [Fact]
    public void ASetAtIndexComposeOnAnArmListIsAccepted()
        => Accepts(Compose("Faction", "Conditions", "SetAtIndex", "ConditionFloat", "0"));

    // GAP4-REJ-SETIDX-NOSTRUCT: a plain-value SetAtIndex on an arm-element list refuses 'compose spec'.
    [Fact]
    public void APlainValueSetAtIndexOnAnArmListIsRefused()
        => Refuses(WritePathRig.Req("Faction", "Conditions", "SetAtIndex", "1", "0"), "compose spec");

    // GAP4-E2E-SETIDX-COMPOSE: SetAtIndex[0] overwrites in place: count 2, [0]=3 new, [1]=2 untouched.
    [Fact]
    public void ASetAtIndexComposeOverwritesTheElementInPlace()
    {
        var fac = new Faction(new FormKey(ModKey.FromFileName(MasterName), 0x930u), SkyrimRelease.SkyrimSE);
        foreach (var v in new[] { "1", "2" })
            WriteEngine.ApplyVerb(fac, Compose("Faction", "Conditions", "Add", "ConditionFloat", fields: new() { ["ComparisonValue"] = v }));
        WriteEngine.ApplyVerb(fac, Compose("Faction", "Conditions", "SetAtIndex", "ConditionFloat", "0", new() { ["ComparisonValue"] = "3" }));
        Assert.Equal(2, fac.Conditions!.Count);
        Assert.Equal(3f, Assert.IsAssignableFrom<IConditionFloatGetter>(fac.Conditions[0]).ComparisonValue);
        Assert.Equal(2f, Assert.IsAssignableFrom<IConditionFloatGetter>(fac.Conditions[1]).ComparisonValue);
    }

    // GAP3-E2E: a composed PackageDataBool(Data=true) lands on Package.Data[0] on disk.
    [Fact]
    public void AComposedPackageDataEntryIsWritten()
    {
        var (o, path) = W.Create("HcNcGap3Ok.esp", Spec("Package", "HcNcGap3Pack",
            Compose("Package", "Data", "Add", "PackageDataBool", "0", new() { ["Data"] = "true", ["Name"] = "HcGap3" })));
        Assert.True(o.Success, o.Error);
        Assert.True(DataZeroIsTrue(W.Open(path), o.Created[0].FormKey));
    }

    // GAP3-E2E-SET: Add Data[0]=false then Set Data[0]=true overwrites; Data==true on disk.
    [Fact]
    public void ASetComposeOverwritesAPackageDataEntry()
    {
        var (o, path) = W.Create("HcNcGap3Set.esp", Spec("Package", "HcNcGap3SetPack",
            Compose("Package", "Data", "Add", "PackageDataBool", "0", new() { ["Data"] = "false" }),
            Compose("Package", "Data", "Set", "PackageDataBool", "0", new() { ["Data"] = "true" })));
        Assert.True(o.Success, o.Error);
        Assert.True(DataZeroIsTrue(W.Open(path), o.Created[0].FormKey));
    }

    // GAP3-REJ-DUP: a second Add of Data[0] refuses 'already present' + 'use Set', no wrapper, no file.
    [Fact]
    public void ASecondAddOfTheSameKeyIsRefusedNamingSet()
    {
        var err = W.Refused("HcNcRejGap3Dup.esp", Spec("Package", "HcNcGap3Dup",
            Compose("Package", "Data", "Add", "PackageDataBool", "0", new() { ["Data"] = "true" }),
            Compose("Package", "Data", "Add", "PackageDataBool", "0", new() { ["Data"] = "false" })));
        Assert.Contains("already present", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("use Set", err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("real inconsistency", err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pre-flight ACCEPTED", err, StringComparison.OrdinalIgnoreCase);
    }

    // GAP3-REJ-BASEARM: composing 'APackageData' itself refuses 'polymorphic base', offering PackageDataBool.
    [Fact]
    public void ComposingThePackageDataBaseIsRefused()
    {
        var reject = Refuses(Compose("Package", "Data", "Add", "APackageData", "0"), "polymorphic base");
        Assert.Contains("PackageDataBool", reject, StringComparison.Ordinal);
    }

    // GAP3-REJ-BASEARM-LIST: composing 'Condition' on Faction.Conditions refuses, offering ConditionFloat.
    [Fact]
    public void ComposingTheConditionBaseIsRefused()
    {
        var reject = Refuses(Compose("Faction", "Conditions", "Add", "Condition"), "polymorphic base");
        Assert.Contains("ConditionFloat", reject, StringComparison.Ordinal);
    }

    // GAP3-OK-BASE-NOOVERREJECT: a concrete struct 'Rank' composed by its own name is accepted.
    [Fact]
    public void AConcreteStructComposedByItsOwnNameIsAccepted()
        => Accepts(Compose("Faction", "Ranks", "Add", "Rank"));

    // GAP3-REJ-BASEARM-E2E: composing the base refuses end to end with no file.
    [Fact]
    public void ComposingTheBaseIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejGap3Base.esp", Spec("Package", "HcNcGap3Base", Compose("Package", "Data", "Add", "APackageData", "0")));
        Assert.Contains("polymorphic base", err, StringComparison.OrdinalIgnoreCase);
    }

    // GAP3-REJ-BASEARM-FIELD: a poly field Set composing 'ScriptFragments' refuses, offering SceneScriptFragments and the dotted path.
    [Fact]
    public void ComposingAConcreteBaseOnAPolymorphicFieldIsRefusedNamingTheDottedPath()
    {
        var reject = Refuses(Compose("DialogResponses", "VirtualMachineAdapter.ScriptFragments", "Set", "ScriptFragments"), "polymorphic base");
        Assert.Contains("SceneScriptFragments", reject, StringComparison.Ordinal);
        Assert.Contains("dotted path", reject, StringComparison.Ordinal);
    }

    // GAP3-REJ-BASEARM-FIELD-ABS: an abstract base 'ANpcLevel' refuses with no dotted-path hint.
    [Fact]
    public void ComposingAnAbstractBaseCarriesNoDottedPathHint()
    {
        var reject = Refuses(Compose("Npc", "Configuration.Level", "Set", "ANpcLevel"), "polymorphic base");
        Assert.DoesNotContain("dotted path", reject, StringComparison.Ordinal);
    }

    // GAP3-OK-ARMFIELD-UNCHANGED: a real arm 'SceneScriptFragments' on that field is accepted.
    [Fact]
    public void ARealArmOnAPolymorphicFieldIsAccepted()
        => Accepts(Compose("DialogResponses", "VirtualMachineAdapter.ScriptFragments", "Set", "SceneScriptFragments"));

    public void Dispose() => _rig?.Dispose();
}
