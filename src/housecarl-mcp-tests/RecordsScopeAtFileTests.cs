using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A small MO2 instance whose plugin names carry the characters a FormID list strips or an '@file' entry
/// claims: a comma, a leading '[', a leading apostrophe and a leading '@'. A master defines one weapon every plugin
/// overrides, and each plugin defines a weapon of its own, so a scan's rows say which plugins it covered.</summary>
public sealed class ScopeNamesWorld : IDisposable
{
    public const string Bracket = "[Hc] Bracket Patch.esp";
    public const string Comma = "HcScope Eyes, Standalone.esp";
    public const string Apostrophe = "'Til Dawn Patch.esp";
    public const string At = "@HcAt Patch.esp";
    public const string Plain = "HcScopePlain.esp";
    public const string Master = "HcScopeMaster.esm";
    public const string OffOrder = "HcScopeOff.esp";

    /// <summary>Each plugin's own weapon, by EditorID.</summary>
    public static readonly IReadOnlyDictionary<string, string> Own = new Dictionary<string, string>
    {
        [Bracket] = "HcOwnBracket", [Comma] = "HcOwnComma", [Apostrophe] = "HcOwnApos", [At] = "HcOwnAt", [Plain] = "HcOwnPlain",
    };

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-scope-names-" + Guid.NewGuid().ToString("N"));
    public LoadOrderService Svc { get; }

    public ScopeNamesWorld()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var inst = Path.Combine(_root, "inst");
        var master = new SkyrimMod(ModKey.FromFileName(Master), SkyrimRelease.SkyrimSE);
        var weapon = master.Weapons.AddNew();
        weapon.EditorID = "HcScopeWeapon";
        var mods = new List<string> { "MasterMod" };
        WriteMod(inst, "MasterMod", master, Array.Empty<ISkyrimModGetter>());
        var plugins = new[] { Bracket, Comma, Apostrophe, At, Plain };
        foreach (var name in plugins)
        {
            var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
            mod.Weapons.GetOrAddAsOverride(weapon);
            mod.Weapons.AddNew().EditorID = Own[name];
            var folder = "Mod" + mods.Count;
            WriteMod(inst, folder, mod, new ISkyrimModGetter[] { master });
            mods.Add(folder);
        }
        // A disabled mod's plugin, off the order, for the off-order scan.
        var off = new SkyrimMod(ModKey.FromFileName(OffOrder), SkyrimRelease.SkyrimSE);
        off.Weapons.GetOrAddAsOverride(weapon);
        WriteMod(inst, "OffMod", off, new ISkyrimModGetter[] { master });

        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var order = new[] { Master }.Concat(plugins).ToArray();
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Concat(order.Select(p => "*" + p + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n-OffMod\r\n" + string.Concat(Enumerable.Reverse(mods).Select(m => "+" + m + "\r\n")));
        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    static void WriteMod(string inst, string folder, SkyrimMod mod, ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(inst, "mods", folder);
        Directory.CreateDirectory(dir);
        mod.BeginWrite.ToPath(Path.Combine(dir, mod.ModKey.FileName)).WithLoadOrder(masters).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>plugins.names takes the '@file' spelling formids= and references= take: one '@&lt;absolute path&gt;'
/// entry stands in place of the list, one plugin filename per line, with the expander's own refusals (#931).</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScopeAtFileTests : RecordsTestBase, IClassFixture<ScopeNamesWorld>
{
    readonly ScopeNamesWorld _names;

    static string TempPath(string ext) => Path.Combine(Path.GetTempPath(), "hc-scope-atfile-" + Guid.NewGuid().ToString("N") + ext);

    /// <summary>The own weapons a scan's rows carry, of the five plugins' own weapons.</summary>
    static string[] OwnIn(string rows) =>
        ScopeNamesWorld.Own.Where(kv => rows.Contains(kv.Value, StringComparison.Ordinal)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    static string[] Sorted(params string[] names) => names.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>The rows are exactly the listed plugins' own weapons, a comma, a leading '[' and a leading apostrophe
    /// kept; the manifest echoes the list file, not the names it held.</summary>
    [Fact]
    public void AnAtFileScopeScansExactlyThePluginsTheFileLists_ACommaABracketAndAnApostropheKept()
    {
        var file = TempPath(".txt");
        var artifact = TempPath(".jsonl");
        File.WriteAllText(file, ScopeNamesWorld.Bracket + "\n" + ScopeNamesWorld.Comma + "\n" + ScopeNamesWorld.Apostrophe + "\n");
        try
        {
            var text = RecordsTools.Records(_names.Svc, plugins: Scope("@" + file), types: new[] { "WEAP" }, to_file: artifact);

            Assert.DoesNotContain("error:", text);
            Assert.DoesNotContain("note:", text);   // a split or trimmed name would be reported as missing here
            var lines = File.ReadAllLines(artifact);
            Assert.Equal(Sorted(ScopeNamesWorld.Bracket, ScopeNamesWorld.Comma, ScopeNamesWorld.Apostrophe),
                         OwnIn(string.Join("\n", lines.Skip(1))));
            Assert.Contains("@" + file.Replace(@"\", @"\\"), lines[0]);
            Assert.DoesNotContain(ScopeNamesWorld.Comma, lines[0]);
        }
        finally { File.Delete(file); File.Delete(artifact); }
    }

    /// <summary>A list file that opens with '[' is read as a JSON array first.</summary>
    [Fact]
    public void AJsonArrayListFileScopesItsPlugins()
    {
        var file = TempPath(".json");
        File.WriteAllText(file, "[\"" + ScopeNamesWorld.Bracket + "\", \"" + ScopeNamesWorld.Comma + "\"]");
        try
        {
            var text = RecordsTools.Records(_names.Svc, plugins: Scope("@" + file), types: new[] { "WEAP" });

            Served(text);
            Assert.DoesNotContain("note:", text);
            Assert.Equal(Sorted(ScopeNamesWorld.Bracket, ScopeNamesWorld.Comma), OwnIn(text));
        }
        finally { File.Delete(file); }
    }

    /// <summary>A filename that starts with '@' is written '@@' inline, alone or beside other names.</summary>
    [Fact]
    public void ADoubledAtNamesAPluginWhoseFilenameStartsWithAt()
    {
        var alone = RecordsTools.Records(_names.Svc, plugins: Scope("@" + ScopeNamesWorld.At), types: new[] { "WEAP" });
        var beside = RecordsTools.Records(_names.Svc, plugins: Scope(ScopeNamesWorld.Plain, "@" + ScopeNamesWorld.At), types: new[] { "WEAP" });

        Served(alone);
        Assert.Equal(new[] { ScopeNamesWorld.At }, OwnIn(alone));
        Served(beside);
        Assert.Equal(Sorted(ScopeNamesWorld.At, ScopeNamesWorld.Plain), OwnIn(beside));
    }

    /// <summary>A FormID result artifact names no plugins, so it is refused by its identity in one sentence rather
    /// than having every FormID reach the scope as a plugin name.</summary>
    [Fact]
    public void AFormIdArtifactIsRefusedByItsIdentityInOneShortSentence()
    {
        var artifact = TempPath(".jsonl");
        try
        {
            RecordsTools.Records(Svc, plugins: Scope(W.MasterName), types: new[] { "WEAP" }, to_file: artifact);

            var text = RecordsTools.Records(Svc, plugins: Scope("@" + artifact), types: new[] { "WEAP" });

            Refused(text, "identities");
            Assert.True(text.Length < 400, $"refusal is {text.Length} chars");
        }
        finally { File.Delete(artifact); }
    }

    [Fact]
    public void AnAtFileThatDoesNotExistIsRefused()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope("@" + TempPath(".txt")), types: new[] { "WEAP" });

        Refused(text, "could not read plugins.names= list file");
    }

    /// <summary>A relative list path is refused with a plain-text list as the example, not a result artifact.</summary>
    [Fact]
    public void ARelativeListPathIsRefusedWithATextFileExample()
    {
        var text = RecordsTools.Records(Svc, plugins: Scope("@plugins.txt"), types: new[] { "WEAP" });

        Refused(text, "plugins.names= list file", "list.txt");
    }

    /// <summary>A list file's lines are read as written: a line starting with '@' is that plugin, with no escape.</summary>
    [Fact]
    public void AFileLineStartingWithAtNamesThatPlugin()
    {
        var file = TempPath(".txt");
        File.WriteAllText(file, ScopeNamesWorld.At + "\n");
        try
        {
            var text = RecordsTools.Records(_names.Svc, plugins: Scope("@" + file), types: new[] { "WEAP" });

            Served(text);
            Assert.DoesNotContain("note:", text);
            Assert.Equal(new[] { ScopeNamesWorld.At }, OwnIn(text));
        }
        finally { File.Delete(file); }
    }

    /// <summary>The off-order scan's manifest echoes the list file too.</summary>
    [Fact]
    public void AnOffOrderScanManifestEchoesTheListFile()
    {
        var file = TempPath(".txt");
        var artifact = TempPath(".jsonl");
        File.WriteAllText(file, ScopeNamesWorld.Master + "\n");
        try
        {
            var text = RecordsTools.Records(_names.Svc, plugins: Scope("@" + file), source: Plugin(ScopeNamesWorld.OffOrder),
                                            types: new[] { "WEAP" }, to_file: artifact);

            Served(text);
            var manifest = Je(File.ReadLines(artifact).First());
            Assert.Equal("@" + file, manifest.GetProperty("query").GetProperty("plugins").GetString());
        }
        finally { File.Delete(file); File.Delete(artifact); }
    }

    /// <summary>A scoped tree states its selection with the list file, not the names it held.</summary>
    [Fact]
    public void AScopedTreeStatesItsSelectionWithTheListFile()
    {
        var file = TempPath(".txt");
        File.WriteAllText(file, ScopeNamesWorld.Comma + "\n");
        try
        {
            var text = RecordsTools.Records(_names.Svc, plugins: Scope("@" + file), source: Plugin(ScopeNamesWorld.Plain),
                                            project: Form("tree"), types: new[] { "WEAP" });

            Served(text, "scope-selected (@" + file + ")");
        }
        finally { File.Delete(file); }
    }

    /// <summary>A json caller gets the refusal as a document with an error member.</summary>
    [Fact]
    public void AnAtFileThatDoesNotExistIsRefusedAsAJsonDocumentOnJson()
    {
        var doc = Je(RecordsTools.Records(Svc, plugins: Scope("@" + TempPath(".txt")), types: new[] { "WEAP" }, format: "json"));

        Assert.Contains("could not read", doc.GetProperty("error").GetString());
    }

    /// <summary>'@file' stands in place of the whole list on plugins.names as on formids=, so a name beside it is
    /// refused the same way rather than spliced, and the remedy is a spelling the tool accepts.</summary>
    [Fact]
    public void AnAtFileBesideAnInlineNameIsRefused()
    {
        var file = TempPath(".txt");
        File.WriteAllText(file, W.OverrideName + "\n");
        try
        {
            var text = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, "@" + file), types: new[] { "WEAP" });

            Refused(text, "mixes", "Pass plugins={\"names\": [\"@<path>\"]} alone");
        }
        finally { File.Delete(file); }
    }

    public RecordsScopeAtFileTests(RecordsFixture f, ScopeNamesWorld names) : base(f) => _names = names;
}
