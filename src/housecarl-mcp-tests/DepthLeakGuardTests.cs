using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A deep read of a condition whose arm carries a <c>System.Type</c> renders that type as one summary token
/// and never walks its reflection surface (HCBR-2026-06-08-01). Migrated from <c>depth-leak-guard</c>.</summary>
[Trait("tier", "unit")]
public sealed class DepthLeakGuardTests
{
    static IReadOnlyList<FieldValue> DepthEight()
    {
        var mod = new SkyrimMod(new ModKey("hc_depthguard", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Conjuration },
        });
        return ReadEngine.ReadFields(mgef, new[] { "Conditions" }, 8).Fields;
    }

    // no reflection leak: nothing under Parameter1Type (Assembly, DefinedTypes, BaseType, Module, ...).
    [Theory]
    [InlineData("Parameter1Type.")]
    [InlineData("Parameter2Type.")]
    [InlineData(".Assembly")]
    [InlineData("DefinedTypes")]
    [InlineData("StructLayoutAttribute")]
    [InlineData("MetadataToken")]
    [InlineData("TypeHandle")]
    [InlineData("UnderlyingSystemType")]
    [InlineData(".BaseType")]
    [InlineData(".Module")]
    public void ADepthEightReadWalksNoReflectionPath(string marker)
        => Assert.DoesNotContain(DepthEight(), f => f.Path.Contains(marker, StringComparison.Ordinal));

    // type kept as one summary token: Parameter1Type renders as a RuntimeType token.
    [Fact]
    public void TheParameterTypeIsOneRuntimeTypeToken()
    {
        var f = DepthEight().Single(x => x.Path.EndsWith(".Parameter1Type", StringComparison.Ordinal));
        Assert.Contains("RuntimeType", f.Token ?? f.Note);
    }

    // useful condition value kept: Data.ActorValue=Conjuration.
    [Fact]
    public void TheConditionValueSurvives()
    {
        var f = DepthEight().Single(x => x.Path.EndsWith(".Data.ActorValue", StringComparison.Ordinal));
        Assert.Equal("Conjuration", f.Token);
    }
}
