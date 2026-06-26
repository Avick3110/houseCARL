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
    //  GUARD — the Wave 2 gate (CI-able): a self-contained, end-to-end compact of a
    //  synthetic multi-plugin fixture with NESTED records, through the REAL
    //  RemapEngine.RenumberModInto + identify + repoint, asserting the on-disk truth.
    // ======================================================================

    /// <summary>
    /// Self-contained regression guard for the Wave-2 NESTED compact (RemapEngine.RenumberModInto). Synthesizes a
    /// Donor.esp carrying one of every nesting shape — a flat weapon, a FormList with an INTERNAL ref, an interior cell
    /// with a placed object (whose Base is an internal ref), a worldspace with an exterior cell with a placed object,
    /// and a dialog topic with an INFO — plus an External.esp that references the weapon. Drives the FULL compact through
    /// the real engine (RenumberModInto → write P′ → identify-pass → RepointInPlace) and asserts the on-disk truth:
    ///   NESTED   — every originating record (flat AND nested) lands at its remapped key in the ESL window, the nesting is
    ///              preserved (cell→placed, ws→ext cell→placed, topic→INFO), and INTERNAL refs (FormList→weapon, the
    ///              placed object's Base→weapon) are repointed to the new keys.
    ///   EXTERNAL — the identify-pass finds External (not Donor) as an external referencer, and RepointInPlace rewrites
    ///              External's reference to the new weapon key.
    /// Run: dotnet run --project src/housecarl-generator remap-wave2-compact-guard
    /// </summary>
    public static int RunCompactGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT/MERGE WAVE 2 — nested compact end-to-end guard  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-remap-wave2-compact-guard");
        if (Directory.Exists(tmpDir)) { try { Directory.Delete(tmpDir, true); } catch { } }
        Directory.CreateDirectory(tmpDir);

        var donorKey = new ModKey("HcW2Donor", ModType.Plugin);
        var extKey = new ModKey("HcW2External", ModType.Plugin);

        // originating fixture keys (deliberately scattered, > 0xFFF, to force a real renumber into the window)
        var waOld = new FormKey(donorKey, 0xA00);   // weapon
        var flOld = new FormKey(donorKey, 0xA01);   // formlist -> [WA] (internal)
        var icOld = new FormKey(donorKey, 0xA02);   // interior cell
        var ipOld = new FormKey(donorKey, 0xA03);   // placed in the interior cell; Base -> WA (internal)
        var wsOld = new FormKey(donorKey, 0xA04);   // worldspace
        var ecOld = new FormKey(donorKey, 0xA05);   // exterior cell under the worldspace
        var epOld = new FormKey(donorKey, 0xA06);   // placed in the exterior cell
        var dtOld = new FormKey(donorKey, 0xA07);   // dialog topic
        var inOld = new FormKey(donorKey, 0xA08);   // INFO under the topic
        var exlKey = new FormKey(extKey, 0x800);    // External's formlist -> [WA] (EXTERNAL ref)

        string donorPath = Path.Combine(tmpDir, donorKey.FileName.String);
        string extPath = Path.Combine(tmpDir, extKey.FileName.String);

        // --- synthesize Donor.esp with every nesting shape ---
        {
            var d = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            d.Weapons.Add(new Weapon(waOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Weap", BasicStats = new WeaponBasicStats { Damage = 10 } });
            var fl = new FormList(flOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2List" };
            fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(waOld));
            d.FormLists.Add(fl);

            var ic = new Cell(icOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2IntCell", Flags = Cell.Flag.IsInteriorCell };
            var ip = new PlacedObject(ipOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2IntRef" };
            ip.Base.SetTo(waOld);                                          // a NESTED internal ref → must repoint to the new weapon key
            ic.Temporary.Add(ip);
            FileInterior(d, ic);

            var ws = new Worldspace(wsOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2WS" };
            var ec = new Cell(ecOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtCell", Grid = new CellGrid { Point = new P2Int(3, -4) } };
            ec.Temporary.Add(new PlacedObject(epOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtRef" });
            FileExterior(ws, ec, 3, -4);
            d.Worldspaces.Add(ws);

            var dt = new DialogTopic(dtOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Topic" };
            dt.Responses.Add(new DialogResponses(inOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Info" });
            d.DialogTopics.Add(dt);

            d.ModHeader.Stats.NextFormID = 0xA09;
            d.BeginWrite.ToPath(donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
        }
        // --- synthesize External.esp {EXL@0x800 -> [WA(donor)]}, master = Donor ---
        {
            using var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE);
            var e = new SkyrimMod(extKey, SkyrimRelease.SkyrimSE);
            var exl = new FormList(exlKey, SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtList" };
            exl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(waOld));
            e.FormLists.Add(exl);
            e.ModHeader.Stats.NextFormID = 0x801;
            e.BeginWrite.ToPath(extPath).WithLoadOrder(new[] { donorOv }).NoNextFormIDProcessing().Write();
        }

        // --- build the ESL remap over Donor's originating records + structurally renumber into P′ ---
        RemapEngine.RemapPlan plan;
        RemapEngine.RenumberResult ren;
        string pPrimePath = Path.Combine(tmpDir, "pprime", donorKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(pPrimePath)!);
        using (var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE))
        {
            var srcKeys = donorOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == donorKey).Select(r => r.FormKey).ToList();
            plan = RemapEngine.BuildSequentialRemap(srcKeys, donorKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
            var pPrime = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            ren = RemapEngine.RenumberModInto(pPrime, donorOv, plan.Dict);
            pPrime.ModHeader.Stats.NextFormID = (uint)(RemapEngine.EslFloor + plan.Dict.Count);
            if (ren.Success) WriteEngine.WriteInPlace(pPrime, Array.Empty<ISkyrimModGetter>(), pPrimePath);
        }

        FormKey New(FormKey old) => plan.Success && plan.Dict.TryGetValue(old, out var nk) ? nk : default;

        bool nestedOk = false; string nestedDetail = ren.Error ?? "";
        if (ren.Success && plan.Success)
        {
            using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
            // flat
            var weap = pp.Weapons.FirstOrDefault(w => w.EditorID == "HcW2Weap");
            var list = pp.FormLists.FirstOrDefault(l => l.EditorID == "HcW2List");
            // interior cell + placed (Base internal ref)
            var intCell = pp.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.EditorID == "HcW2IntCell");
            var intRef = intCell?.Temporary.FirstOrDefault(p => p.EditorID == "HcW2IntRef") as IPlacedObjectGetter;
            // worldspace + exterior cell + placed
            var ws = pp.Worldspaces.FirstOrDefault(w => w.EditorID == "HcW2WS");
            var extCell = ws?.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.EditorID == "HcW2ExtCell");
            var extRef = extCell?.Temporary.FirstOrDefault(p => p.EditorID == "HcW2ExtRef");
            // topic + INFO
            var topic = pp.DialogTopics.FirstOrDefault(t => t.EditorID == "HcW2Topic");
            var info = topic?.Responses.FirstOrDefault();

            bool keys = weap?.FormKey == New(waOld) && list?.FormKey == New(flOld)
                        && intCell?.FormKey == New(icOld) && intRef?.FormKey == New(ipOld)
                        && ws?.FormKey == New(wsOld) && extCell?.FormKey == New(ecOld) && extRef?.FormKey == New(epOld)
                        && topic?.FormKey == New(dtOld) && info?.FormKey == New(inOld);
            bool internalRefs = list?.Items.FirstOrDefault()?.FormKey == New(waOld)        // FormList -> weapon
                                && intRef?.Base.FormKey == New(waOld);                      // placed.Base -> weapon (nested internal)
            bool inWindow = new[] { weap?.FormKey, list?.FormKey, intCell?.FormKey, intRef?.FormKey, ws?.FormKey, extCell?.FormKey, extRef?.FormKey, topic?.FormKey, info?.FormKey }
                            .All(k => k is { } fk && fk.ID >= RemapEngine.EslFloor && fk.ID <= RemapEngine.EslCeiling);
            nestedOk = keys && internalRefs && inWindow;
            nestedDetail = $"copied {ren.RecordsCopied}, renumbered {ren.RecordsRenumbered}; keys={keys} internalRefs={internalRefs} inWindow={inWindow}; " +
                           $"weap={weap?.FormKey} intRef={intRef?.FormKey} extRef={extRef?.FormKey} info={info?.FormKey}";
        }
        Console.WriteLine($"   NESTED   structural compact (flat+cell+worldspace+topic) : {(nestedOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"            {nestedDetail}");

        // --- EXTERNAL: identify-pass finds External (not Donor); RepointInPlace rewrites its ref to the new weapon key. ---
        bool externalOk = false; string extDetail = "";
        using (var resolver = LoadOrderResolver.Build(new[] { donorPath, extPath }))
        {
            var targets = plan.Success ? plan.Dict.Keys.ToHashSet() : new HashSet<FormKey>();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { donorKey.FileName.String };
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);
            bool identifyOk = id.HasExternalReferencers
                              && id.ExternalPlugins.Contains(extKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                              && !id.ExternalPlugins.Contains(donorKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                              && id.UnscannableRecords == 0;
            var rep = RemapEngine.RepointInPlace(resolver, extKey.FileName.String, plan.Success ? plan.Dict : new Dictionary<FormKey, FormKey>());
            FormKey? extAfter = null;
            if (rep.Success)
            {
                using var ee = SkyrimMod.CreateFromBinaryOverlay(extPath, SkyrimRelease.SkyrimSE);
                extAfter = ee.FormLists.First().Items.FirstOrDefault()?.FormKey;
            }
            externalOk = identifyOk && rep.Success && extAfter == New(waOld);
            extDetail = $"identify={identifyOk} (scanned {id.PluginsScanned}), repoint={rep.Success}, external ref {extAfter} -> expect {New(waOld)}";
        }
        Console.WriteLine($"   EXTERNAL identify + in-place repoint                     : {(externalOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"            {extDetail}");

        Console.WriteLine();
        bool pass = nestedOk && externalOk;
        Console.WriteLine($"=== remap-wave2-compact-guard: {(pass ? "PASS" : "FAIL")} ===");
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
