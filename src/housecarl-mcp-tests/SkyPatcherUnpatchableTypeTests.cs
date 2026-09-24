using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A record type SkyPatcher cannot touch reads as its winner on the overlay post state, through both the source read and the pole (#880).</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class SkyPatcherUnpatchableTypeTests : RecordsTestBase
{
    public SkyPatcherUnpatchableTypeTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AnUnpatchableTypeReadsAsItsWinnerOnTheSourceReadAndThePole()
    {
        var source = RecordsTools.Records(Svc, formids: new[] { Fid(W.Keyword) }, source: Overlay("post"), project: Fields("EditorID"));
        Served(source, "EditorID = HcRecKeyword");

        // The post pole is the winner itself, so the delta against the winner is empty and the arm says why.
        var pole = RecordsTools.Records(Svc, formids: new[] { Fid(W.Keyword) }, versus: Overlay("post"),
                                        project: new() { form = "delta", fields = new[] { "EditorID" } });
        Served(pole, "0 differing, 1 identical", "patchable");
    }
}

/// <summary>The layer scan's no-op pass counts an INI target of an unpatchable type as a record it could not replay (#880).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherUnpatchableLayerCountTests : IDisposable
{
    const string PluginName = "HcUnpatchable.esp";
    readonly string _root;
    readonly LoadOrderService _svc;

    public SkyPatcherUnpatchableLayerCountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-unpatchable-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniDir = Path.Combine(mods, "HcUnpatchableIni", "SKSE", "Plugins", "SkyPatcher", "weapon");
        foreach (var d in new[] { profileDir, Path.Combine(_root, "game", "Data"), Path.Combine(mods, "HcUnpatchablePlugins"), iniDir })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcUnpatchableKeyword" });
        mod.BeginWrite.ToPath(Path.Combine(mods, "HcUnpatchablePlugins", PluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // A weapon line that names the keyword by FormID: an explicit target the replay cannot patch.
        File.WriteAllText(Path.Combine(iniDir, "Keyword.ini"), $"filterByWeapons={PluginName}|800:attackDamage=10\r\n");

        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), $"# header\r\n{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcUnpatchableIni\r\n+HcUnpatchablePlugins\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void AnUnpatchableTargetCountsAsARecordTheScanCouldNotReplay()
        => Assert.Contains(_svc.SkyPatcherLayer().NoOpNotes, n => n.Contains("1 targeted"));
}
