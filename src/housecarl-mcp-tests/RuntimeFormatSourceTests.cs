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

    static readonly IReadOnlyDictionary<string, string> ShippedRegions = RuntimeFormatSkillText.Regions(RuntimeFormats.SkyPatcher);

    /// <summary>The shipped source with exact substrings replaced, each of which must be there.</summary>
    static SkyPatcherFormat With(params (string From, string To)[] edits)
    {
        var json = Shipped;
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, json);
            json = json.Replace(from, to);
        }
        return RuntimeFormats.SkyPatcherFrom(json);
    }

    static SkyPatcherFormat With(string from, string to) => With((from, to));

    static string SkillFile(string rel) => Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills", rel);

    // every committed region is what the generator prints now, every printed region has a home, and no skill line says to regenerate
    [Fact]
    public void TheCommittedSkillRegionsMatchAFreshPrint()
    {
        var texts = RuntimeFormatSkillText.Files.ToDictionary(rel => rel, rel => File.ReadAllText(SkillFile(rel)));
        foreach (var (rel, text) in texts)
            Assert.True(RuntimeFormatSkillText.Rewrite(text, ShippedRegions, rel) == text, $"{rel}: a generated region differs from a fresh print. {Remedy}");
        Assert.Empty(RuntimeFormatSkillText.Unplaced(ShippedRegions, texts.Values));
        Assert.All(texts, t => Assert.DoesNotContain("regenerate", t.Value));
    }

    // a printed paragraph speaks for SkyPatcher only: it names no tool of this server
    [Fact]
    public void NoPrintedRegionNamesTheServer()
        => Assert.All(ShippedRegions,
            r => Assert.DoesNotContain("housecarl", r.Value, StringComparison.OrdinalIgnoreCase));

    // a hand edit inside a region is put back by the print
    [Fact]
    public void AHandEditInsideARegionIsRewritten()
    {
        var rel = "skypatcher-authoring/references/grammar-core.md";
        var text = File.ReadAllText(SkillFile(rel));
        var edited = text.Replace("Magic Effect,", "Weapon, Magic Effect,");
        Assert.NotEqual(text, edited);
        Assert.Equal(text, RuntimeFormatSkillText.Rewrite(edited, ShippedRegions, rel));
    }

    // an opened region the generator has no text for, or one never closed, is refused
    [Fact]
    public void AnUnknownOrUnclosedRegionIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite(
            RuntimeFormatSkillText.Begin("skypatcher.nope") + "\nx\n" + RuntimeFormatSkillText.End("skypatcher.nope"), ShippedRegions, "t"));
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite(
            RuntimeFormatSkillText.Begin("skypatcher.passes") + "\nx\n", ShippedRegions, "t"));
    }

    // a Begin marker that is not the whole line opens no region: it is reported unplaced and the rewrite refuses it
    [Fact]
    public void AnIndentedOrMalformedMarkerIsUnplacedAndRefused()
    {
        var indented = "  " + RuntimeFormatSkillText.Begin("skypatcher.passes") + "\nx\n" + RuntimeFormatSkillText.End("skypatcher.passes");
        Assert.Contains("skypatcher.passes", RuntimeFormatSkillText.Unplaced(ShippedRegions, new[] { indented }));
        Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite(indented, ShippedRegions, "t"));
        var ex = Assert.Throws<InvalidOperationException>(() => RuntimeFormatSkillText.Rewrite("<!-- generated:skypatcher.passes-->\nx", ShippedRegions, "t"));
        Assert.Contains("t: line 1", ex.Message);
    }

    // a pass moved in the source moves the server's folder order and the printed run order
    [Fact]
    public void MovingAPassChangesTheFolderOrderAndTheSkillText()
    {
        const string weapon = """{ "folder": "weapon", "name": "Weapon" },""", armor = """{ "folder": "armor", "name": "Armor" },""";
        var swapped = With(weapon + "\n    " + armor, armor + "\n    " + weapon);
        Assert.True(SkyPatcherDiscovery.FolderOrder().Compare("weapon", "armor") < 0);
        Assert.True(SkyPatcherDiscovery.FolderOrder(swapped).Compare("weapon", "armor") > 0);
        Assert.Contains("Ammo, Weapon, Armor,", ShippedRegions["skypatcher.passes"]);
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
        Assert.DoesNotContain("iEnableBookPatching", ShippedRegions["skypatcher.switches"]);
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
        var both = With(("\"root\": \"SKSE/Plugins/SkyPatcher\"", "\"root\": \"SKSE/Patcher\""),
                        ("\"file\": \"SKSE/Plugins/SkyPatcher.ini\"", "\"file\": \"SKSE/Patcher.ini\""));
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

    // the switch-off reason and the draft's name refusal spell the row's key shape and name token
    [Fact]
    public void TheOffReasonAndTheDraftRefusalSpellTheRow()
    {
        var edited = With(("\"keyPrefix\": \"iEnable\"", "\"keyPrefix\": \"bUse\""), ("\"keySuffix\": \"Patching\"", "\"keySuffix\": \"Patcher\""),
                          ("\"nameToken\": \".ini\"", "\"nameToken\": \".cfg\""));
        var off = new SkyPatcherDiscovery.Switches(new Dictionary<string, bool> { ["npc"] = false }, null);
        var none = new SkyPatcherDiscovery.Switches(new Dictionary<string, bool>(), null);
        Assert.Equal("disables 'npc' patching (iEnable…Patching=0)", SkyPatcherDiscovery.SwitchOffReason(off, "npc"));
        Assert.Equal("disables 'npc' patching (bUse…Patcher=0)", SkyPatcherDiscovery.SwitchOffReason(off, "npc", edited));
        Assert.Equal("has no bUse…Patcher key for 'npc', which leaves that patcher off", SkyPatcherDiscovery.SwitchOffReason(none, "npc", edited));
        var dir = Path.GetTempPath();
        Assert.Contains("has no '.ini' in its name — SkyPatcher reads only files with '.ini' in the name",
            SkyPatcherDraft.Prepare(Path.Combine(dir, "a.cfg"), "weapon", SkyPatcherCatalog.Load(), out _));
        Assert.Contains("has no '.cfg' in its name — SkyPatcher reads only files with '.cfg' in the name",
            SkyPatcherDraft.Prepare(Path.Combine(dir, "a.ini"), "weapon", SkyPatcherCatalog.Load(), out _, edited));
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
        var ex = Assert.Throws<InvalidOperationException>(() => With(from, to));
        Assert.Contains(from.Split(':')[0].Trim('"'), ex.Message);
    }
}
