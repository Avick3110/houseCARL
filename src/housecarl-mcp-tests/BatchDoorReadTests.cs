using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Two plugins: Base has two keywords and two armors carrying both; Extra a keyword and two weapons.</summary>
public sealed class BatchDoorReadWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-batch-door-");
    public readonly ModKey Base = new("HcBatchBase", ModType.Master), Extra = new("HcBatchExtra", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public FormKey Armor1 { get; }
    public FormKey Armor2 { get; }
    public FormKey Weapon1 { get; }
    public FormKey Weapon2 { get; }
    public string BaseFile { get; }

    public BatchDoorReadWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var kw = Enumerable.Range(1, 2).Select(i => { var k = b.Keywords.AddNew(); k.EditorID = "HcBatchKw" + i; return k; }).ToList();
        var armors = Enumerable.Range(1, 2).Select(i =>
        {
            var a = b.Armors.AddNew();
            a.EditorID = "HcBatchArmor" + i;
            a.Keywords = new() { kw[0].ToLink(), kw[1].ToLink() };
            return a;
        }).ToList();
        BaseFile = _mo2.InMod("BatchBase", Base);
        b.BeginWrite.ToPath(BaseFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var x = new SkyrimMod(Extra, SkyrimRelease.SkyrimSE);
        var own = x.Keywords.AddNew();
        own.EditorID = "HcBatchKw3";
        var weapons = Enumerable.Range(1, 2).Select(i =>
        {
            var w = x.Weapons.AddNew();
            w.EditorID = "HcBatchWeapon" + i;
            w.Keywords = new() { kw[0].ToLink(), own.ToLink() };
            return w;
        }).ToList();
        x.BeginWrite.ToPath(_mo2.InMod("BatchExtra", Extra)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        (Armor1, Armor2, Weapon1, Weapon2) = (armors[0].FormKey, armors[1].FormKey, weapons[0].FormKey, weapons[1].FormKey);
        var names = new[] { Base, Extra }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                     "+BatchExtra\r\n+BatchBase\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>Two plugins: Base defines a cell and one reference in it; Extra restates the cell and adds a second reference.</summary>
public sealed class BatchParentWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-batch-parent-");
    public readonly ModKey Base = new("HcBatchCellBase", ModType.Master), Extra = new("HcBatchCellExtra", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public FormKey Ref1 { get; }
    public FormKey Ref2 { get; }

    public BatchParentWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var cell = new Cell(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcBatchCell", Flags = Cell.Flag.IsInteriorCell };
        var ref1 = new PlacedObject(b.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcBatchRef1" };
        cell.Temporary.Add(ref1);
        AddCell(b, cell);
        b.BeginWrite.ToPath(_mo2.InMod("CellBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var x = new SkyrimMod(Extra, SkyrimRelease.SkyrimSE);
        var restated = new Cell(cell.FormKey, SkyrimRelease.SkyrimSE) { EditorID = cell.EditorID, Flags = cell.Flags };
        var ref2 = new PlacedObject(x.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcBatchRef2" };
        restated.Temporary.Add(ref2);
        AddCell(x, restated);
        x.BeginWrite.ToPath(_mo2.InMod("CellExtra", Extra)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        (Ref1, Ref2) = (ref1.FormKey, ref2.FormKey);
        var names = new[] { Base, Extra }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                     "+CellExtra\r\n+CellBase\r\n");
        Svc = _mo2.Open();
    }

    static void AddCell(SkyrimMod m, Cell c)
    {
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(c);
        block.SubBlocks.Add(sub);
        m.Cells.Records.Add(block);
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>A batch whose rows sit in two plugins reads their one container group once.</summary>
[Trait("tier", "integration")]
public sealed class BatchParentReadTests : IClassFixture<BatchParentWorld>
{
    readonly BatchParentWorld _w;
    public BatchParentReadTests(BatchParentWorld w) => _w = w;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    // Each row's body reads its own plugin's Cells group; the cell, won by Extra, is read once more for both rows.
    [Fact]
    public void ABatchAcrossTwoPluginsReadsTheContainerGroupOnce()
    {
        var reads = GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Ref1), Fid(_w.Ref2) },
                                          project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "*parent.EditorID" } }),
                                      "HcBatchRef1", "HcBatchRef2");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [("HcBatchCellBase.esm", "Cells")] = 1, [("HcBatchCellExtra.esp", "Cells")] = 2,
        }, reads);
    }

    // One row to a pass: the two rows are two runs, so the cell's group is gathered once per run on top of Ref2's own read.
    [Fact]
    public void EachRunOfPassRowsGathersItsOwnContainers()
    {
        var prior = _w.Svc.ReadArea.BatchPassRows;
        _w.Svc.ReadArea.BatchPassRows = 1;
        try
        {
            var reads = GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Ref1), Fid(_w.Ref2) },
                                              project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "*parent.EditorID" } }),
                                          "HcBatchRef1", "HcBatchRef2");
            Assert.Equal(new Dictionary<(string, string), int>
            {
                [("HcBatchCellBase.esm", "Cells")] = 1, [("HcBatchCellExtra.esp", "Cells")] = 3,
            }, reads);
        }
        finally { _w.Svc.ReadArea.BatchPassRows = prior; }
    }
}

/// <summary>A formids= batch reads its rows and names a group at a time and answers one row per input, in order.</summary>
[Trait("tier", "integration")]
public sealed class BatchDoorReadTests : IClassFixture<BatchDoorReadWorld>
{
    readonly BatchDoorReadWorld _w;
    public BatchDoorReadTests(BatchDoorReadWorld w) => _w = w;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    // Interleaved across the two plugins, with a parse error and a FormID of a plugin outside the order between them.
    string[] Rows => new[] { Fid(_w.Armor1), Fid(_w.Weapon1), "zzz", Fid(_w.Armor2), "123456:HcBatchNope.esp", Fid(_w.Weapon2) };

    string Read(string[] rows) => RecordsTools.Records(_w.Svc, formids: rows, project: new RecordsTools.RecordsProject
                                                      { form = "fields", fields = new[] { "Keywords" }, depth = 2, resolve_names = true });

    [Fact]
    public void ABatchAcrossTwoPluginsReadsEachRowGroupAndEachNameGroupOnce()
    {
        var reads = GroupReadDelta.Of(_w.Svc, () => Read(Rows), "(→ HcBatchKw2)", "(→ HcBatchKw3)");
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [("HcBatchBase.esm", "Armors")] = 1, [("HcBatchBase.esm", "Keywords")] = 1,
            [("HcBatchExtra.esp", "Weapons")] = 1, [("HcBatchExtra.esp", "Keywords")] = 1,
        }, reads);
    }

    Dictionary<(string, string), int> RowReads(int passRows)
    {
        var prior = _w.Svc.ReadArea.BatchPassRows;
        _w.Svc.ReadArea.BatchPassRows = passRows;
        try
        {
            var rows = new[] { Fid(_w.Armor1), Fid(_w.Weapon1), Fid(_w.Armor2), Fid(_w.Weapon2) };
            return GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, formids: rows, project: new RecordsTools.RecordsProject
                                                                        { form = "fields", fields = new[] { "Keywords" } }),
                                     "HcBatchArmor2", "HcBatchWeapon2");
        }
        finally { _w.Svc.ReadArea.BatchPassRows = prior; }
    }

    // Interleaved rows, two to a pass: sorted by group, each group's two rows are one read.
    [Fact]
    public void InterleavedRowsAreSortedSoEachGroupIsReadOncePerPass()
        => Assert.Equal(new Dictionary<(string, string), int> { [("HcBatchBase.esm", "Armors")] = 1, [("HcBatchExtra.esp", "Weapons")] = 1 },
                        RowReads(2));

    // One row to a pass: a group with two rows is read twice, so the cap holds inside one group.
    [Fact]
    public void AGroupWithMoreRowsThanOnePassIsReadOncePerPass()
        => Assert.Equal(new Dictionary<(string, string), int> { [("HcBatchBase.esm", "Armors")] = 2, [("HcBatchExtra.esp", "Weapons")] = 2 },
                        RowReads(1));

    [Fact]
    public void AFormIdOutsideTheOrderWithNoPluginIsItsOwnRowErrorAndTheOtherRowsStillAnswer()
    {
        var r = Read(new[] { "123456:HcBatchNope.esp", Fid(_w.Weapon1) });
        Assert.Contains("is not present in the load order", r);
        Assert.Contains("(→ HcBatchKw3)", r);
    }

    // Base held open: the weapon's Base keyword names Base's fault and is not fetched again; its own keyword still resolves.
    [Fact]
    public void ANameTargetInAPluginThatCannotBeReadNamesItsFault()
    {
        string r;
        using (new FileStream(_w.BaseFile, FileMode.Open, FileAccess.Read, FileShare.None))
            r = Read(new[] { Fid(_w.Weapon1) });
        Assert.Contains("(unresolved: could not read 'HcBatchBase.esm'", r);
        Assert.Contains("(→ HcBatchKw3)", r);
    }

    [Fact]
    public void RowsAnswerInInputOrderWithTheParseErrorInItsSlot()
    {
        var r = Read(Rows);
        var at = new[] { "HcBatchArmor1", "HcBatchWeapon1", "bad FormID 'zzz'", "HcBatchArmor2", "HcBatchNope.esp", "HcBatchWeapon2" }
                 .Select(m => r.IndexOf(m, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0, r));
        Assert.Equal(at.Order().ToList(), at);
    }
}
