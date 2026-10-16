using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A pole plugin that will not open mid-call: an off-order NPC's fault is that file's partial sweep, an in-order one fails the sweep, both with the raw open fault.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleFaultTests : IDisposable
{
    readonly FaceGenPoleWorld _w = new("HcPfRace");
    readonly LoadOrderService _svc;
    readonly string _masterPath;

    public FaceGenPoleFaultTests()
    {
        // PfBase ships the master and bakes N0; Over (in order) and Off (on disk, not in the order) each give N0 another face.
        var master = new SkyrimMod(new ModKey("HcPfMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var n = master.Npcs.AddNew();
        n.EditorID = "HcPfNpc";
        n.Race.SetTo(_w.Race);
        FaceGenPoleWorld.Face(n, 10);
        var over = new SkyrimMod(new ModKey("HcPfOver", ModType.Plugin), SkyrimRelease.SkyrimSE);
        FaceGenPoleWorld.Face(over.Npcs.GetOrAddAsOverride(n), 200);
        var off = new SkyrimMod(new ModKey("HcPfOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        FaceGenPoleWorld.Face(off.Npcs.GetOrAddAsOverride(n), 99);
        _w.Bake("PfBase", n.FormKey);
        _masterPath = _w.Write(master, "PfBase", true, _w.RaceMod);
        _w.Write(over, "PfOver", true, _w.RaceMod, master);
        _w.Write(off, "PfOff", false, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

    FaceGenCheckResult WithPoleLocked(string plugin)
    {
        var warm = _svc.CheckFaceGen(new[] { plugin }, 1000);                  // unlocked, the pole is read and the row is a stale bake
        Assert.Null(warm.Error);
        Assert.Single(warm.Findings, f => f.Class == "stale_bake");
        using var hold = new FileStream(_masterPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return _svc.CheckFaceGen(new[] { plugin }, 1000);
    }

    [Fact]
    public void AnOffOrderNpcsPoleFaultIsThatFilesPartialSweepWithTheRawFault()
    {
        var r = WithPoleLocked("HcPfOff.esp");
        Assert.Null(r.Error);
        Assert.StartsWith("HcPfOff.esp: IOException: ", r.ScanError);
        Assert.DoesNotContain("could not read", r.ScanError);
        Assert.DoesNotContain(r.Findings, f => f.Class == "stale_bake");
    }

    [Fact]
    public void AnInOrderNpcsPoleFaultFailsTheSweepWithTheRawFault()
    {
        var r = WithPoleLocked("HcPfOver.esp");
        Assert.StartsWith("the facegen sweep could not finish — IOException: ", r.Error);
        Assert.DoesNotContain("PluginUnreadableException", r.Error);
    }
}
