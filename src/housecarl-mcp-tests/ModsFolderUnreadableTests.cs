using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A mods folder that exists but will not open (#1385): status says so first, every order read refuses;
/// a single mod folder that will not open is a warning naming it, and the order still answers.</summary>
[Trait("tier", "integration")]
public sealed class ModsFolderUnreadableTests : IClassFixture<ModsUnreadableWorld>
{
    const string Unopened = "could not open the mods folder";
    readonly ModsUnreadableWorld _w;
    public ModsFolderUnreadableTests(ModsUnreadableWorld w) => _w = w;

    [Fact]
    public void StatusLeadsWithTheUnopenedModsFolderAndHowFewPluginsWereRead()
    {
        using var svc = _w.Whole();
        var lines = StatusTools.LoadOrderStatus(svc, _w.Tools).Split('\n');

        Assert.StartsWith("[!] houseCARL " + Unopened, lines[3]);
        Assert.Contains("only 1 of 2 active plugins", lines[3]);
    }

    [Fact]
    public void ARecordReadRefusesRatherThanAnswerFromTheDataFolderAlone()
    {
        using var svc = _w.Whole();
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon });

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("winner=", text);
    }

    [Fact]
    public void ACheckOnAModPluginNamesTheModsFolderNotAStaleProfile()
    {
        using var svc = _w.Whole();
        var text = CheckTools.CheckTool(svc, plugins: new[] { ModsUnreadableWorld.ModPlugin });

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("profile is stale", text);
    }

    [Fact]
    public void StatusOnAModPluginDoesNotSayNoFolderHoldsIt()
    {
        using var svc = _w.Whole();
        var text = StatusTools.LoadOrderStatus(svc, _w.Tools, filter: ModsUnreadableWorld.ModPlugin);

        Assert.Contains("served from: unknown", text);
        Assert.DoesNotContain("no folder holds a file of this name", text);
    }

    [Fact]
    public void AnAssetReadRefusesOnce()
    {
        using var svc = _w.Whole();
        var text = AssetTools.AssetStatus(svc, paths: new[] { "meshes/x.nif" });

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("does not stat", text);
    }

    [Fact]
    public void OneUnopenedModFolderIsNamedAndTheOrderStillAnswers()
    {
        using var svc = _w.Partial();

        Assert.Contains(svc.StatusData().Warnings, w => w.Contains("could not be opened") && w.Contains("'" + ModsUnreadableWorld.BrokenMod + "'"));
        Assert.Contains("winner=", RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }));
    }

    [Fact]
    public void APluginLostToAnUnopenedModFolderIsNotBlamedOnAStaleProfile()
    {
        using var svc = _w.Partial();
        var text = CheckTools.CheckTool(svc, plugins: new[] { ModsUnreadableWorld.LostPlugin });

        Assert.Contains(ModsUnreadableWorld.BrokenMod, text);
        Assert.DoesNotContain("profile is stale", text);
    }
}

/// <summary>Two instances over one game Data folder: one whose mods folder is a junction to a folder that is gone,
/// and one whose mods folder lists but holds a mod folder that is such a junction.</summary>
public sealed class ModsUnreadableWorld : IDisposable
{
    public const string MasterName = "HcMuMaster.esm";
    public const string ModPlugin = "HcMuMod.esp";
    public const string LostPlugin = "HcMuLost.esp";
    public const string BrokenMod = "BrokenMod";

    readonly string _root, _whole, _partial;
    public string MasterWeapon { get; }
    public ToolPathResolver Tools { get; }
    readonly UserConfigStore _store;

    public ModsUnreadableWorld()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-mods-unreadable-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(_root, "game", "Data");
        Directory.CreateDirectory(data);

        var master = new SkyrimMod(new ModKey("HcMuMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcMuW0";
        MasterWeapon = $"{w.FormKey.ID:X6}:{MasterName}";
        master.BeginWrite.ToPath(Path.Combine(data, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        _whole = MakeInstance("whole", new[] { MasterName, ModPlugin }, new[] { "+ModA" });
        Junction(Path.Combine(_whole, "mods"), Path.Combine(_root, "gone-whole"));

        _partial = MakeInstance("partial", new[] { MasterName, ModPlugin, LostPlugin }, new[] { "+" + BrokenMod, "+ModA" });
        var modA = Path.Combine(_partial, "mods", "ModA");
        Directory.CreateDirectory(modA);
        var mod = new SkyrimMod(new ModKey("HcMuMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        mod.Weapons.AddNew().EditorID = "HcMuW1";
        mod.BeginWrite.ToPath(Path.Combine(modA, ModPlugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        Junction(Path.Combine(_partial, "mods", BrokenMod), Path.Combine(_root, "gone-mod"));

        _store = new UserConfigStore(Path.Combine(_root, "user.json"));
        Tools = new ToolPathResolver(_store);
    }

    public LoadOrderService Whole() => LoadOrderService.WithInstance(_whole, 0, _store);
    public LoadOrderService Partial() => LoadOrderService.WithInstance(_partial, 0, _store);

    string MakeInstance(string name, string[] order, string[] mods)
    {
        var inst = Path.Combine(_root, name);
        var profile = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(inst, "overwrite"));
        File.WriteAllText(Path.Combine(inst, Mo2Instance.IniFileName),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n" + string.Join("\r\n", mods) + "\r\n");
        return inst;
    }

    /// <summary>A junction whose target is made and then removed, so the link exists and cannot be followed.</summary>
    static void Junction(string link, string target)
    {
        Directory.CreateDirectory(target);
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            mk!.WaitForExit();
        Directory.Delete(target);
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "the junction was not made");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ }
    }
}
