using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Injected records in a merge (#715): a record whose FormID sits in one donor's space while another plugin
/// defines it. When the definer is in the merge the record is renumbered like any other; when it is not, and when a
/// donor reference cannot be remapped at all, the merge refuses before it builds anything.</summary>
[Trait("tier", "integration")]
public sealed class MergeInjectedRecordTests : IClassFixture<MergeInjectedWorld>
{
    readonly MergeInjectedWorld _w;
    public MergeInjectedRecordTests(MergeInjectedWorld w) => _w = w;

    /// <summary>Space donor plus the donor that injects into it: the injected record is renumbered into the output, so
    /// the merged plugin names neither donor as a master and the write does not fault.</summary>
    [Fact]
    public void AnInjectedRecordWhoseDefinerIsAlsoADonorIsRenumberedIntoTheOutput()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Injector }, "HcInjCarried");

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(MergeInjectedWorld.Space, o.Masters);
        using var m = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.All(m.EnumerateMajorRecords(), r => Assert.Equal(Path.GetFileNameWithoutExtension(o.OutputName), r.FormKey.ModKey.Name));
    }

    /// <summary>The same record injected by a plugin OUTSIDE the merge cannot be renumbered correctly, so the merge
    /// refuses and names both the record and the plugin that defines it.</summary>
    [Fact]
    public void AnInjectedRecordWhoseDefinerIsOutsideTheMergeIsRefusedByName()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Overrider }, "HcInjOutside");

        Assert.False(o.Success);
        Assert.Contains(MergeInjectedWorld.Outsider, o.Error);
        Assert.Contains("000901:" + MergeInjectedWorld.Space, o.Error);
    }

    /// <summary>A donor reference into donor space that no donor defines is refused in the pre-flight, naming the
    /// referencing record — it used to survive the renumber and fail the serialize.</summary>
    [Fact]
    public void ADonorReferenceNoDonorDefinesIsRefusedBeforeAnythingIsBuilt()
    {
        var o = _w.Svc.MergePlugins(new[] { MergeInjectedWorld.Space, MergeInjectedWorld.Dangler }, "HcInjDangling");

        Assert.False(o.Success);
        Assert.Contains("no donor defines", o.Error);
        Assert.Contains(MergeInjectedWorld.Dangler, o.Error);
        Assert.False(Directory.Exists(Path.Combine(_w.ModsDir, "houseCARL - HcInjDangling")));
    }
}

/// <summary>A synthetic MO2 instance for the injected-record arms: a "space" plugin defining one weapon, a plugin
/// outside the merge that injects a record into that space, a donor that injects another one, a donor that merely
/// overrides the outsider's injected record, and a donor referencing a FormID in the space plugin that nothing
/// defines.</summary>
public sealed class MergeInjectedWorld : IDisposable
{
    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }

    public const string Space = "HcInjSpace.esp";        // the plugin whose FormID space is injected into
    public const string Outsider = "HcInjOutsider.esp";  // injects 0x901 into Space, never a donor
    public const string Injector = "HcInjInjector.esp";  // injects 0x900 into Space, a donor
    public const string Overrider = "HcInjOverrider.esp";// overrides the outsider's 0x901
    public const string Dangler = "HcInjDangler.esp";    // references 0x9FF in Space, which nothing defines

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
        }

        var order = new[] { Space, Outsider, Injector, Overrider, Dangler };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+DanglerMod\r\n+OverriderMod\r\n+InjectorMod\r\n+OutsiderMod\r\n+SpaceMod\r\n");

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
    /// (an injected record) or a reference to a FormID there that nothing defines.</summary>
    void WriteCarrier(string folder, ModKey key, ISkyrimModGetter space, uint? injected, bool dangling)
    {
        var dir = Path.Combine(ModsDir, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var own = new Weapon(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Own" };
        if (dangling) own.Template.SetTo(new FormKey(space.ModKey, 0x9FF));
        m.Weapons.Add(own);
        if (injected is { } id)
            m.Weapons.Add(new Weapon(new FormKey(space.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Injected" });
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String))
            .WithLoadOrder(new[] { space }).Write();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
    }
}
