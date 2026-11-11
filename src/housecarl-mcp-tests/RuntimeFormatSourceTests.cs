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
        Assert.Null(SkyPatcherDiscovery.GatePluginOf("Skyrim.esm.cfg"));
        Assert.Equal("Skyrim.esm", SkyPatcherDiscovery.GatePluginOf("Skyrim.esm.cfg", cfg));
        Assert.Contains("ends in '.CFG'", SkyPatcherDiscovery.NotReadReason("a.CFG", _ => true, cfg));
        var printed = RuntimeFormatSkillText.Regions(cfg);
        Assert.Contains("`bandits.cfg.bak`", printed["skypatcher.names"]);
        Assert.Contains("a name ending `.CFG` is not", printed["skypatcher.place"]);
    }

    // a changed root and switches file move what the scan reads and the printed tree and placement path; a switches file not beside the root is refused
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

        var dir = Path.Combine(Path.GetTempPath(), "hc-runtime-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mod = Path.Combine(dir, "mods", "A", "SKSE");
            Directory.CreateDirectory(Path.Combine(mod, "Patcher", "weapon"));
            File.WriteAllText(Path.Combine(mod, "Patcher", "weapon", "a.ini"), "attackDamage=1");
            File.WriteAllText(Path.Combine(mod, "Patcher.ini"), "[Patcher]\niEnableWeaponPatching=1\n");
            foreach (var d in new[] { "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(dir, d));
            using var r = AssetResolver.Build(Path.Combine(dir, "overwrite"), Path.Combine(dir, "mods"), Path.Combine(dir, "Data"), new[] { "A" }, Array.Empty<ActiveArchive>());
            SkyPatcherDiscovery.LayerScan Scan(SkyPatcherFormat? f) => SkyPatcherDiscovery.Scan(r.Capture(), SkyPatcherCatalog.Load(), _ => false, format: f);
            Assert.Empty(Scan(null).Folders);
            Assert.True(Assert.Single(Scan(both).Folders).PatchingEnabled);
            Assert.False(Assert.Single(Scan(moved).Folders).PatchingEnabled);
            var rekeyed = With(("\"root\": \"SKSE/Plugins/SkyPatcher\"", "\"root\": \"SKSE/Patcher\""),
                               ("\"file\": \"SKSE/Plugins/SkyPatcher.ini\"", "\"file\": \"SKSE/Patcher.ini\""),
                               ("\"keyPrefix\": \"iEnable\"", "\"keyPrefix\": \"bUse\""));
            Assert.False(Assert.Single(Scan(rekeyed).Folders).PatchingEnabled);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // the scan's switches notes and the draft's path refusals name the row's switches file and name token; the shipped row spells SkyPatcher.ini and .ini
    [Fact]
    public void TheSwitchesNotesAndTheDraftPathRefusalsNameTheRowsFile()
    {
        var root = ("\"root\": \"SKSE/Plugins/SkyPatcher\"", "\"root\": \"SKSE/Patcher\"");
        var rekeyed = With(root, ("\"file\": \"SKSE/Plugins/SkyPatcher.ini\"", "\"file\": \"SKSE/Patcher.ini\""), ("\"keyPrefix\": \"iEnable\"", "\"keyPrefix\": \"bUse\""));
        var gone = With(root, ("\"file\": \"SKSE/Plugins/SkyPatcher.ini\"", "\"file\": \"SKSE/Gone.ini\""));
        Assert.Equal("SkyPatcher.ini", SkyPatcherDiscovery.SwitchesName());
        Assert.Equal("Patcher.ini", SkyPatcherDiscovery.SwitchesName(rekeyed));

        var dir = Path.Combine(Path.GetTempPath(), "hc-runtime-switches-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mod = Path.Combine(dir, "mods", "A", "SKSE");
            Directory.CreateDirectory(Path.Combine(mod, "Patcher", "weapon"));
            File.WriteAllText(Path.Combine(mod, "Patcher", "weapon", "a.ini"), "attackDamage=1");
            File.WriteAllText(Path.Combine(mod, "Patcher.ini"), "[Patcher]\niEnableWeaponPatching=1\n");
            foreach (var d in new[] { "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(dir, d));
            using var r = AssetResolver.Build(Path.Combine(dir, "overwrite"), Path.Combine(dir, "mods"), Path.Combine(dir, "Data"), new[] { "A" }, Array.Empty<ActiveArchive>());
            IReadOnlyList<string> Notes(SkyPatcherFormat f) => SkyPatcherDiscovery.Scan(r.Capture(), SkyPatcherCatalog.Load(), _ => false, format: f).Notes;
            Assert.Contains(Notes(rekeyed), n => n.StartsWith("Patcher.ini has no bUse…Patching key for 'weapon'"));
            Assert.Contains(Notes(gone), n => n.StartsWith("no loose Gone.ini in this order"));
            using (new FileStream(Path.Combine(mod, "Patcher.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Contains(Notes(rekeyed), n => n.StartsWith("Patcher.ini could not be read"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }

        var cfg = With("\"nameToken\": \".ini\"", "\"nameToken\": \".cfg\"");
        Assert.Contains("the .ini file the draft", SkyPatcherDraft.Prepare("", "weapon", SkyPatcherCatalog.Load(), out _));
        Assert.Contains("the .cfg file the draft", SkyPatcherDraft.Prepare("", "weapon", SkyPatcherCatalog.Load(), out _, cfg));
        var notAbsolute = SkyPatcherDraft.Prepare("a.cfg", "weapon", SkyPatcherCatalog.Load(), out _, cfg);
        Assert.Contains("the .cfg file the draft", notAbsolute);
        Assert.Contains("weapon\\MyPatch.cfg'", notAbsolute);
        Assert.DoesNotContain(".ini", notAbsolute);
        Assert.Contains("(e.g. 'C:\\MO2\\mods\\MyPatch\\SkyPatcher\\weapon\\MyPatch.ini')", SkyPatcherDraft.Prepare("a.ini", "weapon", SkyPatcherCatalog.Load(), out _));
    }

    // a changed delimiter changes how the parser splits a line and the printed grammar
    [Fact]
    public void ChangingADelimiterChangesTheSkillText()
    {
        var piped = With("\"segment\": \":\"", "\"segment\": \"!\"");
        Assert.Equal('!', piped.Grammar.Segment);
        Assert.Single(SkyPatcherParse.ParseLine("a=1!b=2").Segments);
        Assert.Equal(2, SkyPatcherParse.ParseLine("a=1!b=2", piped.Grammar).Segments.Count);
        Assert.Contains("- `!` separates every segment", RuntimeFormatSkillText.Regions(piped)["skypatcher.segments"]);
    }

    // every delimiter of an edited grammar row drives the file parse and the address parse
    [Fact]
    public void EveryDelimiterOfTheRowDrivesTheParse()
    {
        var g = With(("\"assign\": \"=\"", "\"assign\": \"@\""), ("\"item\": \",\"", "\"item\": \"+\""), ("\"compound\": \"~\"", "\"compound\": \"^\""),
                     ("\"address\": \"|\"", "\"address\": \"/\""), ("\"comment\": \";\"", "\"comment\": \"%\""),
                     ("\"commentLeads\": [\"#\"]", "\"commentLeads\": [\"&\"]"), ("\"labelLines\": true", "\"labelLines\": false")).Grammar;
        var lines = SkyPatcherParse.ParseFile("% c\n& c\n[x]\nk@a+b\nn@^Iron Sword^\nf@Skyrim.esm/12^5", g);
        Assert.Equal(new[] { SkyPatcherLineKind.Comment, SkyPatcherLineKind.Comment, SkyPatcherLineKind.Patch }, lines.Take(3).Select(l => l.Kind));
        var items = Assert.Single(lines[3].Segments);
        Assert.Equal("k", items.Key);
        Assert.Equal(new[] { "a", "b" }, items.Values.Select(v => v.Raw));
        Assert.Equal("Iron Sword", Assert.Single(Assert.Single(lines[4].Segments).Values).NameText);
        var packed = Assert.Single(Assert.Single(lines[5].Segments).Values);
        Assert.Equal(2, packed.SubArgs.Count);
        Assert.Equal(new FormAddress("Skyrim.esm", 0x12, null), packed.Address);
        Assert.Null(SkyPatcherParse.TryParseAddress("Skyrim.esm/12"));
        Assert.NotNull(SkyPatcherParse.TryParseAddress("Skyrim.esm/12", g));
        Assert.Equal(SkyPatcherLineKind.Label, SkyPatcherParse.ParseLine("[x]").Kind);

        // the trailing-comment strip cuts at the row's comment, and the row's assign and compound open the name literal it skips
        var stripped = SkyPatcherParse.ParseFile("t@a % tail\nm@^Iron%Sword^", g);
        Assert.Equal("a", Assert.Single(Assert.Single(stripped[0].Segments).Values).Raw);
        Assert.Equal("Iron%Sword", Assert.Single(Assert.Single(stripped[1].Segments).Values).NameText);
        var kept = With("\"trailingComment\": true", "\"trailingComment\": false").Grammar;
        Assert.Equal("a", Assert.Single(Assert.Single(SkyPatcherParse.ParseLine("k=a ; b").Segments).Values).Raw);
        Assert.Equal("a ; b", Assert.Single(Assert.Single(SkyPatcherParse.ParseLine("k=a ; b", kept).Segments).Values).Raw);
    }

    // an edited gate extension decides which file names gate on a plugin and which are never read
    [Fact]
    public void AnEditedGateExtensionGatesTheName()
    {
        var esx = With("\"extensions\": [\".esp\", \".esm\", \".esl\"]", "\"extensions\": [\".esx\", \".esm\", \".esl\"]");
        Assert.Null(SkyPatcherDiscovery.GatePluginOf("Mod.esx.ini"));
        Assert.Equal("Mod.esx", SkyPatcherDiscovery.GatePluginOf("Mod.esx.ini", esx));
        Assert.Null(SkyPatcherDiscovery.NotReadReason("Mod.esx-extra.ini", _ => true));
        Assert.Contains("names a plugin with more text after it", SkyPatcherDiscovery.NotReadReason("Mod.esx-extra.ini", _ => true, esx));

        // the scan gates a file and names why it is not read with the row's extensions
        var dir = Path.Combine(Path.GetTempPath(), "hc-runtime-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var weapon = Path.Combine(dir, "mods", "A", "SKSE", "Plugins", "SkyPatcher", "weapon");
            Directory.CreateDirectory(weapon);
            File.WriteAllText(Path.Combine(weapon, "Mod.esx.ini"), "attackDamage=1");
            foreach (var d in new[] { "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(dir, d));
            using var r = AssetResolver.Build(Path.Combine(dir, "overwrite"), Path.Combine(dir, "mods"), Path.Combine(dir, "Data"), new[] { "A" }, Array.Empty<ActiveArchive>());
            SkyPatcherDiscovery.IniFile File1(SkyPatcherFormat? f) => Assert.Single(Assert.Single(SkyPatcherDiscovery.Scan(r.Capture(), SkyPatcherCatalog.Load(), _ => false, format: f).Folders).Files);
            Assert.Null(File1(null).GatePlugin);
            Assert.Null(File1(null).NotApplied);
            Assert.Equal("Mod.esx", File1(esx).GatePlugin);
            Assert.Contains("filename-gated on plugin 'Mod.esx'", File1(esx).NotApplied);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // an edited switches section and key shape decide which keys the scan reads and the printed switch rule; the scan parses with the row's grammar
    [Fact]
    public void TheSwitchesSectionAndKeyShapeOfTheRowDriveTheScanAndTheSkillText()
    {
        var edited = With(("\"section\": \"Patcher\"", "\"section\": \"Toggles\""), ("\"keyPrefix\": \"iEnable\"", "\"keyPrefix\": \"bUse\""),
                          ("\"keySuffix\": \"Patching\"", "\"keySuffix\": \"On\""), ("\"assign\": \"=\"", "\"assign\": \"@\""));
        var switches = RuntimeFormatSkillText.Regions(edited)["skypatcher.switches"];
        Assert.Contains("**`[Toggles]`** — one `bUse<Type>On=1|0` per record type", switches);
        Assert.Contains("only `bUseLightOn` is on without the key", switches);

        var dir = Path.Combine(Path.GetTempPath(), "hc-runtime-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plugins = Path.Combine(dir, "mods", "A", "SKSE", "Plugins");
            Directory.CreateDirectory(Path.Combine(plugins, "SkyPatcher", "weapon"));
            File.WriteAllText(Path.Combine(plugins, "SkyPatcher", "weapon", "a.ini"), "attackDamage@1");
            Directory.CreateDirectory(Path.Combine(plugins, "SkyPatcher", "armor"));
            File.WriteAllText(Path.Combine(plugins, "SkyPatcher", "armor", "b.ini"), "armorRating=1");
            File.WriteAllText(Path.Combine(plugins, "SkyPatcher.ini"), "[Toggles]\nbUseWeaponOn=1\n");
            foreach (var d in new[] { "overwrite", "Data" }) Directory.CreateDirectory(Path.Combine(dir, d));
            using var r = AssetResolver.Build(Path.Combine(dir, "overwrite"), Path.Combine(dir, "mods"), Path.Combine(dir, "Data"), new[] { "A" }, Array.Empty<ActiveArchive>());
            IReadOnlyList<SkyPatcherDiscovery.FolderScan> Folders(SkyPatcherFormat? f) => SkyPatcherDiscovery.Scan(r.Capture(), SkyPatcherCatalog.Load(), _ => false, format: f).Folders;
            SkyPatcherDiscovery.FolderScan Weapon(SkyPatcherFormat? f) => Folders(f).Single(x => x.Subfolder == "weapon");
            Assert.False(Weapon(null).PatchingEnabled);
            var weapon = Weapon(edited);
            Assert.True(weapon.PatchingEnabled);
            Assert.Equal("attackDamage", Assert.Single(Assert.Single(Assert.Single(weapon.Files).Lines).Segments).Key);

            // the scan's folder order and its no-key switch rule are the row's
            const string w = """{ "folder": "weapon", "name": "Weapon" },""", a = """{ "folder": "armor", "name": "Armor" },""";
            var reordered = With((w + "\n    " + a, a + "\n    " + w), ("\"onWithoutKey\": [\"light\"]", "\"onWithoutKey\": [\"light\", \"weapon\"]"));
            Assert.Equal(new[] { "weapon", "armor" }, Folders(null).Select(x => x.Subfolder));
            Assert.Equal(new[] { "armor", "weapon" }, Folders(reordered).Select(x => x.Subfolder));
            Assert.True(Weapon(reordered).PatchingEnabled);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
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
        var (prop, value) = (from.Split(':')[0].Trim('"'), to.Split(':', 2)[1].Trim().Trim('"'));
        Assert.Contains(prop == "segment" ? $"[grammar.segment]: '{value}'" : $"'{prop}' = '{value}'", ex.Message);
    }
}
