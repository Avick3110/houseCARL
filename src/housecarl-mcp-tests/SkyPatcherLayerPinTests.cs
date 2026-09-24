using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer takes its index view, asset view and overlay session from one build (#879).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLayerPinTests : IDisposable
{
    const string First = "HcPinA.esp";
    const string Second = "HcPinB.esp";
    const string PluginMod = "HcPinPlugins";
    const string IniMod = "HcPinIni";

    readonly string _root;
    readonly string _profileDir;
    readonly LoadOrderService _svc;
    int _profileWrites;

    public SkyPatcherLayerPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-pin-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        _profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var data = Path.Combine(_root, "game", "Data");
        var iniDir = Path.Combine(mods, IniMod, "SKSE", "Plugins", "SkyPatcher", "weapon");
        foreach (var d in new[] { _profileDir, data, Path.Combine(mods, PluginMod), iniDir })
            Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace("\\", "\\\\") + ")\r\n");

        // The first plugin defines the weapon the INI targets; the second does not.
        var a = new SkyrimMod(ModKey.FromFileName(First), SkyrimRelease.SkyrimSE);
        a.Weapons.Add(new Weapon(new FormKey(a.ModKey, 0x800), SkyrimRelease.SkyrimSE)
                      { EditorID = "HcPinWeapon", BasicStats = new WeaponBasicStats { Damage = 10 } });
        a.BeginWrite.ToPath(Path.Combine(mods, PluginMod, First)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var b = new SkyrimMod(ModKey.FromFileName(Second), SkyrimRelease.SkyrimSE);
        b.Weapons.AddNew().EditorID = "HcPinOther";
        b.BeginWrite.ToPath(Path.Combine(mods, PluginMod, Second)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A SET to the value the weapon already has: one no-op write.
        File.WriteAllText(Path.Combine(iniDir, "Pin.ini"), $"filterByWeapons={First}|800:attackDamage=10\r\n");

        File.WriteAllText(Path.Combine(_profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        WriteProfile(First, Second, iniModEnabled: true);

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>Write the plugin order and mod list, with an mtime set from a counter.</summary>
    void WriteProfile(string first, string second, bool iniModEnabled)
    {
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_profileWrites);
        var loadorder = Path.Combine(_profileDir, "loadorder.txt");
        var plugins = Path.Combine(_profileDir, "plugins.txt");
        var modlist = Path.Combine(_profileDir, "modlist.txt");
        File.WriteAllText(loadorder, $"# header\r\n{first}\r\n{second}\r\n");
        File.WriteAllText(plugins, $"*{first}\r\n*{second}\r\n");
        File.WriteAllText(modlist, $"# header\r\n{(iniModEnabled ? "+" : "-")}{IniMod}\r\n+{PluginMod}\r\n");
        foreach (var p in new[] { loadorder, plugins, modlist }) File.SetLastWriteTimeUtc(p, when);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>On a warm service, a profile rewrite right after the pin changes neither the INIs scanned nor the plugins replayed.</summary>
    [Fact]
    public void ARefreshAfterThePinDoesNotSplitTheLayersBuild()
    {
        Assert.Single(_svc.SkyPatcherLayer().NoOps);                           // warms the index and the asset build
        _svc.AssetArea.AfterSkyPatcherPinForGuard = () => WriteProfile(Second, First, iniModEnabled: false);

        var layer = _svc.SkyPatcherLayer();

        Assert.Equal(0, _svc.CaptureView().OrderIndexOf(Second));              // the rewrite landed for the next call
        Assert.Contains(layer.Scan.Folders.SelectMany(f => f.Files), f => f.RelPath.EndsWith("Pin.ini", StringComparison.OrdinalIgnoreCase));
        var noOp = Assert.Single(layer.NoOps);
        Assert.Equal("HcPinWeapon", noOp.EditorId);
        Assert.DoesNotContain(layer.NoOpNotes, n => n.Contains("could not be replayed", StringComparison.Ordinal));
    }
}
