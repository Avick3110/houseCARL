using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="SksePluginReader.Read"/> off disk, moved from the skse-reader-guard probe (arms G and H): a real
/// managed PE with no SKSE export carries a note, no manifest and a known bitness (its NotSkse kind is pinned by
/// SkseImportWalkTests.ASystemDllIsClassifiedNotSkse), and a non-PE file or a missing path degrades to Unreadable with
/// bitness unknown, never a throw and never a fabricated 32-bit claim.</summary>
[Trait("tier", "unit")]
public sealed class SkseReadFromDiskTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("hc-skse-read-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp scratch */ }
    }

    // The reader's own assembly: a real PE image with no SKSE export.
    static SksePluginReader.SksePluginInfo ManagedAssembly() => SksePluginReader.Read(typeof(SksePluginReader).Assembly.Location);

    SksePluginReader.SksePluginInfo NonPeFile()
    {
        var junk = Path.Combine(_dir, "junk.dll");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("this is not a PE file at all, just some bytes"));
        return SksePluginReader.Read(junk);
    }

    SksePluginReader.SksePluginInfo MissingFile() => SksePluginReader.Read(Path.Combine(_dir, "does-not-exist.dll"));

    // Probe G: "NotSkse carries a Q3 note explaining why".
    [Fact]
    public void NotSkseCarriesANote() => Assert.Contains("SKSE", ManagedAssembly().Note);

    // Probe G: "no version manifest for a non-plugin DLL".
    [Fact]
    public void ANonPluginDllHasNoVersionManifest() => Assert.Null(ManagedAssembly().Version);

    // Probe G: "a readable PE reports a DETERMINED bitness (not null)".
    [Fact]
    public void AReadablePeReportsABitness() => Assert.NotNull(ManagedAssembly().Is64Bit);

    // Probe H: "non-PE bytes → Unreadable".
    [Fact]
    public void NonPeBytesAreUnreadable() => Assert.Equal(SksePluginReader.SksePluginKind.Unreadable, NonPeFile().Kind);

    // Probe H: "non-PE → bitness UNKNOWN (null), NOT a false 32-bit claim (finding #1)".
    [Fact]
    public void NonPeBytesHaveUnknownBitness() => Assert.Null(NonPeFile().Is64Bit);

    // Probe H: "missing file → Unreadable, no throw".
    [Fact]
    public void AMissingFileIsUnreadable() => Assert.Equal(SksePluginReader.SksePluginKind.Unreadable, MissingFile().Kind);

    // Probe H: "missing file → bitness UNKNOWN (null)".
    [Fact]
    public void AMissingFileHasUnknownBitness() => Assert.Null(MissingFile().Is64Bit);
}
