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
    const string OverhaulName = "HcPbOverhaul.esp";
    const int NpcCount = 3;

    readonly FaceGenPoleWorld _w = new("HcPbRace");
    readonly LoadOrderService _svc;

    public FaceGenPoleBatchTests()
    {
        // Every NPC has a clean pair in PbBase, which ships the master; the overhaul wins each with another face colour.
        var master = new SkyrimMod(new ModKey("HcPbMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var overhaul = new SkyrimMod(new ModKey("HcPbOverhaul", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < NpcCount; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcPbNpc" + i;
            n.Race.SetTo(_w.Race);
            FaceGenPoleWorld.Face(n, 10);
            FaceGenPoleWorld.Face(overhaul.Npcs.GetOrAddAsOverride(n), 200);
            _w.Bake("PbBase", n.FormKey);
        }
        _w.Write(master, "PbBase", true, _w.RaceMod);
        _w.Write(overhaul, "PbOverhaul", true, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

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
        Assert.Equal(2, passes);                                                // the master's NPC group, for every pole, and the race
        Assert.Equal(0, seeks);
    }

    [Fact]
    public void APluginScopedCheckReadsTheWinnerAndPolePluginsOnceEach()
    {
        var (passes, seeks) = Cost(new[] { OverhaulName });
        Assert.Equal(3, passes);                                                // the overhaul's winners, the master's poles, the race
        Assert.Equal(0, seeks);
    }
}
