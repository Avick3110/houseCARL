using System.Text.RegularExpressions;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NifInspectFixtures;

namespace HousecarlMcpTests;

/// <summary>NifWire.Render, the housecarl_nif_inspect response: alarms before errors, the provider chain on the error
/// path, cut counts that name the remainder, unknown and unrecognized input stated, and the shader section, slot names
/// and every decline reaching the wire rather than only the data model.</summary>
[Trait("tier", "unit")]
public sealed class NifInspectRenderTests
{
    static readonly string[] SummaryOnly = System.Array.Empty<string>();
    static readonly string[] ShaderAndPaths = { "shader", "paths" };
    static readonly string[] ShaderPathsShapes = { "shader", "paths", "shapes" };

    static readonly NifInspect SkMesh = NifService.Inspect(BuildSyntheticSe()).Inspect!;
    static readonly NifInspect Fo4Mesh = NifService.Inspect(BuildSyntheticSe(streamVersion: 130)).Inspect!;
    static readonly NifInspect EffectMesh = NifService.Inspect(BuildSyntheticEffectShader()).Inspect!;

    static string Fo4Render => Render(FakeData(Fo4Mesh, null), ShaderPathsShapes);

    // probe: "success render carries winner + version + SE flag + shape names"
    [Theory]
    [InlineData("read from: \"ModA\" (loose)")]
    [InlineData("20.2.0.7")]
    [InlineData("[Skyrim SE]")]
    [InlineData("Shape0")]
    public void ASuccessRenderCarriesWinnerVersionSeFlagAndShapeNames(string expected)
        => Assert.Contains(expected, Render(FakeData(FakeInspect(3, 1, false), null), SummaryOnly));

    static string AbsentWithBsaFailure => Render(
        FakeData(null, "ABSENT — no active mod or BSA provides this mesh path.", bsaFailures: new[] { "Bad.bsa (loaded by P.esp): truncated" }),
        SummaryOnly);

    // probe: "the read-failure alarm renders BEFORE the ABSENT error (a Q3 alarm can't be buried)"
    [Fact]
    public void TheReadFailureAlarmRendersBeforeTheAbsentError()
    {
        var text = AbsentWithBsaFailure;
        int alarm = text.IndexOf("could NOT be read", StringComparison.Ordinal);
        Assert.True(alarm >= 0, text);
        Assert.True(alarm < text.IndexOf("ABSENT", StringComparison.Ordinal), text);
    }

    // probe: "the error path still shows the winner→loser provider chain, each name inside the delimiter source_provider= takes"
    [Fact]
    public void TheErrorPathStillShowsTheWinnerToLoserProviderChain()
        => Assert.Contains("\"ModA\" (loose) > \"Base.bsa\" (BSA)", AbsentWithBsaFailure);

    // probe: "ambiguity is surfaced as a note"
    [Fact]
    public void AmbiguityIsSurfacedAsANote()
        => Assert.Contains("more than one source", Render(FakeData(FakeInspect(1, 0, false), null, ambiguous: true), SummaryOnly));

    // probe: "a filtered-section cut counts the FILTERED remainder, not the total shape count"
    [Fact]
    public void AFilteredSectionCutCountsTheFilteredRemainder()
    {
        var text = Render(FakeData(FakeInspect(4, 2, false, partsPerShape: 30), null), new[] { "partitions" }, cap: 1_500);
        Assert.Contains("more omitted", text);
        Assert.DoesNotContain("3 more omitted", text);
        Assert.DoesNotContain("4 more omitted", text);
    }

    // probe: "a shapes-detail cut counts the REMAINDER, not the total shape count"
    [Fact]
    public void AShapesDetailCutCountsTheRemainder()
    {
        var text = Render(FakeData(FakeInspect(6, 0, false), null), new[] { "shapes" }, cap: 1_100);
        Assert.Contains("more omitted", text);
        Assert.DoesNotContain("6 more omitted", text);
    }

    // probe: "HasUnknownBlocks with no named types renders 'present', not '0 type(s)'"
    [Fact]
    public void UnknownBlocksWithNoNamedTypesRenderPresent()
    {
        var text = Render(FakeData(FakeInspect(1, 0, true), null), SummaryOnly);
        Assert.Contains("unknown blocks: present", text);
        Assert.DoesNotContain("0 type(s)", text);
    }

    // probe: "an unrecognized sections= token is surfaced, never silently ignored"
    [Fact]
    public void AnUnrecognizedSectionTokenIsSurfaced()
        => Assert.Contains("unrecognized section", Render(FakeData(FakeInspect(1, 0, false), null), SummaryOnly, new[] { "bogus" }));

    // probe: "NOT-SKYRIM-RENDER — the decline is STATED in the shader section and caveated on both slot-listing sections"
    [Fact]
    public void TheSlotDeclineIsStatedInTheShaderSectionAndOnBothSlotSections()
    {
        var text = Fo4Render;
        Assert.Contains("NOT DERIVED for this block", text);
        Assert.Contains("unmodelled, not undetermined", text);
        Assert.True(Regex.Matches(text, "NOT DERIVED").Count >= 3, text);
    }

    // probe: "NOT-SKYRIM-RENDER — the lighting decline states the LAYOUT as its reason, not the library, and prints no value"
    [Fact]
    public void TheLightingDeclineStatesTheLayoutNotTheLibraryAndPrintsNoValue()
    {
        var text = Fo4Render;
        Assert.Contains("lighting values: NOT INTERPRETED for this block", text);
        Assert.Contains("this layout's stream never carried", text);
        Assert.DoesNotContain("NOT READ by this NiflySharp version", text);
        Assert.DoesNotContain("glossiness 80", text);
        Assert.DoesNotContain("glossiness 30", text);
    }

    // probe: "NOT-SKYRIM-RENDER — the decline asserts NO number about this block"
    [Fact]
    public void TheLayoutDeclineAssertsNoNumberAboutTheBlock()
    {
        var text = Fo4Render;
        Assert.DoesNotContain("constant 80", text);
        Assert.DoesNotMatch(@"glossiness is a constant \d", text);
    }

    // probe: "NOT-SKYRIM-RENDER — an ordinary Skyrim mesh carries none of that noise"
    [Fact]
    public void AnOrdinarySkyrimMeshCarriesNoDeclineNoise()
        => Assert.DoesNotContain("NOT DERIVED", Render(FakeData(SkMesh, null), ShaderPathsShapes));

    // probe: "the shader section renders: block, TYPE, layout, decoded flag names, emissive values"
    [Theory]
    [InlineData("--- shader")]
    [InlineData("BSLightingShaderProperty")]
    [InlineData("type SkinTint")]
    [InlineData("[SK layout]")]
    [InlineData("SLSF2 0x02000011")]
    [InlineData("Soft_Lighting")]
    [InlineData("emissive rgb(0.25,0.5,0.75)")]
    public void TheShaderSectionRendersBlockTypeLayoutFlagsAndEmissive(string expected)
        => Assert.Contains(expected, Render(FakeData(SkMesh, null), ShaderAndPaths));

    // probe: "every lighting value reaches the render with its real number, and no unread caveat is left over"
    [Fact]
    public void EveryLightingValueReachesTheRenderWithNoUnreadCaveat()
    {
        var text = Render(FakeData(SkMesh, null), ShaderAndPaths);
        Assert.Contains("glossiness 30", text);
        Assert.Contains("specular 1.5 rgb(1,0.5,0.25)", text);
        Assert.Contains("alpha 0.5", text);
        Assert.Contains("emissive rgb(0.25,0.5,0.75) x2.5", text);
        Assert.DoesNotContain("NOT READ by this NiflySharp version", text);
    }

    // probe: "named slots render as 'tex[N] (Name)' and an undetermined slot stays bare 'tex[N]'"
    [Fact]
    public void NamedSlotsRenderWithTheirNameAndAnUndeterminedSlotStaysBare()
    {
        var text = Render(FakeData(SkMesh, null), ShaderAndPaths);
        Assert.Contains("tex[2] (SoftLighting): ", text);
        Assert.Contains("tex[7] (Specular): ", text);
        Assert.Contains("tex[4]: ", text);
        Assert.DoesNotContain("tex[4] (", text);
    }

    // probe: "the unread values are NAMED on the wire, and none of them printed as its constant"
    [Fact]
    public void UnreadLightingValuesAreNamedOnTheWireAndNoneAsItsConstant()
    {
        var text = Render(FakeData(EffectMesh, null), ShaderAndPaths);
        Assert.Contains("NOT READ by this NiflySharp version", text);
        Assert.Contains("glossiness", text);
        Assert.Contains("alpha", text);
        Assert.DoesNotContain("glossiness 0", text);
        Assert.DoesNotContain("alpha 1", text);
    }

    // probe: "the residual reaches the rendered flag line — an unnamed bit is stated, never silently dropped"
    [Fact]
    public void AnUnnamedBitResidualReachesTheRenderedFlagLine()
    {
        var word = NifService.DecodeFlagWord("TEST", (GappedFlags)0x1D);
        Assert.Contains("(+unknown bits 0x10)", Render(FakeData(FakeShaderInspect(word), null), ShaderAndPaths));
    }

    // probe: "NiAVObject flags decode by deviation from the nif.xml default + the 0x80000 bit (no invented bit-names)"
    [Theory]
    [InlineData("0x80000 clear")]
    [InlineData("vs BSTriShape default 0x8000E")]
    [InlineData("+0x4000000")]
    [InlineData("-0x80000")]
    public void NiAvObjectFlagsDecodeByDeviationFromTheNifXmlDefault(string expected)
        => Assert.Contains(expected, Render(FakeData(FakeInspect(1, 0, false), null), new[] { "shapes" }));
}
