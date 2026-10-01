using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>nif_inspect's sections= parsing (#247): a JSON array sent as a string parses like the plain list, a
/// partly known list renders what it knows, and a list with nothing known is a loud error rather than a silent
/// summary.</summary>
[Trait("tier", "unit")]
public sealed class NifSectionsParseTests
{
    // Probe: "ARRAY-FORM: ["shapes","paths"] → {shapes, paths}, no unknown tokens", the spaced form, and "PLAIN".
    [Theory]
    [InlineData("[\"shapes\",\"paths\"]")]
    [InlineData("[\"shapes\", \"paths\"]")]
    [InlineData("shapes, paths")]
    public void TheArrayAndPlainFormsParseToTheSameSections(string sections)
    {
        var (want, unknown) = NifTools.ParseSections(sections);

        Assert.True(want.SetEquals(new[] { "shapes", "paths" }), string.Join(",", want));
        Assert.Empty(unknown);
    }

    // Probe: "ALL: 'all' expands to the 8 known sections".
    [Fact]
    public void AllExpandsToEveryKnownSection()
    {
        var (want, unknown) = NifTools.ParseSections("all");

        Assert.True(want.SetEquals(new[] { "shapes", "partitions", "alpha", "paths", "shader", "strings", "nodes", "bones" }),
            string.Join(",", want));
        Assert.Empty(unknown);
    }

    // Probe: "PARTIAL: ["shapes","textures"] → {shapes} + unknown {textures}, NOT an error".
    [Fact]
    public void APartlyKnownListKeepsTheKnownSectionAndIsNoError()
    {
        var (want, unknown) = NifTools.ParseSections("[\"shapes\",\"textures\"]");

        Assert.True(want.SetEquals(new[] { "shapes" }), string.Join(",", want));
        Assert.Equal(new[] { "textures" }, unknown);
        Assert.Null(NifTools.SectionsError(want, unknown));
    }

    // Probe: "ALL-UNKNOWN: ["textures"] → loud error naming the token, never a silent summary fallback".
    [Fact]
    public void AListWithNothingKnownIsAnErrorNamingTheToken()
    {
        var (want, unknown) = NifTools.ParseSections("[\"textures\"]");
        var err = NifTools.SectionsError(want, unknown);

        Assert.Empty(want);
        Assert.NotNull(err);
        Assert.Contains("textures", err);
        Assert.Contains("shapes", err);
    }

    // Probe: "EMPTY: '' → nothing requested → NOT an error (summary default)".
    [Fact]
    public void AnEmptyListRequestsNothingAndIsNoError()
    {
        var (want, unknown) = NifTools.ParseSections("");

        Assert.Empty(want);
        Assert.Empty(unknown);
        Assert.Null(NifTools.SectionsError(want, unknown));
    }

    // Probe: "HINT: the known-sections hint points texture-set slot paths at 'shapes'/'paths'".
    [Fact]
    public void TheHintPointsTextureSetPathsAtShapesAndPaths()
    {
        Assert.Contains("texture-set", NifTools.KnownSectionsHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shapes", NifTools.KnownSectionsHint);
        Assert.Contains("paths", NifTools.KnownSectionsHint);
    }
}
