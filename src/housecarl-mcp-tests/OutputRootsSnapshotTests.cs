using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A rider folder's stem is checked against the plugins of the instance its roots came from, even when the instance switches between the capture and the check.</summary>
[Trait("tier", "integration")]
public sealed class OutputRootsSnapshotTests : IDisposable
{
    const string TakenStem = "HcSnapTaken";

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-output-snapshot-" + Guid.NewGuid().ToString("N"));
    readonly string _instanceA;
    readonly string _instanceB;
    readonly LoadOrderService _svc;

    public OutputRootsSnapshotTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _instanceA = StageInstance("a", TakenStem + ".esp");
        _instanceB = StageInstance("b", "HcSnapOther.esp");
        _svc = LoadOrderService.WithInstance(_instanceA, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>One MO2 instance with its own mods folder and one active plugin, <paramref name="plugin"/>.</summary>
    string StageInstance(string name, string plugin)
    {
        var instance = Path.Combine(_root, name);
        var profile = Path.Combine(instance, "profiles", "Default");
        var mod = Path.Combine(instance, "mods", "PluginMod");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        new SkyrimMod(ModKey.FromFileName(plugin), SkyrimRelease.SkyrimSE).BeginWrite
            .ToPath(Path.Combine(mod, plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+PluginMod\r\n");
        return instance;
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void AnInstanceSwitchAfterTheCaptureDoesNotChangeWhichPluginsTakeAStem()
    {
        _ = ((ILoadOrderHost)_svc).Resolver;                      // instance A's order built, so the capture carries its names
        var snapshotA = _svc.ConfiguredRoots();
        Assert.Contains(TakenStem + ".esp", snapshotA.BuiltPluginNames!);

        // The switch lands between the capture and the stem check, and B's order is built too.
        _svc.SetInstance(_instanceB);
        _ = ((ILoadOrderHost)_svc).Resolver;

        var folder = _svc.ResolvePatchModFolder(snapshotA, TakenStem, null, "HcSnapDefault", null);

        // TakenStem.esp is active in A, whose mods folder the new folder lands in, so the stem is suffixed.
        Assert.Equal(TakenStem + "_001", folder.Stem);
        Assert.Equal(Path.Combine(_instanceA, "mods"), Path.GetDirectoryName(folder.ModFolder));
    }
}
