using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ResolverArchives;

namespace HousecarlMcpTests;

/// <summary>asset_status through the real service over a synthetic instance: the winner among loose and archived
/// copies, a base archive as a provider, per-path errors, the archive-read and discovery caveats, an answer with no
/// record index, and a fresh answer after a mod is enabled or the instance switches.</summary>
[Trait("tier", "integration")]
public sealed class AssetStatusServiceTests : IDisposable
{
    readonly List<PlaceInstance> _instances = new();

    public void Dispose() { foreach (var p in _instances) p.Dispose(); }

    PlaceInstance NewInstance() { var p = new PlaceInstance(); _instances.Add(p); return p; }

    /// <summary>Archives only, no plugin file on disk: PluginA.bsa (A) and PluginB.bsa (B) by their plugins, a base
    /// archive holding B, and a loose copy of the FaceGen path in the highest-priority mod.</summary>
    LoadOrderService ArchivesAndALooseCopy()
    {
        var p = NewInstance();
        Write(p.Mod("ModA"), "PluginA.bsa", A());
        Write(p.Mod("ModB"), "PluginB.bsa", B());
        Write(p.Data, "Skyrim - Textures.bsa", B());
        Loose(p.Mod("LooseWins"), FacegenRel);
        p.Profile(new[] { "PluginA.esp", "PluginB.esp" }, new[] { "*PluginA.esp", "*PluginB.esp" },
            new[] { "+LooseWins", "+ModA", "+ModB" }, archiveList: "Skyrim - Textures.bsa");
        return p.Open();
    }

    // Probe: "a loose copy BEATS the BSA copy, contention flagged".
    [Fact]
    public void ALooseCopyBeatsTheArchiveCopy()
    {
        var hit = ArchivesAndALooseCopy().AssetStatus(new[] { FacegenRel }).Results[0].Hit!;

        Assert.True(hit.Exists);
        Assert.Equal(new AssetProvider("LooseWins", AssetKind.Loose), hit.Winner! with { OwningMod = null });
        Assert.True(hit.Ambiguous);
        Assert.Contains(hit.Providers, x => x.Kind == AssetKind.Bsa);
    }

    // Probe: "among BSAs the higher-rank plugin wins + the base archive is a discovered provider".
    [Fact]
    public void TheHigherRankPluginWinsAndTheBaseArchiveIsAProvider()
    {
        var hit = ArchivesAndALooseCopy().AssetStatus(new[] { RankRel }).Results[0].Hit!;

        Assert.Equal("PluginB.bsa", hit.Winner?.Source);
        Assert.Equal(AssetKind.Bsa, hit.Winner?.Kind);
        Assert.Equal(3, hit.Providers.Count);
        Assert.Contains(hit.Providers, x => x.Source == "Skyrim - Textures.bsa");
    }

    // Probe: "an unprovided path is ABSENT", "a drive-rooted path is a per-path error, not a batch failure" and
    // "a clean read reports no archive failures, not incomplete".
    [Fact]
    public void AnAbsentPathAndADriveRootedPathAnswerPerPathOnACleanRead()
    {
        var d = ArchivesAndALooseCopy().AssetStatus(new[] { @"meshes\nope\missing.nif", @"C:\Windows\evil.nif" });

        Assert.False(d.Results[0].Hit!.Exists);
        Assert.Null(d.Results[1].Hit);
        Assert.Contains("drive-rooted", d.Results[1].Error);
        Assert.Empty(d.BsaFailures);
        Assert.False(d.ReadIncomplete);
    }

    // Probe: "the record index can't build with no .esp on disk — yet AssetStatus above resolved: the asset path is
    // decoupled". Asset status answers first, then the index refuses, on one service.
    [Fact]
    public void AssetStatusAnswersWhereTheRecordIndexCannotBuild()
    {
        var svc = ArchivesAndALooseCopy();

        Assert.True(svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit!.Exists);
        Assert.ThrowsAny<Exception>(() => svc.Stats());
    }

    // Probe: "the new asset is absent before its mod is enabled" and "after enabling the mod, its asset resolves (the
    // asset resolver rebuilt on the membership change)".
    [Fact]
    public void ANewlyEnabledModsAssetIsSeenOnTheNextCall()
    {
        const string newRel = @"meshes\new\added.nif";
        var p = NewInstance();
        var modA = p.Mod("ModA");
        File.WriteAllText(Path.Combine(modA, "PluginA.esp"), "dummy");
        Write(modA, "PluginA.bsa", A());
        p.Profile(new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+ModA" });
        var svc = p.Open();
        Assert.False(svc.AssetStatus(new[] { newRel }).Results[0].Hit!.Exists);

        Loose(p.Mod("NewMod"), newRel);
        p.Profile(new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+NewMod", "+ModA" });
        p.TouchModlist();

        var after = svc.AssetStatus(new[] { newRel }).Results[0].Hit!;
        Assert.Equal("NewMod", after.Winner?.Source);
        Assert.Equal(AssetKind.Loose, after.Winner?.Kind);
    }

    // Probe: "a corrupt co-named BSA surfaces as a NAMED BsaFailure THROUGH THE SERVICE", "ReadIncomplete is true
    // through the service", "the missing-Skyrim.ini discovery warning surfaces through the service (.Warnings)" and
    // "the facegen path is ABSENT (its only archive was unreadable)".
    [Fact]
    public void ACorruptArchiveAndAMissingSkyrimIniSurfaceThroughTheService()
    {
        var p = NewInstance();
        Write(p.Mod("ModA"), "PluginA.bsa", Truncated());
        p.Profile(new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+ModA" });
        File.Delete(Path.Combine(p.Prof, "Skyrim.ini"));

        var d = p.Open().AssetStatus(new[] { FacegenRel });

        Assert.Contains(d.BsaFailures, f => f.Contains("PluginA"));
        Assert.True(d.ReadIncomplete);
        Assert.Contains(d.Warnings, w => w.Contains("Skyrim.ini", StringComparison.OrdinalIgnoreCase));
        Assert.False(d.Results[0].Hit!.Exists);
    }

    // Probe: "the tool rejects an empty asset_paths list (Q3)".
    [Fact]
    public void AnEmptyPathListIsRefused()
    {
        var svc = ArchivesAndALooseCopy();

        Assert.Contains("empty", AssetTools.AssetStatus(svc, asset_paths: Array.Empty<string>()), StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "the asset is present under instance 1" and "after SetInstance to instance 2, the asset answer follows
    // the NEW instance (the switch dropped the asset resolver)".
    [Fact]
    public void AnInstanceSwitchRepointsTheAssetAnswer()
    {
        const string switchRel = @"meshes\switch\only-in-1.nif";
        PlaceInstance Make(bool withAsset)
        {
            var p = NewInstance();
            var modA = p.Mod("ModA");
            File.WriteAllText(Path.Combine(modA, "PluginA.esp"), "dummy");
            if (withAsset) Loose(modA, switchRel);
            p.Profile(new[] { "PluginA.esp" }, new[] { "*PluginA.esp" }, new[] { "+ModA" });
            return p;
        }
        var one = Make(withAsset: true);
        var two = Make(withAsset: false);
        var svc = one.Open();
        Assert.True(svc.AssetStatus(new[] { switchRel }).Results[0].Hit!.Exists);

        svc.SetInstance(two.Inst);

        Assert.False(svc.AssetStatus(new[] { switchRel }).Results[0].Hit!.Exists);
    }
}
