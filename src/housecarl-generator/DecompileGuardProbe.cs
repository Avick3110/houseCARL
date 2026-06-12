using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlGenerator;

/// <summary>
/// Decompile guard — locks in housecarl_decompile_script's contract on COMMITTED fixtures (our own
/// authored probe sources, compiled once locally; nothing third-party in the repo), fully
/// self-contained for CI. What CI proves here is the DECOMPILE side; the recompile round-trip
/// proof is the dev-side bulk gate (98.80% exact across 10,189 provable pairs, 2026-06-12 doc) —
/// CI has no CK compiler, and this probe says so rather than pretending.
///
/// Arms (all drive the real seams the tool uses — DecompileTools.WriteObjects, PapyrusClassParents,
/// PapyrusDecompiler):
///   1. CONSTRUCT FIDELITY — decompile the construct-probe fixture (if/elseif/while, short-circuits,
///      arrays, casts, properties, states, events, globals), assert zero failed functions and an
///      EXACT match (modulo line endings) against the committed golden .psc. Any engine output drift
///      shows up here and forces a reviewed golden update.
///   2. UNREADABLE PEX LOUD — a truncated copy must throw out of Mutagen's parser (the tool maps it
///      to the named unreadable-class error); no partial output.
///   3. NEVER-OVERWRITE — a pre-existing target .psc stops WriteObjects with the path reported and
///      the file byte-untouched.
///   4. HIERARCHY DEGRADATION IS SOFT — the upcast fixture (Foo(a) where a:Actor, Foo wants
///      ObjectReference) decompiles WITH the committed vanilla map to the bare implicit form, and
///      WITHOUT any map to the explicit-cast form — correct either way, exactly the documented
///      degraded mode.
///   5. MULTI-OBJECT FLAG TABLE — a two-object pex (fixture parsed twice, second renamed) keeps
///      Hidden/Conditional on BOTH outputs: the per-object view must carry the file's user-flag
///      table or the flags silently vanish (PR #47 review must-fix; Conditional is functional).
///
/// Fixtures: src/housecarl-generator/fixtures/decompile/ (paths relative to the repo root, where CI
/// and the docs run this). Regenerate goldens after a REVIEWED engine-output change:
///   dotnet run --project src/housecarl-generator decompile-guard --write-golden
/// </summary>
internal static class DecompileGuardProbe
{
    const string FixtureDir = @"src/housecarl-generator/fixtures/decompile";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" decompile guard — engine fidelity + tool output contract");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var fixtures = Path.GetFullPath(FixtureDir);
        if (!Directory.Exists(fixtures))
        {
            Console.Error.WriteLine($"  FAIL  fixtures not found at {fixtures} — run from the repo root");
            return 1;
        }
        var constructPex = Path.Combine(fixtures, "HC_SpikeProbe01.pex");
        var upcastPex = Path.Combine(fixtures, "HC_UpcastProbe.pex");
        var goldenPath = Path.Combine(fixtures, "HC_SpikeProbe01.golden.psc");
        var upcastGoldenPath = Path.Combine(fixtures, "HC_UpcastProbe.golden.psc");

        // the committed vanilla baseline — the same asset the server ships beside the exe
        var (edges, baselineNote) = PapyrusClassParents.LoadBaseline(
            Path.GetFullPath(@"src/housecarl-mcp/vanilla-class-parents.json"));

        // ---- golden (re)generation mode -------------------------------------------------------
        if (args.Contains("--write-golden"))
        {
            File.WriteAllText(goldenPath, Decompile(constructPex, edges).Source);
            File.WriteAllText(upcastGoldenPath, Decompile(upcastPex, edges).Source);
            Console.WriteLine($"goldens written: {goldenPath}, {upcastGoldenPath} — review the diff before committing.");
            return 0;
        }

        // ---- 1) construct fidelity vs the committed golden ------------------------------------
        Console.WriteLine("--- 1: construct-probe fixture — zero failures + exact golden match ---");
        Check(baselineNote is null, $"committed vanilla baseline loads ({edges.Count} edges)");
        var work = Path.Combine(Path.GetTempPath(), "hc-decompile-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var pex = PexFile.CreateFromFile(constructPex, GameCategory.Skyrim);
            var o = DecompileTools.WriteObjects(pex, edges, work);
            Check(o.ExistingTarget is null && !o.UnnamedObject, "writes without refusal on a fresh folder");
            Check(o.Written.Count == 1 && Path.GetFileName(o.Written[0]) == "HC_SpikeProbe01.psc",
                  "one .psc, named by the OBJECT (filename==ScriptName rule)");
            Check(o.FunctionsFailed == 0, $"zero failed functions (of {o.FunctionsTotal})");
            Check(o.OptimizerHints == 0, "no optimizer hints on canonical CK-compiler output");
            Check(File.Exists(goldenPath), "committed golden exists");
            if (File.Exists(goldenPath) && o.Written.Count == 1)
            {
                var got = Norm(File.ReadAllText(o.Written[0]));
                var want = Norm(File.ReadAllText(goldenPath));
                Check(got == want, "decompiled source EXACTLY matches the golden (modulo line endings)");
                if (got != want)
                {
                    var g = got.Split('\n'); var w = want.Split('\n');
                    for (int i = 0; i < Math.Max(g.Length, w.Length); i++)
                        if (i >= g.Length || i >= w.Length || g[i] != w[i])
                        { Console.WriteLine($"         first diff at line {i + 1}:\n           got:  {(i < g.Length ? g[i] : "<EOF>")}\n           want: {(i < w.Length ? w[i] : "<EOF>")}"); break; }
                }
            }

            // ---- 3) never-overwrite (same work dir: the file from arm 1 now pre-exists) -------
            Console.WriteLine();
            Console.WriteLine("--- 3: never-overwrite — pre-existing target refuses, file untouched ---");
            var target = Path.Combine(work, "HC_SpikeProbe01.psc");
            File.WriteAllText(target, "SENTINEL — must survive");
            var before = File.ReadAllText(target);
            var o3 = DecompileTools.WriteObjects(pex, edges, work);
            Check(o3.ExistingTarget is not null && o3.ExistingTarget.Equals(target, StringComparison.OrdinalIgnoreCase),
                  "refusal names the existing target");
            Check(o3.Written.Count == 0, "nothing written on refusal");
            Check(File.ReadAllText(target) == before, "the existing file is byte-untouched");
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* temp scratch */ } }

        // ---- 6) statement-level JMPT fires the optimizer hint (live-fire finding 2026-06-12) -----
        // The §4 catalog's named Caprica marker: the CK compiler emits statement conditionals as JMPF
        // ALWAYS (its JMPTs live only inside short-circuit arms). Real Caprica ships (Campfire,
        // NL_MCM) are shape-canonical except for this inversion, so the original four flow-hint
        // sites underfired on them. Mutate the fixture exactly as an optimizer would — flip one
        // statement-level JMPF to JMPT — and the hint must fire while the source stays clean
        // (negated condition). Arm 1 above pins the other side: the UNMUTATED canonical fixture
        // must stay at zero hints (no false positives on PCompiler output, short-circuits included).
        Console.WriteLine();
        Console.WriteLine("--- 6: statement-level JMPT (optimizer inversion) fires the hint ---");
        {
            var mutated = PexFile.CreateFromFile(constructPex, GameCategory.Skyrim);
            var tally = ((PexObject)mutated.Objects[0]).States
                .SelectMany(s => s.Functions)
                .FirstOrDefault(f => string.Equals(f.FunctionName, "Tally", StringComparison.OrdinalIgnoreCase))?.Function;
            Check(tally is not null, "fixture carries the plain-conditional victim function (Tally)");
            if (tally is not null)
            {
                var jmpf = tally.Instructions.FirstOrDefault(x => x.OpCode == InstructionOpcode.JMPF);
                Check(jmpf is not null, "victim has a statement-level JMPF to invert");
                if (jmpf is not null)
                {
                    jmpf.OpCode = InstructionOpcode.JMPT;
                    var r6 = PapyrusDecompiler.DecompileFile(mutated, edges);
                    Check(r6.FunctionsFailed == 0, "inverted branch still structures clean (negated condition)");
                    Check(r6.OptimizerHints > 0, $"the optimizer hint FIRES on statement-level JMPT (hints={r6.OptimizerHints})");
                }
            }
        }

        // ---- 2) unreadable pex throws loud ------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 2: truncated pex — Mutagen throws, the tool's named unreadable class ---");
        var corrupt = Path.Combine(Path.GetTempPath(), "hc-decompile-guard-corrupt-" + Guid.NewGuid().ToString("N") + ".pex");
        try
        {
            var bytes = File.ReadAllBytes(constructPex);
            File.WriteAllBytes(corrupt, bytes[..(bytes.Length / 3)]);
            var threw = false;
            try { PexFile.CreateFromFile(corrupt, GameCategory.Skyrim); }
            catch { threw = true; }
            Check(threw, "truncated pex throws out of the parser (mapped to the named error, no output written)");
        }
        finally { try { File.Delete(corrupt); } catch { /* temp scratch */ } }

        // ---- 5) multi-object pex carries the user-flag TABLE (PR #47 review must-fix 1) ----------
        // PexFile.UserFlags maps bit→name per FILE; the per-object emission path must see the same
        // table or Hidden/Conditional silently vanish on the multi-object path (Conditional is
        // FUNCTIONAL — losing it breaks quest condition reads on recompile).
        Console.WriteLine();
        Console.WriteLine("--- 5: multi-object pex — Hidden/Conditional survive the per-object split ---");
        var work5 = Path.Combine(Path.GetTempPath(), "hc-decompile-guard5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work5);
        try
        {
            var multi = PexFile.CreateFromFile(constructPex, GameCategory.Skyrim);
            var second = (PexObject)PexFile.CreateFromFile(constructPex, GameCategory.Skyrim).Objects[0];
            second.Name = "HC_CloneProbe";
            multi.Objects.Add(second);
            var o5 = DecompileTools.WriteObjects(multi, edges, work5);
            Check(o5.Written.Count == 2 && o5.FunctionsFailed == 0, $"both objects written, none failed (wrote {o5.Written.Count})");
            var first = o5.Written.Count > 0 ? File.ReadAllText(o5.Written[0]) : "";
            var clone = o5.Written.Count > 1 ? File.ReadAllText(o5.Written[1]) : "";
            Check(first.Contains("extends Quest Hidden"), "object 1 keeps its Hidden flag on the multi-object path");
            Check(clone.Contains("extends Quest Hidden"), "object 2 keeps its Hidden flag on the multi-object path");
        }
        finally { try { Directory.Delete(work5, recursive: true); } catch { /* temp scratch */ } }

        // ---- 4) hierarchy degradation is soft ----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- 4: upcast fixture — implicit with the map, explicit without, correct both ways ---");
        {
            var withMap = Decompile(upcastPex, edges);
            var without = Decompile(upcastPex, null);
            Check(withMap.FunctionsFailed == 0 && without.FunctionsFailed == 0, "both modes structure clean");
            Check(withMap.Source.Contains("Foo(a)") && !withMap.Source.Contains("as ObjectReference", StringComparison.OrdinalIgnoreCase),
                  "WITH the vanilla map: the Actor→ObjectReference upcast stays implicit (Foo(a))");
            Check(without.Source.Contains("as ObjectReference", StringComparison.OrdinalIgnoreCase),
                  "WITHOUT a map: the upcast renders explicitly — correct source, the documented degraded mode");
            Check(File.Exists(upcastGoldenPath) && Norm(withMap.Source) == Norm(File.ReadAllText(upcastGoldenPath)),
                  "with-map output matches its golden");
        }

        Console.WriteLine();
        Console.WriteLine("(recompile round-trip proof = the dev-side bulk gate; CI has no CK compiler and this probe does not pretend otherwise)");
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    static PapyrusDecompiler.Result Decompile(string pexPath, IReadOnlyDictionary<string, string>? edges)
        => PapyrusDecompiler.DecompileFile(PexFile.CreateFromFile(pexPath, GameCategory.Skyrim), edges);

    static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
}
