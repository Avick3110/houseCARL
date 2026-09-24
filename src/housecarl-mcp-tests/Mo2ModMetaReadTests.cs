using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The meta.ini Nexus update-cache reader: the <c>[General]</c> fields read through the QSettings value
/// grammar, the exact-key match that keeps <c>1\modid</c> from shadowing <c>modid</c>, and the <c>[installedFiles]</c>
/// file ids. Migrated from the <c>mo2-modmeta-guard</c> probe; each test names the probe arm it carries.</summary>
[Trait("tier", "unit")]
public sealed class Mo2ModMetaReadTests : IDisposable
{
    readonly string _dir;

    public Mo2ModMetaReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hc-modmeta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Write the lines as a meta.ini in this test's own folder and read it back.</summary>
    ModMetaIni? Read(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllLines(path, lines);
        return Mo2ModMeta.Read(path);
    }

    /// <summary>Probe arm A: a standard Nexus meta.ini, whitespace around one '=', and [installedFiles] after [General].</summary>
    ModMetaIni? ReadStandard() => Read(
        "[General]",
        "gameName=SkyrimSE",
        "modid=12604",
        "version = 6.9",
        "newestVersion=6.11",
        "ignoredVersion=6.10",
        "lastNexusUpdate=1778020881",
        "[installedFiles]",
        @"1\modid=99999",
        @"1\fileid=749043");

    // A: standard meta.ini reads
    [Fact]
    public void AStandardMetaIniReads() => Assert.NotNull(ReadStandard());

    // A: modid = 12604 (NOT the [installedFiles] 99999); the second file puts 1\modid first, so only the exact-key match holds it
    [Fact]
    public void ModIdIsTheGeneralKeyNotTheInstalledFilesModId()
    {
        Assert.Equal(12604, ReadStandard()!.ModId);
        var reversed = Read("[installedFiles]", @"1\modid=99999", @"1\fileid=749043", "[General]", "modid=12604");
        Assert.Equal(12604, reversed!.ModId);
    }

    // A: version tolerates whitespace-around-= -> '6.9'
    [Fact]
    public void VersionToleratesWhitespaceAroundTheEquals() => Assert.Equal("6.9", ReadStandard()!.Version);

    // A: newestVersion = 6.11
    [Fact]
    public void NewestVersionReads() => Assert.Equal("6.11", ReadStandard()!.NewestVersion);

    // A: ignoredVersion = 6.10
    [Fact]
    public void IgnoredVersionReads() => Assert.Equal("6.10", ReadStandard()!.IgnoredVersion);

    // A: lastNexusUpdate raw unix seconds
    [Fact]
    public void LastNexusUpdateIsTheRawUnixSeconds() => Assert.Equal("1778020881", ReadStandard()!.LastNexusUpdate);

    // A: [installedFiles] 1\fileid -> [749043] (NOT the 1\modid 99999)
    [Fact]
    public void InstalledFileIdsTakeTheFileIdNotTheModId() => Assert.Equal(new[] { 749043 }, ReadStandard()!.InstalledFileIds);

    /// <summary>Probe arm B: the QSettings quirks — @ByteArray wrap, @Invalid unset, surrounding quotes, doubled backslash.</summary>
    ModMetaIni? ReadQuirks() => Read(
        "[General]",
        "modid=@ByteArray(266)",
        "version=\"5.2SE\"",
        "newestVersion=@Invalid()",
        @"ignoredVersion=a\\b");

    // B: @ByteArray(266) -> 266
    [Fact]
    public void ByteArrayWrappedModIdUnwraps() => Assert.Equal(266, ReadQuirks()!.ModId);

    // B: surrounding quotes stripped -> 5.2SE
    [Fact]
    public void SurroundingQuotesAreStripped() => Assert.Equal("5.2SE", ReadQuirks()!.Version);

    // B: @Invalid() -> null
    [Fact]
    public void InvalidReadsAsNull() => Assert.Null(ReadQuirks()!.NewestVersion);

    // B: doubled backslash unescaped -> a\b
    [Fact]
    public void DoubledBackslashIsUnescaped() => Assert.Equal(@"a\b", ReadQuirks()!.IgnoredVersion);

    // B: no [installedFiles] section -> empty fileids (never null)
    [Fact]
    public void NoInstalledFilesSectionGivesEmptyFileIds()
    {
        var ids = ReadQuirks()!.InstalledFileIds;
        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    // C: non-integer modid -> 0
    [Fact]
    public void NonIntegerModIdReadsAsZero() => Assert.Equal(0, Read("[General]", "version=1.0", "modid=notanumber")!.ModId);

    // C: absent fields -> null; C: no [installedFiles] -> empty fileids (still a record, never a throw)
    [Fact]
    public void AbsentFieldsReadAsNullAndFileIdsEmpty()
    {
        var meta = Read("[General]", "version=1.0", "modid=notanumber")!;
        Assert.Null(meta.NewestVersion);
        Assert.Null(meta.IgnoredVersion);
        Assert.Null(meta.LastNexusUpdate);
        Assert.Empty(meta.InstalledFileIds);
    }

    // E: multi fileid, size= interleaved, out-of-order N -> [111,222] by index
    [Fact]
    public void MultipleFileIdsComeBackInIndexOrder()
    {
        var meta = Read(
            "[General]",
            "modid=126608",
            "[installedFiles]",
            @"2\fileid=222",
            @"1\modid=126608",
            "size=2",
            @"2\modid=126608",
            @"1\fileid=111");
        Assert.Equal(new[] { 111, 222 }, meta!.InstalledFileIds);
    }

    // F: [installedFiles] size=0 -> empty fileids (FOMOD/manual); stray fileid in [Plugins] ignored
    [Fact]
    public void AFileIdOutsideInstalledFilesIsIgnored()
    {
        var meta = Read(
            "[General]",
            "modid=35546",
            "[installedFiles]",
            "size=0",
            "[Plugins]",
            @"1\fileid=999");
        Assert.Empty(meta!.InstalledFileIds);
    }

    // D: missing file -> null, no throw
    [Fact]
    public void AMissingFileReadsAsNull() => Assert.Null(Mo2ModMeta.Read(Path.Combine(_dir, "does-not-exist.ini")));
}
