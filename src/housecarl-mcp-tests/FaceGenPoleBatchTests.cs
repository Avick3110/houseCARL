using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A facegen check over several stale-bake NPCs in one plugin reads that plugin's NPC group once, not once per NPC.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleBatchTests : IDisposable
{
    const string MasterName = "HcPbMaster.esm";
    const string OverhaulName = "HcPbOverhaul.esp";
    const int NpcCount = 3;

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-fg-polebatch-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;

    public FaceGenPoleBatchTests()
    {
        var instance = Path.Combine(_root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var baseDir = Path.Combine(instance, "mods", "PbBase");
        var overDir = Path.Combine(instance, "mods", "PbOverhaul");
        foreach (var d in new[] { profile, baseDir, overDir, Path.Combine(_root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        // Every NPC has a clean pair in PbBase, which ships the master; the overhaul wins each with another face colour.
        var master = new SkyrimMod(new ModKey("HcPbMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = master.Races.AddNew();
        race.EditorID = "HcPbRace";
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var overhaul = new SkyrimMod(new ModKey("HcPbOverhaul", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < NpcCount; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcPbNpc" + i;
            n.Race.SetTo(race);
            n.TextureLighting = System.Drawing.Color.FromArgb(0, 10, 20, 30);
            foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
            {
                var p = Path.Combine(baseDir, FaceGenPath.For(n.FormKey, slot));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
            overhaul.Npcs.GetOrAddAsOverride(n).TextureLighting = System.Drawing.Color.FromArgb(0, 200, 100, 50);
        }
        master.BeginWrite.ToPath(Path.Combine(baseDir, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        overhaul.BeginWrite.ToPath(Path.Combine(overDir, OverhaulName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + OverhaulName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + MasterName + "\r\n*" + OverhaulName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+PbOverhaul\r\n+PbBase\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=Skyrim - Meshes0.bsa\r\n");
        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    (long Passes, long Seeks) Cost(IReadOnlyList<string>? plugins)
    {
        _svc.CheckFaceGen(plugins, 1000);                                       // warms the index and the asset build
        long passes = _svc.Counters.CollectPasses, seeks = _svc.Counters.BodySeeks;
        var r = _svc.CheckFaceGen(plugins, 1000);
        Assert.Equal(NpcCount, r.Findings.Count(f => f.Class == "stale_bake"));
        return (_svc.Counters.CollectPasses - passes, _svc.Counters.BodySeeks - seeks);
    }

    [Fact]
    public void AWholeOrderCheckReadsThePolePluginsNpcGroupOnce()
    {
        var (passes, seeks) = Cost(null);
        Assert.Equal(1, passes);                                                // the master's NPC group, for every pole
        Assert.Equal(1, seeks);                                                 // the race alone
    }

    [Fact]
    public void APluginScopedCheckReadsTheWinnerAndPolePluginsOnceEach()
    {
        var (passes, seeks) = Cost(new[] { OverhaulName });
        Assert.Equal(2, passes);                                                // the overhaul's winners, the master's poles
        Assert.Equal(1, seeks);                                                 // the race alone
    }
}
