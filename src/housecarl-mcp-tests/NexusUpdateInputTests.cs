using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The update check's input side: <c>NexusTools.ParseUpdatePairs</c>, the <c>id#fileid</c> grammar, and
/// <c>NexusClient.GroupRequests</c>, which merges one mod id's file ids across MO2 folders. Migrated from the
/// <c>nexus-file-check-guard</c> probe; each test carries the probe arm's wording. No network, no key, no MO2 instance.</summary>
[Trait("tier", "unit")]
public sealed class NexusUpdateInputTests
{
    static readonly (List<(int modId, string? installed, IReadOnlyList<int> fileIds)> pairs, List<string> bad) Parsed =
        NexusTools.ParseUpdatePairs("99786#585300, 126608#533265#533266, 12604=6.9, 266 4.3.8a, 3863, 99786#, abc#1, junk");

    // Probe: "parse: 5 readable entries".
    [Fact]
    public void FiveEntriesAreReadable() => Assert.Equal(5, Parsed.pairs.Count);

    // Probe: "parse: '99786#585300' → file-level [585300]".
    [Fact]
    public void AnIdHashFileIdIsFileLevel()
    {
        var p = Assert.Single(Parsed.pairs, p => p.modId == 99786);
        Assert.Null(p.installed);
        Assert.Equal(new[] { 585300 }, p.fileIds);
    }

    // Probe: "parse: '126608#533265#533266' → two fileids".
    [Fact]
    public void TwoHashesGiveTwoFileIds() =>
        Assert.Equal(new[] { 533265, 533266 }, Assert.Single(Parsed.pairs, p => p.modId == 126608).fileIds);

    // Probe: "parse: '12604=6.9' → version, no fileid".
    [Fact]
    public void AnEqualsGivesAVersionAndNoFileId()
    {
        var p = Assert.Single(Parsed.pairs, p => p.modId == 12604);
        Assert.Equal("6.9", p.installed);
        Assert.Empty(p.fileIds);
    }

    // Probe: "parse: '266 4.3.8a' → space-separated version".
    [Fact]
    public void ASpaceSeparatesAVersion() =>
        Assert.Equal("4.3.8a", Assert.Single(Parsed.pairs, p => p.modId == 266).installed);

    // Probe: "parse: bare '3863' → latest-only".
    [Fact]
    public void ABareIdHasNoVersionAndNoFileId()
    {
        var p = Assert.Single(Parsed.pairs, p => p.modId == 3863);
        Assert.Null(p.installed);
        Assert.Empty(p.fileIds);
    }

    // Probe: "parse: '99786#' (no fileid), 'abc#1' (bad modid), 'junk' → surfaced as bad, not silently dropped".
    [Theory]
    [InlineData("99786#")]
    [InlineData("abc#1")]
    [InlineData("junk")]
    public void AnUnreadableEntryIsSurfacedAsBad(string entry) => Assert.Contains(entry, Parsed.bad);

    static readonly (List<int> order, Dictionary<int, (string? installed, List<int> fileIds)> map) Grouped =
        NexusClient.GroupRequests(new (int, string?, IReadOnlyList<int>)[]
        {
            (126608, "1.3", new[] { 533265 }),
            (126608, "1.1", new[] { 533266 }),
            (99786, null, new[] { 585300 }),
            (126608, null, new[] { 533265 }),
        });

    // Probe: "group: 2 modIds, first-seen order".
    [Fact]
    public void GroupingKeepsFirstSeenOrder() => Assert.Equal(new[] { 126608, 99786 }, Grouped.order);

    // Probe: "group: same modId across folders MERGES fileids (never dedup-drops a folder), dedupes repeats".
    [Fact]
    public void OneModIdAcrossFoldersMergesFileIdsAndDedupesRepeats() =>
        Assert.Equal(new[] { 533265, 533266 }, Grouped.map[126608].fileIds);

    // Probe: "group: keeps the FIRST non-empty installed version".
    [Fact]
    public void GroupingKeepsTheFirstInstalledVersion() => Assert.Equal("1.3", Grouped.map[126608].installed);
}
