using HousecarlMcp;
using Xunit;
using Xunit.Abstractions;
using static HousecarlMcpTests.PlaceInstance;

namespace HousecarlMcpTests;

/// <summary>The place service over a synthetic MO2 instance: an explicit source placed over a wrong winner wins the
/// VFS once enabled, an archive source places its entry, a lone provider auto-resolves and two or none are refused,
/// the fresh folder is removed when nothing placed and kept when something did, and an overwrite through into= writes
/// the new bytes crash-atomically. Every test builds its own instance.</summary>
[Trait("tier", "integration")]
public sealed class PlaceServiceLaneTests
{
    readonly ITestOutputHelper _out;
    public PlaceServiceLaneTests(ITestOutputHelper output) => _out = output;

    // Probe D: "before placing, the wrong loose copy wins the VFS", "the asset placed into a fresh houseCARL mod
    // folder", "the placement reports the CURRENT winner to sort above", "the placed file holds the SOURCE bytes
    // byte-exact", "originals untouched", "after enabling the placed mod on top, IT wins the VFS".
    [Fact]
    public void AnExplicitSourcePlacedOverAWrongWinnerWinsTheVfsOnceEnabledOnTop()
    {
        using var p = new PlaceInstance();
        var wrong = p.Mod("WrongFace");
        var wrongBytes = new byte[] { 0xBA, 0xD0 };
        Loose(wrong, FacegenRel, wrongBytes);
        var correctBytes = new byte[] { 0x60, 0x0D, 0x60, 0x0D };
        var correctSrc = p.Scratch("correct-face.nif", correctBytes);
        p.ProfileWithDummy("WrongFace", "+WrongFace");
        var svc = p.Open();
        Assert.Equal("WrongFace", svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Winner?.Source);

        var outcome = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, correctSrc) }, patchName: null, into: null);

        var r0 = outcome.Results[0];
        Assert.True(r0.Placed, r0.Error);
        Assert.Equal("WrongFace (loose)", r0.CurrentWinner);
        var placed = PlacedAt(outcome, FacegenRel);
        Assert.NotNull(placed);
        Assert.Equal(correctBytes, File.ReadAllBytes(placed!));
        Assert.Equal(correctBytes, File.ReadAllBytes(correctSrc));
        Assert.Equal(wrongBytes, File.ReadAllBytes(Path.Combine(wrong, FacegenRel)));

        var placedMod = Path.GetFileName(outcome.ModFolder!);
        p.Profile(new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+" + placedMod, "+WrongFace" });
        p.TouchModlist();
        var after = svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Winner;
        Assert.Equal(placedMod, after?.Source);
        Assert.Equal(AssetKind.Loose, after?.Kind);
    }

    // Probe E: "a .bsa source places (entry derived from the destination)", "the placed bytes equal the
    // natively-extracted BSA entry, byte-exact", and "a QUOTED .bsa source extracts the ENTRY (placed bytes == the
    // entry, NOT the whole archive read as loose)".
    [Fact]
    public void AnArchiveSourcePlacesItsEntryForTheDestinationQuotedOrNot()
    {
        using var p = new PlaceInstance();
        p.Profile(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var bsa = p.Scratch("Source Archive.bsa", FaceArchive());
        var svc = p.Open();

        foreach (var source in new[] { bsa, "\"" + bsa + "\"" })
        {
            var outcome = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, source) }, null, null);
            Assert.True(outcome.Results[0].Placed, outcome.Results[0].Error);
            Assert.Equal(ArchiveFaceBytes, File.ReadAllBytes(PlacedAt(outcome, FacegenRel)!));
        }
    }

    // Probe F: "a SOLE provider auto-resolves with no source=".
    [Fact]
    public void ASoleProviderAutoResolvesWithNoSource()
    {
        using var p = new PlaceInstance();
        var b = new byte[] { 1, 1, 1 };
        Loose(p.Mod("OnlyMod"), FacegenRel, b);
        p.ProfileWithDummy("OnlyMod", "+OnlyMod");

        var o = p.Open().PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(b, File.ReadAllBytes(PlacedAt(o, FacegenRel)!));
    }

    // Probe F: "TWO providers + no source= is REFUSED (no guess)", "the refusal NAMES both providers", and "the
    // refusal leaks NO on-disk path".
    [Fact]
    public void TwoProvidersWithNoSourceAreRefusedByNameWithNoGuessAndNoPath()
    {
        using var p = new PlaceInstance();
        foreach (var m in new[] { "ModA", "ModB" }) Loose(p.Mod(m), FacegenRel, new byte[] { 2 });
        p.ProfileWithDummy("ModA", "+ModA", "+ModB");

        var o = p.Open().PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null);

        var r = o.Results[0];
        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceWillNotGuess, r.Error);
        Assert.Contains("ModA", r.Error);
        Assert.Contains("ModB", r.Error);
        Assert.DoesNotContain(p.Mods, r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(o.ModFolder);
    }

    // Probe F: "NO provider + no source= is REFUSED with guidance".
    [Fact]
    public void NoProviderWithNoSourceIsRefused()
    {
        using var p = new PlaceInstance();
        p.Profile(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var r = p.Open().PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains("no copy to auto-place", r.Error);
    }

    // Probe G: "an all-failed batch reports no mod folder" and "a fresh folder with NOTHING placed is removed — no
    // orphan". Strengthened: the folder is shown to have been created by the same call shape first, so its absence
    // afterwards is the cleanup and not a folder that was never made.
    [Fact]
    public void AnAllFailedFreshBatchRemovesTheFolderItCreated()
    {
        using var p = new PlaceInstance();
        Loose(p.Mod("GMod"), FacegenRel, new byte[] { 5, 5 });
        p.ProfileWithDummy("GMod", "+GMod");
        var svc = p.Open();
        var made = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, "GHostFolder", null);
        Assert.Equal(Path.Combine(p.Mods, "houseCARL - GHostFolder"), made.ModFolder);

        var allFail = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\absent\x.nif", null) }, "GHostFolder2", null);

        Assert.Null(allFail.ModFolder);
        Assert.False(Directory.Exists(Path.Combine(p.Mods, "houseCARL - GHostFolder2")));
        Assert.Null(allFail.LeftoverFolder);
    }

    // The same cleanup never reaches an into= folder the caller already owns: an all-failed batch into it leaves it
    // and what it holds.
    [Fact]
    public void AnAllFailedBatchIntoAnExistingFolderLeavesThatFolder()
    {
        using var p = new PlaceInstance();
        Loose(p.Mod("GMod"), FacegenRel, new byte[] { 5, 5 });
        p.ProfileWithDummy("GMod", "+GMod");
        var svc = p.Open();
        var first = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, null) }, "Owned", null);
        Assert.NotNull(first.ModFolder);

        var allFail = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\absent\x.nif", null) }, null, "Owned");

        Assert.False(allFail.Results[0].Placed);
        Assert.True(File.Exists(Path.Combine(first.ModFolder!, FacegenRel)));
    }

    // Probe G: "a PARTIAL batch keeps the folder with the good file present".
    [Fact]
    public void APartialBatchKeepsTheFolderWithTheGoodFile()
    {
        using var p = new PlaceInstance();
        Loose(p.Mod("GMod"), FacegenRel, new byte[] { 5, 5 });
        p.ProfileWithDummy("GMod", "+GMod");

        var partial = p.Open().PlaceAssets(new[]
        {
            new PlaceRequest(FacegenRel, null),
            new PlaceRequest(@"meshes\absent\y.nif", null),
        }, "GKeepFolder", null);

        Assert.NotNull(partial.ModFolder);
        Assert.True(File.Exists(Path.Combine(partial.ModFolder!, FacegenRel)));
        Assert.False(partial.Results[1].Placed);
    }

    // Probe G: "a drive-rooted destination is a per-asset named error" and "a '..'-escaping destination is rejected".
    [Fact]
    public void ADriveRootedOrEscapingDestinationIsANamedPerAssetError()
    {
        using var p = new PlaceInstance();
        Loose(p.Mod("GMod"), FacegenRel, new byte[] { 5, 5 });
        p.ProfileWithDummy("GMod", "+GMod");
        var svc = p.Open();

        var bad = svc.PlaceAssets(new[] { new PlaceRequest(@"C:\Windows\evil.nif", @"C:\x") }, null, null).Results[0];
        var esc = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\..\..\evil.nif", FacegenRel) }, null, null).Results[0];

        Assert.False(bad.Placed);
        Assert.Contains("drive-rooted", bad.Error);
        Assert.False(esc.Placed);
        Assert.Contains("parent-escaping", esc.Error);
    }

    // Probe H: "first place into a fresh folder succeeds", "second place into= the existing folder succeeds",
    // "overwrite via the SERVICE yields the NEW bytes byte-exact, not the stale prior", and "the service place
    // preserves the dest creation time — routes through AtomicFile (File.Replace), not File.Move" (stops on a
    // tunneling host, as the probe did).
    [Fact]
    public void ASecondPlaceIntoTheSameFolderWritesTheNewBytesAndKeepsTheCreationTime()
    {
        using var p = new PlaceInstance();
        p.Profile(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var svc = p.Open();
        var srcV1 = p.Scratch("v1.nif", new byte[] { 1, 1, 1 });
        var v2 = new byte[] { 2, 2, 2, 2, 2, 2 };
        var srcV2 = p.Scratch("v2.nif", v2);

        var first = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, srcV1) }, "RouteProv", null);
        Assert.True(first.Results[0].Placed, first.Results[0].Error);
        var dest = Path.Combine(first.ModFolder!, FacegenRel);
        var oldCreate = new DateTime(2019, 6, 6, 0, 0, 0, DateTimeKind.Utc);
        bool tunnelingMasks = PlaceCoreTests.TunnelingMasksCreationTime(first.ModFolder!, oldCreate);
        File.SetCreationTimeUtc(dest, oldCreate);

        var second = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, srcV2) }, null, "RouteProv");

        Assert.True(second.Results[0].Placed, second.Results[0].Error);
        Assert.Equal(v2, File.ReadAllBytes(dest));
        if (tunnelingMasks)
        {
            _out.WriteLine("Skipped the creation-time half: file system tunneling keeps it through File.Move on this host.");
            return;
        }
        Assert.Equal(oldCreate, File.GetCreationTimeUtc(dest));
    }
}
