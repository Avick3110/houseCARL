using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>NexusClient.ComputeStatus</c>, the file-level currency verdict from the installed file ids and a canned
/// file page. Migrated from the <c>nexus-file-check-guard</c> probe; each test carries the probe arm's wording. No
/// network, no key, no MO2 instance.</summary>
[Trait("tier", "unit")]
public sealed class NexusFileVerdictTests
{
    static NexusUpdateStatus Amon(bool found, string? installed, params int[] fileIds) =>
        NexusClient.ComputeStatus(99786, found, found ? "AMON ENB" : null, "2.0.0.0", installed, fileIds, NexusFilePages.AmonEnb());

    // Probe A: "installed file in a LIVE category → Current (NOT compared to the v10 preset)", "the installed file is reported Live".
    [Fact]
    public void AnInstalledLiveMainIsCurrentNotComparedToTheNewestPreset()
    {
        var s = Amon(true, "2.0.0.0", 585300);
        Assert.Equal(UpdateVerdict.Current, s.Verdict);
        Assert.Equal(FileVerdict.Live, Assert.Single(s.Files).Verdict);
    }

    // Probe B: "installed file in ARCHIVED → Outdated", "the installed file is reported Superseded".
    [Fact]
    public void AnInstalledArchivedFileIsOutdatedAndSuperseded()
    {
        var s = Amon(true, "1", 483794);
        Assert.Equal(UpdateVerdict.Outdated, s.Verdict);
        Assert.Equal(FileVerdict.Superseded, Assert.Single(s.Files).Verdict);
    }

    // Probe B: "points to the newest SAME-NAME live file (Esp Fix v2), not the v10 preset".
    [Fact]
    public void ARetiredFilePointsToTheNewestSameNameLiveFile()
    {
        var f = Assert.Single(Amon(true, "1", 483794).Files);
        Assert.Equal("Amon NAT III Esp Fix", f.NewestSameName);
        Assert.Equal("2", f.NewestSameVersion);
    }

    // Probe C: "installed fileid absent from the file list → FileGone (loud)", "the installed file is reported Missing".
    [Fact]
    public void AnInstalledFileIdAbsentFromThePageIsFileGone()
    {
        var s = Amon(true, null, 999999);
        Assert.Equal(UpdateVerdict.FileGone, s.Verdict);
        Assert.Equal(FileVerdict.Missing, Assert.Single(s.Files).Verdict);
    }

    // Probe D: "multi-file, ≥1 retired → Outdated headline", "both installed files detailed".
    [Fact]
    public void ManyInstalledFilesWithOneRetiredAreOutdatedAndBothDetailed()
    {
        var s = Amon(true, null, 585300, 483794);
        Assert.Equal(UpdateVerdict.Outdated, s.Verdict);
        Assert.Equal(2, s.Files.Count);
    }

    // Probe D2: "multi-file, all live → Current".
    [Fact]
    public void ManyInstalledFilesAllLiveAreCurrent() =>
        Assert.Equal(UpdateVerdict.Current, Amon(true, null, 585300, 775265).Verdict);

    // Probe E: "no fileid + version → NoFileId (loud, never a confident mod-level verdict)", "LiveMainCount counts
    // every live MAIN (3) — the multi-main ambiguity signal".
    [Fact]
    public void NoFileIdWithAVersionOnAMultiMainPageIsNoFileIdCountingEveryMain()
    {
        var s = Amon(true, "2.0.0.0");
        Assert.Equal(UpdateVerdict.NoFileId, s.Verdict);
        Assert.Equal(3, s.LiveMainCount);
    }

    // Probe F: "bare id (no version, no fileid) → LatestOnly".
    [Fact]
    public void ABareIdIsLatestOnly() =>
        Assert.Equal(UpdateVerdict.LatestOnly,
            NexusClient.ComputeStatus(3863, true, "Some Mod", "1.0", null, Array.Empty<int>(), NexusFilePages.AmonEnb()).Verdict);

    // Probe G: "not in search AND no files returned → NotFound (genuinely gone/wrong-id)".
    [Fact]
    public void NotInTheSearchAndNoFilesIsNotFound() =>
        Assert.Equal(UpdateVerdict.NotFound,
            NexusClient.ComputeStatus(1, false, null, null, "1.0", new[] { 123 }, NexusFilePages.Empty()).Verdict);

    // Probe J: "found=false but modFiles returned files → check them (Current), NOT NotFound (nxm-only)", "a mod
    // resolved via its file list is reported Found — it exists (a null friendly name is fine)".
    [Fact]
    public void ASearchAbsentModWithFilesIsCheckedFromThemAndReportedFound()
    {
        var s = Amon(false, null, 585300);
        Assert.Equal(UpdateVerdict.Current, s.Verdict);
        Assert.True(s.Found);
    }

    // Probe J2: "found=false + retired installed file → Outdated (checked, not NotFound)".
    [Fact]
    public void ASearchAbsentModWithARetiredInstalledFileIsOutdated() =>
        Assert.Equal(UpdateVerdict.Outdated, Amon(false, "1", 483794).Verdict);

    // Probe J3: "found=false, files present, no fileid → NoFileId fallback (exists), not NotFound".
    [Fact]
    public void ASearchAbsentModWithFilesAndNoFileIdIsNoFileId()
    {
        var s = Amon(false, "2.0.0.0");
        Assert.Equal(UpdateVerdict.NoFileId, s.Verdict);
        Assert.True(s.Found);
    }

    // Probe J4: "found=false + files present + installed fileid absent → FileGone (loud), not NotFound".
    [Fact]
    public void ASearchAbsentModWhoseInstalledFileIsGoneIsFileGone() =>
        Assert.Equal(UpdateVerdict.FileGone, Amon(false, "1.0", 999999).Verdict);

    // Probe H: "no fileid, single live MAIN → NoFileId + LiveMainCount 1 + newest MAIN surfaced".
    [Fact]
    public void NoFileIdOnASingleMainPageSurfacesTheNewestMain()
    {
        var s = NexusClient.ComputeStatus(266, true, "Solo Mod", "3.0", "3.0", Array.Empty<int>(), NexusFilePages.SingleMain());
        Assert.Equal(UpdateVerdict.NoFileId, s.Verdict);
        Assert.Equal(1, s.LiveMainCount);
        Assert.Equal("3.1", s.LatestMainVersion);
    }

    // Probe I: "unknown category → treated Live, category carried through (not mis-retired)".
    [Fact]
    public void AnUnknownCategoryIsLiveAndCarriedThrough()
    {
        var page = new List<(int, string, string?, string, long)> { (20, "New", "1", "PENDING_REVIEW", 100L) };
        var s = NexusClient.ComputeStatus(500, true, "New Cat", "1", null, new[] { 20 }, page);
        Assert.Equal(UpdateVerdict.Current, s.Verdict);
        var f = Assert.Single(s.Files);
        Assert.Equal(FileVerdict.Live, f.Verdict);
        Assert.Equal("PENDING_REVIEW", f.Category);
    }

    // Probe K: "an installed REMOVED file → FileRemoved, never Live".
    [Fact]
    public void AnInstalledRemovedFileIsFileRemoved()
    {
        var s = NexusClient.ComputeStatus(600, true, "Withdrawn", "2", "1", new[] { 30 }, NexusFilePages.Removed());
        Assert.Equal(UpdateVerdict.FileRemoved, s.Verdict);
        var f = Assert.Single(s.Files);
        Assert.Equal(FileVerdict.Removed, f.Verdict);
        Assert.Equal("REMOVED", f.Category);
    }

    // Probe K: "DELETED is the same withdrawal bucket as REMOVED".
    [Fact]
    public void ADeletedFileIsTheSameWithdrawalBucket() =>
        Assert.Equal(UpdateVerdict.FileRemoved,
            NexusClient.ComputeStatus(601, true, "Withdrawn Two", "2", "1", new[] { 41 }, NexusFilePages.Deleted()).Verdict);

    // Probe K: "a retired file is NOT pointed at a withdrawn same-name file as its replacement".
    [Fact]
    public void ARetiredFileIsNotPointedAtAWithdrawnSameNameFile()
    {
        var s = NexusClient.ComputeStatus(601, true, "Withdrawn Two", "2", "1", new[] { 40 }, NexusFilePages.Deleted());
        Assert.Equal(UpdateVerdict.Outdated, s.Verdict);
        Assert.Null(Assert.Single(s.Files).NewestSameName);
    }

    // The withdrawn-over-retired order (docs/architecture/nexus.md): one removed and one retired installed file lead with FileRemoved.
    [Fact]
    public void AWithdrawnFileOutranksARetiredOne()
    {
        var page = NexusFilePages.Removed();
        page.Add((32, "Other Patch", "1", "ARCHIVED", 150L));
        Assert.Equal(UpdateVerdict.FileRemoved,
            NexusClient.ComputeStatus(600, true, "Withdrawn", null, null, new[] { 32, 30 }, page).Verdict);
    }
}
