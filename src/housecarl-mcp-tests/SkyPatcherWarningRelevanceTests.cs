using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An overlay read carries the SkyPatcher warnings that bear on its records; a note from a line that cannot
/// reach them is one pointer to housecarl_skypatcher_layer filter=, which lists it under its line (#1093).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherWarningRelevanceTests : IDisposable
{
    const string PluginName = "HcSpRel.esp";
    const string IniName = "HcRelevance.ini";
    readonly string _root;
    readonly LoadOrderService _svc;
    readonly string _heel, _plain;

    public SkyPatcherWarningRelevanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-relevance-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniDir = Path.Combine(mods, "HcSpRelIni", "SKSE", "Plugins", "SkyPatcher", "armor");
        foreach (var d in new[] { profileDir, Path.Combine(_root, "game", "Data"), Path.Combine(mods, "HcSpRelPlugins"), iniDir })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var heelKw = new Keyword(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcHeelKw" };
        var addKw = new Keyword(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcAddKw" };
        mod.Keywords.Add(heelKw);
        mod.Keywords.Add(addKw);
        var heel = new Armor(new FormKey(mod.ModKey, 0x810), SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcHeelBoots",
            Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { heelKw.ToLink() },
        };
        var plain = new Armor(new FormKey(mod.ModKey, 0x811), SkyrimRelease.SkyrimSE) { EditorID = "HcPlainCuirass" };
        mod.Armors.Add(heel);
        mod.Armors.Add(plain);
        mod.BeginWrite.ToPath(Path.Combine(mods, "HcSpRelPlugins", PluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _heel = RecordsWorld.Fid(heel.FormKey);
        _plain = RecordsWorld.Fid(plain.FormKey);

        // :1 an Or list (a missing keyword cannot flip it), :2 a bare list (a missing keyword forces NoMatch), :3 an unknown key.
        File.WriteAllText(Path.Combine(iniDir, IniName),
            "filterByKeywordsOr=HcNoSuchA,HcHeelKw,HcNoSuchB:keywordsToAdd=HcAddKw\r\n"
            + "filterByKeywords=HcHeelKw,HcNoSuchC:keywordsToAdd=HcAddKw\r\n"
            + "filterByBogusThing=1:keywordsToAdd=HcAddKw\r\n");

        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), $"# header\r\n{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcSpRelIni\r\n+HcSpRelPlugins\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static JsonElement Post => JsonDocument.Parse("{\"overlay\": \"skypatcher\", \"state\": \"post\"}").RootElement.Clone();

    string Read(string fid, string? format = null) => RecordsTools.Records(_svc, formids: new[] { fid }, source: Post, format: format);

    [Fact]
    public void ARecordNoLineReachesGetsOnePointerAndNoWarningText()
    {
        var text = Read(_plain);

        Assert.Contains($"3 note(s) on lines that do not reach these records — {ToolNames.SkypatcherLayer} filter={IniName} lists them", text);
        Assert.DoesNotContain("HcNoSuchA", text);
        Assert.DoesNotContain("HcNoSuchC", text);
    }

    [Fact]
    public void ARecordTheOrLineMatchesGetsItsWarnings()
        => Assert.Contains("keyword 'HcNoSuchA' (in a filterByKeywordsOr) resolves to nothing", Read(_heel));

    // the bare list's resolved keyword is on the record, so only the missing one stops the line
    [Fact]
    public void AMissingKeywordThatStopsABareLineRidesTheRead()
        => Assert.Contains("keyword 'HcNoSuchC' (in a filterByKeywords) resolves to nothing", Read(_heel));

    [Fact]
    public void AnUnknownKeyRidesEveryRead()
        => Assert.Contains("'filterByBogusThing' are not in the SkyPatcher reference", Read(_plain));

    [Fact]
    public void TheJsonWarningsMemberCarriesThePointer()
    {
        using var doc = JsonDocument.Parse(Read(_plain, "json"));
        Assert.Contains($"filter={IniName} lists them", doc.RootElement.GetProperty("skypatcher_warnings").GetString());
    }

    [Fact]
    public void TheLayerFilterShowsTheLintUnderItsLine()
    {
        var text = SkyPatcherTools.SkyPatcherLayer(_svc, filter: IniName);

        int line1 = text.IndexOf(":1  filterByKeywordsOr", StringComparison.Ordinal);
        int lint = text.IndexOf("[!] keyword 'HcNoSuchA' (in a filterByKeywordsOr) resolves to nothing", StringComparison.Ordinal);
        int line2 = text.IndexOf(":2  filterByKeywords=", StringComparison.Ordinal);
        Assert.True(line1 >= 0 && line1 < lint && lint < line2, text);
    }
}
