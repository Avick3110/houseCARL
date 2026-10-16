using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A locked pole shared by an in-order NPC and an off-order one fails the whole facegen sweep, not just the off-order file.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleMixedFaultTests : IDisposable
{
    readonly FaceGenPoleWorld _w = new("HcPmRace");
    readonly LoadOrderService _svc;
    readonly string _masterPath;
    static readonly string[] Plugins = { "HcPmOver.esp", "HcPmOff.esp" };

    public FaceGenPoleMixedFaultTests()
    {
        // PmBase ships the master and bakes both NPCs; Over (in order) wins N0, Off (on disk, not in the order) gives N1 another face.
        var master = new SkyrimMod(new ModKey("HcPmMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var over = new SkyrimMod(new ModKey("HcPmOver", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var off = new SkyrimMod(new ModKey("HcPmOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 2; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcPmNpc" + i;
            n.Race.SetTo(_w.Race);
            FaceGenPoleWorld.Face(n, 10);
            FaceGenPoleWorld.Face((i == 0 ? over : off).Npcs.GetOrAddAsOverride(n), 200);
            _w.Bake("PmBase", n.FormKey);
        }
        _masterPath = _w.Write(master, "PmBase", true, _w.RaceMod);
        _w.Write(over, "PmOver", true, _w.RaceMod, master);
        _w.Write(off, "PmOff", false, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

    [Fact]
    public void APoleFaultSharedByInOrderAndOffOrderNpcsFailsTheSweep()
    {
        var warm = _svc.CheckFaceGen(Plugins, 1000);                           // unlocked, both NPCs are stale bakes on one pole
        Assert.Null(warm.Error);
        Assert.Equal(new[] { "HcPmNpc0", "HcPmNpc1" },
                     warm.Findings.Where(f => f.Class == "stale_bake").Select(f => f.EditorId).ToArray());
        using var hold = new FileStream(_masterPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = _svc.CheckFaceGen(Plugins, 1000);
        Assert.StartsWith("the facegen sweep could not finish — IOException: ", r.Error);
        Assert.Null(r.ScanError);
    }
}
