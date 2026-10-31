using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A post-state delta lists its SkyPatcher warnings in row order, though it reads its winners a group at a
/// time in door order (#1282 review r1 #1).</summary>
[Trait("tier", "integration")]
public sealed class PostPoleWarningOrderTests : IDisposable
{
    const string FirstName = "HcWoFirst.esp", LastName = "HcWoLast.esp";
    const int LastLines = 15, FirstLines = 10;
    readonly string _root;
    readonly LoadOrderService _svc;
    readonly FormKey _early, _late;

    // HcWoFirst (loads first) holds HcEarly, HcWoLast holds HcLate; each armor meets its own Or lines, each naming a
    // missing keyword, so each replay raises its own warnings.
    public PostPoleWarningOrderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-post-warning-order-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniDir = Path.Combine(mods, "HcWoIni", "SKSE", "Plugins", "SkyPatcher", "armor");
        foreach (var d in new[] { profileDir, Path.Combine(_root, "game", "Data"), Path.Combine(mods, "HcWoPlugins"), iniDir })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace('\\', '/') + ")\r\n");

        _early = Write(Path.Combine(mods, "HcWoPlugins", FirstName), "HcEarly", "HcEarlyKw");
        _late = Write(Path.Combine(mods, "HcWoPlugins", LastName), "HcLate", "HcLateKw");
        File.WriteAllText(Path.Combine(iniDir, "HcWo.ini"),
            string.Concat(Enumerable.Range(1, FirstLines).Select(i => $"filterByKeywordsOr=HcNoSuchEarly{i},HcEarlyKw:keywordsToAdd=HcEarlyKw\r\n"))
            + string.Concat(Enumerable.Range(1, LastLines).Select(i => $"filterByKeywordsOr=HcNoSuchLate{i},HcLateKw:keywordsToAdd=HcLateKw\r\n")));

        var order = new[] { FirstName, LastName };
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(order.Select(o => o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(order.Select(o => "*" + o + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcWoIni\r\n+HcWoPlugins\r\n");
        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    static FormKey Write(string path, string armor, string keyword)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(path)), SkyrimRelease.SkyrimSE);
        var kw = new Keyword(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = keyword };
        mod.Keywords.Add(kw);
        var a = new Armor(new FormKey(mod.ModKey, 0x810), SkyrimRelease.SkyrimSE)
        {
            EditorID = armor,
            Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() },
        };
        mod.Armors.Add(a);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return a.FormKey;
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>Rows in the reverse of door order keep the first row's warnings and the second's first lines up to
    /// the cap, as main lists them, and still walk each winner plugin once per pole.</summary>
    [Fact]
    public void ADeltaListsItsPostWarningsInRowOrder()
    {
        long passes = _svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_svc, formids: new[] { RecordsWorld.Fid(_late), RecordsWorld.Fid(_early) },
            source: JsonDocument.Parse("{\"overlay\": \"skypatcher\", \"state\": \"post\"}").RootElement.Clone(),
            versus: JsonDocument.Parse("\"winner\"").RootElement.Clone(),
            project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } });
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(4, _svc.Counters.CollectPasses - passes);

        int keptEarly = HousecarlCore.SkyPatcherOverlay.WarningSink.Cap - LastLines;
        static int At(string text, string kw) => text.IndexOf($"keyword '{kw}' (in a filterByKeywordsOr)", StringComparison.Ordinal);
        var late = Enumerable.Range(1, LastLines).Select(i => At(r, $"HcNoSuchLate{i}")).ToList();
        var early = Enumerable.Range(1, FirstLines).Select(i => At(r, $"HcNoSuchEarly{i}")).ToList();
        Assert.All(late, at => Assert.True(at >= 0, r));
        Assert.Equal(Enumerable.Range(0, keptEarly), early.Select((at, i) => (at, i)).Where(e => e.at >= 0).Select(e => e.i));
        Assert.True(late.Max() < early.Where(at => at >= 0).Min(), r);
        Assert.Contains($"{FirstLines - keptEarly} further warning(s) not listed", r);
    }
}
