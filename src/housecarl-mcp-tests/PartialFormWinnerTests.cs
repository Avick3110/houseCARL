using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1209 read half: a partial-form override (record flag 0x4000) loads none of its data in game, so it does
/// not win. The winner is the full copy below it, for every read; the partial's plugin still touches the key.</summary>
[Trait("tier", "integration")]
public sealed class PartialFormWinnerTests : IDisposable
{
    const string MasterName = "HcPfMaster.esm";
    const string MasterPartialName = "HcPfMasterPartial.esm";
    const string PartialName = "HcPfPartial.esp";
    const int Partial = 0x4000;

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-partial-form-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;
    readonly LoadOrderService _svc;
    readonly FormKey _cell;        // the master's interior cell; the esp overrides it as a partial form
    readonly FormKey _ref;         // a placed object in that cell
    readonly FormKey _location;    // the cell's location, which only the full copy carries
    readonly FormKey _masterCell;  // a master interior cell; the master-flagged plugin overrides it as a partial form
    readonly FormKey _gridCell;    // a temporary exterior cell; the esp overrides it with 0x4000 set
    readonly FormKey _plainCell;   // a master interior cell the esp overrides normally

    public PartialFormWinnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var master = new SkyrimMod(new ModKey("HcPfMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var loc = master.Locations.AddNew(); loc.EditorID = "HcPfLocation";
        _location = loc.FormKey;
        var obj = master.Statics.AddNew(); obj.EditorID = "HcPfStatic";
        Cell Interior(FormKey fk, string edid) => new(fk, SkyrimRelease.SkyrimSE)
            { EditorID = edid, Flags = Cell.Flag.IsInteriorCell | Cell.Flag.PublicArea };
        var cell = Interior(master.GetNextFormKey(), "HcPfCell");
        cell.Location.SetTo(loc);
        var placed = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPfRef" };
        placed.Base.SetTo(obj);
        cell.Temporary.Add(placed);
        var masterCell = Interior(master.GetNextFormKey(), "HcPfMasterCell");
        var plainCell = Interior(master.GetNextFormKey(), "HcPfPlainCell");
        FileInterior(master, cell, masterCell, plainCell);
        var ws = new Worldspace(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcPfWorld" };
        var grid = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            { EditorID = "HcPfGridCell", Flags = Cell.Flag.HasWater, Grid = new CellGrid { Point = new P2Int(0, 0) } };
        FileExterior(ws, grid);
        master.Worldspaces.Add(ws);
        _cell = cell.FormKey; _ref = placed.FormKey; _masterCell = masterCell.FormKey;
        _gridCell = grid.FormKey; _plainCell = plainCell.FormKey;

        // Each override carries its EDID only, as an xEdit-made partial form does (Mutagen pads the rest).
        Cell Over(Cell of, int flags, Cell.Flag cellFlags) => new(of.FormKey, SkyrimRelease.SkyrimSE)
            { EditorID = of.EditorID, MajorRecordFlagsRaw = flags, Flags = cellFlags, Grid = of.Grid?.DeepCopy() };

        var masterPartial = new SkyrimMod(new ModKey("HcPfMasterPartial", ModType.Master), SkyrimRelease.SkyrimSE);
        FileInterior(masterPartial, Over(masterCell, Partial, 0));

        var esp = new SkyrimMod(ModKey.FromNameAndExtension(PartialName), SkyrimRelease.SkyrimSE);
        FileInterior(esp, Over(cell, Partial, 0), Over(plainCell, 0, Cell.Flag.IsInteriorCell));
        var espWs = new Worldspace(ws.FormKey, SkyrimRelease.SkyrimSE) { EditorID = ws.EditorID };
        FileExterior(espWs, Over(grid, Partial, 0));
        esp.Worldspaces.Add(espWs);

        _mods = Path.Combine(_root, "inst", "mods");
        void Place(string mod, SkyrimMod m, params ISkyrimModGetter[] order)
        {
            Directory.CreateDirectory(Path.Combine(_mods, mod));
            m.BeginWrite.ToPath(Path.Combine(_mods, mod, m.ModKey.FileName)).WithLoadOrder(order).Write();
        }
        Place("MasterMod", master);
        Place("MasterPartialMod", masterPartial, master);
        Place("PartialMod", esp, master);

        var inst = Path.Combine(_root, "inst");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(inst, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var names = new[] { MasterName, MasterPartialName, PartialName };
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", names) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("", names.Select(n => "*" + n + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+PartialMod\r\n+MasterPartialMod\r\n+MasterMod\r\n");
        _svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static void FileInterior(SkyrimMod m, params Cell[] cells)
    {
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        foreach (var c in cells) sub.Cells.Add(c);
        block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
    }

    static void FileExterior(Worldspace ws, Cell cell)
    {
        var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        sub.Items.Add(cell); blk.Items.Add(sub); ws.SubCells.Add(blk);
    }

    static string Fid(FormKey fk) => FormIdToken.Of(fk);

    string Fields(FormKey fk, JsonElement? source, params string[] fields) =>
        RecordsTools.Records(_svc, formids: new[] { Fid(fk) }, source: source,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = fields });

    static JsonElement Pole(string plugin) => JsonDocument.Parse(JsonSerializer.Serialize(plugin)).RootElement.Clone();

    [Fact]
    public void ResolveNamesTheFullCopyBelowAPartialFormAsTheWinner()
    {
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_cell)!.Value.WinnerPlugin);
        var r = Fields(_cell, null, "Location", "Flags");
        Assert.Contains("winner=" + MasterName, r);
        Assert.Contains(Fid(_location), r);
        Assert.Contains("PublicArea", r);
    }

    [Fact]
    public void TheTreeLabelsAndDiffsAgainstTheFullCopy()
    {
        var r = RecordsTools.Records(_svc, formids: new[] { Fid(_cell) },
                                     project: new RecordsTools.RecordsProject { form = "tree", fields = new[] { "Location" } });
        Assert.Contains("1. " + MasterName + "  (winner)", r);
        Assert.DoesNotContain(PartialName + "  (winner)", r);
        Assert.Contains("vs " + MasterName, r);
    }

    [Fact]
    public void AWinnerScanCountsThePartialsCellUnderTheFullCopysPlugin()
    {
        var r = RecordsTools.Records(_svc, types: new[] { "CELL" }, where: new[] { "winner = " + PartialName },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.DoesNotContain("HcPfCell ", r);
        Assert.Contains("HcPfPlainCell", r);
        var m = RecordsTools.Records(_svc, types: new[] { "CELL" }, where: new[] { "winner = " + MasterName },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.Contains("HcPfCell ", m);
    }

    [Fact]
    public void AParentHopFromARefInThatCellReadsTheFullCopy() =>
        Assert.Contains("PublicArea", Fields(_ref, null, "*parent.Flags"));

    [Fact]
    public void ANamedReadOfThePartialsPluginStillReturnsItsOwnCopy()
    {
        var r = Fields(_cell, Pole(PartialName), "MajorFlags", "Location");
        Assert.Contains("bit14", r);
        Assert.DoesNotContain(Fid(_location), r);
    }

    /// <summary>Pins unchanged behaviour: the partial's plugin still touches the key and counts toward its depth.</summary>
    [Fact]
    public void MembershipStillCountsThePartialsPlugin()
    {
        var view = _svc.CaptureView();
        Assert.Equal(new[] { MasterName, PartialName }, view.TouchingPlugins(_cell));
        Assert.Equal(2, view.ResolveWinner(_cell)!.Value.OverrideDepth);
        Assert.True(view.Touches(_cell, PartialName));
    }

    [Fact]
    public void AnApplyBuildsItsOverrideFromTheFullCopyWithoutThePartialFlag()
    {
        var r = ApplyTools.Apply(_svc, ops: JsonDocument.Parse(JsonSerializer.Serialize(new[]
            { new { formid = Fid(_cell), field_path = "EditorID", value = "HcPfCellEdited" } })).RootElement.Clone(), patch: "HcPfApply");
        var path = Directory.GetFiles(_mods, "HcPfApply*.esp", SearchOption.AllDirectories).Single();
        using var patch = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var cell = patch.EnumerateMajorRecords<ICellGetter>().Single(c => c.FormKey == _cell);
        Assert.True((cell.MajorRecordFlagsRaw & Partial) == 0, r);
        Assert.Equal(_location, cell.Location.FormKey);
        Assert.True(cell.Flags.HasFlag(Cell.Flag.PublicArea), r);
    }

    [Fact]
    public void APartialFormInAMasterFlaggedPluginDoesNotWinEither() =>
        Assert.Equal(MasterName, _svc.CaptureView().ResolveWinner(_masterCell)!.Value.WinnerPlugin);

    /// <summary>xEdit's rule: 0x4000 on a temporary exterior cell is not a partial form, so the override wins.</summary>
    [Fact]
    public void AFlaggedTemporaryExteriorCellWinsAsANormalOverride() =>
        Assert.Equal(PartialName, _svc.CaptureView().ResolveWinner(_gridCell)!.Value.WinnerPlugin);

    /// <summary>Pins unchanged behaviour: an override with no partial flag wins as before.</summary>
    [Fact]
    public void AnUnflaggedOverrideStillWins() =>
        Assert.Equal(PartialName, _svc.CaptureView().ResolveWinner(_plainCell)!.Value.WinnerPlugin);
}
