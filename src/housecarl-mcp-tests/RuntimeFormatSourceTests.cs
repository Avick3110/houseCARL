using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The runtime-format source drives both the server's SkyPatcher file rules and the skill paragraphs printed from it; the committed skill text matches a fresh print.</summary>
[Trait("tier", "unit")]
public sealed class RuntimeFormatSourceTests
{
    const string Remedy = "regenerate and commit the result: dotnet run --project src/housecarl-generator -c Release -- skill-text";

    static readonly string Shipped = RuntimeFormats.SourceText().Replace("\r\n", "\n");

    /// <summary>The shipped source with one exact substring replaced.</summary>
    static SkyPatcherFormat With(string from, string to)
    {
        Assert.Contains(from, Shipped);
        return RuntimeFormats.SkyPatcherFrom(Shipped.Replace(from, to));
    }

    static string SkillFile(string rel) => Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills", rel);

    // every committed region is what the generator prints now, and every printed region has a home
    [Fact]
    public void TheCommittedSkillRegionsMatchAFreshPrint()
    {
        var regions = RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher);
        foreach (var rel in RuntimeFormatSkillText.Files)
        {
            var text = File.ReadAllText(SkillFile(rel));
            Assert.True(RuntimeFormatSkillText.Rewrite(text, regions, rel) == text, $"{rel}: a generated region differs from a fresh print. {Remedy}");
        }
        var placed = regions.Keys.Where(t => RuntimeFormatSkillText.Files.Any(f => File.ReadAllText(SkillFile(f)).Contains(RuntimeFormatSkillText.Begin(t)))).ToList();
        Assert.Equal(regions.Keys.OrderBy(k => k), placed.OrderBy(k => k));
    }

    // a printed paragraph speaks for SkyPatcher only: it names no tool of this server
    [Fact]
    public void NoPrintedRegionNamesTheServer()
        => Assert.All(RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher),
            r => Assert.DoesNotContain("housecarl", r.Value, StringComparison.OrdinalIgnoreCase));

    // a hand edit inside a region is put back by the print
    [Fact]
    public void AHandEditInsideARegionIsRewritten()
    {
        var rel = "skypatcher-authoring/references/grammar-core.md";
        var text = File.ReadAllText(SkillFile(rel));
        var edited = text.Replace("Magic Effect,", "Weapon, Magic Effect,");
        Assert.NotEqual(text, edited);
        Assert.Equal(text, RuntimeFormatSkillText.Rewrite(edited, RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher), rel));
    }

    // an opened region the generator has no text for, or one never closed, is refused
    [Fact]
    public void AnUnknownOrUnclosedRegionIsRefused()
    {
        var regions = RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher);
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite(
            RuntimeFormatSkillText.Begin("skypatcher.nope") + "\nx\n" + RuntimeFormatSkillText.End("skypatcher.nope"), regions, "t"));
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite(
            RuntimeFormatSkillText.Begin("skypatcher.passes") + "\nx\n", regions, "t"));
    }

    // a pass moved in the source moves the server's folder order and the printed run order
    [Fact]
    public void MovingAPassChangesTheFolderOrderAndTheSkillText()
    {
        const string weapon = """{ "folder": "weapon", "name": "Weapon" },""", armor = """{ "folder": "armor", "name": "Armor" },""";
        var swapped = With(weapon + "\n    " + armor, armor + "\n    " + weapon);
        Assert.True(SkyPatcherDiscovery.FolderOrder().Compare("weapon", "armor") < 0);
        Assert.True(SkyPatcherDiscovery.FolderOrder(swapped).Compare("weapon", "armor") > 0);
        Assert.Contains("Ammo, Weapon, Armor,", RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher)["skypatcher.passes"]);
        Assert.Contains("Ammo, Armor, Weapon,", RuntimeFormatSkillText.Regions(swapped)["skypatcher.passes"]);
    }

    // a patcher added to onWithoutKey runs without its switch, and the printed switch rule names its key
    [Fact]
    public void APatcherOnWithoutItsKeyRunsAndTheSkillTextNamesIt()
    {
        var withBook = With("\"onWithoutKey\": [\"light\"]", "\"onWithoutKey\": [\"light\", \"book\"]");
        var none = new SkyPatcherDiscovery.Switches(new Dictionary<string, bool>(), null);
        Assert.False(SkyPatcherDiscovery.PatcherRuns(none, "book"));
        Assert.True(SkyPatcherDiscovery.PatcherRuns(none, "book", withBook));
        Assert.True(SkyPatcherDiscovery.PatcherRuns(none, "LIGHT"));
        Assert.DoesNotContain("iEnableBookPatching", RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher)["skypatcher.switches"]);
        Assert.Contains("`iEnableLightPatching` and `iEnableBookPatching` are on", RuntimeFormatSkillText.Regions(withBook)["skypatcher.switches"]);
    }

    // a changed name token changes which files are read and every printed name rule
    [Fact]
    public void ChangingTheNameTokenChangesWhatIsReadAndTheSkillText()
    {
        var cfg = With("\"nameToken\": \".ini\"", "\"nameToken\": \".cfg\"");
        Assert.True(SkyPatcherDiscovery.HasIniName("a.ini"));
        Assert.False(SkyPatcherDiscovery.HasIniName("a.cfg"));
        Assert.True(SkyPatcherDiscovery.HasIniName("a.cfg", cfg));
        var printed = RuntimeFormatSkillText.Regions(cfg);
        Assert.Contains("`bandits.cfg.bak`", printed["skypatcher.names"]);
        Assert.Contains("a name ending `.CFG` is not", printed["skypatcher.place"]);
    }

    // a changed root moves the printed folder tree and placement path, and a switches file not beside it is refused
    [Fact]
    public void ChangingTheRootChangesTheTreeAndThePlacementPath()
    {
        var moved = With("\"root\": \"SKSE/Plugins/SkyPatcher\"", "\"root\": \"SKSE/Patcher\"");
        Assert.Equal("SKSE\\Patcher", moved.Root);
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Regions(moved));
        var both = RuntimeFormats.SkyPatcherFrom(Shipped
            .Replace("\"root\": \"SKSE/Plugins/SkyPatcher\"", "\"root\": \"SKSE/Patcher\"")
            .Replace("\"file\": \"SKSE/Plugins/SkyPatcher.ini\"", "\"file\": \"SKSE/Patcher.ini\""));
        var printed = RuntimeFormatSkillText.Regions(both);
        Assert.Contains("`Data/SKSE/Patcher/<subfolder>/<name>.ini`", printed["skypatcher.place"]);
        Assert.Contains("    ├── Patcher.ini", printed["skypatcher.tree"]);
        Assert.DoesNotContain("Plugins", printed["skypatcher.tree"]);
    }

    // a changed delimiter changes the printed grammar
    [Fact]
    public void ChangingADelimiterChangesTheSkillText()
    {
        var piped = With("\"segment\": \":\"", "\"segment\": \"!\"");
        Assert.Equal('!', piped.Grammar.Segment);
        Assert.Contains("- `!` separates every segment", RuntimeFormatSkillText.Regions(piped)["skypatcher.segments"]);
    }

    // the version the source names is the catalog's
    [Fact]
    public void TheSourceAndTheCatalogNameOneVersion()
        => Assert.Equal(SkyPatcherCatalog.Load().Version, RuntimeFormats.SkyPatcher.Version);

    // a value the server has no rule for fails the load, each by name
    [Theory]
    [InlineData("\"typeFolder\": \"firstComponent\"", "\"typeFolder\": \"anyComponent\"")]
    [InlineData("\"tokenMatch\": \"anywhere\"", "\"tokenMatch\": \"suffix\"")]
    [InlineData("\"endingCase\": \"lower\"", "\"endingCase\": \"any\"")]
    [InlineData("\"kind\": \"pluginStem\"", "\"kind\": \"none\"")]
    [InlineData("\"moreText\": \"neverRead\"", "\"moreText\": \"read\"")]
    [InlineData("\"rootFiles\": \"notRead\"", "\"rootFiles\": \"read\"")]
    [InlineData("\"looseOnly\": true", "\"looseOnly\": false")]
    [InlineData("\"missing\": \"off\"", "\"missing\": \"on\"")]
    [InlineData("\"value\": \"leadingInteger\"", "\"value\": \"boolean\"")]
    [InlineData("\"files\": \"breadthFirst\"", "\"files\": \"depthFirst\"")]
    [InlineData("\"compare\": \"ordinalIgnoreCase\"", "\"compare\": \"ordinal\"")]
    [InlineData("\"runs\": \"early\"", "\"runs\": \"late\"")]
    [InlineData("\"set\": \"lastReadWins\"", "\"set\": \"firstReadWins\"")]
    [InlineData("\"add\": \"accumulate\"", "\"add\": \"replace\"")]
    [InlineData("\"segment\": \":\"", "\"segment\": \"::\"")]
    public void AValueTheServerHasNoRuleForFailsTheLoad(string from, string to)
    {
        Assert.Contains(from, Shipped);
        var ex = Assert.Throws<InvalidOperationException>(() => RuntimeFormats.SkyPatcherFrom(Shipped.Replace(from, to)));
        Assert.Contains(from.Split(':')[0].Trim('"'), ex.Message);
    }
}
