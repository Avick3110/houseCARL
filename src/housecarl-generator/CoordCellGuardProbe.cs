using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for COORDINATE-KEYED cell CREATE (the §4-(b) seam —
/// <c>dev/plans/COORD_KEYED_CELL_CREATE_BUILD_2026-06-20.md</c>), in the pattern of nested-create-guard. Drives the REAL
/// product path (<see cref="WritePatchBuilder.CreateRecords"/>) against a SYNTHESIZED master in TEMP — NO Skyrim.esm, so
/// it runs in CI (the manual <see cref="CoordCellProbe"/> scout samples vanilla). The master carries a Worldspace (the
/// exterior-cell parent) + a Weapon (the non-Worldspace-parent reject target). Where the scout proved the Mutagen block
/// math in isolation, this proves the mechanism THROUGH the production cleave — parent override → AddExteriorCell /
/// AddInteriorCell → multi-master serialize → re-open from disk.
/// Run: dotnet run --project src/housecarl-generator -- coord-cell-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"):
///   EXTERIOR    — a Cell created with parent=&lt;Worldspace FormKey&gt; + grid="1000,-1000" lands under
///                 block(31,-32)/subblock(125,-125) on disk, grid (1000,-1000), local 0x800+ (block math, real path).
///   INTERIOR    — a Cell created with NO parent + NO grid self-files into the Cells group at block=id%10/
///                 subblock=(id/10)%10, IsInteriorCell ON, local 0x800+.
///   PLACED      — a one-shot exterior Cell + a PlacedObject parent=&lt;that cell's editorid&gt; collection=Temporary:
///                 the ref lands in the new cell's Temporary (Placed-into-not-yet-present-cell rides the sibling path).
///   REJ-NOWS    — a Cell + grid with NO parent refuses loud (an exterior cell needs a Worldspace), NO file written.
///   REJ-NOGRID  — a Cell + a Worldspace parent but NO grid refuses loud (ambiguous), NO file written.
///   REJ-BADGRID — a Cell + a Worldspace parent + a non-numeric grid refuses loud (the "X,Y" format), NO file written.
///   REJ-NONWS   — a Cell + grid with a NON-Worldspace (Weapon) parent refuses loud, NO file written.
///   DUP-REJECT  — an into= re-run of the SAME cell editorid refuses loud (no silent duplicate — cells carry a stable
///                 EditorID, so the flat nested-children append carve-out does NOT transfer; PR #94 review).
/// </summary>
public static class CoordCellGuardProbe
{
    [CiProbe("coord-cell-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — COORDINATE-KEYED cell CREATE (§4-(b))  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-coord-cell-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: a master carrying a Worldspace (the exterior-cell parent) + a Weapon (the non-Worldspace reject). ---
        var mKey = new ModKey("HcCcGdMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey masterWsFk, masterWeapFk;
        try
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var ws = m.Worldspaces.AddNew(); ws.EditorID = "HcCcWorld";
            masterWsFk = ws.FormKey;
            var w = m.Weapons.AddNew(); w.EditorID = "HcCcWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            masterWeapFk = w.FormKey;
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the fixture master: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
            return 1;
        }

        bool fixturesOk;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var view = r.Capture();
            fixturesOk = view.ResolveWinner(masterWsFk) is not null && view.ResolveWinner(masterWeapFk) is not null;
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        var shaBefore = Sha(mPath);
        Console.WriteLine($"-- setup: master {mKey.FileName} with worldspace {masterWsFk}, weapon {masterWeapFk}; fixtures-resolve={fixturesOk}; corpus generated --");
        Console.WriteLine();

        var results = new List<(string name, bool pass, string detail)>();

        // ---------- EXTERIOR: a Cell placed by grid under the Worldspace ----------
        {
            string pPath = Path.Combine(tmpDir, "HcCcExterior.esp");
            int gx = 1000, gy = -1000;
            int bx = FloorDiv(gx, 32), by = FloorDiv(gy, 32), sx = FloorDiv(gx, 8), sy = FloorDiv(gy, 8);
            var specs = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcExtCell", ParentRef = masterWsFk.ToString(), Grid = $"{gx},{gy}", Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool ok = o.Success && o.Created.Count == 1;
            var cellFk = ok ? o.Created[0].FormKey : default;
            var loc = ok ? LocateExterior(pPath, masterWsFk, cellFk) : null;
            bool present = loc.HasValue;
            bool placedRight = loc is { } l && l.bx == bx && l.by == by && l.sx == sx && l.sy == sy;
            bool gridOk = loc is { } g && g.gx == gx && g.gy == gy;
            bool floored = ok && cellFk.ID >= 0x800 && cellFk.ModKey.FileName.String == "HcCcExterior.esp";
            bool pass = ok && present && placedRight && gridOk && floored;
            results.Add(("EXTERIOR cell by grid", pass,
                $"created={YN(ok)} present={YN(present)} block/sub-correct={YN(placedRight)} grid-correct={YN(gridOk)} local>=0x800={YN(floored)}{Err(o)}"));
        }

        // ---------- INTERIOR: a parentless Cell self-files by FormID digits ----------
        {
            string pPath = Path.Combine(tmpDir, "HcCcInterior.esp");
            var specs = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcIntCell", Edits = Array.Empty<WriteRequest>() } };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool ok = o.Success && o.Created.Count == 1;
            var cellFk = ok ? o.Created[0].FormKey : default;
            var loc = ok ? LocateInterior(pPath, cellFk) : null;
            bool present = loc.HasValue;
            bool placedRight = loc is { } l && l.block == (int)(cellFk.ID % 10) && l.sub == (int)((cellFk.ID / 10) % 10);
            bool interior = loc is { } l2 && l2.interior;
            bool floored = ok && cellFk.ID >= 0x800 && cellFk.ModKey.FileName.String == "HcCcInterior.esp";
            bool pass = ok && present && placedRight && interior && floored;
            results.Add(("INTERIOR cell by FormID digits", pass,
                $"created={YN(ok)} present={YN(present)} block/sub-correct={YN(placedRight)} interior-flag={YN(interior)} local>=0x800={YN(floored)}{Err(o)}"));
        }

        // ---------- PLACED: one-shot exterior cell + a PlacedObject into its Temporary (sibling nested path) ----------
        {
            string pPath = Path.Combine(tmpDir, "HcCcPlaced.esp");
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcPCell", ParentRef = masterWsFk.ToString(), Grid = "1500,1500", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "PlacedObject", EditorId = "HcCcPRef", ParentRef = "HcCcPCell", IntoCollection = "Temporary", Edits = Array.Empty<WriteRequest>() },
            };
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
            bool ok = o.Success && o.Created.Count == 2;
            var cellFk = ok ? o.Created[0].FormKey : default;
            var refFk = ok ? o.Created[1].FormKey : default;
            var temp = ok ? CellTemporary(pPath, masterWsFk, cellFk) : null;
            bool refUnder = temp is not null && temp.Contains(refFk);
            bool pass = ok && refUnder;
            results.Add(("PLACED into new exterior cell", pass,
                $"created={(o.Success ? o.Created.Count : 0)} ref-in-temporary={YN(refUnder)}{Err(o)}"));
        }

        // ---------- REJECT arms (all-or-nothing, no file written, message named) ----------
        results.Add(RejectArm("REJ-NOWS   grid, no parent", tmpDir, "RejNoWs", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcRej1", Grid = "1,2", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("Worldspace", StringComparison.OrdinalIgnoreCase)));

        results.Add(RejectArm("REJ-NOGRID parent, no grid", tmpDir, "RejNoGrid", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcRej2", ParentRef = masterWsFk.ToString(), Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("grid", StringComparison.OrdinalIgnoreCase)));

        results.Add(RejectArm("REJ-BADGRID non-numeric grid", tmpDir, "RejBadGrid", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcRej3", ParentRef = masterWsFk.ToString(), Grid = "abc", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("X,Y", StringComparison.OrdinalIgnoreCase) || msg.Contains("two integers", StringComparison.OrdinalIgnoreCase)));

        results.Add(RejectArm("REJ-NONWS  grid + Weapon parent", tmpDir, "RejNonWs", mPath, rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcRej4", ParentRef = masterWeapFk.ToString(), Grid = "1,2", Edits = Array.Empty<WriteRequest>() } },
            msg => msg.Contains("Worldspace", StringComparison.OrdinalIgnoreCase)));

        // ---- DUP-REJECT: an into= re-run of the SAME cell editorid refuses loud (no silent duplicate — cells carry a
        //      stable EditorID, so the flat nested-children append carve-out does not transfer; PR #94 review). ----
        {
            string pPath = Path.Combine(tmpDir, "HcCcDup.esp");
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcCcDupCell", Edits = Array.Empty<WriteRequest>() } };
            var o1 = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: false);
            var o2 = WritePatchBuilder.CreateRecords(r, rulebook, spec, pPath, extend: true);   // into= the same patch, same editorid
            bool firstOk = o1.Success && o1.Created.Count == 1;
            bool refused = !o2.Success && (o2.Error ?? "").Contains("already exists", StringComparison.OrdinalIgnoreCase);
            results.Add(("DUP-REJECT into= duplicate cell editorid", firstOk && refused,
                $"first-created={YN(firstOk)} re-run-refused={YN(refused)} err=[{(o2.Error ?? "").Replace('\n', ' ')}]"));
        }

        bool srcOk = shaBefore == Sha(mPath);

        // ---- VERDICT ----
        Console.WriteLine("Results:");
        foreach (var (name, pass, detail) in results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-32} {detail}");
        Console.WriteLine($"  [{(srcOk ? "PASS" : "FAIL")}] {"master byte-untouched",-32}");
        Console.WriteLine();
        bool allPass = results.All(r => r.pass) && srcOk;
        Console.WriteLine(allPass
            ? "=== ALL CHECKS PASS — coordinate-keyed cell create proven through the production create cleave\n" +
              "    (exterior by grid, interior by FormID digits, placed-into-new-cell, + the malformed rejects). ==="
            : "=== FAIL — see the checks above (a !! is the thing to resolve). ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return allPass ? 0 : 1;
    }

    /// <summary>A REJECT arm: drive CreateRecords expecting refusal, assert NO file written + the message matches.</summary>
    static (string, bool, string) RejectArm(string name, string tmpDir, string tag, string mPath, CorpusRulebook rulebook,
        WritePatchBuilder.CreateSpec[] specs, Func<string, bool> msgOk)
    {
        string outPath = Path.Combine(tmpDir, $"HcCc_{tag}.esp");
        using var r = LoadOrderResolver.Build(new[] { mPath });
        var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, outPath, extend: false);
        bool refused = !o.Success;
        bool named = refused && msgOk(o.Error ?? "");
        bool noFile = !File.Exists(outPath);
        bool pass = refused && named && noFile;
        return (name, pass, $"refused={YN(refused)} msg-named={YN(named)} no-file={YN(noFile)}" + (refused ? "" : $"  (unexpectedly wrote; created={o.Created.Count})"));
    }

    // ---- re-open helpers ----

    static (int bx, int by, int sx, int sy, int gx, int gy)? LocateExterior(string path, FormKey wsFk, FormKey cellFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var ws = back.Worldspaces.FirstOrDefault(w => w.FormKey == wsFk);
            if (ws is null) return null;
            foreach (var b in ws.SubCells ?? Enumerable.Empty<IWorldspaceBlockGetter>())
                foreach (var s in b.Items ?? Enumerable.Empty<IWorldspaceSubBlockGetter>())
                    foreach (var c in s.Items ?? Enumerable.Empty<ICellGetter>())
                        if (c.FormKey == cellFk && c.Grid is not null)
                            return (b.BlockNumberX, b.BlockNumberY, s.BlockNumberX, s.BlockNumberY, c.Grid.Point.X, c.Grid.Point.Y);
            return null;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static List<FormKey>? CellTemporary(string path, FormKey wsFk, FormKey cellFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var ws = back.Worldspaces.FirstOrDefault(w => w.FormKey == wsFk);
            if (ws is null) return null;
            foreach (var b in ws.SubCells ?? Enumerable.Empty<IWorldspaceBlockGetter>())
                foreach (var s in b.Items ?? Enumerable.Empty<IWorldspaceSubBlockGetter>())
                    foreach (var c in s.Items ?? Enumerable.Empty<ICellGetter>())
                        if (c.FormKey == cellFk)
                            return c.Temporary?.Select(x => x.FormKey).ToList() ?? new List<FormKey>();
            return null;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static (int block, int sub, bool interior)? LocateInterior(string path, FormKey cellFk)
    {
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var b in back.Cells.Records)
                foreach (var s in b.SubBlocks ?? Enumerable.Empty<ICellSubBlockGetter>())
                    foreach (var c in s.Cells ?? Enumerable.Empty<ICellGetter>())
                        if (c.FormKey == cellFk)
                            return (b.BlockNumber, s.BlockNumber, c.Flags.HasFlag(Cell.Flag.IsInteriorCell));
            return null;
        }
        catch { return null; }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
    static string Err(WritePatchBuilder.CreateOutcome o) => o.Success ? "" : "  err=" + (o.Error ?? "").Replace('\n', ' ');
    static string YN(bool b) => b ? "Y" : "N";
    static string Sha(string p) { using var s = File.OpenRead(p); using var h = SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
}
