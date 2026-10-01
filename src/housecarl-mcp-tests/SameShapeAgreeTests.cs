using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A field that lives on several arms of a polymorphic base is admitted when the arms agree in write-legal
/// shape: float and float? agree, a different cardinality, display type or CLR type does not. Migrated from the
/// sameshape-agree-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class SameShapeAgreeTests
{
    static WriteRequest Set(string type, string value, params string[] path) =>
        new() { RecordType = type, Path = path, Verb = "Set", Value = value };

    static FieldSchema Leaf(Type t) => new()
    {
        Name = "X", Cardinality = "scalar", Type = "float", Writable = true, IsIdentity = false,
        GetterTypeAssemblyQualified = t.AssemblyQualifiedName, MutableTypeAssemblyQualified = t.AssemblyQualifiedName,
    };

    // A: Set Perk.Effects[0].Value (float vs float? across arms — write-legal-identical) passes pre-flight
    [Fact]
    public void APerkEffectValueThatIsFloatOnOneArmAndNullableFloatOnAnotherPassesPreflight()
        => Assert.Null(TestCorpus.Rulebook.Validate(Set("Perk", "5", "Effects[0]", "Value")));

    // C: Condition.ComparisonValue (formlink vs scalar — cardinality conflict) stays rejected at the SameShape gate
    [Fact]
    public void AConditionComparisonValueThatIsALinkOnOneArmAndAFloatOnAnotherIsRefused()
        => Assert.Contains("CONFLICTING shapes",
            TestCorpus.Rulebook.Validate(Set("MagicEffect", "000800:Skyrim.esm", "Conditions[0]", "ComparisonValue")));

    // D: APackageData.Data (bool/uint/float — genuine conflict) stays rejected
    [Fact]
    public void APackageDataValueThatIsBoolUintOrFloatByArmIsRefused()
        => Assert.Contains("CONFLICTING shapes", TestCorpus.Rulebook.Validate(Set("Package", "1", "Data[0]", "Data")));

    // E1: arms equal on every display facet but binding float vs int REJECT
    [Fact]
    public void ArmsThatShowTheSameTypeButBindFloatAndIntDisagree()
        => Assert.False(CorpusRulebook.SameShape(Leaf(typeof(float)), Leaf(typeof(int))));

    // E2: the same two facets binding float vs float? AGREE
    [Fact]
    public void ArmsThatBindFloatAndNullableFloatAgree()
        => Assert.True(CorpusRulebook.SameShape(Leaf(typeof(float)), Leaf(typeof(float?))));

    [Fact]
    public void ArmsWhoseTypeNamesDoNotResolveAgreeOnlyWhenTheNamesMatch()
    {
        FieldSchema Unknown(string aq) => new()
        {
            Name = "X", Cardinality = "scalar", Type = "float", Writable = true, IsIdentity = false,
            GetterTypeAssemblyQualified = aq, MutableTypeAssemblyQualified = aq,
        };
        Assert.True(CorpusRulebook.SameShape(Unknown("No.Such.TypeA, Nowhere"), Unknown("No.Such.TypeA, Nowhere")));
        Assert.False(CorpusRulebook.SameShape(Unknown("No.Such.TypeA, Nowhere"), Unknown("No.Such.TypeB, Nowhere")));
    }

    // Apply-1: ApplyVerb resolves Effects[0] to the live PerkEntryPointModifyValue arm and sets Value=5
    [Fact]
    public void ThePreflightAdmittedSetLandsOnTheLiveNullableArm()
    {
        var perk = new SkyrimMod(new ModKey("HcSameShape", ModType.Plugin), SkyrimRelease.SkyrimSE).Perks.AddNew();
        perk.Effects.Add(new PerkEntryPointModifyValue());
        WriteEngine.ApplyVerb(perk, Set("Perk", "5", "Effects[0]", "Value"));
        Assert.Equal(5f, Assert.IsType<PerkEntryPointModifyValue>(perk.Effects[0]).Value);
    }
}
