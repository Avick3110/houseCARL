using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer reads its index view and its overlay session off ONE resolver (#879). It read the
/// resolver getter twice, so a profile refresh between the two handed the replay a session from a different build than
/// the view it scanned against: plugin indices from one order, overlays opened from another.</summary>
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

        // The first plugin defines the one weapon the INI targets; the second defines nothing it names.
        var a = new SkyrimMod(ModKey.FromFileName(First), SkyrimRelease.SkyrimSE);
        a.Weapons.Add(new Weapon(new FormKey(a.ModKey, 0x800), SkyrimRelease.SkyrimSE)
                      { EditorID = "HcPinWeapon", BasicStats = new WeaponBasicStats { Damage = 10 } });
        a.BeginWrite.ToPath(Path.Combine(mods, PluginMod, First)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var b = new SkyrimMod(ModKey.FromFileName(Second), SkyrimRelease.SkyrimSE);
        b.Weapons.AddNew().EditorID = "HcPinOther";
        b.BeginWrite.ToPath(Path.Combine(mods, PluginMod, Second)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A SET to the value the weapon already carries: the replay reports it as a no-op write, but only when the
        // session it opens reads the same plugin the view says holds the record.
        File.WriteAllText(Path.Combine(iniDir, "Pin.ini"), $"filterByWeapons={First}|800:attackDamage=10\r\n");

        File.WriteAllText(Path.Combine(_profileDir, "modlist.txt"), $"# header\r\n+{IniMod}\r\n+{PluginMod}\r\n");
        File.WriteAllText(Path.Combine(_profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        WriteOrder(First, Second);

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>Write the plugin order. A reorder keeps both files' lengths, so the mtime is SET from a counter to
    /// carry the change.</summary>
    void WriteOrder(string first, string second)
    {
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_profileWrites);
        var loadorder = Path.Combine(_profileDir, "loadorder.txt");
        var plugins = Path.Combine(_profileDir, "plugins.txt");
        File.WriteAllText(loadorder, $"# header\r\n{first}\r\n{second}\r\n");
        File.WriteAllText(plugins, $"*{first}\r\n*{second}\r\n");
        File.SetLastWriteTimeUtc(loadorder, when);
        File.SetLastWriteTimeUtc(plugins, when);
    }

    public void Dispose()
    {
        _svc.BeforeSkyPatcherSessionForGuard = null;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    /// <summary>The order is flipped after the view is taken and before the session opens. Read off one pinned
    /// resolver, the replay still finds the weapon in the plugin the view named and reports the no-op write; read off
    /// the getter a second time, the session is the flipped build and the view's plugin index opens the other file.</summary>
    [Fact]
    public void ARefreshBetweenTheViewAndTheSessionDoesNotSplitTheLayersBuild()
    {
        _svc.BeforeSkyPatcherSessionForGuard = () => WriteOrder(Second, First);

        var layer = _svc.SkyPatcherLayer();
        _svc.BeforeSkyPatcherSessionForGuard = null;

        Assert.Equal(0, _svc.CaptureView().OrderIndexOf(Second));              // the flip did land: the next read follows it
        var noOp = Assert.Single(layer.NoOps);
        Assert.Equal("HcPinWeapon", noOp.EditorId);
        Assert.DoesNotContain(layer.NoOpNotes, n => n.Contains("could not be replayed", StringComparison.Ordinal));
    }
}
