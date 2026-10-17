using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A FLST linking four armors, two cells holding more references than one scan chunk, and three plugins:
/// Base defines everything, Mid re-states three armors and the first cell, Top re-states three armors and both cells.</summary>
public sealed class DoorGroupReadWorld : IDisposable
{
    /// <summary>References in the first cell: exactly one scan chunk, so the second cell's lone reference starts another.</summary>
    public const int RefsInA = 10_000;

    readonly ScratchMo2 _mo2 = new("hc-door-reads-");
    public readonly ModKey Base = new("HcDoorBase", ModType.Master), Mid = new("HcDoorMid", ModType.Plugin), Top = new("HcDoorTop", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public string Root => _mo2.Root;
    public FormKey Armor1 { get; }

    public DoorGroupReadWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var armors = Enumerable.Range(1, 3).Select(i => { var a = b.Armors.AddNew(); a.EditorID = "HcDoorArmor" + i; return a; }).ToList();
        var list = b.FormLists.AddNew();
        list.EditorID = "HcDoorList";
        // A fourth armor only Base holds, last in the list: the scan meets it after Top's, the door before.
        var baseOnly = b.Armors.AddNew();
        baseOnly.EditorID = "HcDoorArmor4";
        baseOnly.BodyTemplate = new BodyTemplate { ArmorType = ArmorType.Clothing };
        foreach (var a in armors.Append(baseOnly)) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(a.FormKey));
        var cellA = new Cell(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDoorCellA", Flags = Cell.Flag.IsInteriorCell, Name = "HcDoorRoomA" };
        for (int i = 0; i < RefsInA; i++) cellA.Temporary.Add(new PlacedObject(b.GetNextFormKey(), SkyrimRelease.SkyrimSE));
        var cellB = new Cell(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDoorCellB", Flags = Cell.Flag.IsInteriorCell, Name = "HcDoorRoomB" };
        cellB.Temporary.Add(new PlacedObject(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDoorRefB", Base = new FormLinkNullable<IPlaceableObjectGetter>(armors[0].FormKey) });
        Armor1 = armors[0].FormKey;
        AddCells(b, cellA, cellB);
        b.BeginWrite.ToPath(_mo2.InMod("DoorBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var mid = Overrider(Mid, armors);
        AddCells(mid, Restated(cellA));
        mid.BeginWrite.ToPath(_mo2.InMod("DoorMid", Mid)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();
        var top = Overrider(Top, armors);
        top.Armors[armors[0].FormKey].BodyTemplate = new BodyTemplate { ArmorType = ArmorType.HeavyArmor };
        AddCells(top, Restated(cellA), Restated(cellB));
        top.BeginWrite.ToPath(_mo2.InMod("DoorTop", Top)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        var names = new[] { Base, Mid, Top }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                     "+DoorTop\r\n+DoorMid\r\n+DoorBase\r\n");
        Svc = _mo2.Open();
    }

    static SkyrimMod Overrider(ModKey key, IEnumerable<Armor> armors)
    {
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        foreach (var a in armors) m.Armors.Add(new Armor(a.FormKey, SkyrimRelease.SkyrimSE) { EditorID = a.EditorID });
        return m;
    }

    static Cell Restated(Cell c) => new(c.FormKey, SkyrimRelease.SkyrimSE) { EditorID = c.EditorID, Flags = c.Flags, Name = c.Name?.String };

    static void AddCells(SkyrimMod m, params Cell[] cells)
    {
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        foreach (var c in cells) sub.Cells.Add(c);
        block.SubBlocks.Add(sub);
        m.Cells.Records.Add(block);
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>Through the read door a scan reads each plugin's group once per call: the scanned type's group once per
/// pass over the candidates, and each group of targets or containers once, however many keys and chunks ask for them.</summary>
[Trait("tier", "integration")]
public sealed class DoorGroupReadTests : IClassFixture<DoorGroupReadWorld>
{
    readonly DoorGroupReadWorld _w;
    public DoorGroupReadTests(DoorGroupReadWorld w) => _w = w;

    Dictionary<(string, string), int> Run(Func<string> call, string expect) => GroupReadDelta.Of(_w.Svc, call, expect);

    string B => _w.Base.FileName.String;
    string M => _w.Mid.FileName.String;
    string T => _w.Top.FileName.String;

    /// <summary>The three armors Top wins are read in one walk of Top's Armors group, not one per armor. Base's FormLists
    /// is read twice: the collect pass and the scan each read it once. Mid and Top have no FormLists group to read.</summary>
    [Fact]
    public void ALinkScanReadsTheTargetGroupOnce()
    {
        var d = Run(() => RecordsTools.Records(_w.Svc, types: new[] { "FormList" }, where: new[] { "Items[*any]->editorid startswith HcDoorArmor" }),
                    "HcDoorList");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "FormLists")] = 2,
            [(B, "Armors")] = 1, [(T, "Armors")] = 1,
        }, d);
    }

    /// <summary>The '*all' and '*none' folds collect their targets the same way, so the same groups are read as many times.</summary>
    [Theory]
    [InlineData("Items[*all]->editorid startswith HcDoorArmor")]
    [InlineData("Items[*none]->editorid = HcDoorNoSuchArmor")]
    public void EveryFoldReadsTheTargetGroupOnce(string where)
    {
        var d = Run(() => RecordsTools.Records(_w.Svc, types: new[] { "FormList" }, where: new[] { where }), "HcDoorList");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "FormLists")] = 2,
            [(B, "Armors")] = 1, [(T, "Armors")] = 1,
        }, d);
    }

    /// <summary>A typed error names the record the scan meets first in candidate order (Top's heavy armor), not the one
    /// the door meets first in load order (Base's clothing).</summary>
    [Fact]
    public void ALinkScanErrorNamesTheCandidateOrderRecord()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "FormList" }, where: new[] { "Items[*any]->BodyTemplate.ArmorType > 5" });
        Assert.Contains("read 'HeavyArmor'", r);
    }

    /// <summary>The same for a '*parent' typed error: a container judged under an error is not memoised, so the scan
    /// names the cell its first reference sits in.</summary>
    [Fact]
    public void AParentScanErrorNamesTheCandidateOrderRecord()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "PlacedObject" }, where: new[] { "*parent.Name > 5" });
        Assert.True(r.Contains("read 'HcDoorRoomA'"), r);
    }

    /// <summary>Both cells, won by Top and asked for by two scan chunks, come from one read of Top's Cells group. The
    /// candidates come off the index, so the references are streamed once, by the scan. No plugin has a Worldspaces group.</summary>
    [Fact]
    public void AParentScanReadsTheContainerGroupOnce()
    {
        var d = Run(() => RecordsTools.Records(_w.Svc, types: new[] { "PlacedObject" }, where: new[] { "*parent.EditorID = HcDoorCellB" }),
                    "HcDoorRefB");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "Cells")] = 1, [(M, "Cells")] = 1, [(T, "Cells")] = 2,
        }, d);
    }

    /// <summary>A formid universe larger than one chunk, filtered with no where=, reads its winners' group once.</summary>
    [Fact]
    public void AFormidUniverseReadsEachWinnerGroupOnce()
    {
        var list = Path.Combine(_w.Root, "refs-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var w = RecordsTools.Records(_w.Svc, types: new[] { "PlacedObject" }, to_file: list);
        Assert.False(w.StartsWith("error", StringComparison.Ordinal), w);
        var d = Run(() => RecordsTools.Records(_w.Svc, formids: new[] { "@" + list },
                                               references: new[] { ScratchMo2.Fid(_w.Armor1) }),
                    "HcDoorRefB");
        Assert.Equal(new Dictionary<(string, string), int> { [(B, "Cells")] = 1 }, d);
    }
}
