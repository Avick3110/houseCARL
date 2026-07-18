using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// nif-inspect batch-wire guard (#229 — mesh_paths array on housecarl_nif_inspect) — locks the four batch contracts
/// of NifWire.Render over NifInspectBatchData, fully self-contained (constructed per-path results through the REAL
/// renderer via InternalsVisibleTo — no game data, no MO2 instance, no file I/O).
///
/// Arms:
///   1. INPUT ORDER — three results render as three per-mesh blocks in the order passed, never re-sorted.
///   2. PER-PATH ERROR ISOLATION — a failing path in the middle of the batch renders ITS named error line while
///      both neighbors still render their full summaries (a batch is never aborted by one bad path — Q3 per-path
///      loudness, batch-level resilience).
///   3. BATCH ALARMS ONCE, FIRST — the BSA read-failure alarm (batch-level: one asset capture pins every path)
///      renders exactly once, BEFORE the first per-mesh block, so a long batch can't truncate it away and a
///      3-mesh batch doesn't repeat it 3 times.
///   4. EXPLICIT CUT — a max_chars smaller than the batch cuts with the omitted-MESH count named ("N more mesh(es)
///      omitted at max_chars=..."), never a silent truncation; the alarms from arm 3 survive the cut.
///
/// Teeth (mutation-RED, verified at authoring): early-return from the render loop on the first Error result →
/// arm 2 FAILS; move AppendReadFailures inside the per-mesh loop → arm 3 FAILS; drop the omitted-count notice on
/// the cap break → arm 4 FAILS.
/// </summary>
internal static class NifInspectBatchGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nif-inspect batch-wire guard — input order, per-path errors, one-shot alarms, explicit cut");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noUnknown = Array.Empty<string>();
        const int BigCap = 80_000;

        const string PathA = "meshes\\a\\first.nif";
        const string PathB = "meshes\\b\\second.nif";
        const string PathC = "meshes\\c\\third.nif";

        // Arm 1 — input order: three OK results must appear as blocks in the order passed.
        var ordered = Batch(new[] { Ok(PathA, "ShapeA"), Ok(PathB, "ShapeB"), Ok(PathC, "ShapeC") });
        var o1 = NifWire.Render(ordered, none, noUnknown, BigCap);
        int ia = o1.IndexOf(PathA, StringComparison.Ordinal), ib = o1.IndexOf(PathB, StringComparison.Ordinal), ic = o1.IndexOf(PathC, StringComparison.Ordinal);
        Check(ia >= 0 && ib > ia && ic > ib, "1. input order: three meshes render in the order passed");

        // Arm 2 — per-path error isolation: B fails ABSENT between two clean reads; both neighbors keep their
        // summaries and B carries its named error.
        var mixed = Batch(new[] { Ok(PathA, "ShapeA"), NifInspectData.Fail(PathB, "ABSENT — no active mod or BSA provides this mesh path."), Ok(PathC, "ShapeC") });
        var o2 = NifWire.Render(mixed, none, noUnknown, BigCap);
        Check(o2.Contains("ABSENT") && o2.Contains("'ShapeA'") && o2.Contains("'ShapeC'"),
            "2. per-path error: the middle path's ABSENT is loud and both neighbors still render");
        Check(Regex.Matches(o2, Regex.Escape("read from:")).Count == 2,
            "2b. per-path error: exactly the two clean reads carry a 'read from:' resolution");

        // Arm 3 — batch alarms once, before the per-mesh blocks.
        var alarmed = new NifInspectBatchData(
            new[] { Ok(PathA, "ShapeA"), Ok(PathB, "ShapeB"), Ok(PathC, "ShapeC") },
            new[] { "Broken - Textures.bsa (header refused)" }, Array.Empty<string>(), "TestProfile");
        var o3 = NifWire.Render(alarmed, none, noUnknown, BigCap);
        Check(Regex.Matches(o3, Regex.Escape("could NOT be read")).Count == 1,
            "3. batch alarms: the BSA read-failure alarm renders exactly once for a 3-mesh batch");
        Check(o3.IndexOf("could NOT be read", StringComparison.Ordinal) < o3.IndexOf(PathA, StringComparison.Ordinal),
            "3b. batch alarms: the alarm renders BEFORE the first per-mesh block");

        // Arm 4 — explicit cut: a cap the header + alarm + first mesh exhausts must name the omitted-mesh count
        // (and the arm-3 alarm, rendered first, must survive the cut).
        var o4 = NifWire.Render(alarmed, none, noUnknown, 400);
        Check(o4.Contains("more mesh(es) omitted at max_chars=400"),
            "4. explicit cut: a small max_chars names the omitted-mesh count, never a silent truncation");
        Check(o4.Contains("could NOT be read"),
            "4b. explicit cut: the batch-level alarm still renders under the cut (alarms-first)");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
        return fail;
    }

    /// <summary>A batch with no batch-level alarms.</summary>
    static NifInspectBatchData Batch(IReadOnlyList<NifInspectData> results)
        => new(results, Array.Empty<string>(), Array.Empty<string>(), "TestProfile");

    /// <summary>A clean per-path result: one loose provider, a minimal 2-block SE mesh with one named shape.</summary>
    static NifInspectData Ok(string rel, string shapeName)
        => new(rel, new NifProvider("ModA", "loose"), new[] { new NifProvider("ModA", "loose") }, false, false, Mesh(shapeName), null);

    /// <summary>A minimal, valid-shaped SE inspect model — enough for the summary renderer (version line, block
    /// census, shape-name list, node count).</summary>
    static NifInspect Mesh(string shapeName) => new(
        "20.2.0.7", 12, 100, true, 2,
        new[] { new NifBlockTypeCount("NiNode", 1), new NifBlockTypeCount("BSTriShape", 1) },
        false, Array.Empty<string>(),
        new[] { new NifShape(shapeName, 0, 1f, "BSTriShape", null, null, Array.Empty<NifPartition>(), null, Array.Empty<NifTexture>(), Array.Empty<string>()) },
        Array.Empty<NifNode>(), Array.Empty<string>());
}
