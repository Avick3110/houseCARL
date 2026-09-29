using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Donors whose .STRINGS live in the game-Data folder keep their FULL+DESC through a merge, in both shapes,
/// and a donor whose strings resolve nowhere is refused (the former merge-service-guard arms LOCALIZED, #362, and
/// NOWHERE, #371).</summary>
[Trait("tier", "integration")]
public sealed class MergeServiceLocalizedTests : IClassFixture<MergeServiceLocalizedWorld>
{
    readonly MergeServiceLocalizedWorld _w;
    public MergeServiceLocalizedTests(MergeServiceLocalizedWorld w) => _w = w;

    static LocalizedStringsFixture.Spec A => MergeServiceLocalizedWorld.LocA;
    static LocalizedStringsFixture.Spec B => MergeServiceLocalizedWorld.LocB;

    WritePatchBuilder.MergeOutcome Merge(string output, params LocalizedStringsFixture.Spec[] donors)
    {
        var o = _w.Svc.MergePlugins(donors.Select(d => d.Key.FileName.String).ToArray(), output);
        Assert.True(o.Success, o.Error);
        return o;
    }

    // LOCALIZED fixture: the donor read with the BARE overlay is blank
    [Fact]
    public void TheDonorReadWithTheBareOverlayIsBlank()
    {
        var (name, desc) = LocalizedStringsFixture.ReadBackBare(
            Path.Combine(_w.Built.Mods, A.ModFolder, A.Key.FileName.String), LocalizedStringsFixture.WeaponEdid(A));
        Assert.True(string.IsNullOrEmpty(name), name);
        Assert.True(string.IsNullOrEmpty(desc), desc);
    }

    // LOCALIZED single-donor merge (RENAME) carries FULL+DESC
    [Fact]
    public void ASingleDonorRenameCarriesNameAndDescription()
    {
        var o = Merge("HcMgLocRen.esp", A);
        Assert.Equal((A.Name, A.Desc), LocalizedStringsFixture.ReadBackBare(o.OutputPath, LocalizedStringsFixture.WeaponEdid(A)));
    }

    // LOCALIZED multi-donor merge carries FULL+DESC for EVERY donor
    [Fact]
    public void AMultiDonorMergeCarriesNameAndDescriptionForEveryDonor()
    {
        var o = Merge("HcMgLocComb.esp", A, B);
        Assert.Equal((A.Name, A.Desc), LocalizedStringsFixture.ReadBackBare(o.OutputPath, LocalizedStringsFixture.WeaponEdid(A)));
        Assert.Equal((B.Name, B.Desc), LocalizedStringsFixture.ReadBackBare(o.OutputPath, LocalizedStringsFixture.WeaponEdid(B)));
    }

    // LOCALIZED output is written NON-localized with strings inline
    // LOCALIZED de-localization is STATED in the merge report, naming the donor
    [Fact]
    public void TheOutputIsWrittenNonLocalizedAndTheReportSaysSoNamingTheDonor()
    {
        var o = Merge("HcMgLocFlag.esp", A);
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
            Assert.False(ov.UsingLocalization);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(o.OutputPath)!, "Strings")));

        var r = WriteTools.RenderMerge(o);
        Assert.Contains("flagged LOCALIZED", r);
        Assert.Contains(A.Key.FileName.String, r);
        Assert.Contains("is NOT localized", r);
    }

    // NOWHERE-resolving strings REFUSE the merge, named, nothing written
    [Fact]
    public void StringsResolvingNowhereRefuseTheMergeNamedWithNothingWritten()
    {
        var o = _w.GoneSvc.MergePlugins(new[] { MergeServiceLocalizedWorld.LocGone.Key.FileName.String }, "HcMgResRen.esp");
        Assert.False(o.Success);
        Assert.Contains("LOCALIZED", o.Error);
        Assert.Contains(".STRINGS", o.Error);
        Assert.True(string.IsNullOrEmpty(o.OutputPath) || !File.Exists(o.OutputPath), o.OutputPath);
    }
}

/// <summary>Two instances: one whose two localized donors keep their strings in game-Data (with a game-Data
/// Skyrim.esm, which the resolver needs to find DataDir at all), and one whose donor's strings are nowhere.</summary>
public sealed class MergeServiceLocalizedWorld : IDisposable
{
    internal static readonly LocalizedStringsFixture.Spec LocA = new("LocA", new ModKey("HcMgLocA", ModType.Plugin), "LOC A NAME", "LOC A DESC");
    internal static readonly LocalizedStringsFixture.Spec LocB = new("LocB", new ModKey("HcMgLocB", ModType.Plugin), "LOC B NAME", "LOC B DESC");
    internal static readonly LocalizedStringsFixture.Spec LocGone = new("LocGone", new ModKey("HcMgLocGone", ModType.Plugin),
        "GONE NAME", "GONE DESC", StringsNowhere: true);

    public string Root { get; }
    internal LocalizedStringsFixture.Built Built { get; }
    public LoadOrderService Svc { get; }
    public LoadOrderService GoneSvc { get; }

    public MergeServiceLocalizedWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-service-loc-" + Guid.NewGuid().ToString("N"));
        var loc = Path.Combine(Root, "loc");
        Built = LocalizedStringsFixture.Build(loc, new[] { LocA, LocB });
        Svc = LoadOrderService.WithInstance(Built.Instance, 0, new UserConfigStore(Path.Combine(loc, "houseCARL.user.json")));
        Svc.Stats();

        var gone = Path.Combine(Root, "gone");
        var goneBuilt = LocalizedStringsFixture.Build(gone, new[] { LocGone });
        GoneSvc = LoadOrderService.WithInstance(goneBuilt.Instance, 0, new UserConfigStore(Path.Combine(gone, "houseCARL.user.json")));
        GoneSvc.Stats();
    }

    public void Dispose()
    {
        Svc.Dispose();
        GoneSvc.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
