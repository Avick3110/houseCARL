using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>PapyrusSourceRoots</c>, the modlist scan for Papyrus source folders (migrated from the <c>compile-ergonomics-guard</c>
/// probe, part G): both layouts, the <c>.psc</c> gate, the given precedence order, the dedup, and <c>SplitGameData</c>.
/// </summary>
[Trait("tier", "unit")]
public sealed class PapyrusSourceRootsDiscoverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-pyroots-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _seDir, _leDir, _emptyDir, _longExt, _stockData;

    public PapyrusSourceRootsDiscoverTests()
    {
        _seDir = Mk("SKSE", @"Source\Scripts", "Actor.psc", "Game.psc");
        _leDir = Mk("OldMod", @"Scripts\Source", "Legacy.psc");
        _emptyDir = Mk("PexOnly", @"Source\Scripts");
        _longExt = Mk("LongExt", @"Source\Scripts", "NotASource.pscx");
        _stockData = Mk("StockGameData", @"Source\Scripts", "Form.psc", "Quest.psc");
        Directory.CreateDirectory(Path.Combine(_root, "NoSources"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    string Mk(string mod, string layout, params string[] files)
    {
        var d = Path.Combine(_root, mod, layout);
        Directory.CreateDirectory(d);
        foreach (var f in files) File.WriteAllText(Path.Combine(d, f), "Scriptname X\n");
        return d;
    }

    (string, string) Root(string name, string? folder = null) => (name, Path.Combine(_root, folder ?? name));

    IReadOnlyList<PapyrusSourceRoot> DiscoverMixed() => PapyrusSourceRoots.Discover(new[]
    {
        Root("overwrite"), Root("SKSE"), Root("PexOnly"), Root("OldMod"), Root("LongExt"), Root("NoSources"),
    });

    // Probe G: "only the two roots that actually HOLD .psc are returned".
    [Fact]
    public void OnlyRootsHoldingPscAreReturned()
    {
        Assert.Equal(2, DiscoverMixed().Count);
    }

    // Probe G: "the SE layout (Source\Scripts) is found and tagged with its providing mod".
    [Fact]
    public void TheSeLayoutIsFoundAndTaggedWithItsMod()
    {
        var se = DiscoverMixed()[0];
        Assert.Equal((_seDir, "SKSE", @"Source\Scripts"), (se.Dir, se.Provider, se.Layout));
    }

    // Probe G: "the LE layout (Scripts\Source) is found too".
    [Fact]
    public void TheLeLayoutIsFound()
    {
        var le = DiscoverMixed()[1];
        Assert.Equal((_leDir, @"Scripts\Source"), (le.Dir, le.Layout));
    }

    // Probe G: "a source folder with NO .psc contributes nothing (would widen the path for free)".
    [Fact]
    public void AFolderWithNoPscContributesNothing()
    {
        Assert.DoesNotContain(DiscoverMixed(), f => f.Dir == _emptyDir);
    }

    // Probe G: "a folder holding only '.pscx' is NOT a source folder (shape-check — the 8.3 quirk this guards did not reproduce here)".
    [Fact]
    public void AFolderHoldingOnlyPscxIsNotASourceFolder()
    {
        Assert.False(PapyrusSourceRoots.HasSources(_longExt));
    }

    // Probe G: "the GIVEN root order is preserved — that order IS MO2 precedence".
    [Fact]
    public void TheGivenRootOrderIsKept()
    {
        Assert.Equal(new[] { "SKSE", "OldMod" }, DiscoverMixed().Select(f => f.Provider));
    }

    // Probe G: "the same folder twice → ONE entry, the higher-precedence root keeps it".
    [Fact]
    public void TheSameFolderTwiceIsOneEntryForTheFirstRoot()
    {
        var found = PapyrusSourceRoots.Discover(new[] { Root("SKSE"), Root("Clone", "SKSE") });
        Assert.Equal("SKSE", Assert.Single(found).Provider);
    }

    IReadOnlyList<PapyrusSourceRoot> WithData() =>
        PapyrusSourceRoots.Discover(new[] { Root("SKSE"), Root("Data", "StockGameData") });

    // Probe G: "control: the scan DOES find the game's own Data sources", "SplitGameData takes the game's Data sources out
    // of the MOD candidates by PATH", "…and takes nothing else", "…and HANDS IT BACK".
    [Fact]
    public void SplitGameDataTakesOutTheDataSourcesByPathAndHandsThemBack()
    {
        var withData = WithData();
        Assert.Contains(withData, r => r.Dir == _stockData);
        var (mods, gameData) = PapyrusSourceRoots.SplitGameData(withData, Path.Combine(_root, "StockGameData"));
        Assert.Equal(new[] { "SKSE" }, mods.Select(r => r.Provider));
        Assert.Equal(_stockData, gameData);
    }

    // Probe G: "a blank data dir splits NOTHING (Path.Combine would otherwise resolve the layouts against the process CWD)".
    [Fact]
    public void ABlankDataDirSplitsNothing()
    {
        var withData = WithData();
        var (mods, gameData) = PapyrusSourceRoots.SplitGameData(withData, null);
        Assert.Equal(withData.Count, mods.Count);
        Assert.Null(gameData);
    }

    // Probe G: "a data dir whose sources the scan never found yields no game-Data folder (no phantom vanilla path)".
    [Fact]
    public void ADataDirTheScanNeverFoundYieldsNoGameDataFolder()
    {
        var (mods, gameData) = PapyrusSourceRoots.SplitGameData(
            PapyrusSourceRoots.Discover(new[] { Root("SKSE") }), Path.Combine(_root, "StockGameData"));
        Assert.Single(mods);
        Assert.Null(gameData);
    }

    // Probe G: "an unusable root is skipped, never thrown on (a lost import dir must not cost the compile)".
    [Fact]
    public void AnUnusableRootIsSkipped()
    {
        var found = PapyrusSourceRoots.Discover(new[] { ("bad", "\0not a path"), Root("gone", "nope"), ("blank", ""), Root("SKSE") });
        Assert.Equal("SKSE", Assert.Single(found).Provider);
    }
}
