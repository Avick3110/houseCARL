using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A cell created by grid under a worldspace through the create cleave lands in the block and sub-block its
/// grid names; a parentless cell lands in the block and sub-block its id names, flagged interior; a placed reference lands in a same-call exterior cell; a malformed cell create is refused with no file
/// written; and an into= re-run of a cell's editorid is refused. Migrated from the coord-cell-guard probe.</summary>
[Trait("tier", "integration")]
public sealed class CoordCellCreateTests : IDisposable
{
    readonly WritePathRig _rig = new();
    readonly string _masterPath, _masterSha;
    readonly LoadOrderResolver _order;
    readonly FormKey _world, _weapon;

    public CoordCellCreateTests()
    {
        var m = new SkyrimMod(ModKey.FromFileName("HcCcGdMaster.esm"), SkyrimRelease.SkyrimSE);
        var ws = m.Worldspaces.AddNew(); ws.EditorID = "HcCcWorld";
        var w = m.Weapons.AddNew(); w.EditorID = "HcCcWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        (_world, _weapon) = (ws.FormKey, w.FormKey);
        _masterPath = _rig.Write(m);
        _masterSha = Sha(_masterPath);
        _order = _rig.Order(_masterPath);
    }

    public void Dispose() => _rig.Dispose();

    static WritePatchBuilder.CreateSpec Cell(string edid, string? parent = null, string? grid = null) =>
        new() { RecordType = "Cell", EditorId = edid, ParentRef = parent, Grid = grid, Edits = Array.Empty<WriteRequest>() };

    WritePatchBuilder.CreateOutcome Create(string path, bool extend, params WritePatchBuilder.CreateSpec[] specs) =>
        WritePatchBuilder.CreateRecords(_order, TestCorpus.Rulebook, specs, path, extend);

    IEnumerable<(IWorldspaceBlockGetter B, IWorldspaceSubBlockGetter S, ICellGetter C)> ExteriorCells(string path)
    {
        var ws = _rig.Open(path).Worldspaces.Single(w => w.FormKey == _world);
        foreach (var b in ws.SubCells)
            foreach (var s in b.Items)
                foreach (var c in s.Items)
                    yield return (b, s, c);
    }

    // EXTERIOR cell by grid: block/sub-correct, grid-correct, local>=0x800, in the patch
    [Fact]
    public void AnExteriorCellLandsInTheBlockAndSubBlockItsGridNames()
    {
        var path = _rig.Out("HcCcExterior.esp");
        var o = Create(path, false, Cell("HcCcExtCell", _world.ToString(), "1000,-1000"));
        Assert.True(o.Success, o.Error);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(fk.ID >= 0x800);
        Assert.Equal("HcCcExterior.esp", fk.ModKey.FileName.String);

        var (b, s, c) = ExteriorCells(path).Single(x => x.C.FormKey == fk);
        // 1000/32 = 31, floor(-1000/32) = -32; 1000/8 = 125, -1000/8 = -125.
        Assert.Equal((31, -32), ((int)b.BlockNumberX, (int)b.BlockNumberY));
        Assert.Equal((125, -125), ((int)s.BlockNumberX, (int)s.BlockNumberY));
        Assert.Equal((1000, -1000), (c.Grid!.Point.X, c.Grid.Point.Y));
        // master byte-untouched
        Assert.Equal(_masterSha, Sha(_masterPath));
    }

    // INTERIOR cell by FormID digits: block=id%10, sub=(id/10)%10, interior flag, local>=0x800, re-opened from disk
    [Fact]
    public void AParentlessCellLandsInTheBlockAndSubBlockItsIdNamesWithTheInteriorFlag()
    {
        var path = _rig.Out("HcCcInterior.esp");
        var o = Create(path, false, Cell("HcCcIntCell"));
        Assert.True(o.Success, o.Error);
        var fk = Assert.Single(o.Created).FormKey;
        Assert.True(fk.ID >= 0x800);
        Assert.Equal("HcCcInterior.esp", fk.ModKey.FileName.String);

        var (block, sub, cell) = (from b in _rig.Open(path).Cells.Records
                                  from s in b.SubBlocks
                                  from c in s.Cells
                                  where c.FormKey == fk
                                  select (b.BlockNumber, s.BlockNumber, c)).Single();
        Assert.Equal((int)(fk.ID % 10), block);
        Assert.Equal((int)(fk.ID / 10 % 10), sub);
        Assert.True(cell.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Cell.Flag.IsInteriorCell));
    }

    // PLACED into new exterior cell: the ref lands in the new cell's Temporary
    [Fact]
    public void APlacedObjectLandsInTheTemporaryOfASameCallExteriorCell()
    {
        var path = _rig.Out("HcCcPlaced.esp");
        var o = Create(path, false,
            Cell("HcCcPCell", _world.ToString(), "1500,1500"),
            new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcCcPRef", ParentRef = "HcCcPCell", IntoCollection = "Temporary", Edits = Array.Empty<WriteRequest>() });
        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.Created.Count);
        var cell = ExteriorCells(path).Single(x => x.C.FormKey == o.Created[0].FormKey).C;
        Assert.Contains(o.Created[1].FormKey, cell.Temporary.Select(r => r.FormKey));
    }

    string Refused(string tag, WritePatchBuilder.CreateSpec spec)
    {
        var path = _rig.Out($"HcCc_{tag}.esp");
        var o = Create(path, false, spec);
        Assert.False(o.Success, "the create was accepted");
        Assert.False(File.Exists(path), "a refused create wrote a file");
        return o.Error!;
    }

    // REJ-NOWS grid, no parent
    [Fact]
    public void AGridWithNoParentIsRefusedNamingWorldspace()
        => Assert.Contains("Worldspace", Refused("RejNoWs", Cell("HcCcRej1", grid: "1,2")), StringComparison.OrdinalIgnoreCase);

    // REJ-NOGRID parent, no grid
    [Fact]
    public void AWorldspaceParentWithNoGridIsRefusedNamingGrid()
        => Assert.Contains("grid", Refused("RejNoGrid", Cell("HcCcRej2", _world.ToString())), StringComparison.OrdinalIgnoreCase);

    // REJ-BADGRID non-numeric grid
    [Fact]
    public void ANonNumericGridIsRefusedNamingTheFormat()
        => Assert.Contains("X,Y", Refused("RejBadGrid", Cell("HcCcRej3", _world.ToString(), "abc")), StringComparison.OrdinalIgnoreCase);

    // REJ-NONWS grid + Weapon parent; the refusal's own words, since a raw cast failure also says "Worldspace"
    [Fact]
    public void AGridUnderANonWorldspaceParentIsRefusedNamingWorldspace()
    {
        var error = Refused("RejNonWs", Cell("HcCcRej4", _weapon.ToString(), "1,2"));
        Assert.Contains("nests under a Worldspace", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolved to a Weapon", error, StringComparison.OrdinalIgnoreCase);
    }

    // DUP-REJECT into= duplicate cell editorid
    [Fact]
    public void AnIntoReRunOfTheSameCellEditorIdIsRefused()
    {
        var path = _rig.Out("HcCcDup.esp");
        var first = Create(path, false, Cell("HcCcDupCell"));
        Assert.True(first.Success, first.Error);
        var again = Create(path, true, Cell("HcCcDupCell"));
        Assert.False(again.Success);
        Assert.Contains("already exists", again.Error, StringComparison.OrdinalIgnoreCase);
    }

    static string Sha(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
}
