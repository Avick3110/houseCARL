using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A gather over seek types that share groups reads each shared group once, not once per type.</summary>
[Trait("tier", "integration")]
public sealed class SharedGroupGatherTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-shared-group-");
    readonly ModKey _key = new("HcSharedGroup", ModType.Master);
    readonly FormKey _inCell, _inRef, _exCell, _exRef, _npc, _actor, _arrow, _hazard;

    public SharedGroupGatherTests()
    {
        _inCell = new FormKey(_key, 0xC01); _inRef = new FormKey(_key, 0xC02);
        _exCell = new FormKey(_key, 0xD01); _exRef = new FormKey(_key, 0xD02);
        _npc = new FormKey(_key, 0xE01);
        _actor = new FormKey(_key, 0xC03); _arrow = new FormKey(_key, 0xC04); _hazard = new FormKey(_key, 0xC05);
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var inCell = new Cell(_inCell, SkyrimRelease.SkyrimSE) { EditorID = "HcSgInterior", Flags = Cell.Flag.IsInteriorCell };
        inCell.Temporary.Add(new PlacedObject(_inRef, SkyrimRelease.SkyrimSE) { EditorID = "HcSgInRef" });
        inCell.Temporary.Add(new PlacedNpc(_actor, SkyrimRelease.SkyrimSE) { EditorID = "HcSgActor" });
        inCell.Temporary.Add(new PlacedArrow(_arrow, SkyrimRelease.SkyrimSE) { EditorID = "HcSgArrow" });
        inCell.Persistent.Add(new PlacedHazard(_hazard, SkyrimRelease.SkyrimSE) { EditorID = "HcSgHazard" });
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(inCell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        var exCell = new Cell(_exCell, SkyrimRelease.SkyrimSE) { EditorID = "HcSgExterior" };
        exCell.Temporary.Add(new PlacedObject(_exRef, SkyrimRelease.SkyrimSE) { EditorID = "HcSgExRef" });
        var ws = new Worldspace(new FormKey(_key, 0xF01), SkyrimRelease.SkyrimSE) { EditorID = "HcSgWorld" };
        var wb = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var wsub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        wsub.Items.Add(exCell); wb.Items.Add(wsub); ws.SubCells.Add(wb); m.Worldspaces.Add(ws);
        m.Npcs.Add(new Npc(_npc, SkyrimRelease.SkyrimSE) { EditorID = "HcSgNpc" });
        m.BeginWrite.ToPath(_mo2.InMod("SharedGroup", _key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(_key.FileName + "\r\n", "*" + _key.FileName + "\r\n", "+SharedGroup\r\n");
    }

    public void Dispose() => _mo2.Delete();

    /// <summary>Cells, references, an actor and two traps need both Cells and Worldspaces: one read each, plus Npcs for
    /// the NPC, every key typed with no flat walk. The traps walk the shared group as their own subtype.</summary>
    [Fact]
    public void CellsAndTheirReferencesReadTheSharedGroupsOnce()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var sink = new Dictionary<FormKey, IMajorRecordGetter>();
        var c = svc.Counters;
        var reads = new Dictionary<(string Plugin, string Group), int>(c.GroupReads);
        long passes = c.CollectPasses, typed = c.TypedCollectPasses;
        pin.View.CollectRecords(session, _key.FileName, new[] { _inCell, _inRef, _exCell, _exRef, _npc, _actor, _arrow, _hazard }, sink);
        Assert.IsAssignableFrom<ICellGetter>(sink[_inCell]);
        Assert.IsAssignableFrom<ICellGetter>(sink[_exCell]);
        Assert.Equal("HcSgInRef", Assert.IsAssignableFrom<IPlacedObjectGetter>(sink[_inRef]).EditorID);
        Assert.Equal("HcSgExRef", Assert.IsAssignableFrom<IPlacedObjectGetter>(sink[_exRef]).EditorID);
        Assert.IsAssignableFrom<INpcGetter>(sink[_npc]);
        Assert.Equal("HcSgActor", Assert.IsAssignableFrom<IPlacedNpcGetter>(sink[_actor]).EditorID);
        Assert.Equal("HcSgArrow", Assert.IsAssignableFrom<IPlacedArrowGetter>(sink[_arrow]).EditorID);
        Assert.Equal("HcSgHazard", Assert.IsAssignableFrom<IPlacedHazardGetter>(sink[_hazard]).EditorID);
        Assert.Equal(new Dictionary<string, int> { ["Cells"] = 1, ["Worldspaces"] = 1, ["Npcs"] = 1 }, GroupReadsSince(c, reads));
        Assert.Equal(1, c.CollectPasses - passes);
        Assert.Equal(1, c.TypedCollectPasses - typed);
    }

    /// <summary>The groups read since <paramref name="before"/>, by group name, with how many times each.</summary>
    internal static Dictionary<string, int> GroupReadsSince(HousecarlCore.CostCounters c, Dictionary<(string Plugin, string Group), int> before)
        => c.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                       .ToDictionary(kv => kv.Key.Group, kv => kv.Value - before.GetValueOrDefault(kv.Key));
}
