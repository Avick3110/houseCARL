using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlGenerator;

/// <summary>
/// STEP 0 SCOUT — COORDINATE-KEYED nested CREATE (the §4-(b) seam the nested/dialogue plan deferred).
/// Throwaway recon, not a standing instrument. Writes to a temp dir; touches no tracked file; SHA-guards the source.
///
/// <see cref="NestedCreateProbe"/> proved the FormKey-parented families (INFO under a DialogTopic; Placed into an
/// already-overridden Cell) and CHARACTERIZED — but never round-tripped — the coordinate-keyed seam: exterior Cell,
/// interior Cell, and Placed-into-a-not-yet-present-cell, whose structural parents are the FormKey-LESS
/// WorldspaceBlock/WorldspaceSubBlock (exterior) and CellBlock/CellSubBlock (interior) structs. Aaron's call (2026-06-20):
/// CLOSE THE GAP — so this scout answers the BUILD questions, fail-loud per shape (Q3):
///
///   B  OVERRIDE SHAPE — does GetOrAddAsOverride(Worldspace) come back THIN (empty SubCells, so adding one cell costs
///      one block path) or DEEP (carries all of Tamriel's cells, so a one-cell add would override the whole worldspace)?
///      Decides the whole build approach.
///   C  BLOCK MATH — does the exterior 32-cell-block / 8-cell-subblock division (floored, incl. negatives) and the
///      interior FormID-digit keying MATCH vanilla? Confirms the arithmetic against real data, never assumed.
///   D  EXTERIOR ROUND-TRIP — construct a new exterior Cell with a Grid, compute its block/subblock, find-or-CONSTRUCT
///      the WorldspaceBlock + WorldspaceSubBlock, add the cell, serialize, RE-OPEN clean: cell present under the right
///      block/subblock, FormKey local 0x800+, OFST regenerated, source byte-unchanged. + a Placed ref into the new cell.
///   E  INTERIOR ROUND-TRIP — same, into the top-level Cells group (CellBlock/CellSubBlock by FormID digits).
///
/// If a round-trip won't serialize/reopen, or OFST drops the cell, or the override is unavoidably DEEP: STOP and surface
/// to Aaron (§4-(b) stays a declared loud-fail boundary), never a per-type shim.
///
/// Run: <c>dotnet run --project src/housecarl-generator -- coord-cell-probe [sourcePlugin]</c>
/// </summary>
public static class CoordCellProbe
{
    const string DefaultSource = @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data\Skyrim.esm";
    static readonly FormKey Tamriel = FormKey.Factory("00003C:Skyrim.esm");

    public static int RunProbe(string[] args)
    {
        var src = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultSource;
        var asm = typeof(IArmorGetter).Assembly;

        Console.WriteLine("################  STEP 0 SCOUT — COORDINATE-KEYED nested CREATE (exterior/interior Cell, Placed-into-new-cell)  ################");
        Console.WriteLine($"source: {src}");
        Console.WriteLine();
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source plugin not found: {src}"); return 1; }

        var shaBefore = Sha(src);
        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var cache = sourceMod.ToImmutableLinkCache();

        var outDir = Path.Combine(Path.GetTempPath(), "hc-coord-cell-probe");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        bool extOk = true, intOk = true, placedOk = true;

        // ============================================================ PHASE A : structure discovery ============================================================
        Console.WriteLine("===== PHASE A : exact shapes (reflection — measure, don't assume) =====");
        DumpType("Worldspace", typeof(Worldspace), "SubCells", "OffsetData");
        DumpType("WorldspaceBlock", typeof(WorldspaceBlock), "BlockNumberX", "BlockNumberY", "GroupType", "Items");
        DumpType("WorldspaceSubBlock", typeof(WorldspaceSubBlock), "BlockNumberX", "BlockNumberY", "GroupType", "Items");
        DumpType("Cell", typeof(Cell), "Grid", "Flags");
        DumpType("CellGrid", typeof(CellGrid), "Point", "Flags");
        DumpType("Cells (interior top-level group)", typeof(SkyrimMod).GetProperty("Cells")!.PropertyType, "Records");
        DumpType("CellBlock", typeof(CellBlock), "BlockNumber", "GroupType", "SubBlocks");
        DumpType("CellSubBlock", typeof(CellSubBlock), "BlockNumber", "GroupType", "Cells");
        Console.WriteLine($"   GroupTypeEnum values: {string.Join(", ", Enum.GetNames(typeof(GroupTypeEnum)))}");
        Console.WriteLine($"   Cell.Flag values (interior bit): {(Enum.GetNames(typeof(Cell.Flag)).FirstOrDefault(n => n.Contains("Interior")) ?? "(none named *Interior*)")}");
        Console.WriteLine();

        // ============================================================ PHASE B : override shape (thin vs deep) ============================================================
        Console.WriteLine("===== PHASE B : GetOrAddAsOverride(Worldspace Tamriel) — THIN or DEEP? =====");
        var wsGetter = cache.Resolve<IWorldspaceGetter>(Tamriel);
        int srcBlocks = wsGetter.SubCells?.Count ?? 0;
        int srcCells = CountWsCells(wsGetter.SubCells);
        Console.WriteLine($"   SOURCE Tamriel: SubCells blocks={srcBlocks}  total exterior cells (overlay, lazy)≈{srcCells}");
        Console.WriteLine($"   SOURCE Tamriel OffsetData (OFST seek-cache): {(wsGetter.OffsetData.HasValue ? wsGetter.OffsetData.Value.Length + " bytes present" : "absent")}  (Mutagen drops it on write — see PHASE D OFST=)");
        {
            var patch = new SkyrimMod(new ModKey("hc_ws_shape", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var wsOv = (Worldspace)WriteEngine.GenericGetOrAddAsOverride(patch, wsGetter, cache);
            int ovBlocks = wsOv.SubCells?.Count ?? 0;
            int ovCells = CountWsCellsSettable(wsOv.SubCells);
            Console.WriteLine($"   OVERRIDE: SubCells blocks={ovBlocks}  cells={ovCells}  ==>  {(ovCells == 0 ? "THIN (empty SubCells — add the one new block path, clean)" : ovCells >= srcCells && srcCells > 0 ? "DEEP (carries the whole worldspace — a one-cell add would override everything; build must thin it)" : $"PARTIAL ({ovCells} cells carried)")}");
        }
        Console.WriteLine();

        // ============================================================ PHASE C : block math vs vanilla ============================================================
        Console.WriteLine("===== PHASE C : block arithmetic vs vanilla (exterior /32 block, /8 subblock, floored) =====");
        {
            int chec99 = 0, ext = 0, extMatch = 0;
            foreach (var block in wsGetter.SubCells ?? Enumerable.Empty<IWorldspaceBlockGetter>())
            {
                foreach (var sub in block.Items ?? Enumerable.Empty<IWorldspaceSubBlockGetter>())
                foreach (var c in sub.Items ?? Enumerable.Empty<ICellGetter>())
                {
                    if (c.Grid is null) continue;
                    int gx = c.Grid.Point.X, gy = c.Grid.Point.Y;
                    bool bm = block.BlockNumberX == FloorDiv(gx, 32) && block.BlockNumberY == FloorDiv(gy, 32);
                    bool sm = sub.BlockNumberX == FloorDiv(gx, 8) && sub.BlockNumberY == FloorDiv(gy, 8);
                    ext++; if (bm && sm) extMatch++;
                    else if (chec99 < 6) { Console.WriteLine($"     MISMATCH grid=({gx},{gy}) block=({block.BlockNumberX},{block.BlockNumberY}) exp=({FloorDiv(gx,32)},{FloorDiv(gy,32)}) sub=({sub.BlockNumberX},{sub.BlockNumberY}) exp=({FloorDiv(gx,8)},{FloorDiv(gy,8)})"); chec99++; }
                    if (ext >= 4000) goto doneExt;
                }
            }
            doneExt:
            Console.WriteLine($"   EXTERIOR: {extMatch}/{ext} sampled cells match block=floor(grid/32), subblock=floor(grid/8)  => {(ext > 0 && extMatch == ext ? "CONFIRMED" : "!! divisor/floor wrong — read mismatches")}");
        }
        {
            // interior keying: derive from vanilla — block & subblock vs FormID decimal digits.
            int interior = 0, match = 0, shown = 0;
            foreach (var cb in sourceMod.Cells.Records)
            {
                foreach (var sb in cb.SubBlocks ?? Enumerable.Empty<ICellSubBlockGetter>())
                foreach (var c in sb.Cells ?? Enumerable.Empty<ICellGetter>())
                {
                    uint id = c.FormKey.ID;
                    bool m = cb.BlockNumber == (int)(id % 10) && sb.BlockNumber == (int)((id / 10) % 10);
                    interior++; if (m) match++;
                    else if (shown < 6) { Console.WriteLine($"     INT MISMATCH id=0x{id:X6}({id}) block={cb.BlockNumber} exp={id%10} sub={sb.BlockNumber} exp={(id/10)%10}"); shown++; }
                    if (interior >= 2000) goto doneInt;
                }
            }
            doneInt:
            Console.WriteLine($"   INTERIOR: {match}/{interior} sampled cells match block=id%10, subblock=(id/10)%10  => {(interior > 0 && match == interior ? "CONFIRMED" : "!! interior keying differs — read mismatches")}");
        }
        Console.WriteLine();

        // ============================================================ PHASE D : EXTERIOR round-trip ============================================================
        Console.WriteLine("===== PHASE D : EXTERIOR cell create (construct block+subblock from Grid) + Placed-into-it =====");
        try
        {
            var patch = new SkyrimMod(new ModKey("hc_ext_cell", ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.EnsureFormIdFloor(patch);
            var wsOv = (Worldspace)WriteEngine.GenericGetOrAddAsOverride(patch, wsGetter, cache);

            // a deliberately far-flung empty grid → guarantees a NEW block + NEW subblock (exercises the construct path).
            int gx = 1000, gy = -1000;
            int bx = FloorDiv(gx, 32), by = FloorDiv(gy, 32), sx = FloorDiv(gx, 8), sy = FloorDiv(gy, 8);
            var cellFk = patch.GetNextFormKey("HC_S0_ExtCell");
            var cell = new Cell(cellFk, SkyrimRelease.SkyrimSE)
            {
                Grid = new CellGrid { Point = new P2Int(gx, gy) },
                Flags = default,   // exterior: IsInteriorCell OFF
            };
            var (block, sub) = FindOrAddExteriorBlock(wsOv, bx, by, sx, sy);
            sub.Items.Add(cell);

            // Placed ref into the new cell's Temporary.
            var refFk = patch.GetNextFormKey("HC_S0_ExtRef");
            var placed = new PlacedObject(refFk, SkyrimRelease.SkyrimSE);
            cell.Temporary.Add(placed);

            var outPath = Path.Combine(outDir, patch.ModKey.FileName);
            WriteEngine.WritePatch(patch, sourceMod, outPath);
            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            var backWs = back.Worldspaces.FirstOrDefault(w => w.FormKey == Tamriel);
            var (reBlock, reSub, reCell) = LocateExteriorCell(backWs?.SubCells, cellFk);
            bool present = reCell is not null;
            bool gridOk = reCell?.Grid is { } g && g.Point.X == gx && g.Point.Y == gy;
            bool blockOk = reBlock is not null && reBlock.BlockNumberX == bx && reBlock.BlockNumberY == by
                           && reSub is not null && reSub.BlockNumberX == sx && reSub.BlockNumberY == sy;
            bool floored = cellFk.ID >= 0x800 && cellFk.ModKey == patch.ModKey;
            bool refPresent = reCell is not null && (reCell.Temporary?.Any(r => r.FormKey == refFk) ?? false);
            int patchCells = backWs is null ? -1 : CountWsCells(backWs.SubCells);
            var ofst = backWs?.OffsetData;
            int ofstLen = ofst.HasValue ? ofst.Value.Length : -1;

            extOk = present && gridOk && blockOk && floored;
            placedOk = refPresent;
            Console.WriteLine($"[{(extOk ? "PASS" : "FAIL")}] exterior cell {cellFk} grid=({gx},{gy}) -> block=({bx},{by}) subblock=({sx},{sy})");
            Console.WriteLine($"         reopened-present={present} grid-correct={gridOk} block/subblock-correct={blockOk} local>=0x800={floored}");
            Console.WriteLine($"         patch carries {patchCells} exterior cell(s) (expect 1 — a THIN override delta, not all of Tamriel)  OFST regenerated bytes={ofstLen}");
            Console.WriteLine($"[{(refPresent ? "PASS" : "FAIL")}] Placed ref {refFk} into the new cell's Temporary  reopened-present={refPresent}");
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] exterior round-trip — {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}"); extOk = false; }
        Console.WriteLine();

        // ============================================================ PHASE E : INTERIOR round-trip ============================================================
        Console.WriteLine("===== PHASE E : INTERIOR cell create (CellBlock/CellSubBlock by FormID digits) =====");
        try
        {
            var patch = new SkyrimMod(new ModKey("hc_int_cell", ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.EnsureFormIdFloor(patch);
            var cellFk = patch.GetNextFormKey("HC_S0_IntCell");
            uint id = cellFk.ID;
            int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
            var cell = new Cell(cellFk, SkyrimRelease.SkyrimSE) { Flags = ParseInteriorFlag() };
            AddInteriorCell(patch, blockN, subN, cell);

            var outPath = Path.Combine(outDir, patch.ModKey.FileName);
            WriteEngine.WritePatch(patch, sourceMod, outPath);
            using var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            var (reBlock, reSub, reCell) = LocateInteriorCell(back, cellFk);
            bool present = reCell is not null;
            bool placedRight = reBlock == blockN && reSub == subN;
            bool floored = cellFk.ID >= 0x800 && cellFk.ModKey == patch.ModKey;
            bool interiorFlag = reCell is not null && reCell.Flags.HasFlag(ParseInteriorFlag());
            intOk = present && placedRight && floored && interiorFlag;
            Console.WriteLine($"[{(intOk ? "PASS" : "FAIL")}] interior cell {cellFk} (id={id}) -> block={blockN} subblock={subN}");
            Console.WriteLine($"         reopened-present={present} block/subblock-correct={placedRight} (got block={reBlock} sub={reSub}) local>=0x800={floored} interior-flag={interiorFlag}");
        }
        catch (Exception ex) { Console.WriteLine($"[FAIL] interior round-trip — {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}"); intOk = false; }
        Console.WriteLine();

        // ============================================================ VERDICT ============================================================
        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine("===== COORDINATE-KEYED STEP 0 VERDICT =====");
        Console.WriteLine($"   Exterior cell create (block math + thin override + reopen) : {(extOk ? "HOLDS" : "!! FAILED — read [FAIL] above")}");
        Console.WriteLine($"   Placed-into-new-cell                                       : {(placedOk ? "HOLDS" : "!! FAILED")}");
        Console.WriteLine($"   Interior cell create (FormID-digit blocks + reopen)        : {(intOk ? "HOLDS" : "!! FAILED")}");
        Console.WriteLine($"   source byte-unchanged                                      : {(sourceUnchanged ? "YES" : "!! NO — CHANGED")}");
        Console.WriteLine();
        var pass = extOk && intOk && placedOk && sourceUnchanged;
        Console.WriteLine(pass
            ? "=== STEP 0 PASS: coordinate-keyed create is TRACTABLE — a single block-math algorithm (floor(grid/32|8) exterior, FormID digits interior) places a constructed cell into find-or-built block structs; round-trips clean; OFST regenerates. Build is §4-(b) DELIBERATE-BUILD, not a per-type shim. Report to Aaron. ==="
            : "=== STEP 0: a coordinate-keyed shape did NOT round-trip — read the [FAIL] lines. Per §1.4: STOP and surface to Aaron; the loud-fail boundary stays until resolved. ===");
        return pass ? 0 : 1;
    }

    // ---------- exterior block tree: find-or-construct ----------

    static (WorldspaceBlock block, WorldspaceSubBlock sub) FindOrAddExteriorBlock(Worldspace ws, int bx, int by, int sx, int sy)
    {
        var block = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null)
        {
            block = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = (short)by, GroupType = GroupTypeEnum.ExteriorCellBlock };
            ws.SubCells.Add(block);
        }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null)
        {
            sub = new WorldspaceSubBlock { BlockNumberX = (short)sx, BlockNumberY = (short)sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            block.Items.Add(sub);
        }
        return (block, sub);
    }

    static (IWorldspaceBlockGetter? block, IWorldspaceSubBlockGetter? sub, ICellGetter? cell) LocateExteriorCell(
        IReadOnlyList<IWorldspaceBlockGetter>? blocks, FormKey fk)
    {
        foreach (var b in blocks ?? (IReadOnlyList<IWorldspaceBlockGetter>)Array.Empty<IWorldspaceBlockGetter>())
            foreach (var s in b.Items ?? Enumerable.Empty<IWorldspaceSubBlockGetter>())
                foreach (var c in s.Items ?? Enumerable.Empty<ICellGetter>())
                    if (c.FormKey == fk) return (b, s, c);
        return (null, null, null);
    }

    // ---------- interior block tree: find-or-construct ----------

    static void AddInteriorCell(SkyrimMod patch, int blockN, int subN, Cell cell)
    {
        var records = patch.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static (int block, int sub, ICellGetter? cell) LocateInteriorCell(ISkyrimModGetter back, FormKey fk)
    {
        foreach (var b in back.Cells.Records)
            foreach (var s in b.SubBlocks ?? Enumerable.Empty<ICellSubBlockGetter>())
                foreach (var c in s.Cells ?? Enumerable.Empty<ICellGetter>())
                    if (c.FormKey == fk) return (b.BlockNumber, s.BlockNumber, c);
        return (-99, -99, null);
    }

    static Cell.Flag ParseInteriorFlag()
        => (Cell.Flag)Enum.Parse(typeof(Cell.Flag), Enum.GetNames(typeof(Cell.Flag)).First(n => n.Contains("Interior")));

    // ---------- helpers ----------

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    static int CountWsCells(IReadOnlyList<IWorldspaceBlockGetter>? blocks)
    {
        int n = 0;
        foreach (var b in blocks ?? (IReadOnlyList<IWorldspaceBlockGetter>)Array.Empty<IWorldspaceBlockGetter>())
            foreach (var s in b.Items ?? Enumerable.Empty<IWorldspaceSubBlockGetter>())
                n += s.Items?.Count ?? 0;
        return n;
    }

    static int CountWsCellsSettable(IList<WorldspaceBlock>? blocks)
    {
        int n = 0;
        foreach (var b in blocks ?? (IList<WorldspaceBlock>)Array.Empty<WorldspaceBlock>())
            foreach (var s in b.Items)
                n += s.Items.Count;
        return n;
    }

    static void DumpType(string label, Type t, params string[] props)
    {
        Console.WriteLine($"   {label} ({t.Name}):");
        foreach (var name in props)
        {
            var p = t.GetProperty(name);
            Console.WriteLine(p is null
                ? $"      {name,-12} : (ABSENT — surface, don't guess)"
                : $"      {name,-12} : {Pretty(p.PropertyType)}");
        }
    }

    static string Sha(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    static string Pretty(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name; var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }
        return Nullable.GetUnderlyingType(t)?.Name is { } u ? u + "?" : t.Name;
    }
}
