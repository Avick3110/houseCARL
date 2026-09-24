using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The decompiler's class-parent cache across an instance switch: a scan that snapshotted the old mods folder
/// must not publish the old tree as the complete map once the switch has invalidated it.</summary>
[Trait("tier", "unit")]
public sealed class ClassParentsInstanceSwitchTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-class-parents-switch-" + Guid.NewGuid().ToString("N"));
    readonly string _oldInstance;
    readonly string _newInstance;
    readonly LoadOrderService _svc;

    public ClassParentsInstanceSwitchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _oldInstance = StageInstance("old", "HcOldTreeChild", "HcOldTreeHost");
        _newInstance = StageInstance("new", "HcNewTreeChild", "HcNewTreeHost");
        _svc = LoadOrderService.WithInstance(_oldInstance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>One MO2 instance whose mods tree holds one loose .psc naming <paramref name="child"/> extends <paramref name="parent"/>.</summary>
    string StageInstance(string name, string child, string parent)
    {
        var instance = Path.Combine(_root, name);
        var profile = Path.Combine(instance, "profiles", "Default");
        var source = Path.Combine(instance, "mods", "ScriptMod", "Scripts", "Source");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+ScriptMod\r\n");
        File.WriteAllText(Path.Combine(source, child + ".psc"), $"ScriptName {child} extends {parent}\r\n");
        return instance;
    }

    public void Dispose()
    {
        _svc.BeforeClassParentsPublishForGuard = null;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void AnInstanceSwitchMidScanDoesNotCacheTheOldTree()
    {
        // The switch lands after the old mods folder is snapshotted under the gate and before the scan publishes.
        _svc.BeforeClassParentsPublishForGuard = () => _svc.SetInstance(_newInstance);

        var during = _svc.ClassParentsForDecompile();
        _svc.BeforeClassParentsPublishForGuard = null;

        Assert.False(during.Edges.ContainsKey("HcOldTreeChild"));
        Assert.Contains("the MO2 instance changed during this call", during.TopUpMissing);

        var after = _svc.ClassParentsForDecompile();

        Assert.Equal("HcNewTreeHost", after.Edges["HcNewTreeChild"]);
        Assert.False(after.Edges.ContainsKey("HcOldTreeChild"));
        Assert.Null(after.TopUpMissing);
    }
}
