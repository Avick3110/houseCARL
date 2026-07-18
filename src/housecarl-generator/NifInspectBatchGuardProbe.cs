using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// nif-inspect batch-wire guard (#229 — mesh_paths array on housecarl_nif_inspect; hardened by the PR #243 review) —
/// locks the six batch contracts of NifWire.Render over NifInspectBatchData, fully self-contained (constructed
/// per-path results through the REAL renderer via InternalsVisibleTo — no game data, no MO2 instance, no file I/O).
/// The synthetic mesh model is NifServiceGuardProbe.FakeInspect — ONE builder for both render guards, not a fork.
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
///   5. ABSENT HEDGED AT POINT OF USE — an ABSENT result in a batch whose scan was incomplete (BSA read failures /
///      discovery warnings) carries BOTH per-path hedge lines right under its ABSENT line (asset_status parity —
///      the top-of-output alarm alone scrolls away in a long batch), and a non-ABSENT error is NOT hedged.
///   6. FIRST MESH ALWAYS ANSWERS — max_chars never starves a single-path call of its core answer: even when the
///      alarms alone exhaust the cap, the first mesh's block still renders (and no bogus omitted notice appears).
///
/// Teeth (mutation-RED, verified at authoring): early-return from the render loop on the first Error result →
/// arm 2 FAILS; move AppendReadFailures inside the per-mesh loop → arm 3 FAILS; drop the omitted-count notice on
/// the cap break → arm 4 FAILS; drop the per-path hedge in AppendMesh → arm 5 FAILS; drop the shown &gt; 0 guard on
/// the cap check → arm 6 FAILS.
/// </summary>
internal static class NifInspectBatchGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" nif-inspect batch-wire guard — order, isolation, one-shot alarms, explicit cut, ABSENT hedge, first-mesh answer");
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
        var mixed = Batch(new[] { Ok(PathA, "ShapeA"), Absent(PathB), Ok(PathC, "ShapeC") });
        var o2 = NifWire.Render(mixed, none, noUnknown, BigCap);
        Check(o2.Contains("ABSENT") && o2.Contains("'ShapeA0'") && o2.Contains("'ShapeC0'"),
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

        // Arm 5 — ABSENT hedged at point of use: with BOTH batch caveats present, the ABSENT result carries both
        // per-path hedge lines; the neighboring non-ABSENT parse error is NOT hedged (the hedge is ABSENT-specific).
        var caveated = new NifInspectBatchData(
            new[] { Absent(PathA), NifInspectData.Fail(PathB, "NiflySharp refused this mesh — not a NIF.") },
            new[] { "Broken - Textures.bsa (header refused)" }, new[] { "Skyrim.ini not found — base archives unscanned" }, "TestProfile");
        var o5 = NifWire.Render(caveated, none, noUnknown, BigCap);
        Check(o5.Contains("the mesh could live in the unreadable archive") && o5.Contains("BSAs that weren't enumerated"),
            "5. ABSENT hedge: both per-path hedge lines render under the ABSENT (read-failure + discovery)");
        Check(o5.IndexOf("ABSENT", StringComparison.Ordinal) < o5.IndexOf("could live in the unreadable archive", StringComparison.Ordinal),
            "5b. ABSENT hedge: the hedge sits at POINT OF USE (under the ABSENT line, not only in the top alarm)");
        Check(Regex.Matches(o5, Regex.Escape("may be incomplete")).Count == 2,
            "5c. ABSENT hedge: the non-ABSENT error is NOT hedged (exactly one hedged path, two hedge lines)");

        // Arm 6 — first mesh always answers: a cap smaller than the header+alarms still renders the sole mesh's
        // core block (resolution line), and no omitted notice fires for a fully-rendered batch.
        var soloAlarmed = new NifInspectBatchData(
            new[] { Ok(PathA, "ShapeA") },
            new[] { "Broken - Textures.bsa (header refused)" }, Array.Empty<string>(), "TestProfile");
        var o6 = NifWire.Render(soloAlarmed, none, noUnknown, 50);
        Check(o6.Contains("read from:") && o6.Contains(PathA),
            "6. first-mesh answer: a tiny max_chars cannot starve a single-path call of its resolution");
        Check(!o6.Contains("more mesh(es) omitted"),
            "6b. first-mesh answer: no bogus omitted notice when every mesh rendered");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
        return fail;
    }

    /// <summary>A batch with no batch-level alarms.</summary>
    static NifInspectBatchData Batch(IReadOnlyList<NifInspectData> results)
        => new(results, Array.Empty<string>(), Array.Empty<string>(), "TestProfile");

    /// <summary>A clean per-path result: one loose provider, a minimal 1-shape SE mesh named
    /// <paramref name="shapePrefix"/>0 (via the shared NifServiceGuardProbe.FakeInspect builder).</summary>
    static NifInspectData Ok(string rel, string shapePrefix)
        => new(rel, new NifProvider("ModA", "loose"), new[] { new NifProvider("ModA", "loose") }, false, false,
            NifServiceGuardProbe.FakeInspect(1, 0, false, Array.Empty<string>(), namePrefix: shapePrefix), null);

    /// <summary>An ABSENT per-path result — the no-provider outcome the renderer hedges at point of use.</summary>
    static NifInspectData Absent(string rel)
        => new(rel, null, Array.Empty<NifProvider>(), false, Absent: true, null,
            "ABSENT — no active mod or BSA provides this mesh path.");
}
