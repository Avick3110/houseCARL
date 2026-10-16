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
