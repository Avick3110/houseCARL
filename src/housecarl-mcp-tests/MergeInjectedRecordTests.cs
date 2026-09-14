using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Injected records in a merge (#715): a record whose FormID sits in one donor's space while another plugin
/// carries it. It is renumbered into the output like any other record, a plugin outside the merge carrying it too is
/// warned about, and a donor reference no donor holds is refused before anything is built.</summary>
[Trait("tier", "integration")]
public sealed class MergeInjectedRecordTests : IClassFixture<MergeInjectedWorld>
{
    readonly MergeInjectedWorld _w;
    public MergeInjectedRecordTests(MergeInjectedWorld w) => _w = w;

    /// <summary>Space donor plus the donor that injects into it: the injected record is renumbered into the output, so
    /// the merged plugin names neither donor as a master and the write does not fault.</summary>
    [Fact]
    public void AnInjectedRecordCarriedByADonorIsRenumberedIntoTheOutput()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Injector }, "HcInjCarried");

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(MergeInjectedWorld.Space, o.Masters);
        using var m = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.All(m.EnumerateMajorRecords(), r => Assert.Equal(Path.GetFileNameWithoutExtension(o.OutputName), r.FormKey.ModKey.Name));
    }

    /// <summary>A plugin outside the merge that carries the same injected record is a dependent like any other: the
    /// merge renumbers the record and NAMES that plugin in the external-overrider warning, rather than refusing over a
    /// claim about which plugin defines it — which the load order cannot settle.</summary>
    [Fact]
    public void APluginOutsideTheMergeCarryingTheSameInjectedRecordIsWarnedAboutByName()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Overrider }, "HcInjOutside");

        Assert.True(o.Success, o.Error);
        Assert.Contains(MergeInjectedWorld.Outsider, o.ExternalOverriders);
        Assert.Contains(MergeInjectedWorld.Outsider, WriteTools.RenderMerge(o));
    }

    /// <summary>A donor reference into donor space that no donor holds is refused in the pre-flight, naming the
    /// referencing record — it used to survive the renumber and fail the serialize.</summary>
    [Fact]
    public void ADonorReferenceNoDonorHoldsIsRefusedBeforeAnythingIsBuilt()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Dangler }, "HcInjDangling");

        Assert.False(o.Success);
        Assert.Contains("no donor holds", o.Error);
        Assert.Contains(MergeInjectedWorld.Dangler, o.Error);
        Assert.False(Directory.Exists(Path.Combine(_w.ModsDir, "houseCARL - HcInjDangling")));
    }

    /// <summary>A DELETED record's links are not live, so the donor scan does not read them: the same stale reference
    /// that refuses the merge above is ignored here, and the merge goes through.</summary>
    [Fact]
    public void ADeletedRecordsStaleReferenceDoesNotRefuseTheMerge()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Deleted }, "HcInjDeletedOut");

        Assert.True(o.Success, o.Error);
    }
}

/// <summary>A synthetic MO2 instance for the injected-record arms: a "space" plugin defining one weapon, a plugin
/// outside the merge that injects a record into that space, a donor that injects another one, a donor that merely
/// overrides the outsider's injected record, and two donors referencing a FormID in the space plugin that nothing
/// holds — one of them on a deleted record.</summary>
public sealed class MergeInjectedWorld : IDisposable
{
    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }

    public const string Space = "HcInjSpace.esp";        // the plugin whose FormID space is injected into
    public const string Outsider = "HcInjOutsider.esp";  // injects 0x901 into Space, never a donor
    public const string Injector = "HcInjInjector.esp";  // injects 0x900 into Space, a donor
    public const string Overrider = "HcInjOverrider.esp";// overrides the outsider's 0x901
    public const string Dangler = "HcInjDangler.esp";    // references 0x9FF in Space, which nothing holds
    public const string Deleted = "HcInjDeleted.esp";    // same reference, on a DELETED record

    public MergeInjectedWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-merge-injected-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        ModsDir = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, ModsDir, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var spaceKey = new ModKey("HcInjSpace", ModType.Plugin);
        var spacePath = WriteSpace(spaceKey);
        using (var space = SkyrimMod.CreateFromBinaryOverlay(spacePath, SkyrimRelease.SkyrimSE))
        {
            // Each of these carries a record under the SPACE plugin's ModKey — an injected record, which Mutagen
            // writes with that plugin as a master.
            WriteCarrier("OutsiderMod", new ModKey("HcInjOutsider", ModType.Plugin), space, injected: 0x901, dangling: false);
            WriteCarrier("InjectorMod", new ModKey("HcInjInjector", ModType.Plugin), space, injected: 0x900, dangling: false);
            WriteCarrier("OverriderMod", new ModKey("HcInjOverrider", ModType.Plugin), space, injected: 0x901, dangling: false);
            WriteCarrier("DanglerMod", new ModKey("HcInjDangler", ModType.Plugin), space, injected: null, dangling: true);
            WriteCarrier("DeletedMod", new ModKey("HcInjDeleted", ModType.Plugin), space, injected: null, dangling: true, deleted: true);
        }

        var order = new[] { Space, Outsider, Injector, Overrider, Dangler, Deleted };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+DeletedMod\r\n+DanglerMod\r\n+OverriderMod\r\n+InjectorMod\r\n+OutsiderMod\r\n+SpaceMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    string WriteSpace(ModKey key)
    {
        var dir = Path.Combine(ModsDir, "SpaceMod");
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcInjSpaceWeap" });
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    /// <summary>A plugin holding one record of its own plus, optionally, a record in the space plugin's FormID space
    /// (an injected record), a reference to a FormID there that nothing holds, and that reference on a DELETED record.</summary>
    void WriteCarrier(string folder, ModKey key, ISkyrimModGetter space, uint? injected, bool dangling, bool deleted = false)
    {
        var dir = Path.Combine(ModsDir, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var own = new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Own" };
        if (dangling) own.Template.SetTo(new FormKey(space.ModKey, 0x9FF));
        if (deleted) own.IsDeleted = true;
        m.Weapons.Add(own);
        if (injected is { } id)
            m.Weapons.Add(new Weapon(new FormKey(space.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Injected" });
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String))
            .WithLoadOrder(new[] { space }).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { /* temp cleanup best-effort */ }
    }
}
