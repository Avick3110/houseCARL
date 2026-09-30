using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world whose switched-OFF mod carries forty weapons: enough rows that the off-order scan's summary is wider
/// than its floor, spill block included, so a cap the server serves can cut it (#986). The shared worlds' off-order
/// files are a few records, whose whole answer is narrower than a spill block.</summary>
public sealed class OffOrderScanWorld : IDisposable
{
    public string Root { get; }
    public string OffName { get; }
    public LoadOrderService Svc { get; }

    public OffOrderScanWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-offscan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        var masterKey = new ModKey("HcOffScanMaster", ModType.Master);
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        master.Keywords.AddNew().EditorID = "HcOffScanKw";

        var offKey = ModKey.FromNameAndExtension("HcOffScan.esp");
        OffName = offKey.FileName.String;
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        for (int i = 1; i <= 40; i++)
        {
            var w = off.Weapons.AddNew();
            w.EditorID = $"HcOffScanLongSwordNumber{i:D2}";
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)i, Weight = 1 };
        }

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "OffScanMasterMod"));
        Directory.CreateDirectory(Path.Combine(mods, "OffScanOffMod"));
        var masterName = masterKey.FileName.String;
        master.BeginWrite.ToPath(Path.Combine(mods, "OffScanMasterMod", masterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        off.BeginWrite.ToPath(Path.Combine(mods, "OffScanOffMod", OffName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + masterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + masterName + "\r\n");
        // OffScanOffMod is switched OFF: its plugin is on disk and out of the active order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n-OffScanOffMod\r\n+OffScanMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The off-order scan's cut notice, read at a cap the server serves the call cut.</summary>
[Trait("tier", "integration")]
public sealed class OffOrderScanCutTests : IClassFixture<OffOrderScanWorld>
{
    readonly OffOrderScanWorld _w;
    public OffOrderScanCutTests(OffOrderScanWorld w) => _w = w;

    // Moved from RecordsRemedyGrammarTests, whose world's off-order file is too small to cut above its floor.
    [Fact]
    public void TheOffOrderScanNamesNoneEither_ItPassesNoFieldPathsAtAll()
    {
        var source = System.Text.Json.JsonDocument.Parse("\"" + _w.OffName + "\"").RootElement.Clone();
        var (_, text) = RenderFloorAssert.ServedCut(
            c => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: source, max_chars: c),
            t => t.Contains(" returned matches before hitting", StringComparison.Ordinal));
        var cut = text.Split('\n').First(l => l.Contains("... [truncated: rendered") && l.Contains(" returned matches before hitting"));

        Assert.Contains("lower limit= or raise max_chars", cut);
        Assert.DoesNotContain("drop ", cut);
    }
}
