using System.Drawing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A small MO2 instance for the batched facegen pole reads: a race plugin, then plugins written in load order, each in its own mod.</summary>
sealed class FaceGenPoleWorld : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "hc-fg-poleorder-" + Guid.NewGuid().ToString("N"));
    public readonly string Instance;
    public readonly Race Race;
    public readonly SkyrimMod RaceMod;
    readonly string _profile;
    readonly List<(string Plugin, string Mod, bool Active)> _plugins = new();
    public LoadOrderService? Svc;

    public FaceGenPoleWorld(string raceName)
    {
        Instance = Path.Combine(Root, "instance");
        _profile = Path.Combine(Instance, "profiles", "Default");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        RaceMod = new SkyrimMod(new ModKey(raceName, ModType.Master), SkyrimRelease.SkyrimSE);
        Race = RaceMod.Races.AddNew();
        Race.EditorID = raceName + "Race";
        Race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        Write(RaceMod, raceName + "Mod");
    }

    public string ModDir(string mod) => Path.Combine(Instance, "mods", mod);

    // Writes the plugin into its own mod folder; an inactive one is on disk but in no profile list.
    public string Write(SkyrimMod mod, string modFolder, bool active = true, params ISkyrimModGetter[] masters)
    {
        var path = Path.Combine(ModDir(modFolder), mod.ModKey.FileName);
        Directory.CreateDirectory(ModDir(modFolder));
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        _plugins.Add((mod.ModKey.FileName, modFolder, active));
        return path;
    }

    public void Bake(string modFolder, FormKey npc)
    {
        foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
        {
            var p = Path.Combine(ModDir(modFolder), FaceGenPath.For(npc, slot));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }
    }

    public static void Face(Npc n, int shade) => n.TextureLighting = Color.FromArgb(0, shade, shade, shade);

    public LoadOrderService Open()
    {
        var active = _plugins.Where(p => p.Active).Select(p => p.Plugin).ToList();
        File.WriteAllText(Path.Combine(_profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", active) + "\r\n");
        File.WriteAllText(Path.Combine(_profile, "plugins.txt"), string.Concat(active.Select(p => "*" + p + "\r\n")));
        // modlist.txt is highest priority first.
        File.WriteAllText(Path.Combine(_profile, "modlist.txt"),
            "# header\r\n" + string.Concat(_plugins.Select(p => p.Mod).Distinct().Reverse().Select(m => "+" + m + "\r\n")));
        File.WriteAllText(Path.Combine(_profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=Skyrim - Meshes0.bsa\r\n");
        return Svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc?.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

/// <summary>A plugins= facegen check lists its rows in selection order, whichever winner plugin each NPC is read from.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleRowOrderTests : IDisposable
{
    const int NpcCount = 6;
    readonly FaceGenPoleWorld _w = new("HcPoRace");
    readonly LoadOrderService _svc;

    public FaceGenPoleRowOrderTests()
    {
        // Six master NPCs. Bake1 bakes N0-N2 and Bake2 N3-N5 (two pole plugins); A wins the even NPCs and B the odd,
        // so the winner groups interleave in selection order, and every NPC is a stale bake.
        var master = new SkyrimMod(new ModKey("HcPoMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var bake1 = new SkyrimMod(new ModKey("HcPoBake1", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bake2 = new SkyrimMod(new ModKey("HcPoBake2", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var a = new SkyrimMod(new ModKey("HcPoA", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var b = new SkyrimMod(new ModKey("HcPoB", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < NpcCount; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcPoNpc" + i;
            n.Race.SetTo(_w.Race);
            FaceGenPoleWorld.Face(n, 10);
            FaceGenPoleWorld.Face((i < 3 ? bake1 : bake2).Npcs.GetOrAddAsOverride(n), i < 3 ? 1 : 2);
            FaceGenPoleWorld.Face((i % 2 == 0 ? a : b).Npcs.GetOrAddAsOverride(n), i % 2 == 0 ? 200 : 150);
            _w.Bake(i < 3 ? "PoBake1" : "PoBake2", n.FormKey);
        }
        _w.Write(master, "PoMaster", true, _w.RaceMod);
        _w.Write(bake1, "PoBake1", true, _w.RaceMod, master);
        _w.Write(bake2, "PoBake2", true, _w.RaceMod, master);
        _w.Write(a, "PoA", true, _w.RaceMod, master);
        _w.Write(b, "PoB", true, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

    static readonly string[] Expected =
    {
        "HcPoNpc0|HcPoA.esp|HcPoBake1.esp", "HcPoNpc1|HcPoB.esp|HcPoBake1.esp", "HcPoNpc2|HcPoA.esp|HcPoBake1.esp",
        "HcPoNpc3|HcPoB.esp|HcPoBake2.esp", "HcPoNpc4|HcPoA.esp|HcPoBake2.esp", "HcPoNpc5|HcPoB.esp|HcPoBake2.esp",
    };

    static string Row(FaceGenFinding f)
    {
        Assert.Equal("stale_bake", f.Class);
        var d = f.Detail ?? "";
        string pole = d.Contains("own plugin HcPoBake1.esp") ? "HcPoBake1.esp" : d.Contains("own plugin HcPoBake2.esp") ? "HcPoBake2.esp" : "?";
        Assert.StartsWith("winner " + f.RecordWinner + " disagrees", d);
        return $"{f.EditorId}|{f.RecordWinner}|{pole}";
    }

    [Fact]
    public void RowsFollowSelectionOrderAcrossInterleavedWinnersAndTwoPoles()
    {
        var r = _svc.CheckFaceGen(new[] { "HcPoMaster.esm" }, 1000);
        Assert.Null(r.Error);
        Assert.Equal(Expected, r.Findings.Select(Row).ToArray());
    }

    [Fact]
    public void ALimitCutKeepsTheFirstRowsInSelectionOrder()
    {
        var r = _svc.CheckFaceGen(new[] { "HcPoMaster.esm" }, 4);
        Assert.Null(r.Error);
        Assert.Equal(Expected.Take(4).ToArray(), r.Findings.Select(Row).ToArray());
    }
}

/// <summary>A pole plugin that will not open mid-call: an off-order NPC's fault is that file's partial sweep, an in-order one fails the sweep, both with the raw open fault.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleFaultTests : IDisposable
{
    readonly FaceGenPoleWorld _w = new("HcPfRace");
    readonly LoadOrderService _svc;
    readonly string _masterPath;

    public FaceGenPoleFaultTests()
    {
        // PfBase ships the master and bakes N0; Over (in order) and Off (on disk, not in the order) each give N0 another face.
        var master = new SkyrimMod(new ModKey("HcPfMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var n = master.Npcs.AddNew();
        n.EditorID = "HcPfNpc";
        n.Race.SetTo(_w.Race);
        FaceGenPoleWorld.Face(n, 10);
        var over = new SkyrimMod(new ModKey("HcPfOver", ModType.Plugin), SkyrimRelease.SkyrimSE);
        FaceGenPoleWorld.Face(over.Npcs.GetOrAddAsOverride(n), 200);
        var off = new SkyrimMod(new ModKey("HcPfOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        FaceGenPoleWorld.Face(off.Npcs.GetOrAddAsOverride(n), 99);
        _w.Bake("PfBase", n.FormKey);
        _masterPath = _w.Write(master, "PfBase", true, _w.RaceMod);
        _w.Write(over, "PfOver", true, _w.RaceMod, master);
        _w.Write(off, "PfOff", false, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

    FaceGenCheckResult WithPoleLocked(string plugin)
    {
        var warm = _svc.CheckFaceGen(new[] { plugin }, 1000);                  // unlocked, the pole is read and the row is a stale bake
        Assert.Null(warm.Error);
        Assert.Single(warm.Findings, f => f.Class == "stale_bake");
        using var hold = new FileStream(_masterPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return _svc.CheckFaceGen(new[] { plugin }, 1000);
    }

    [Fact]
    public void AnOffOrderNpcsPoleFaultIsThatFilesPartialSweepWithTheRawFault()
    {
        var r = WithPoleLocked("HcPfOff.esp");
        Assert.Null(r.Error);
        Assert.StartsWith("HcPfOff.esp: IOException: ", r.ScanError);
        Assert.DoesNotContain("could not read", r.ScanError);
        Assert.DoesNotContain(r.Findings, f => f.Class == "stale_bake");
    }

    [Fact]
    public void AnInOrderNpcsPoleFaultFailsTheSweepWithTheRawFault()
    {
        var r = WithPoleLocked("HcPfOver.esp");
        Assert.StartsWith("the facegen sweep could not finish — IOException: ", r.Error);
        Assert.DoesNotContain("PluginUnreadableException", r.Error);
    }
}
