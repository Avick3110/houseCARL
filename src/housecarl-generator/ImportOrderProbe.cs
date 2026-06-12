using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Import-order guard — locks in the rule that caller-passed import_dirs OUTRANK the auto-added
/// vanilla source folder in housecarl_compile_script's import list.
///
/// Why it matters: the CK compiler resolves every referenced script to the FIRST matching .psc
/// across the import dirs, and mods routinely ship EXTENDED copies of vanilla sources (SKSE's
/// Actor.psc/Game.psc/Form.psc above all). With vanilla ranked above the user's folders, the
/// vanilla copy wins and any call to an extended function fails "not a function or does not exist"
/// even though the user explicitly passed the right folder (measured twice during the PEX bulk
/// gate, spike findings §5.12).
///
/// Two arms:
///   1. PURE (always runs, CI-safe) — fabricates a fake game layout in temp (compiler path + a
///      Data\Source\Scripts dir), drives the REAL <see cref="CompileTools.BuildImports"/>, and
///      asserts the order contract: script's own folder FIRST, caller dirs NEXT, vanilla LAST,
///      case-insensitive dedup, quote-trim.
///   2. END-TO-END (self-skips without the real CK compiler) — the SKSE-shadow proof: an import
///      dir carries a copy of vanilla Form.psc extended with a marker global function; a target
///      script calls it. Compiled with the EXACT list BuildImports produced, the compile succeeds
///      only if the user's dir outranks vanilla. A control compile WITHOUT the import dir must
///      fail (proves the marker really resolves from the user's copy, not from vanilla).
///
/// Run: dotnet run --project src/housecarl-generator import-order-guard ["&lt;PapyrusCompiler.exe&gt;"]
/// </summary>
internal static class ImportOrderProbe
{
    // Aaron's known Steam compiler (same default as CompileProbe). Override via arg.
    const string DefaultCompiler = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Papyrus Compiler\PapyrusCompiler.exe";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" import-order guard — import_dirs outrank the vanilla auto-import");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // ---- 1) PURE: order contract on the real BuildImports, fabricated game layout ----
        Console.WriteLine("--- 1: BuildImports order contract (fabricated layout; always runs) ---");
        var fake = Path.Combine(Path.GetTempPath(), "hc-import-order-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fakeCompiler = Path.Combine(fake, "game", "Papyrus Compiler", "PapyrusCompiler.exe");
            var fakeVanilla = Path.Combine(fake, "game", "Data", "Source", "Scripts");
            var scriptDir = Path.Combine(fake, "work");
            var userA = Path.Combine(fake, "skse src");      // spaced path on purpose
            var userB = Path.Combine(fake, "other");
            foreach (var d in new[] { Path.GetDirectoryName(fakeCompiler)!, fakeVanilla, scriptDir, userA, userB })
                Directory.CreateDirectory(d);

            var got = CompileTools.BuildImports(scriptDir, fakeCompiler, $"\"{userA}\";{userB}");
            Check(got.Count == 4, $"4 dirs assembled (got {got.Count}: {string.Join(" | ", got)})");
            Check(got.Count > 0 && got[0].Equals(scriptDir, StringComparison.OrdinalIgnoreCase),
                  "the script's own folder is FIRST");
            var iVan = got.FindIndex(d => d.Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase));
            var iA = got.FindIndex(d => d.Equals(userA, StringComparison.OrdinalIgnoreCase));
            var iB = got.FindIndex(d => d.Equals(userB, StringComparison.OrdinalIgnoreCase));
            Check(iA >= 0, "quoted spaced import dir survives (quote-trim)");
            Check(iVan == got.Count - 1, $"vanilla auto-import is LAST (index {iVan} of {got.Count - 1})");
            Check(iA >= 0 && iB >= 0 && iVan > iA && iVan > iB,
                  "every caller dir outranks the vanilla auto-import");

            var dedup = CompileTools.BuildImports(scriptDir, fakeCompiler, scriptDir.ToUpperInvariant() + ";" + fakeVanilla);
            Check(dedup.Count == 2, $"case-insensitive dedup holds (got {dedup.Count})");
            // PR #46 review pin: a caller re-passing the auto-added vanilla dir (defensively, ahead of
            // their real dirs) must NOT pin vanilla into the caller slot and resurrect the shadowing —
            // the auto-vanilla is authoritative-LAST regardless of where the caller listed it.
            var repass = CompileTools.BuildImports(scriptDir, fakeCompiler, fakeVanilla + ";" + userB);
            Check(repass.Count == 3 && repass[^1].Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase)
                  && repass[1].Equals(userB, StringComparison.OrdinalIgnoreCase),
                  "re-passed vanilla stays LAST; the caller's real dir keeps the caller slot");
            var none = CompileTools.BuildImports(scriptDir, fakeCompiler, null);
            Check(none.Count == 2 && none[^1].Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase),
                  "no import_dirs → own folder + vanilla, vanilla last");
            // script ITSELF in the vanilla folder: the own-folder slot survives (vanilla == scriptDir).
            var inVan = CompileTools.BuildImports(fakeVanilla, fakeCompiler, userB);
            Check(inVan.Count == 2 && inVan[0].Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase),
                  "script inside the vanilla folder keeps its own-folder slot first");
        }
        finally { try { Directory.Delete(fake, recursive: true); } catch { /* temp scratch; non-fatal */ } }

        // ---- 2) END-TO-END: the SKSE-shadow proof against the real compiler ----
        Console.WriteLine();
        Console.WriteLine("--- 2: real compile — extended vanilla copy in import_dirs must win ---");
        var compiler = args.Length > 0 ? args[0] : DefaultCompiler;
        var realVanilla = File.Exists(compiler)
            ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(compiler))!, "Data", "Source", "Scripts")
            : null;
        if (realVanilla is null || !Directory.Exists(realVanilla) || !File.Exists(Path.Combine(realVanilla, "Form.psc")))
        {
            Console.WriteLine($"  SKIP  no compiler/vanilla sources at '{compiler}' (pass the PapyrusCompiler.exe path as an arg to run this layer)");
        }
        else
        {
            var work = Path.Combine(Environment.CurrentDirectory, ".import-order-probe-gen");
            var extDir = Path.Combine(work, "ext");
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(extDir); Directory.CreateDirectory(outDir);
            try
            {
                // the shadow: vanilla Form.psc + a marker global only OUR copy has
                File.WriteAllText(Path.Combine(extDir, "Form.psc"),
                    File.ReadAllText(Path.Combine(realVanilla, "Form.psc")) +
                    "\nint Function HC_ImportOrderProbeExt() global\n    return 1\nEndFunction\n");
                File.WriteAllText(Path.Combine(work, "HCImportOrderTarget.psc"),
                    "Scriptname HCImportOrderTarget extends Quest\n\nint Function UseExt()\n    return Form.HC_ImportOrderProbeExt()\nEndFunction\n");

                // the EXACT list the tool would assemble, ext dir passed as import_dirs
                var imports = CompileTools.BuildImports(work, compiler, extDir);
                var r = HousecarlCore.PapyrusCompile.CompileObject(compiler, "HCImportOrderTarget", imports, outDir);
                Check(r.Ran, "compiler ran");
                Check(r.Success && r.PexPath is not null,
                      "extended copy WINS: target calling the marker function compiles" +
                      (r.Success ? "" : $" (first error: {(r.Diagnostics.Count > 0 ? r.Diagnostics[0].ToString() : r.Stderr.Trim())})"));

                // control: WITHOUT the ext dir the marker must be unknown (it is not in real vanilla)
                var ctlImports = CompileTools.BuildImports(work, compiler, null);
                var ctl = HousecarlCore.PapyrusCompile.CompileObject(compiler, "HCImportOrderTarget", ctlImports, outDir);
                Check(ctl.Ran && !ctl.Success,
                      "control without import_dirs FAILS (the marker resolves only from the caller's copy)");
            }
            finally { try { Directory.Delete(work, recursive: true); } catch { /* in-dir scratch; non-fatal */ } }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
