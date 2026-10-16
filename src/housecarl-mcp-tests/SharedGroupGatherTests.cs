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
    readonly FormKey _inCell, _inRef, _exCell, _exRef, _npc;

    public SharedGroupGatherTests()
    {
        _inCell = new FormKey(_key, 0xC01); _inRef = new FormKey(_key, 0xC02);
        _exCell = new FormKey(_key, 0xD01); _exRef = new FormKey(_key, 0xD02);
        _npc = new FormKey(_key, 0xE01);
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var inCell = new Cell(_inCell, SkyrimRelease.SkyrimSE) { EditorID = "HcSgInterior", Flags = Cell.Flag.IsInteriorCell };
        inCell.Temporary.Add(new PlacedObject(_inRef, SkyrimRelease.SkyrimSE) { EditorID = "HcSgInRef" });
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

    /// <summary>Cells and references need both Cells and Worldspaces: one read each, plus Npcs for the NPC. Walked
    /// per type it is five (Cells and Worldspaces for the cells, again for the references, then Npcs).</summary>
    [Fact]
    public void CellsAndTheirReferencesReadTheSharedGroupsOnce()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var sink = new Dictionary<FormKey, IMajorRecordGetter>();
        var c = svc.Counters;
        long copies = c.GroupCopies, passes = c.CollectPasses, typed = c.TypedCollectPasses;
        pin.View.CollectRecords(session, _key.FileName, new[] { _inCell, _inRef, _exCell, _exRef, _npc }, sink);
        Assert.IsAssignableFrom<ICellGetter>(sink[_inCell]);
        Assert.IsAssignableFrom<ICellGetter>(sink[_exCell]);
        Assert.Equal("HcSgInRef", Assert.IsAssignableFrom<IPlacedObjectGetter>(sink[_inRef]).EditorID);
        Assert.Equal("HcSgExRef", Assert.IsAssignableFrom<IPlacedObjectGetter>(sink[_exRef]).EditorID);
        Assert.IsAssignableFrom<INpcGetter>(sink[_npc]);
        Assert.Equal(3, c.GroupCopies - copies);
        Assert.Equal(1, c.CollectPasses - passes);
        Assert.Equal(1, c.TypedCollectPasses - typed);
    }
}
