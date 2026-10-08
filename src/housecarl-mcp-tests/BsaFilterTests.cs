using HousecarlGenerator;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.BsaExtractArchives;

namespace HousecarlMcpTests;

/// <summary>housecarl_bsa_list and housecarl_bsa_extract under= (asset_status's selector grammar over one archive)
/// and bsa_list counts_only=, over the in-memory three-file archive. The out_path= lane never touches the service, so
/// it is passed as null.</summary>
[Trait("tier", "unit")]
public sealed class BsaFilterTests : IDisposable
{
    readonly string _work;
    readonly string _archive;

    public BsaFilterTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "hc-bsa-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
        _archive = Write(_work, "filter.bsa", BsaBuilder.Build(105, Named, ThreeFiles));
    }

    public void Dispose() { try { Directory.Delete(_work, recursive: true); } catch { /* temp scratch */ } }

    [Fact]
    public void AnExactPathListsOnlyThatFile()
    {
        var r = BsaTools.BsaList(_archive, under: new[] { "scripts/main.pex" });

        Assert.Contains("main.pex", r);
        Assert.DoesNotContain("helper.pex", r);
        Assert.DoesNotContain(".fuz", r);
    }

    [Fact]
    public void ADoubleStarGlobMatchesAcrossFolders()
    {
        var r = BsaTools.BsaList(_archive, under: new[] { "sound/**/*.fuz" });

        Assert.Contains("hello_000012ab_1.fuz", r);
        Assert.DoesNotContain(".pex", r);
    }

    [Fact]
    public void BackSlashesAndMixedCaseMatchTheArchivePath()
    {
        var r = BsaTools.BsaList(_archive, under: new[] { @"SCRIPTS\Helper.PEX" });

        Assert.Contains("helper.pex", r);
        Assert.DoesNotContain("main.pex", r);
    }

    [Fact]
    public void CountsOnlyGivesTheMatchCountAndNoPaths()
    {
        var r = BsaTools.BsaList(_archive, under: new[] { "scripts/*.pex" }, counts_only: true);

        Assert.Contains("2 matching", r);
        Assert.DoesNotContain("main.pex", r);
    }

    [Fact]
    public void AFilteredExtractWritesOnlyTheMatches()
    {
        var dest = Path.Combine(_work, "out");

        var r = BsaTools.BsaExtract(null!, _archive, out_path: dest, under: new[] { "scripts/*.pex" });

        Assert.Contains("2 of 3", r);
        Assert.Equal(2, Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void AListNamesTheSelectorThatMatchedNothingBesideALiveOne()
    {
        var r = BsaTools.BsaList(_archive, under: new[] { "scripts/main.pex", "scripts/mian.pex" }, counts_only: true);

        Assert.Contains("1 matching", r);
        Assert.Contains("under 'scripts/mian.pex' matched no file in 'filter.bsa'", r);
        Assert.DoesNotContain("under 'scripts/main.pex' matched", r);
    }

    [Fact]
    public void AnExtractNamesTheSelectorThatMatchedNothingBesideALiveOne()
    {
        var dest = Path.Combine(_work, "dead");

        var r = BsaTools.BsaExtract(null!, _archive, out_path: dest, under: new[] { "scripts/main.pex", "scripts/mian.pex" });

        Assert.Contains("1 of 3", r);
        Assert.Contains("under 'scripts/mian.pex' matched no file in 'filter.bsa'", r);
        Assert.Single(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFilterMatchingNothingRefusesAndExtractsNothing()
    {
        var dest = Path.Combine(_work, "none");

        var r = BsaTools.BsaExtract(null!, _archive, out_path: dest, under: new[] { "meshes/**" });

        Assert.StartsWith("error:", r);
        Assert.Contains("filter.bsa", r);
        Assert.False(Directory.Exists(dest));
    }
}
