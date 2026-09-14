using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A donor whose master is not active in the load order is refused from the donor HEADERS (#729), before the
/// whole-order identify pass the merge would otherwise run first.</summary>
[Trait("tier", "integration")]
public sealed class MergeMissingMasterPreflightTests : IClassFixture<MergeMissingMasterWorld>
{
    readonly MergeMissingMasterWorld _w;
    public MergeMissingMasterPreflightTests(MergeMissingMasterWorld w) => _w = w;

    /// <summary>The refusal names the absent master and says to enable it, and nothing is written.</summary>
    [Fact]
    public void ADonorWhoseMasterIsInactiveIsRefusedByName()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeMissingMasterWorld.Orphan }, "HcMmOrphanRename");

        Assert.False(o.Success);
        Assert.Contains(MergeMissingMasterWorld.AbsentMaster, o.Error);
        Assert.Contains("Enable that master first", o.Error);
    }

    /// <summary>And it is refused WITHOUT the identify pass: that walk reads every plugin in the order and is the
    /// expensive half of a merge, so a header-level refusal must arrive before it.</summary>
    [Fact]
    public void TheRefusalSkipsTheIdentifyPass()
    {
        var before = RemapEngine.IdentifyPasses;

        var o = _w.Svc.MergePlugins(new[] { MergeMissingMasterWorld.Orphan }, "HcMmNoIdentify");

        Assert.False(o.Success);
        Assert.Equal(0, RemapEngine.IdentifyPasses - before);
    }

    /// <summary>The control: a donor whose masters are all active merges, and that one does run the identify pass —
    /// so the arm above is measuring the skip, not a counter that never moves.</summary>
    [Fact]
    public void AnIntactDonorStillRunsTheIdentifyPass()
    {
        var before = RemapEngine.IdentifyPasses;

        var o = _w.Svc.MergePlugins(new[] { MergeMissingMasterWorld.Intact }, "HcMmIntactRename");

        Assert.True(o.Success, o.Error);
        Assert.Equal(1, RemapEngine.IdentifyPasses - before);
    }
}

/// <summary>A synthetic MO2 instance with two masters — one activated, one installed but NOT activated — and two
/// donors, each defining a weapon templated on one of them. The donor over the inactive master is the orphan.</summary>
public sealed class MergeMissingMasterWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string ActiveMaster = "HcMmActive.esm";
    public const string AbsentMaster = "HcMmAbsent.esm";
    public const string Intact = "HcMmIntact.esp";     // masters the ACTIVE master
    public const string Orphan = "HcMmOrphan.esp";     // masters the plugin that is installed but not activated

    public MergeMissingMasterWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-missing-master-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var activeKey = new ModKey("HcMmActive", ModType.Master);
        var absentKey = new ModKey("HcMmAbsent", ModType.Master);
        var activePath = WriteMaster(mods, "ActiveMasterMod", activeKey);
        var absentPath = WriteMaster(mods, "AbsentMasterMod", absentKey);
        WriteDependent(mods, "IntactMod", new ModKey("HcMmIntact", ModType.Plugin), activeKey, activePath);
        WriteDependent(mods, "OrphanMod", new ModKey("HcMmOrphan", ModType.Plugin), absentKey, absentPath);

        // AbsentMaster is installed and its mod folder is enabled, but the PLUGIN is not activated — the shape the
        // issue measured (a master unticked under a profile that still carries its dependents).
        var order = new[] { ActiveMaster, Intact, Orphan };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+OrphanMod\r\n+IntactMod\r\n+AbsentMasterMod\r\n+ActiveMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static string WriteMaster(string mods, string folder, ModKey key)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap" });
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    /// <summary>A donor with one record of its own whose template points into the master, so its header declares that
    /// master.</summary>
    static void WriteDependent(string mods, string folder, ModKey key, ModKey masterKey, string masterPath)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        using var master = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var w = new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Own" };
        w.Template.SetTo(new FormKey(masterKey, 0x800));
        m.Weapons.Add(w);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String))
            .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
    }
}
