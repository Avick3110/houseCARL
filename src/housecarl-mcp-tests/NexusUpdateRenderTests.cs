using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>Render.Updates</c>, the file-level update check's text: the withdrawn-file row, the summary counts read in
/// the order the groups print, and the <c>LiveMainCount</c> sentence. Migrated from the <c>nexus-file-check-guard</c>
/// probe; each test carries the probe arm's wording. No network, no key, no MO2 instance.</summary>
[Trait("tier", "unit")]
public sealed class NexusUpdateRenderTests
{
    static readonly NexusUpdateStatus Removed =
        NexusClient.ComputeStatus(600, true, "Withdrawn", "2", "1", new[] { 30 }, NexusFilePages.Removed());
    static readonly NexusUpdateStatus Retired =
        NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "1", new[] { 483794 }, NexusFilePages.AmonEnb());
    static readonly NexusUpdateStatus Gone =
        NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", null, new[] { 999999 }, NexusFilePages.AmonEnb());
    static readonly NexusUpdateStatus MultiMain =
        NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "2.0.0.0", Array.Empty<int>(), NexusFilePages.AmonEnb());
    static readonly NexusUpdateStatus Current =
        NexusClient.ComputeStatus(99786, true, "AMON ENB", "2.0.0.0", "2.0.0.0", new[] { 585300 }, NexusFilePages.AmonEnb());
    static readonly NexusUpdateStatus LatestOnly =
        NexusClient.ComputeStatus(3863, true, "Some Mod", "1.0", null, Array.Empty<int>(), NexusFilePages.AmonEnb());
    static readonly NexusUpdateStatus NotFound =
        NexusClient.ComputeStatus(1, false, null, null, "1.0", new[] { 123 }, NexusFilePages.Empty());

    static string Text(params NexusUpdateStatus[] results) => Render.Updates(results, NexusClient.SkyrimSe, Array.Empty<string>());

    // Probe K: "the render says the file was withdrawn and what to do — never 'current' beside a [REMOVED] category".
    [Fact]
    public void AWithdrawnFileRowSaysRemovedAndReadThePageNeverCurrent()
    {
        var text = Text(Removed);
        Assert.DoesNotContain("— current", text);
        Assert.Contains("[REMOVED]", text);
        Assert.Contains("REMOVED by the author", text);
        Assert.Contains("Read the page", text);
    }

    // Probe K: "the withdrawn mods get their own group and their own count in the summary line".
    [Fact]
    public void WithdrawnModsGetTheirOwnGroupAndCount()
    {
        var text = Text(Removed);
        Assert.Contains("FILE REMOVED —", text);
        Assert.Contains(" 1 file-removed ", text);
    }

    // Probe K: "the summary leads with file-removed, like the groups below it".
    [Fact]
    public void TheSummaryLeadsWithFileRemovedLikeTheGroups()
    {
        var text = Text(Retired, Removed);
        Assert.True(text.IndexOf(" file-removed ", StringComparison.Ordinal) < text.IndexOf(" outdated ", StringComparison.Ordinal));
        Assert.True(text.IndexOf("FILE REMOVED —", StringComparison.Ordinal) < text.IndexOf("OUTDATED —", StringComparison.Ordinal));
    }

    // Probe K: "every verdict's count reads in the same order as its group — the two halves never disagree".
    [Fact]
    public void EveryVerdictsCountReadsInTheSameOrderAsItsGroup()
    {
        var text = Text(Removed, Retired, Gone, MultiMain, Current, LatestOnly, NotFound);
        var counts = new[] { " file-removed ", " outdated ", " file-gone ", " no-fileid ", " current ", " latest-only ", " not-found" };
        var groups = new[] { "FILE REMOVED —", "OUTDATED —", "FILE GONE —", "NO FILEID —", "current —", "latest version ", "not found on " };
        var countAt = counts.Select(t => text.IndexOf(t, StringComparison.Ordinal)).ToArray();
        var groupAt = groups.Select(t => text.IndexOf(t, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, countAt);
        Assert.DoesNotContain(-1, groupAt);
        Assert.Equal(countAt.Order(), countAt);
        Assert.Equal(groupAt.Order(), groupAt);
    }

    // Not a probe assert; the docs pin line's "LiveMainCount sentence" for arm E: a multi-main page says how many mains it has.
    [Fact]
    public void AMultiMainNoFileIdRowSaysHowManyMainsThePageHas() =>
        Assert.Contains("3 current MAIN files", Text(MultiMain));

    // Not a probe assert; the docs pin line's "LiveMainCount sentence" for arm H: a single-main page is a version compare only.
    [Fact]
    public void ASingleMainNoFileIdRowIsAVersionCompareOnly() =>
        Assert.Contains("VERSION compare only",
            Text(NexusClient.ComputeStatus(266, true, "Solo Mod", "3.0", "3.0", Array.Empty<int>(), NexusFilePages.SingleMain())));

    // Not a probe assert; the retired row names the same-name replacement ComputeStatus found.
    [Fact]
    public void ARetiredRowNamesTheSameNameReplacement() =>
        Assert.Contains("newest 'Amon NAT III Esp Fix' v2", Text(Retired));
}
