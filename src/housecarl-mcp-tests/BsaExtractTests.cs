using HousecarlCore;
using HousecarlGenerator;
using Xunit;
using static HousecarlMcpTests.BsaExtractArchives;

namespace HousecarlMcpTests;

/// <summary>The read path housecarl_bsa_extract rides on: <see cref="BsaArchive.Unpack"/> and <see cref="BsaArchive.List"/>
/// over uncompressed archives authored in memory, so no BSArch and no real archive is needed. Migrated from the
/// bsa-extract-guard probe; each test carries that probe's check wording in a comment.</summary>
[Trait("tier", "unit")]
public sealed class BsaExtractTests : IDisposable
{
    readonly string _work;

    public BsaExtractTests()
    {
        _work = Path.Combine(Path.GetTempPath(), "hc-bsa-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_work);
    }

    public void Dispose() { try { Directory.Delete(_work, recursive: true); } catch { /* temp scratch */ } }

    string ThreeFileArchive(uint version) =>
        Write(_work, $"round-{version}.bsa", BsaBuilder.Build(version, Named, ThreeFiles));

    // probe: "v{version}: extract Success" and "every file extracted byte-correct at the right path"
    [Theory]
    [InlineData(105u)]
    [InlineData(104u)]
    public void UnpackWritesEveryFileByteCorrectAtItsPath(uint version)
    {
        var dest = Path.Combine(_work, "out");

        var result = BsaArchive.Unpack(ThreeFileArchive(version), dest);

        Assert.True(result.Ran && result.Success, result.RunError ?? result.Raw);
        Assert.True(AllBytesCorrect(dest, ThreeFiles));
    }

    // probe: "v{version}: reports 3 written"
    [Theory]
    [InlineData(105u)]
    [InlineData(104u)]
    public void UnpackReportsHowManyFilesItWrote(uint version)
    {
        var result = BsaArchive.Unpack(ThreeFileArchive(version), Path.Combine(_work, "out"));

        Assert.Contains("extracted 3", result.Raw, StringComparison.OrdinalIgnoreCase);
    }

    // probe: "v{version}: re-extract is a content-aware no-op"
    [Theory]
    [InlineData(105u)]
    [InlineData(104u)]
    public void ASecondUnpackIntoTheSameFolderWritesNothing(uint version)
    {
        var archive = ThreeFileArchive(version);
        var dest = Path.Combine(_work, "out");
        BsaArchive.Unpack(archive, dest);

        var again = BsaArchive.Unpack(archive, dest);

        Assert.True(again.Ran && again.Success, again.RunError ?? again.Raw);
        Assert.Contains("already present", again.Raw, StringComparison.OrdinalIgnoreCase);
    }

    // probe: "'..' entry -> the IsUnder guard refuses (not an upstream reject)"
    [Fact]
    public void AnEntryResolvingOutsideTheDestinationIsRefused()
    {
        var result = BsaArchive.Unpack(Write(_work, "evil.bsa", Escaping()), Path.Combine(_work, "evil-dest"));

        Assert.True(result.Ran, result.RunError);
        Assert.False(result.Success);
        Assert.Contains("path traversal", result.Raw, StringComparison.OrdinalIgnoreCase);
    }

    // probe: "nothing written outside the destination"
    [Fact]
    public void ARefusedEscapingEntryLeavesNothingOutsideTheDestination()
    {
        BsaArchive.Unpack(Write(_work, "evil.bsa", Escaping()), Path.Combine(_work, "evil-dest"));

        Assert.False(File.Exists(Path.Combine(_work, "escape.txt")));
    }

    // probe: "lying header count -> no silent success" (the reader throws on it; the failure comes from the extract catch)
    [Fact]
    public void AHeaderThatUndercountsItsFilesIsNotASuccess()
    {
        var lie = BsaBuilder.WithDeclaredFileCount(BsaBuilder.Build(105, Named, ThreeFiles), 1);

        var result = BsaArchive.Unpack(Write(_work, "count-lie.bsa", lie), Path.Combine(_work, "count-out"));

        Assert.False(result.Success);
    }

    /// <summary>The three-file archive with its header folder count (offset 16) zeroed: the reader enumerates nothing and does not throw.</summary>
    static byte[] ZeroFolders(uint version)
    {
        var archive = BsaBuilder.Build(version, Named, ThreeFiles);
        BitConverter.GetBytes(0u).CopyTo(archive, 16);
        return archive;
    }

    // #217 guard: a read that comes back empty is refused against the header's own file count, not reported as success
    [Theory]
    [InlineData(105u)]
    [InlineData(104u)]
    public void AnUnpackThatReadsNothingOfAThreeFileHeaderIsRefused(uint version)
    {
        var result = BsaArchive.Unpack(Write(_work, "zero.bsa", ZeroFolders(version)), Path.Combine(_work, "zero-out"));

        Assert.True(result.Ran, result.RunError);
        Assert.False(result.Success);
        Assert.Contains("declares 3", result.Raw);
    }

    // #217 guard, list side: an empty enumeration of a three-file header is not a short list
    [Theory]
    [InlineData(105u)]
    [InlineData(104u)]
    public void AListThatReadsNothingOfAThreeFileHeaderIsRefused(uint version)
    {
        var result = BsaArchive.List(Write(_work, "zero.bsa", ZeroFolders(version)));

        Assert.True(result.Ran, result.RunError);
        Assert.False(result.Success);
        Assert.Contains("declares 3", result.Raw);
    }

    static readonly byte[] Garbage = { 0x42, 0x53, 0x41, 0x00, 1, 2, 3, 4 };

    // probe: "garbage archive -> loud open error"
    [Fact]
    public void UnpackOfANonArchiveFailsWithAnOpenError()
    {
        var result = BsaArchive.Unpack(Write(_work, "junk.bsa", Garbage), Path.Combine(_work, "junk-out"));

        Assert.False(result.Ran);
        Assert.False(string.IsNullOrWhiteSpace(result.RunError));
    }

    // probe: "list of a garbage archive -> loud error too"
    [Fact]
    public void ListOfANonArchiveFailsWithAnOpenError()
    {
        var result = BsaArchive.List(Write(_work, "junk.bsa", Garbage));

        Assert.False(result.Ran);
        Assert.False(string.IsNullOrWhiteSpace(result.RunError));
    }
}
