using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin ticked in <c>plugins.txt</c> whose only copy sits in a mod folder <c>modlist.txt</c> has
/// switched off (#957): MO2's VFS does not serve the file, so the game does not load it whatever the tick says.</summary>
public sealed class TickedPluginSwitchedOffModWorld : IDisposable
{
    public string Root { get; }
    public string OffName => "HcTsOff.esp";
    public string OffMod => "HcTsOffMod";
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }

    public TickedPluginSwitchedOffModWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-ticked-switched-off-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var vanillaDir = Path.Combine(mods, "VanillaStub");
        var offDir = Path.Combine(mods, OffMod);
        foreach (var d in new[] { vanillaDir, offDir }) Directory.CreateDirectory(d);

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = sky.Races.AddNew(); race.EditorID = "HcTsVanillaRace";
        sky.BeginWrite.ToPath(Path.Combine(vanillaDir, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var off = new SkyrimMod(new ModKey("HcTsOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kw = off.Keywords.AddNew(); kw.EditorID = "HcTsOffKeyword";
        off.BeginWrite.ToPath(Path.Combine(offDir, OffName)).WithLoadOrder(new ISkyrimModGetter[] { sky }).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\nSkyrim.esm\r\n" + OffName + "\r\n");
        // The tick with nothing served behind it: the plugin is checked, its only folder is switched off.
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*" + OffName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n-" + OffMod + "\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TickedPluginSwitchedOffModFixture : IDisposable
{
    public TickedPluginSwitchedOffModWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}


/// <summary>A plugin the VFS does not serve is not active, whatever plugins.txt says: every lane that answers
/// "is it active" says not, and names the switched-off folder holding the copy.</summary>
[Trait("tier", "integration")]
public sealed class TickedPluginSwitchedOffModTests : IClassFixture<TickedPluginSwitchedOffModFixture>
{
    readonly TickedPluginSwitchedOffModWorld W;
    public TickedPluginSwitchedOffModTests(TickedPluginSwitchedOffModFixture f) => W = f.W;

    static JsonElement Je(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    [Fact]
    public void TheStatusFilterCallsTheTickedPluginNotActiveAndNamesTheSwitchedOffFolder()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools, filter: W.OffName);
        var line = Assert.Single(r.Split('\n'), l => l.Contains("as a plugin:", StringComparison.Ordinal));
        Assert.Contains("NOT ACTIVE", line);
        Assert.Contains($"mod '{W.OffMod}', which is switched OFF", line);
    }

    [Fact]
    public void TheStatusSummaryDoesNotCountTheTickedPluginActive()
    {
        var r = StatusTools.LoadOrderStatus(W.Svc, W.Tools);
        // Skyrim.esm is the one implicit master; the ticked plugin is not served, so no checked plugin is active.
        Assert.Contains("active:   1  (0 checked + 1 implicit", r);
        Assert.Contains($"load order lists '{W.OffName}', but it is provided by mod '{W.OffMod}', which is switched OFF", r);
    }

    [Fact]
    public void APlainNameSourceReadsTheSwitchedOffCopyAndSaysWhereItIs()
    {
        var r = RecordsTools.Records(W.Svc, source: Je(JsonSerializer.Serialize(W.OffName)), types: new[] { "KYWD" });
        Assert.Contains($"OUT-OF-LOAD-ORDER (mod '{W.OffMod}' (DISABLED); NOT active — that mod folder is switched OFF", r);
        Assert.Contains("HcTsOffKeyword", r);
    }

    [Fact]
    public void TheFileAndModSourceReadsTheSwitchedOffCopy()
    {
        var r = RecordsTools.Records(W.Svc, source: Je("{\"file\":\"" + W.OffName + "\",\"mod\":\"" + W.OffMod + "\"}"),
                                     types: new[] { "KYWD" });
        Assert.Contains("OUT-OF-LOAD-ORDER", r);
        Assert.Contains("HcTsOffKeyword", r);
    }

    [Fact]
    public void AWinnerReadRefusalNamesTheSwitchedOffFolderAndTheOffOrderSpelling()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { "000800:" + W.OffName });
        Assert.Contains($"mod '{W.OffMod}', which is switched OFF", r);
        Assert.Contains("source={\"file\": \"" + W.OffName + "\", \"mod\": \"" + W.OffMod + "\"}", r);
        Assert.DoesNotContain("stale", r);
    }
}
