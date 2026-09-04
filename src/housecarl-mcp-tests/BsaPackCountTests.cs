using System.Security.AccessControl;
using System.Security.Principal;
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

    static BsaPackResult Packed(int? packed, IReadOnlyList<string> rootSkipped) =>
        new(true, packed, packed ?? 0, rootSkipped, "", null, null);

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
    public void ADbFileIsNotArchivableBecauseBsarchDropsIt()
    {
        var src = Source("with-db");
        WriteFile(src, Path.Combine("textures", "a.dds"));
        WriteFile(src, Path.Combine("textures", "Thumbs.db"));

        var scan = BsaArchive.ScanPackSource(src);

        Assert.Equal(1, scan.Archivable);
    }

    [Fact]
    public void AFileWithNoExtensionIsNotArchivableBecauseBsarchDropsIt()
    {
        var src = Source("with-extensionless");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        WriteFile(src, Path.Combine("meshes", "LICENSE"));
        WriteFile(src, "Thumbs.db");

        var scan = BsaArchive.ScanPackSource(src);

        Assert.Equal(1, scan.Archivable);
        Assert.Equal(new[] { "Thumbs.db" }, scan.RootFiles);   // the root listing stays whole — it says what was left loose
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
    public void AnUncountableArchiveFormatReportsNoCount()
    {
        var report = BsaTools.PackReport(Packed(null, Array.Empty<string>()), "Test.ba2", @"C:\mods\Test.ba2", "-fo4", compress: false);

        Assert.DoesNotContain("file(s) into", report);
        Assert.Contains("not counted or checked against the source", report);
        Assert.DoesNotContain("WARNING", report);
    }

    [Fact]
    public void ABsaWhoseHeaderCouldNotBeReadWarnsInsteadOfBlamingTheFormat()
    {
        var report = BsaTools.PackReport(Packed(null, Array.Empty<string>()), "Test.bsa", @"C:\mods\Test.bsa", "-sse", compress: false);

        Assert.Contains("WARNING", report);
        Assert.Contains("could not read its .bsa header", report);
    }

    [Fact]
    public void ASourceThatCouldNotBeScannedSaysTheArchiveWasNotChecked()
    {
        var r = new BsaPackResult(true, 3, null, Array.Empty<string>(), "", null, null);

        var report = BsaTools.PackReport(r, "Test.bsa", @"C:\mods\Test.bsa", "-sse", compress: false);

        Assert.Contains("packed 3 file(s) into Test.bsa", report);
        Assert.Contains("could not be fully scanned", report);
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

    [Fact]
    public void ARunThatPackedNothingReportsNoCountAtAll()
    {
        var src = Source("stuck-scratch");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        var archive = Path.Combine(_root, "Out.bsa");
        var scratch = Path.Combine(_root, "Out.houseCARL-tmp.bsa");
        File.WriteAllText(scratch, "stale");
        using var held = new FileStream(scratch, FileMode.Open, FileAccess.Read, FileShare.None);   // undeletable, so the pack refuses up front

        var r = BsaArchive.Pack(Path.Combine(_root, "no-such-bsarch.exe"), src, archive, "-sse", compress: false, timeoutMs: 5_000);

        Assert.False(r.Ran);
        Assert.Contains("stale houseCARL scratch", r.RunError);
        Assert.Null(r.Packed);     // nothing was produced, so the count is unknown, not zero
        Assert.Null(r.Expected);
    }

    /// <summary>A subfolder the process cannot list throws out of the scan; the pack still runs, unchecked, and says so.</summary>
    [Fact]
    public void ASourceThatCannotBeListedPacksUncheckedInsteadOfRefusing()
    {
        if (!OperatingSystem.IsWindows()) return;   // the deny ACE that makes a folder unlistable is Windows-only
        var src = Source("unlistable");
        WriteFile(src, Path.Combine("meshes", "a.nif"));
        var denied = Path.Combine(src, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "b.nif"), "x");

        var info = new DirectoryInfo(denied);
        var acl = info.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory, AccessControlType.Deny);
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            Assert.ThrowsAny<Exception>(() => BsaArchive.ScanPackSource(src));

            var r = BsaArchive.Pack(Path.Combine(_root, "no-such-bsarch.exe"), src, Path.Combine(_root, "Out.bsa"), "-sse", compress: false, timeoutMs: 5_000);

            Assert.Null(r.Expected);                                   // nothing to cross-check against
            Assert.DoesNotContain("source folder", r.RunError ?? "");  // the scan did not refuse the pack — BSArch was reached
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }
}
