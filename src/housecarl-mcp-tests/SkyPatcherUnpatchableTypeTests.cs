using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A record type SkyPatcher cannot touch reads as its winner on the overlay post state, through both the source read and the pole (#880).</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherUnpatchableTypeTests : IDisposable
{
    const string PluginName = "HcUnpatchable.esp";
    const string PluginMod = "HcUnpatchablePlugins";
    const string IniMod = "HcUnpatchableIni";
    const string KeywordEid = "HcUnpatchableKeyword";

    readonly string _root;
    readonly LoadOrderService _svc;
    readonly string _keyword;

    public SkyPatcherUnpatchableTypeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-skypatcher-unpatchable-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniDir = Path.Combine(mods, IniMod, "SKSE", "Plugins", "SkyPatcher", "weapon");
        foreach (var d in new[] { profileDir, Path.Combine(_root, "game", "Data"), Path.Combine(mods, PluginMod), iniDir })
            Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace('\\', '/') + ")\r\n");

        // A keyword has no SkyPatcher field map; the weapon gives the layer one live line.
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        var kw = new Keyword(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = KeywordEid };
        mod.Keywords.Add(kw);
        mod.Weapons.Add(new Weapon(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE)
                        { EditorID = "HcUnpatchableWeapon", BasicStats = new WeaponBasicStats { Damage = 10 } });
        mod.BeginWrite.ToPath(Path.Combine(mods, PluginMod, PluginName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _keyword = "000800:" + PluginName;

        File.WriteAllText(Path.Combine(iniDir, "Unpatchable.ini"), $"filterByWeapons={PluginName}|801:attackDamage=12\r\n");

        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), $"# header\r\n{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"*{PluginName}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), $"# header\r\n+{IniMod}\r\n+{PluginMod}\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static JsonElement PostPole => JsonDocument.Parse("{\"overlay\": \"skypatcher\", \"state\": \"post\"}").RootElement.Clone();

    [Fact]
    public void AnUnpatchableTypeReadsAsItsWinnerOnTheSourceReadAndThePole()
    {
        // Source read: the post state of an unpatchable record is its winner, served rather than failed.
        var source = RecordsTools.Records(_svc, formids: new[] { _keyword }, source: PostPole,
                                          project: new() { form = "fields", fields = new[] { "EditorID" } });
        Assert.False(source.StartsWith("error:", StringComparison.Ordinal), source);
        Assert.Contains(KeywordEid, source);
        Assert.DoesNotContain("not a SkyPatcher-patchable type", source);

        // Pole: the post side is the winner, named as such, so the delta against the winner is empty.
        var pole = RecordsTools.Records(_svc, formids: new[] { _keyword }, versus: PostPole,
                                        project: new() { form = "delta", fields = new[] { "EditorID" } });
        Assert.False(pole.StartsWith("error:", StringComparison.Ordinal), pole);
        Assert.Contains("type not SkyPatcher-patchable", pole);
        Assert.DoesNotContain("not a SkyPatcher-patchable type", pole);
    }
}
