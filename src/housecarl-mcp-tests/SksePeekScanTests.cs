using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The string half of the SKSE static peek (<see cref="SksePeek"/>): ASCII and UTF-16LE extraction, the filter that keeps
/// compiler noise out of a DLL's config surface, and a failed read reported as failed. Planted byte images, no world.
/// Migrated from the skse-peek-guard probe, part 1 (arms A to D).
/// </summary>
[Trait("tier", "unit")]
public sealed class SksePeekScanTests
{
    static byte[] Image(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] Ascii(string s) => [.. Encoding.ASCII.GetBytes(s), 0];
    static byte[] Wide(string s) => [.. Encoding.Unicode.GetBytes(s), 0, 0];

    /// <summary>Code-like filler: every byte is below 0x20, so it is never a printable run.</summary>
    static byte[] Junk(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 0x1F)).ToArray();

    // probe A: "an embedded config path is extracted"; "an embedded plugin name is extracted"
    [Fact]
    public void AnAsciiConfigPathAndPluginNameAreExtracted()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Data\\SKSE\\Plugins\\SkyPatcher\\armor\\"), Junk(64),
                                         Ascii("Dawnguard.esm"), Junk(32), Ascii("nope")));
        Assert.Contains("Data\\SKSE\\Plugins\\SkyPatcher\\armor\\", r.ConfigPaths);
        Assert.Contains("Dawnguard.esm", r.PluginRefs);
    }

    // probe A: "a clean scan carries no failure note"
    [Fact]
    public void ACleanScanCarriesNoFailureNote()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Dawnguard.esm")));
        Assert.False(r.Failed);
        Assert.Null(r.Note);
    }

    // probe A: "accounting counts every run scanned, not just the shown ones"
    [Fact]
    public void TheRunCountIncludesRunsThatAreNotShown()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Data\\SKSE\\Plugins\\SkyPatcher\\armor\\"), Junk(64),
                                         Ascii("Dawnguard.esm"), Junk(32), Ascii("nope")));
        Assert.Equal(2, r.ConfigPaths.Count + r.PluginRefs.Count);
        Assert.True(r.RunsScanned >= 3, $"runs scanned: {r.RunsScanned}");
    }

    // probe B: "a WIDE config path is extracted"; "a WIDE plugin name is extracted"
    [Fact]
    public void AWideConfigPathAndPluginNameAreExtracted()
    {
        var r = SksePeek.ScanBytes(Image(Wide("Data\\SKSE\\Plugins\\Trails\\config.json"), Junk(16), Wide("Skyrim.esm")));
        Assert.Contains("Data\\SKSE\\Plugins\\Trails\\config.json", r.ConfigPaths);
        Assert.Contains("Skyrim.esm", r.PluginRefs);
    }

    // probe B: "ASCII and WIDE strings in ONE image are both found"
    [Fact]
    public void AsciiAndWideStringsInOneImageAreBothFound()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Data\\a.ini"), Wide("Data\\b.toml")));
        Assert.Equal(2, r.ConfigPaths.Count);
        Assert.Contains("Data\\a.ini", r.ConfigPaths);
        // The wide run starts one byte early here (the ASCII run's last 'i' pairs with its terminator), so match the tail.
        Assert.Contains(r.ConfigPaths, p => p.EndsWith("Data\\b.toml"));
    }

    // probe C: "format strings + type soup are NOT config paths"; "a bare extension + a quoted token are NOT plugin refs"
    [Fact]
    public void FormatStringsTypeSoupBareExtensionsAndQuotedTokensAreNotFindings()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("%s.json"), Ascii(".esp"),
                                         Ascii("class std::basic_string<char>.ini"), Ascii("\"quoted.esm\"")));
        Assert.Empty(r.ConfigPaths);
        Assert.Empty(r.PluginRefs);
    }

    // probe C: "a plugin ref inside a PATH yields the FILENAME"; "a path that IS a plugin ref classifies as the plugin ref, not double-counted"
    [Fact]
    public void APluginNameInsideAPathYieldsTheFileNameOnly()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Data\\Dawnguard.esm")));
        Assert.Equal(new[] { "Dawnguard.esm" }, r.PluginRefs);
        Assert.Empty(r.ConfigPaths);
    }

    // probe C: "fmt/spdlog {} and printf %s templates are NOT plugin names"
    [Fact]
    public void FmtAndPrintfTemplatesAreNotPluginNames()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("{}.esp"), Ascii("loading {}.esm"), Ascii("%s.esp")));
        Assert.Empty(r.PluginRefs);
    }

    // probe C: "a {}-template PATH is still config surface (shown, not adjudicated)"
    [Fact]
    public void ATemplatePathIsStillConfigSurface()
    {
        var r = SksePeek.ScanBytes(Image(Ascii("Data/SKSE/Plugins/versionlib-{}.bin")));
        Assert.Contains("Data/SKSE/Plugins/versionlib-{}.bin", r.ConfigPaths);
    }

    // probe D: "an image with no strings yields nothing"; "that is a SUCCESSFUL scan"
    [Fact]
    public void AnImageWithNoStringsIsASuccessfulEmptyScan()
    {
        var r = SksePeek.ScanBytes(Image(Junk(512)));
        Assert.Empty(r.ConfigPaths);
        Assert.Empty(r.PluginRefs);
        Assert.False(r.Failed);
    }

    // probe D: "a missing image is a FAILED peek with a reason — never an empty-but-clean-looking result"
    [Fact]
    public void AMissingImageIsAFailedPeekWithAReason()
    {
        var r = SksePeek.Scan(Path.Combine(Path.GetTempPath(), "hc-peek-missing-" + Guid.NewGuid().ToString("N") + ".dll"));
        Assert.True(r.Failed);
        Assert.False(string.IsNullOrEmpty(r.Note));
    }
}
