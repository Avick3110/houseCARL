using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A mods folder that exists but will not open (#1385): status says so first, every order read and write
/// refuses; a single mod folder that will not open is named at the head of every answer off the order, and nothing
/// blames its plugins on a stale profile, a typo or a missing install.</summary>
[Trait("tier", "integration")]
public sealed class ModsFolderUnreadableTests : IClassFixture<ModsUnreadableWorld>
{
    const string Unopened = "could not open the mods folder";
    readonly ModsUnreadableWorld _w;
    public ModsFolderUnreadableTests(ModsUnreadableWorld w) => _w = w;

    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- the whole mods folder will not open ----

    [Fact]
    public void StatusLeadsWithTheUnopenedModsFolderAndHowFewPluginsWereRead()
    {
        using var svc = _w.Whole();
        var lines = StatusTools.LoadOrderStatus(svc, _w.Tools).Split('\n');

        Assert.StartsWith("[!] houseCARL " + Unopened, lines[3]);
        Assert.Contains("only 1 of 3 active plugins", lines[3]);
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
    public void AConflictTreeRefusesRatherThanShowOnePluginTouchingTheRecord()
    {
        using var svc = _w.Whole();
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }, project: new RecordsTools.RecordsProject { form = "tree" });

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("touch this record", text);
    }

    [Fact]
    public void AWholeOrderSweepRefusesRatherThanReportAScanOfTheDataFolder()
    {
        using var svc = _w.Whole();
        var text = CheckTools.CheckTool(svc, counts_only: true);

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("scanned", text);
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
    public void WritingASeqRefusesOnTheModsFolderRatherThanFailInternally()
    {
        using var svc = _w.Whole();
        var text = SeqTools.WriteSeq(svc, ModsUnreadableWorld.MasterName, patch: "HcMuSeq");

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("internal houseCARL failure", text);
    }

    [Fact]
    public void PointingAtTheInstanceSaysItsModsFolderWillNotOpen()
    {
        using var svc = _w.Whole();
        var text = SetupTools.SetMo2Instance(svc, _w.WholePath);

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("resolves on the next read", text);
    }

    [Fact]
    public void AModsFolderThatStopsOpeningAfterTheBuildRefusesTheNextRead()
    {
        var (svcOwner, target) = _w.Toggle();
        using var svc = svcOwner;
        Assert.Contains("winner=", RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }));

        Directory.Move(target, target + "-away");
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon });

        Assert.Contains(Unopened, text);
        Assert.DoesNotContain("winner=", text);
    }

    // ---- one enabled mod folder will not open ----

    [Fact]
    public void StatusLeadsWithTheUnopenedModFolder()
    {
        using var svc = _w.Partial();
        var lines = StatusTools.LoadOrderStatus(svc, _w.Tools).Split('\n');

        Assert.Contains("could not be opened", lines[0]);
        Assert.Contains(ModsUnreadableWorld.BrokenMod, lines[0]);
    }

    [Fact]
    public void ARecordReadStillAnswersAndNamesTheUnopenedModFolder()
    {
        using var svc = _w.Partial();
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon });

        Assert.StartsWith("[!]", text);
        Assert.Contains(ModsUnreadableWorld.BrokenMod, text.Split('\n')[0]);
        Assert.Contains("winner=", text);
    }

    [Fact]
    public void ASweepNamesTheUnopenedModFolder()
    {
        using var svc = _w.Partial();

        Assert.Contains(ModsUnreadableWorld.BrokenMod, CheckTools.CheckTool(svc, counts_only: true).Split('\n')[0]);
    }

    [Fact]
    public void AWritePlanNamesTheUnopenedModFolder()
    {
        using var svc = _w.Partial();
        var text = ApplyTools.Apply(svc, dry_run: true,
            ops: Je("[{\"formid\":\"" + _w.MasterWeapon + "\",\"field_path\":\"BasicStats.Damage\",\"op\":\"Set\",\"value\":\"9\"}]"));

        Assert.Contains(ModsUnreadableWorld.BrokenMod, text.Split('\n')[0]);
    }

    [Fact]
    public void AJsonAnswerCarriesTheNoteAsItsFirstMember()
    {
        using var svc = _w.Partial();
        var json = JsonDocument.Parse(RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }, format: "json")).RootElement;

        Assert.Contains(ModsUnreadableWorld.BrokenMod, json.EnumerateObject().First().Value.GetString());
    }

    [Fact]
    public void APluginLostToAnUnopenedModFolderIsNotBlamedOnAStaleProfileOrATypo()
    {
        using var svc = _w.Partial();
        var text = CheckTools.CheckTool(svc, plugins: new[] { ModsUnreadableWorld.LostPlugin }, counts_only: true);

        Assert.Contains(ModsUnreadableWorld.BrokenMod, text);
        Assert.DoesNotContain("profile is stale", text);
        Assert.DoesNotContain("Check the filename", text);
    }

    [Fact]
    public void ReadingTheLostPluginsVersionNamesTheUnopenedModFolder()
    {
        using var svc = _w.Partial();
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }, source: Je("\"" + ModsUnreadableWorld.LostPlugin + "\""));

        Assert.Contains("may be in one of them", text);
        Assert.DoesNotContain("Check the filename", text);
    }

    [Fact]
    public void StatusOnTheLostPluginSaysWhereItIsServedFromIsUnknown()
    {
        using var svc = _w.Partial();
        var text = StatusTools.LoadOrderStatus(svc, _w.Tools, filter: ModsUnreadableWorld.LostPlugin);

        Assert.Contains("served from: unknown", text);
        Assert.Contains(ModsUnreadableWorld.BrokenMod, text);
        Assert.DoesNotContain("no folder holds a file of this name", text);
    }

    [Fact]
    public void AMasterInTheUnopenedModFolderIsNotCalledNotInstalled()
    {
        using var svc = _w.Partial();
        var text = CheckTools.CheckTool(svc, plugins: new[] { ModsUnreadableWorld.DepPlugin });

        Assert.Contains("not in any folder houseCARL could open", text);
        Assert.DoesNotContain("NOT installed anywhere", text);
    }

    [Fact]
    public void AnAssetAnswerSaysTheUnopenedModFoldersArchivesWereNotScanned()
    {
        using var svc = _w.Partial();
        var text = AssetTools.AssetStatus(svc, paths: new[] { "meshes/x.nif" });

        Assert.Contains("BSAs are not in the asset scan", text);
        Assert.Contains(ModsUnreadableWorld.BrokenMod, text);
    }

    [Fact]
    public void AHiddenPluginInAnEnabledModIsNotInTheOrder()
    {
        var (profile, mods, data) = _w.HiddenPluginInstance();

        var order = Mo2LoadOrder.Build(profile, mods, data);

        Assert.DoesNotContain(order.OrderedPaths, p => Path.GetFileName(p) == ModsUnreadableWorld.ModPlugin);
    }

    [Fact]
    public void AModFolderThatOpensAgainIsReadOnTheNextCall()
    {
        var (svcOwner, target) = _w.PartialToggle(liveAtStart: false);
        using var svc = svcOwner;
        Assert.StartsWith("[!]", RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }));

        _w.PutLostPlugin(target);
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon });

        Assert.DoesNotContain(ModsUnreadableWorld.BrokenMod, text);
        Assert.Contains("winner=", text);
    }

    [Fact]
    public void AModFolderThatStopsOpeningAfterTheBuildIsNamedOnTheNextCall()
    {
        var (svcOwner, target) = _w.PartialToggle(liveAtStart: true);
        using var svc = svcOwner;
        Assert.DoesNotContain(ModsUnreadableWorld.BrokenMod, RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon }));

        Directory.Move(target, target + "-away");
        var text = RecordsTools.Records(svc, formids: new[] { _w.MasterWeapon });

        Assert.Contains(ModsUnreadableWorld.BrokenMod, text.Split('\n')[0]);
        Assert.DoesNotContain("could not be parsed", text);
    }

    [Fact]
    public void ASavedFindingsFileDoesNotSayInstallAMasterBehindAnUnopenedFolder()
    {
        using var svc = _w.Partial();
        var file = Path.Combine(_w.Scratch, "dep-" + Guid.NewGuid().ToString("N") + ".jsonl");
        CheckTools.CheckTool(svc, plugins: new[] { ModsUnreadableWorld.DepPlugin }, to_file: file);
        var row = File.ReadAllLines(file).Single(l => l.Contains("\"missing_master\""));

        Assert.Contains("could not be", row);
        Assert.DoesNotContain("install it", row);
    }

    [Fact]
    public void AProviderBehindAnUnopenedModFolderIsNotCalledOutOfTheOrder()
    {
        using var svc = _w.Partial();
        var text = AssetTools.AssetStatus(svc, paths: new[] { "meshes/x.nif" }, provider: "HcMuLost.bsa");

        Assert.Contains("may be in one of them", text);
        Assert.DoesNotContain("not in the active load order", text);
    }
}

/// <summary>Instances over one game Data folder: one whose mods folder is a junction to a folder that is gone, one whose
/// mods folder lists but holds a mod folder that is such a junction, and, per call, one whose mods folder can be taken away.</summary>
public sealed class ModsUnreadableWorld : IDisposable
{
    public const string MasterName = "HcMuMaster.esm";
    public const string ModPlugin = "HcMuMod.esp";
    public const string LostPlugin = "HcMuLost.esp";
    public const string DepPlugin = "HcMuDep.esp";
    public const string BrokenMod = "BrokenMod";

    readonly string _root, _partial, _modFile, _depFile, _lostFile;
    int _toggles;
    public string Scratch => _root;
    public string WholePath { get; }
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

        var mod = new SkyrimMod(new ModKey("HcMuMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        mod.Weapons.AddNew().EditorID = "HcMuW1";
        _modFile = Path.Combine(_root, ModPlugin);
        mod.BeginWrite.ToPath(_modFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A plugin whose one master is the lost plugin, which sits behind the unopened mod folder.
        var lost = new SkyrimMod(new ModKey("HcMuLost", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var lostWeapon = lost.Weapons.AddNew();
        lostWeapon.EditorID = "HcMuW2";
        var dep = new SkyrimMod(new ModKey("HcMuDep", ModType.Plugin), SkyrimRelease.SkyrimSE);
        dep.Weapons.GetOrAddAsOverride(lostWeapon).EditorID = "HcMuW2b";
        _depFile = Path.Combine(_root, DepPlugin);
        dep.BeginWrite.ToPath(_depFile).WithLoadOrder(new ISkyrimModGetter[] { lost }).Write();
        _lostFile = Path.Combine(_root, LostPlugin);
        lost.BeginWrite.ToPath(_lostFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        WholePath = MakeInstance("whole", new[] { MasterName, ModPlugin, LostPlugin }, new[] { "+ModA" });
        Junction(Path.Combine(WholePath, "mods"), Path.Combine(_root, "gone-whole"));

        _partial = MakeInstance("partial", new[] { MasterName, ModPlugin, LostPlugin, DepPlugin }, new[] { "+" + BrokenMod, "+ModA" });
        var modA = Path.Combine(_partial, "mods", "ModA");
        Directory.CreateDirectory(modA);
        File.Copy(_modFile, Path.Combine(modA, ModPlugin));
        File.Copy(_depFile, Path.Combine(modA, DepPlugin));
        Junction(Path.Combine(_partial, "mods", BrokenMod), Path.Combine(_root, "gone-mod"));

        _store = new UserConfigStore(Path.Combine(_root, "user.json"));
        Tools = new ToolPathResolver(_store);
    }

    public LoadOrderService Whole() => LoadOrderService.WithInstance(WholePath, 0, _store);
    public LoadOrderService Partial() => LoadOrderService.WithInstance(_partial, 0, _store);

    /// <summary>A fresh instance whose mods folder is a junction to a real folder, and that folder, to move away mid-session.</summary>
    public (LoadOrderService Svc, string ModsTarget) Toggle()
    {
        var inst = MakeInstance("toggle" + Interlocked.Increment(ref _toggles), new[] { MasterName, ModPlugin }, new[] { "+ModA" });
        var target = Path.Combine(_root, Path.GetFileName(inst) + "-mods");
        Directory.CreateDirectory(Path.Combine(target, "ModA"));
        File.Copy(_modFile, Path.Combine(target, "ModA", ModPlugin));
        Junction(Path.Combine(inst, "mods"), target, keepTarget: true);
        return (LoadOrderService.WithInstance(inst, 0, _store), target);
    }

    /// <summary>A fresh partial instance whose broken mod folder is a junction to a folder that holds the lost plugin
    /// (<paramref name="liveAtStart"/>) or does not exist yet, and that folder.</summary>
    public (LoadOrderService Svc, string ModTarget) PartialToggle(bool liveAtStart)
    {
        var inst = MakeInstance("ptoggle" + Interlocked.Increment(ref _toggles), new[] { MasterName, ModPlugin, LostPlugin },
                                new[] { "+" + BrokenMod, "+ModA" });
        Directory.CreateDirectory(Path.Combine(inst, "mods", "ModA"));
        File.Copy(_modFile, Path.Combine(inst, "mods", "ModA", ModPlugin));
        var target = Path.Combine(_root, Path.GetFileName(inst) + "-broken");
        Junction(Path.Combine(inst, "mods", BrokenMod), target, keepTarget: liveAtStart);
        if (liveAtStart) PutLostPlugin(target);
        return (LoadOrderService.WithInstance(inst, 0, _store), target);
    }

    /// <summary>A fresh instance whose one enabled mod holds the mod plugin marked hidden: its profile, mods and Data folders.</summary>
    public (string Profile, string Mods, string Data) HiddenPluginInstance()
    {
        var inst = MakeInstance("hidden" + Interlocked.Increment(ref _toggles), new[] { ModPlugin }, new[] { "+ModA" });
        var file = Path.Combine(inst, "mods", "ModA", ModPlugin);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(_modFile, file);
        File.SetAttributes(file, FileAttributes.Hidden);
        return (Path.Combine(inst, "profiles", "Default"), Path.Combine(inst, "mods"), Path.Combine(_root, "game", "Data"));
    }

    /// <summary>Make <paramref name="dir"/> hold the lost plugin.</summary>
    public void PutLostPlugin(string dir)
    {
        Directory.CreateDirectory(dir);
        File.Copy(_lostFile, Path.Combine(dir, LostPlugin), overwrite: true);
    }

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

    /// <summary>A junction to <paramref name="target"/>; unless kept, the target is then removed, so the link exists and cannot be followed.</summary>
    static void Junction(string link, string target, bool keepTarget = false)
    {
        Directory.CreateDirectory(target);
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            mk!.WaitForExit();
        if (!keepTarget) Directory.Delete(target);
        Assert.True(new DirectoryInfo(link).LinkTarget is not null, "the junction was not made");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ }
    }
}
