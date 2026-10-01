using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A one-plugin synthetic MO2 instance for the compact carry tests: the plugin in its own mod folder, the
/// loose files the test drops beside it, and a service over it. Built fresh per test, because a compact writes a new
/// mod folder or rewrites the plugin in place.</summary>
public sealed class CompactCarryInstance : IDisposable
{
    public string Root { get; }
    public string ModDir { get; }
    public ModKey Key { get; }
    public string PluginName => Key.FileName.String;
    LoadOrderService? _svc;

    public CompactCarryInstance(string pluginStem, Action<SkyrimMod> build)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-compact-carry-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(Root, "mods");
        var prof = Path.Combine(Root, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(Root, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(Root, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        Key = new ModKey(pluginStem, ModType.Plugin);
        ModDir = Path.Combine(mods, pluginStem);
        Directory.CreateDirectory(ModDir);
        var m = new SkyrimMod(Key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(ModDir, PluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + PluginName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+" + pluginStem + "\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
    }

    /// <summary>Drops a loose file into the plugin's own mod folder.</summary>
    public void Loose(string rel, byte[] bytes)
    {
        var p = Path.Combine(ModDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>Runs the compact through the service, as the tool does.</summary>
    public WritePatchBuilder.CompactOutcome Compact(bool inPlace = false)
    {
        _svc ??= LoadOrderService.WithInstance(Root, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
        _svc.Stats();
        return inPlace ? _svc.CompactPlugin(PluginName, inPlace: true, acknowledge: true) : _svc.CompactPlugin(PluginName);
    }

    /// <summary>True when the file at <paramref name="path"/> exists and holds exactly <paramref name="bytes"/>.</summary>
    public static bool Holds(string path, byte[] bytes) => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes);

    public void Dispose()
    {
        _svc?.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
