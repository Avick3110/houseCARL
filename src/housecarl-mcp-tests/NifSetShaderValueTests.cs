using NiflySharp;
using NiflySharp.Blocks;
using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>set_shader_value (#291): the six lighting values write where the block's own type has a setter and refuse
/// loud where it does not. Migrated from the nif-set-guard probe's shader arms.</summary>
[Trait("tier", "unit")]
public sealed class NifSetShaderValueTests
{
    readonly byte[] _shader = Shader();

    NifSetOutcome SetValue(string shape, string name, params float[] nums) =>
        NifService.Set(_shader, new[] { new NifSetOp(NifSetOpKind.SetShaderValue, shape, ShaderValue: name, ShaderNumbers: nums) });

    // probe: "INiShader still declares all six lighting values GET-ONLY — the reason the write gate reflects the CONCRETE block"
    [Theory]
    [InlineData("EmissiveColor")] [InlineData("EmissiveMultiple")] [InlineData("Glossiness")]
    [InlineData("SpecularStrength")] [InlineData("SpecularColor")] [InlineData("Alpha")]
    public void TheShaderInterfaceDeclaresEachLightingValueGetOnly(string property)
    {
        Assert.False(typeof(INiShader).GetProperty(property)!.CanWrite);
    }

    // probe: "ReallyWrites says YES on BSLightingShaderProperty, with the component count read off the property TYPE (scalar 1 / colour 3)"
    [Fact]
    public void ReallyWritesSaysYesOnTheLightingShaderWithTheComponentCount()
    {
        Assert.Equal(new NifShaderWritability(true, 1, null), NifService.ReallyWrites(typeof(BSLightingShaderProperty), "Glossiness"));
        Assert.Equal(new NifShaderWritability(true, 3, null), NifService.ReallyWrites(typeof(BSLightingShaderProperty), "SpecularColor"));
    }

    // probe: "ReallyWrites says NO-SETTER (not unknown-type) on BSEffectShaderProperty"
    [Theory]
    [InlineData("Glossiness")] [InlineData("Alpha")]
    public void ReallyWritesSaysNoSetterOnTheEffectShader(string property)
    {
        var w = NifService.ReallyWrites(typeof(BSEffectShaderProperty), property);
        Assert.False(w.Writable);
        Assert.Null(w.UnknownTypeName);
    }

    // probe: "a SETTABLE property of an unmarshalable type reports unknown-type — a distinct state from no-setter"
    [Fact]
    public void ASettablePropertyOfAnUnmarshalableTypeReportsUnknownType()
    {
        var w = NifService.ReallyWrites(typeof(UnmarshalableShaderStandIn), nameof(UnmarshalableShaderStandIn.Glossiness));
        Assert.False(w.Writable);
        Assert.Equal("String", w.UnknownTypeName);
    }

    // probe: "set_shader_value {name} -> {want} lands and reads back"
    [Theory]
    [InlineData("glossiness", "55", 55f)]
    [InlineData("specular_strength", "3.25", 3.25f)]
    [InlineData("emissive_multiple", "7.5", 7.5f)]
    [InlineData("alpha", "0.125", 0.125f)]
    [InlineData("emissive_color", "rgb(0.1,0.2,0.3)", 0.1f, 0.2f, 0.3f)]
    [InlineData("specular_color", "rgb(0.4,0.6,0.8)", 0.4f, 0.6f, 0.8f)]
    public void EachLightingValueLandsAndReadsBack(string name, string want, params float[] nums)
    {
        var o = SetValue("LitShape", name, nums);
        Assert.Null(o.Error);
        var sh = ShapeOf(o.WrittenBytes, "LitShape")!.Shader!;
        var got = name switch
        {
            "glossiness" => Fmt(sh.Glossiness),
            "specular_strength" => Fmt(sh.SpecularStrength),
            "emissive_multiple" => Fmt(sh.EmissiveMultiple),
            "alpha" => Fmt(sh.Alpha),
            "emissive_color" => Rgb(sh.EmissiveColor),
            _ => Rgb(sh.SpecularColor),
        };
        Assert.Equal(want, got);
    }

    // probe: "a glossiness write preserved the other five lighting values"
    [Fact]
    public void AGlossinessWriteKeepsTheOtherFiveValues()
    {
        var sh = ShapeOf(SetValue("LitShape", "glossiness", 55f).WrittenBytes, "LitShape")!.Shader!;
        Assert.Equal("1.5", Fmt(sh.SpecularStrength));
        Assert.Equal("2.5", Fmt(sh.EmissiveMultiple));
        Assert.Equal("0.5", Fmt(sh.Alpha));
        Assert.Equal("rgb(0.25,0.5,0.75)", Rgb(sh.EmissiveColor));
        Assert.Equal("rgb(1,0.5,0.25)", Rgb(sh.SpecularColor));
    }

    // probe: "the report says: 1 op, header untouched, exactly ONE block changed (the shader)"
    [Fact]
    public void AShaderWriteReportsOneOpOneBlockAndNoHeaderChange()
    {
        var r = SetValue("LitShape", "glossiness", 55f).Report!;
        Assert.Single(r.Ops);
        Assert.False(r.HeaderChanged);
        Assert.Single(r.ChangedBlocks);
    }

    // probe: "set_shader_value {name} on a BSEffectShaderProperty → refused AS UNSETTABLE, block type NAMED, nothing written"
    [Theory]
    [InlineData("glossiness")] [InlineData("specular_strength")] [InlineData("emissive_multiple")]
    [InlineData("alpha")] [InlineData("emissive_color")] [InlineData("specular_color")]
    public void EachValueOnTheEffectShaderRefusesAsUnsettable(string name)
    {
        var o = SetValue("EffShape", name, name.EndsWith("_color") ? new[] { 0.1f, 0.2f, 0.3f } : new[] { 1f });
        Assert.Contains("BSEffectShaderProperty", o.Error);
        Assert.Contains("not settable on that block type", o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "a scalar given 3 numbers → named refusal"
    [Fact]
    public void AScalarGivenThreeNumbersRefuses() => Assert.Contains("takes 1 number", SetValue("LitShape", "glossiness", 1f, 2f, 3f).Error);

    // probe: "a colour given 1 number → named refusal"
    [Fact]
    public void AColourGivenOneNumberRefuses() => Assert.Contains("takes 3 numbers", SetValue("LitShape", "specular_color", 1f).Error);

    // probe: "an unknown shader_value → named refusal listing the accepted names"
    [Fact]
    public void AnUnknownValueNameRefusesWithTheAcceptedNames() => Assert.Contains("glossiness", SetValue("LitShape", "shininess", 1f).Error);

    // probe: "the British spelling resolves AT ANY CASE (not just lower), alongside mixed case and the hyphen form"
    [Theory]
    [InlineData("Specular_Colour", "SpecularColor")]
    [InlineData("EMISSIVE_COLOUR", "EmissiveColor")]
    [InlineData("specular_colour", "SpecularColor")]
    [InlineData("Glossiness", "Glossiness")]
    [InlineData("specular-strength", "SpecularStrength")]
    public void AValueNameResolvesAtAnyCaseSpellingAndSeparator(string wire, string property)
    {
        Assert.Equal(property, NifService.ShaderValueProperty(wire));
    }

    // probe: "set_shader_value on a shape with NO shader → named refusal"
    [Fact]
    public void AShapeWithNoShaderRefuses() => Assert.Contains("no shader property", SetValue("NoShaderShape", "glossiness", 1f).Error);

    // probe: "an out-of-convention colour is WRITTEN, not refused (real meshes carry them)"
    [Fact]
    public void AnOutOfConventionColourIsWritten()
    {
        var o = SetValue("LitShape", "specular_color", 255f, 255f, 255f);
        Assert.Null(o.Error);
        Assert.Equal("rgb(255,255,255)", Rgb(ShapeOf(o.WrittenBytes, "LitShape")!.Shader!.SpecularColor));
    }

    // probe: "…and the report WARNS, naming the NifSkope 0-255 confusion"
    [Fact]
    public void AnOutOfConventionColourIsWarnedAbout()
    {
        var warnings = SetValue("LitShape", "specular_color", 255f, 255f, 255f).Report!.Warnings;
        Assert.Contains(warnings, x => x.Contains("outside the 0-1 range") && x.Contains("255"));
    }

    // probe: "an unbounded value (glossiness 900) is NOT warned about" / "an in-range alpha is not warned about"
    [Theory]
    [InlineData("glossiness", 900f)]
    [InlineData("alpha", 0.25f)]
    public void AnUnboundedOrInRangeValueIsNotWarnedAbout(string name, float value)
    {
        var o = SetValue("LitShape", name, value);
        Assert.Null(o.Error);
        Assert.DoesNotContain(o.Report!.Warnings, x => x.Contains("outside the 0-1 range"));
    }

    // probe: "an SE-stream mesh still parses its shader as the SK layout — the premise that makes the layout gate redundant rather than dead"
    [Fact]
    public void AnSeMeshParsesItsShaderAsTheSkyrimLayout()
    {
        Assert.True(NifService.Inspect(_shader).Inspect!.IsSkyrimSE);
        Assert.Equal("SK", ShapeOf(_shader, "LitShape")!.Shader!.GameType);
    }
}
