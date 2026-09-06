using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Compile-rider proof (EXTERNAL_TOOL_BRIDGE_PLAN step 2). Two layers:
///   1. PARSER regression — <see cref="PapyrusCompile.ParseDiagnostics"/> on the EXACT stderr format the real CK compiler
///      emits (captured 2026-06-05): "&lt;fullpath&gt;(line,col): message". Pure, always runnable.
///   2. END-TO-END — drives the shipped <see cref="PapyrusCompile.CompileObject"/> against the REAL PapyrusCompiler.exe on
///      a good and a deliberately-broken script, asserting good→.pex+Success and bad→no-pex+diagnostics. Skipped (not
///      failed) if the compiler isn't present; pass its path as the first arg to point elsewhere.
///
/// Run: dotnet run --project src/housecarl-generator compile-probe ["&lt;PapyrusCompiler.exe&gt;"]
/// </summary>
internal static class CompileProbe
{
    // Aaron's known Steam compiler (the CK ships with the vanilla Steam install, not the MO2 game copy). Override via arg.
    const string DefaultCompiler = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Papyrus Compiler\PapyrusCompiler.exe";

    [CiProbe("compile-probe")]
    public static int Run(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" compile rider — step 2: stderr parser + (if compiler present) real .psc → .pex");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- 1) PARSER on the real captured format ----
        Console.WriteLine("--- 1: ParseDiagnostics on the real CK stderr format ---");
        var realStderr = string.Join("\n", new[]
        {
            @"C:\mod\Source\Scripts\HCBad.psc(4,4): no viable alternative at character '@'",
            @"C:\mod\Source\Scripts\HCBad.psc(4,18): no viable alternative at input 'papyrus'",
            @"C:\mod\Source\Scripts\HCBad.psc(4,12): Unknown user flag papyrus",
            @"C:\mod\Source\Scripts\HCBad.psc(5,0): missing EOF at 'EndFunction'",
        });
        var diags = PapyrusCompile.ParseDiagnostics(realStderr);
        Check(diags.Count == 4, $"parses 4 diagnostics (got {diags.Count})");
        Check(diags.Count > 0 && diags[0].Line == 4 && diags[0].Col == 4 && diags[0].Message == "no viable alternative at character '@'",
              "first diagnostic: line=4 col=4 message intact");
        Check(diags.Count > 3 && diags[3].Line == 5 && diags[3].Col == 0, "later diagnostic line/col parsed");
        Check(PapyrusCompile.ParseDiagnostics("").Count == 0, "empty stderr → no diagnostics");
        Check(PapyrusCompile.ParseDiagnostics("Batch compile of 1 files finished. 1 succeeded, 0 failed.").Count == 0,
              "a non-diagnostic line is ignored (not mis-parsed)");

        // ---- 2) END-TO-END against the real compiler ----
        Console.WriteLine();
        Console.WriteLine("--- 2: real compile via PapyrusCompile.CompileObject ---");
        var compiler = args.Length > 0 ? args[0] : DefaultCompiler;
        if (!File.Exists(compiler))
        {
            Console.WriteLine($"  SKIP  no compiler at '{compiler}' (pass the PapyrusCompiler.exe path as an arg to run this layer)");
        }
        else
        {
            var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compiler));
            var vanilla = Path.Combine(gameRoot!, "Data", "Source", "Scripts");
            Check(Directory.Exists(vanilla), $"vanilla sources derived from compiler dir exist ({vanilla})");

            var work = Path.Combine(Path.GetTempPath(), ".compile-probe-gen");
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(work, "HCGood.psc"),
                "Scriptname HCGood extends Quest\n\nFunction Foo()\n    Debug.Trace(\"ok from houseCARL\")\nEndFunction\n");
            File.WriteAllText(Path.Combine(work, "HCBad.psc"),
                "Scriptname HCBad extends Quest\n\nFunction Foo()\n    @@@ not valid papyrus\nEndFunction\n");
            var imports = new[] { work, vanilla };
            try
            {
                var good = PapyrusCompile.CompileObject(compiler, "HCGood", imports, outDir);
                Check(good.Ran, "good: compiler ran");
                Check(good.Success && good.PexPath is not null && File.Exists(good.PexPath), "good: Success + .pex written");
                Check(good.Diagnostics.Count == 0, $"good: no diagnostics (got {good.Diagnostics.Count})");

                var bad = PapyrusCompile.CompileObject(compiler, "HCBad", imports, outDir);
                Check(bad.Ran, "bad: compiler ran");
                Check(!bad.Success && bad.PexPath is null, "bad: Success=false + no .pex");
                Check(bad.Diagnostics.Count > 0, $"bad: structured diagnostics produced (got {bad.Diagnostics.Count})");
                if (bad.Diagnostics.Count > 0) Console.WriteLine("         e.g. " + bad.Diagnostics[0]);

                // RECOMPILE behaviour (Aaron 2026-06-06): a prior .pex is LEFT in place — a successful recompile updates
                // it (success = THIS run wrote it, detected by the write-time advancing, NOT by a pre-delete); a FAILED
                // recompile must NOT destroy the last good build (non-destructive — the user deletes outputs at will).
                // Without a .pex from the good compile there is nothing to recompile over; say that rather than
                // dereference the null and report the whole probe as a throw.
                if (good.PexPath is null)
                {
                    Console.WriteLine("  SKIP  recompile checks: the good compile produced no .pex (see the failed check above)");
                }
                else
                {
                    var pex = good.PexPath;
                    var firstWriteUtc = File.GetLastWriteTimeUtc(pex);
                    var reGood = PapyrusCompile.CompileObject(compiler, "HCGood", imports, outDir);
                    Check(reGood.Success && File.Exists(pex), "recompile (still valid): Success + .pex present");
                    Check(File.GetLastWriteTimeUtc(pex) > firstWriteUtc, "recompile (still valid): .pex rewritten this run (write-time advanced; no pre-delete)");

                    File.WriteAllText(Path.Combine(work, "HCGood.psc"),
                        "Scriptname HCGood extends Quest\n\nFunction Foo()\n    @@@ broken edit\nEndFunction\n");
                    var reBad = PapyrusCompile.CompileObject(compiler, "HCGood", imports, outDir);
                    Check(!reBad.Success && reBad.PexPath is null, "recompile (now broken): reports failure, no new .pex");
                    Check(File.Exists(pex), "recompile (now broken): the PRIOR .pex is LEFT INTACT (non-destructive)");
                }
            }
            finally { try { Directory.Delete(work, recursive: true); } catch { /* temp scratch; non-fatal */ } }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
