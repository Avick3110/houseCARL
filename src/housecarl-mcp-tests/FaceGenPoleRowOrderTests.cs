using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugins= facegen check lists its rows in selection order, whichever winner plugin each NPC is read from.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenPoleRowOrderTests : IDisposable
{
    const int NpcCount = 6;
    readonly FaceGenPoleWorld _w = new("HcPoRace");
    readonly LoadOrderService _svc;

    public FaceGenPoleRowOrderTests()
    {
        // Six master NPCs. Bake1 bakes N0-N2 and Bake2 N3-N5 (two pole plugins); A wins the even NPCs and B the odd,
        // so the winner groups interleave in selection order, and every NPC is a stale bake.
        var master = new SkyrimMod(new ModKey("HcPoMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var bake1 = new SkyrimMod(new ModKey("HcPoBake1", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bake2 = new SkyrimMod(new ModKey("HcPoBake2", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var a = new SkyrimMod(new ModKey("HcPoA", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var b = new SkyrimMod(new ModKey("HcPoB", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < NpcCount; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcPoNpc" + i;
            n.Race.SetTo(_w.Race);
            FaceGenPoleWorld.Face(n, 10);
            FaceGenPoleWorld.Face((i < 3 ? bake1 : bake2).Npcs.GetOrAddAsOverride(n), i < 3 ? 1 : 2);
            FaceGenPoleWorld.Face((i % 2 == 0 ? a : b).Npcs.GetOrAddAsOverride(n), i % 2 == 0 ? 200 : 150);
            _w.Bake(i < 3 ? "PoBake1" : "PoBake2", n.FormKey);
        }
        _w.Write(master, "PoMaster", true, _w.RaceMod);
        _w.Write(bake1, "PoBake1", true, _w.RaceMod, master);
        _w.Write(bake2, "PoBake2", true, _w.RaceMod, master);
        _w.Write(a, "PoA", true, _w.RaceMod, master);
        _w.Write(b, "PoB", true, _w.RaceMod, master);
        _svc = _w.Open();
    }

    public void Dispose() => _w.Dispose();

    static readonly string[] Expected =
    {
        "HcPoNpc0|HcPoA.esp|HcPoBake1.esp", "HcPoNpc1|HcPoB.esp|HcPoBake1.esp", "HcPoNpc2|HcPoA.esp|HcPoBake1.esp",
        "HcPoNpc3|HcPoB.esp|HcPoBake2.esp", "HcPoNpc4|HcPoA.esp|HcPoBake2.esp", "HcPoNpc5|HcPoB.esp|HcPoBake2.esp",
    };

    static string Row(FaceGenFinding f)
    {
        Assert.Equal("stale_bake", f.Class);
        var d = f.Detail ?? "";
        string pole = d.Contains("own plugin HcPoBake1.esp") ? "HcPoBake1.esp" : d.Contains("own plugin HcPoBake2.esp") ? "HcPoBake2.esp" : "?";
        Assert.StartsWith("winner " + f.RecordWinner + " disagrees", d);
        return $"{f.EditorId}|{f.RecordWinner}|{pole}";
    }

    [Fact]
    public void RowsFollowSelectionOrderAcrossInterleavedWinnersAndTwoPoles()
    {
        var r = _svc.CheckFaceGen(new[] { "HcPoMaster.esm" }, 1000);
        Assert.Null(r.Error);
        Assert.Equal(Expected, r.Findings.Select(Row).ToArray());
    }

    [Fact]
    public void ALimitCutKeepsTheFirstRowsInSelectionOrder()
    {
        var r = _svc.CheckFaceGen(new[] { "HcPoMaster.esm" }, 4);
        Assert.Null(r.Error);
        Assert.Equal(Expected.Take(4).ToArray(), r.Findings.Select(Row).ToArray());
    }
}
