using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The repack file-count lane: BSArch archives only files under a subfolder, so a pack enumerates the source
/// first, names the root-level files it drops, and refuses when the produced archive's count disagrees with the source.
/// No BSArch needed — the enumeration, the refusal sentence and the tool's own render are exercised directly.</summary>
[Trait("tier", "unit")]
public sealed class BsaPackCountTests : IDisposable
{
    readonly string _root;

    public BsaPackCountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-bsa-pack-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    string Source(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteFile(string dir, string relative)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    static BsaPackResult Packed(int packed, IReadOnlyList<string> rootSkipped) =>
        new(true, packed, packed, rootSkipped, "", null, null);

    [Fact]
    public void SubfolderedSourceCountsEveryFileUnderAFolder()
    {
        var src = Source("subfoldered");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        WriteFile(src, Path.Combine("meshes", "deep", "b.nif"));
        WriteFile(src, Path.Combine("scripts", "c.pex"));

        var scan = BsaArchive.ScanPackSource(src);

        Assert.Equal(3, scan.Archivable);
        Assert.Empty(scan.RootFiles);
    }

    [Fact]
    public void RootLevelFilesAreNotArchivableAndAreNamed()
    {
        var src = Source("with-root");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        WriteFile(src, "readme.txt");
        WriteFile(src, "loose.nif");

        var scan = BsaArchive.ScanPackSource(src);

        Assert.Equal(1, scan.Archivable);
        Assert.Equal(new[] { "loose.nif", "readme.txt" }, scan.RootFiles.OrderBy(f => f).ToArray());
    }

    [Fact]
    public void RepackReportsTheFileCount()
    {
        var report = BsaTools.PackReport(Packed(3, Array.Empty<string>()), "Test.bsa", @"C:\mods\Test.bsa", "-sse", compress: false);

        Assert.Contains("packed 3 file(s) into Test.bsa", report);
    }

    [Fact]
    public void RepackReportsTheSkippedRootFiles()
    {
        var report = BsaTools.PackReport(Packed(1, new[] { "readme.txt", "loose.nif" }), "Test.bsa", @"C:\mods\Test.bsa", "-sse", compress: false);

        Assert.Contains("2 file(s) at the source folder's root were NOT archived", report);
        Assert.Contains("subfolder", report);
    }

    [Fact]
    public void ACountMismatchRefusesNamingBothNumbers()
    {
        var refusal = BsaArchive.PackCountError(packed: 2, expected: 5, "Test.bsa");

        Assert.NotNull(refusal);
        Assert.Contains("2 file(s)", refusal);
        Assert.Contains("5", refusal);
        Assert.Null(BsaArchive.PackCountError(packed: 5, expected: 5, "Test.bsa"));
    }

    [Fact]
    public void APackThatNeverRanStillReportsWhatTheSourceOffered()
    {
        var src = Source("never-ran");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        WriteFile(src, "readme.txt");

        var r = BsaArchive.Pack(Path.Combine(_root, "no-such-bsarch.exe"), src, Path.Combine(_root, "Out.bsa"), "-sse", compress: false, timeoutMs: 5_000);

        Assert.False(r.Ran);
        Assert.Equal(1, r.Expected);
        Assert.Equal(new[] { "readme.txt" }, r.RootSkipped);
    }
}
