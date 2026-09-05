using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>load_order_status' lookup= reports a plugin's LOCALIZED header flag (#376), so the in-place write refusal
/// that flag causes can be seen coming instead of being met halfway through a job.</summary>
[Trait("tier", "integration")]
public sealed class StatusLocalizedLookupTests : IClassFixture<StatusLocalizedWorld>
{
    readonly StatusLocalizedWorld _w;
    public StatusLocalizedLookupTests(StatusLocalizedWorld w) => _w = w;

    string Lookup(string name) => StatusTools.LoadOrderStatus(_w.Svc, _w.Tools, lookup: name);

    [Fact]
    public void ALocalizedPluginIsNamedAsSuchWithTheInPlaceConsequence()
    {
        var text = Lookup(StatusLocalizedWorld.Localized);

        Assert.Contains("localized:", text);
        Assert.Contains("YES (header flag set)", text);
        Assert.Contains("IN-PLACE", text);
    }

    [Fact]
    public void APlainPluginSaysSoRatherThanSayingNothing()
    {
        var text = Lookup(StatusLocalizedWorld.Plain);

        Assert.Contains("no (header flag clear)", text);
    }

    /// <summary>A mod folder is not a plugin, so there is no header to read and no line to write — the flag must not
    /// be claimed for something that has none.</summary>
    [Fact]
    public void AModFolderLookupCarriesNoLocalizedLine()
    {
        Assert.DoesNotContain("localized:", Lookup("PlainMod"));
    }
}

/// <summary>A synthetic MO2 instance with one plugin flagged LOCALIZED in its header and one not. The flag is stamped
/// onto the written file's TES4 header rather than set through Mutagen, so the fixture needs no string tables.</summary>
public sealed class StatusLocalizedWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }

    public const string Localized = "HcLocLoc.esp";
    public const string Plain = "HcLocPlain.esp";

    /// <summary>The LOCALIZED bit in the TES4 record's flags field, which starts at byte 8 of a plugin file.</summary>
    const byte LocalizedBit = 0x80;
    const int HeaderFlagsOffset = 8;

    public StatusLocalizedWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-status-loc-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var locPath = Write(mods, "LocMod", new ModKey("HcLocLoc", ModType.Plugin));
        Write(mods, "PlainMod", new ModKey("HcLocPlain", ModType.Plugin));

        var bytes = File.ReadAllBytes(locPath);
        bytes[HeaderFlagsOffset] |= LocalizedBit;
        File.WriteAllBytes(locPath, bytes);

        var order = new[] { Localized, Plain };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), string.Join("\r\n", order.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+PlainMod\r\n+LocMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    static string Write(string mods, string folder, ModKey key)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0x801), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap" });
        var path = Path.Combine(dir, key.FileName.String);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
