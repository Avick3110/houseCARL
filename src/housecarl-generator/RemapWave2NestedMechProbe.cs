using System.Collections;
using System.Reflection;
using Noggog;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE — WAVE 2 nested-renumber mechanism pin (the load-bearing unknown for the compact tool's nested scope).
///
/// Wave 1 proved the FLAT renumber: <c>record.Duplicate(newKey)</c> into a fresh mod + <c>RemapLinks</c>, placing each
/// duplicate via its flat top-level group. <see cref="RemapEngine.RenumberRecordsInto"/> REFUSES LOUD on a record that
/// lives ONLY in a nested group (a Cell, a Placed* under a cell, an INFO under a topic) — there is no flat group to
/// place the duplicate into. Wave 2 must CLOSE that gap so a cell-/dialogue-bearing mod compacts. This probe settles,
/// self-contained (synthetic, TEMP, no MO2/Skyrim.esm), the three facts the closing mechanism rests on:
///
///   1. Does <c>Cell.Duplicate(newKey)</c> / <c>Worldspace.Duplicate</c> / <c>DialogTopic.Duplicate</c> DEEP-COPY their
///      nested child records (placed refs / exterior cells / INFOs), and do those children keep their OLD FormKeys?
///      (If Duplicate dropped or shared them, the "duplicate the container, then renumber the children in place" plan
///      would be wrong.)
///   2. Is <c>IMajorRecordGetterEnumerable</c> the by-construction discriminator for "a sub-object that CONTAINS records
///      but is not itself a record" (the block-tree structs WorldspaceBlock/SubBlock, CellBlock/SubBlock) — so the
///      recursive renumber can tell "renumber this element" (an IMajorRecordGetter) from "recurse THROUGH this element"
///      (a record-container struct) without a hand-coded per-family list?
///   3. Does the recursive Duplicate-and-replace renumber, applied to (interior cell + placed), (worldspace + exterior
///      cell + placed), and (topic + INFO), produce the right identities on disk after a write + re-read?
///
/// Run: dotnet run --project src/housecarl-generator remap-wave2-nested-mech
/// </summary>
public static class RemapWave2NestedMechProbe
{
    public static int RunMechanism(string[] args)
    {
        Console.WriteLine("################  COMPACT/MERGE WAVE 2 — nested-renumber mechanism pin  ################");
        Console.WriteLine();

        // ---- 2: is IMajorRecordGetterEnumerable the recurse-discriminator? Report what the block structs implement. ----
        Console.WriteLine("DISCRIMINATOR — does each block-tree struct implement IMajorRecordGetterEnumerable (the recurse marker)?");
        foreach (var t in new[] { typeof(WorldspaceBlock), typeof(WorldspaceSubBlock), typeof(CellBlock), typeof(CellSubBlock), typeof(Cell), typeof(Worldspace), typeof(DialogTopic), typeof(Weapon) })
        {
            bool enumGetter = typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(t);
            bool isRec = typeof(IMajorRecordGetter).IsAssignableFrom(t);
            Console.WriteLine($"   {t.Name,-20} IMajorRecordGetterEnumerable={enumGetter,-5} IMajorRecordGetter={isRec}");
        }
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-remap-wave2-nested-mech");
        if (Directory.Exists(tmpDir)) { try { Directory.Delete(tmpDir, true); } catch { } }
        Directory.CreateDirectory(tmpDir);

        var key = new ModKey("HcRemapNested", ModType.Plugin);

        // ---- 1 + 3a: INTERIOR CELL + a PlacedObject child. Duplicate deep-copy, then renumber both, place by new id. ----
        Console.WriteLine("EXPERIMENT A — interior cell {C@0xC00 -> [PlacedObject P@0xD00]} renumber C->0x800, P->0x801:");
        bool aOk = false;
        try
        {
            var cOld = new FormKey(key, 0xC00);
            var pOld = new FormKey(key, 0xD00);
            var cNew = new FormKey(key, 0x800);
            var pNew = new FormKey(key, 0x801);
            var dict = new Dictionary<FormKey, FormKey> { [cOld] = cNew, [pOld] = pNew };

            // source mod with one interior cell holding one placed object
            var src = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var srcCell = new Cell(cOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestCell", Flags = Cell.Flag.IsInteriorCell };
            srcCell.Temporary.Add(new PlacedObject(pOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestRef", Scale = 1.5f });
            FileInterior(src, srcCell);

            // Duplicate deep-copy check: duplicate the cell, see if Temporary came along at the OLD key.
            var dupCheck = (Cell)((IMajorRecordGetter)srcCell).Duplicate(cNew);
            int dupChildCount = dupCheck.Temporary.Count;
            FormKey? dupChildKey = dupCheck.Temporary.FirstOrDefault()?.FormKey;
            bool deepCopied = dupChildCount == 1 && dupChildKey == pOld;
            Console.WriteLine($"   Cell.Duplicate deep-copied Temporary? count={dupChildCount} childKey={dupChildKey} (expect 1 @ {pOld})  => {deepCopied}");

            // Real renumber into a fresh target via the prototype recursion, then write + reread.
            var tgt = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var renCell = (Cell)RenumberOne(srcCell, dict);
            FileInterior(tgt, renCell);
            tgt.ModHeader.Stats.NextFormID = 0x802;
            tgt.RemapLinks(dict);

            string path = Path.Combine(tmpDir, "A", key.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tgt.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            using var rb = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var cells = rb.EnumerateMajorRecords<ICellGetter>().ToList();
            var rbCell = cells.FirstOrDefault();
            var rbChild = rbCell?.Temporary.FirstOrDefault();
            aOk = deepCopied
                  && cells.Count == 1 && rbCell!.FormKey == cNew
                  && rbChild is not null && rbChild.FormKey == pNew;
            Console.WriteLine($"   on-disk: cell={rbCell?.FormKey} (expect {cNew}); placed={rbChild?.FormKey} (expect {pNew})  => {(aOk ? "PASS" : "FAIL")}");
        }
        catch (Exception ex) { Console.WriteLine($"   THREW {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();

        // ---- 3b: WORLDSPACE + an EXTERIOR cell + a placed child. The exterior cell lives in the worldspace's SubCells
        //         tree (Duplicate brings the whole tree); the renumber fixes the cell + placed identities IN the tree. ----
        Console.WriteLine("EXPERIMENT B — worldspace {W@0xE00 -> SubCells -> ext Cell EC@0xE01 -> [P@0xE02]} renumber all into 0x800,0x801,0x802:");
        bool bOk = false;
        try
        {
            var wOld = new FormKey(key, 0xE00);
            var ecOld = new FormKey(key, 0xE01);
            var pOld = new FormKey(key, 0xE02);
            var wNew = new FormKey(key, 0x800);
            var ecNew = new FormKey(key, 0x801);
            var pNew = new FormKey(key, 0x802);
            var dict = new Dictionary<FormKey, FormKey> { [wOld] = wNew, [ecOld] = ecNew, [pOld] = pNew };

            var src = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var ws = new Worldspace(wOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestWS" };
            var extCell = new Cell(ecOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestExtCell", Grid = new CellGrid { Point = new P2Int(3, -4) } };
            extCell.Temporary.Add(new PlacedObject(pOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestExtRef", Scale = 2.0f });
            FileExterior(ws, extCell, 3, -4);
            src.Worldspaces.Add(ws);

            // Duplicate deep-copy check on the worldspace.
            var dupWs = (Worldspace)((IMajorRecordGetter)ws).Duplicate(wNew);
            int dupCells = dupWs.EnumerateMajorRecords<ICellGetter>().Count();
            Console.WriteLine($"   Worldspace.Duplicate brought {dupCells} nested cell(s) (expect 1)");

            var tgt = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            tgt.Worldspaces.Add((Worldspace)RenumberOne(ws, dict));
            tgt.ModHeader.Stats.NextFormID = 0x803;
            tgt.RemapLinks(dict);

            string path = Path.Combine(tmpDir, "B", key.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tgt.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            using var rb = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var rbWs = rb.Worldspaces.FirstOrDefault();
            var rbCell = rbWs?.EnumerateMajorRecords<ICellGetter>().FirstOrDefault();
            var rbPlaced = rbCell?.Temporary.FirstOrDefault();
            bOk = rbWs is not null && rbWs.FormKey == wNew
                  && rbCell is not null && rbCell.FormKey == ecNew
                  && rbPlaced is not null && rbPlaced.FormKey == pNew;
            Console.WriteLine($"   on-disk: ws={rbWs?.FormKey} (expect {wNew}); extCell={rbCell?.FormKey} (expect {ecNew}); placed={rbPlaced?.FormKey} (expect {pNew})  => {(bOk ? "PASS" : "FAIL")}");
        }
        catch (Exception ex) { Console.WriteLine($"   THREW {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();

        // ---- 3c: DIALOG TOPIC + an INFO child. INFO lives in DialogTopic.Responses; Duplicate brings it. ----
        Console.WriteLine("EXPERIMENT C — topic {T@0xF00 -> Responses -> INFO@0xF01} renumber T->0x800, INFO->0x801:");
        bool cOk = false;
        try
        {
            var tOld = new FormKey(key, 0xF00);
            var iOld = new FormKey(key, 0xF01);
            var tNew = new FormKey(key, 0x800);
            var iNew = new FormKey(key, 0x801);
            var dict = new Dictionary<FormKey, FormKey> { [tOld] = tNew, [iOld] = iNew };

            var src = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var topic = new DialogTopic(tOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestTopic" };
            topic.Responses.Add(new DialogResponses(iOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNestInfo" });
            src.DialogTopics.Add(topic);

            var dupTopic = (DialogTopic)((IMajorRecordGetter)topic).Duplicate(tNew);
            int dupInfos = dupTopic.Responses.Count;
            Console.WriteLine($"   DialogTopic.Duplicate brought {dupInfos} INFO(s) (expect 1)");

            var tgt = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            tgt.DialogTopics.Add((DialogTopic)RenumberOne(topic, dict));
            tgt.ModHeader.Stats.NextFormID = 0x802;
            tgt.RemapLinks(dict);

            string path = Path.Combine(tmpDir, "C", key.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            tgt.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            using var rb = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var rbTopic = rb.DialogTopics.FirstOrDefault();
            var rbInfo = rbTopic?.Responses.FirstOrDefault();
            cOk = rbTopic is not null && rbTopic.FormKey == tNew
                  && rbInfo is not null && rbInfo.FormKey == iNew;
            Console.WriteLine($"   on-disk: topic={rbTopic?.FormKey} (expect {tNew}); info={rbInfo?.FormKey} (expect {iNew})  => {(cOk ? "PASS" : "FAIL")}");
        }
        catch (Exception ex) { Console.WriteLine($"   THREW {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();

        bool pass = aOk && bOk && cOk;
        Console.WriteLine($"=== remap-wave2-nested-mech: {(pass ? "PASS" : "FAIL")} — the recursive Duplicate-and-replace renumber is{(pass ? "" : " NOT")} viable ===");
        try { Directory.Delete(tmpDir, true); } catch { }
        return pass ? 0 : 1;
    }

    // ======================================================================
    //  PROTOTYPE of the recursive renumber the engine will adopt (proven here first).
    // ======================================================================

    /// <summary>Renumber ONE record: Duplicate it under its new-or-same FormKey (Mutagen's deep-copy under a new
    /// identity — nested children come along at their OLD keys), then recursively renumber those descendants in place.</summary>
    static IMajorRecord RenumberOne(IMajorRecordGetter rec, IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        var newKey = dict.TryGetValue(rec.FormKey, out var nk) ? nk : rec.FormKey;
        var dup = rec.Duplicate(newKey);
        RenumberDescendants(dup, dict);
        return dup;
    }

    /// <summary>Walk a container's child records and renumber each in place. A list/property element that IS a record
    /// (<see cref="IMajorRecordGetter"/>) is REPLACED by its renumbered Duplicate; one that merely CONTAINS records
    /// (<see cref="IMajorRecordGetterEnumerable"/> but not a record itself — the FormKey-less block-tree structs) is
    /// recursed THROUGH. Everything else (scalars, FormLinks, value structs) is skipped — RemapLinks handles outgoing
    /// links separately.</summary>
    static void RenumberDescendants(object container, IReadOnlyDictionary<FormKey, FormKey> dict)
    {
        foreach (var prop in container.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(container); } catch { continue; }
            if (val is null) continue;

            if (val is IList list && val is not string)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var el = list[i];
                    if (el is IMajorRecordGetter childRec) list[i] = RenumberOne(childRec, dict);
                    else if (el is IMajorRecordGetterEnumerable) RenumberDescendants(el, dict);
                }
            }
            else if (val is IMajorRecordGetter singleRec)
            {
                if (prop.CanWrite) prop.SetValue(container, RenumberOne(singleRec, dict));
            }
            else if (val is IMajorRecordGetterEnumerable nestedContainer)
            {
                RenumberDescendants(nestedContainer, dict);
            }
        }
    }

    // ---- minimal block-tree filing helpers (mirror WriteEngine.AddInteriorCell / AddExteriorCell), just enough for the fixtures ----

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static void FileExterior(Worldspace ws, Cell cell, int gridX, int gridY)
    {
        int bx = (int)Math.Floor(gridX / 32.0), by = (int)Math.Floor(gridY / 32.0);
        int sx = (int)Math.Floor(gridX / 8.0), sy = (int)Math.Floor(gridY / 8.0);
        var block = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null) { block = new WorldspaceBlock { BlockNumberX = (short)bx, BlockNumberY = (short)by, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(block); }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null) { sub = new WorldspaceSubBlock { BlockNumberX = (short)sx, BlockNumberY = (short)sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
        sub.Items.Add(cell);
    }
}
