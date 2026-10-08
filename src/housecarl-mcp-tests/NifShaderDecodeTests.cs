using System.Linq;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>NifService.Inspect reads a shape's shader: block, layout, type, both flag words by the library's own bit
/// names, every lighting value, and texture slot names derived from type and flags. Where it cannot read a value (a
/// block the library only stubs, or a non-Skyrim layout) it reports nothing rather than a constant.</summary>
[Trait("tier", "unit")]
public sealed class NifShaderDecodeTests
{
    static readonly NifInspectOutcome Sk = NifService.Inspect(NifInspectFixtures.BuildSyntheticSe());
    static readonly NifInspectOutcome Fo4 = NifService.Inspect(NifInspectFixtures.BuildSyntheticSe(streamVersion: 130));
    static readonly NifInspectOutcome Effect = NifService.Inspect(NifInspectFixtures.BuildSyntheticEffectShader());

    static NifShape SkShape => Sk.Inspect!.Shapes.Single(s => s.Name == "GuardShape");
    static NifShader SkShader => SkShape.Shader ?? throw new Xunit.Sdk.XunitException("no shader on the SK shape");
    static NifShape Fo4Shape => Fo4.Inspect!.Shapes.Single(s => s.Name == "GuardShape");
    static NifShader Fo4Shader => Fo4Shape.Shader ?? throw new Xunit.Sdk.XunitException("no shader on the FO4 shape");
    static NifShader EffectShader => Effect.Inspect!.Shapes.Single(s => s.Name == "EffectShape").Shader
        ?? throw new Xunit.Sdk.XunitException("no shader on the effect shape");

    static string? Slot(NifShape s, int n) => s.Textures.FirstOrDefault(t => t.Slot == n)?.SlotName;

    // probe: "shader block + game layout + TYPE enum read"
    [Fact]
    public void TheShaderBlockLayoutAndTypeRead()
    {
        Assert.Equal("BSLightingShaderProperty", SkShader.BlockType);
        Assert.Equal("SK", SkShader.GameType);
        Assert.Equal("SkinTint", SkShader.ShaderType);
    }

    // probe: "SLSF1 decodes to its named bits, in bit order"
    [Fact]
    public void Slsf1DecodesToItsNamedBitsInBitOrder()
    {
        var w = SkShader.Flags1!;
        Assert.Equal(("SLSF1", 0x1003u, 0u), (w.Label, w.Raw, w.UnknownBits));
        Assert.Equal(new[] { "Specular", "Skinned", "Model_Space_Normals" }, w.Names);
    }

    // probe: "SLSF2 decodes to its named bits"
    [Fact]
    public void Slsf2DecodesToItsNamedBits()
    {
        var w = SkShader.Flags2!;
        Assert.Equal(("SLSF2", 0x2000011u, 0u), (w.Label, w.Raw, w.UnknownBits));
        Assert.Equal(new[] { "ZBuffer_Write", "Double_Sided", "Soft_Lighting" }, w.Names);
    }

    // probe: "emissive colour round-trips exactly"
    [Fact]
    public void TheEmissiveColourRoundTripsExactly()
    {
        var e = SkShader.EmissiveColor;
        Assert.NotNull(e);
        Assert.Equal((0.25f, 0.5f, 0.75f), (e!.R, e.G, e.B));
    }

    // probe: "every lighting value round-trips its authored value, none of them the old stub constant"
    [Fact]
    public void EveryLightingValueRoundTripsItsAuthoredValue()
    {
        Assert.Equal(30f, SkShader.Glossiness);
        Assert.Equal(1.5f, SkShader.SpecularStrength);
        Assert.Equal(2.5f, SkShader.EmissiveMultiple);
        Assert.Equal(0.5f, SkShader.Alpha);
        var c = SkShader.SpecularColor;
        Assert.NotNull(c);
        Assert.Equal((1f, 0.5f, 0.25f), (c!.R, c.G, c.B));
    }

    // probe: "texture slots carry their SEMANTIC names, derived from shader type + flags"
    [Theory]
    [InlineData(0, "Diffuse")]
    [InlineData(1, "Normal")]
    [InlineData(2, "SoftLighting")]
    [InlineData(7, "Specular")]
    public void TextureSlotsCarryTheirSemanticNamesFromShaderTypeAndFlags(int slot, string name)
        => Assert.Equal(name, Slot(SkShape, slot));

    // probe: "an UNDETERMINED slot stays unnamed rather than getting a confident wrong label"
    [Fact]
    public void AnUndeterminedSlotStaysUnnamed()
    {
        Assert.Contains(SkShape.Textures, t => t.Slot == 4);
        Assert.Null(Slot(SkShape, 4));
    }

    // probe: "the FO4-layout mesh parses clean" + "the fixture really is read as the FO4 layout"
    [Fact]
    public void TheStream130MeshParsesCleanAsTheFo4Layout()
    {
        Assert.Null(Fo4.Error);
        Assert.Equal("FO4", Fo4Shader.GameType);
    }

    // probe: "NOT-SKYRIM — every slot is UNNAMED on a non-SK layout, including the ones nifly's helpers answer 'true' for"
    [Fact]
    public void EverySlotIsUnnamedOnANonSkyrimLayout()
    {
        Assert.NotEmpty(Fo4Shape.Textures);
        Assert.All(Fo4Shape.Textures, t => Assert.Null(t.SlotName));
    }

    // probe: "NOT-SKYRIM — every lighting value is DECLINED on a non-SK layout" (glossiness would read the constant 80)
    [Fact]
    public void EveryLightingValueIsDeclinedOnANonSkyrimLayout()
    {
        Assert.Null(Fo4Shader.Glossiness);
        Assert.Null(Fo4Shader.SpecularStrength);
        Assert.Null(Fo4Shader.SpecularColor);
        Assert.Null(Fo4Shader.EmissiveColor);
        Assert.Null(Fo4Shader.EmissiveMultiple);
        Assert.Null(Fo4Shader.Alpha);
    }

    // probe: "the effect-shader mesh parses clean" + "the fixture really is an SK-layout effect shader, and it reports NO shader type"
    [Fact]
    public void TheEffectShaderMeshIsSkLayoutWithNoShaderType()
    {
        Assert.Null(Effect.Error);
        Assert.Equal(("BSEffectShaderProperty", "SK"), (EffectShader.BlockType, EffectShader.GameType));
        Assert.Null(EffectShader.ShaderType);
    }

    // probe: "every lighting value this block only STUBS is reported as unread"
    [Fact]
    public void EveryLightingValueABlockOnlyStubsIsReportedUnread()
    {
        Assert.Null(EffectShader.EmissiveColor);
        Assert.Null(EffectShader.EmissiveMultiple);
        Assert.Null(EffectShader.Glossiness);
        Assert.Null(EffectShader.SpecularStrength);
        Assert.Null(EffectShader.SpecularColor);
        Assert.Null(EffectShader.Alpha);
    }

    // probe: "an unnamed bit is surfaced as a residual mask, and a combo member peels before its constituent bits"
    [Fact]
    public void AnUnnamedBitIsAResidualMaskAndAComboPeelsBeforeItsParts()
    {
        var w = NifService.DecodeFlagWord("TEST", (NifInspectFixtures.GappedFlags)0x1D);
        Assert.Equal((0x1Du, 0x10u), (w.Raw, w.UnknownBits));
        Assert.Equal(new[] { "Beta", "Combo" }, w.Names);
    }
}
