using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Import-order guard — locks in housecarl_compile_script's import-path RANKING: the script's own
/// folder, then caller-passed import_dirs=/import_set=, then the modlist's auto-discovered source
/// folders (#200), then the vanilla sources LAST.
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
///      asserts the order contract: script's own folder FIRST, caller dirs NEXT, auto-discovered
///      modlist dirs AFTER those, vanilla LAST, case-insensitive dedup, quote-trim — including the
///      Data-root case, where the modlist scan hands the vanilla folder back as an ordinary auto dir
///      and it must be pulled to the end anyway.
///   2. END-TO-END (self-skips without the real CK compiler) — the SKSE-shadow proof: an import
///      dir carries a copy of vanilla Form.psc extended with a marker global function; a target
///      script calls it. Compiled with the EXACT list BuildImports produced, the compile succeeds
///      only if that dir outranks vanilla. Driven for BOTH ranks that must beat vanilla (a caller
///      import_dirs= and an auto-discovered mod), with a control compile WITHOUT the dir that must
///      fail (proving the marker really resolves from the extended copy, not from vanilla).
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

            // ---- AUTO-DISCOVERED modlist dirs (#200): a THIRD rank, between the caller and vanilla ----
            var autoA = Path.Combine(fake, "mods", "SKSE", "Source", "Scripts");
            var autoB = Path.Combine(fake, "mods", "PapyrusUtil", "Source", "Scripts");
            foreach (var d in new[] { autoA, autoB }) Directory.CreateDirectory(d);

            var withAuto = CompileTools.BuildImports(scriptDir, fakeCompiler, userB, new[] { autoA, autoB });
            Check(withAuto.Count == 5, $"5 dirs assembled with 2 auto dirs (got {withAuto.Count})");
            var iAutoA = withAuto.FindIndex(d => d.Equals(autoA, StringComparison.OrdinalIgnoreCase));
            var iAutoB = withAuto.FindIndex(d => d.Equals(autoB, StringComparison.OrdinalIgnoreCase));
            var iUserB = withAuto.FindIndex(d => d.Equals(userB, StringComparison.OrdinalIgnoreCase));
            var iVan2 = withAuto.FindIndex(d => d.Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase));
            Check(withAuto[0].Equals(scriptDir, StringComparison.OrdinalIgnoreCase), "own folder still FIRST with auto dirs in play");
            Check(iUserB < iAutoA, "the CALLER's import_dirs outrank the auto-discovered modlist dirs");
            Check(iAutoA < iAutoB, "auto dirs keep the order they were given — that order is MO2 priority, which decides which copy wins");
            Check(iAutoB < iVan2 && iVan2 == withAuto.Count - 1, "the auto dirs outrank vanilla, and vanilla is still LAST");

            // THE Data-ROOT CASE, and the reason the vanilla re-slot had to survive this change: the game's Data folder
            // is itself a VFS loose root, so the modlist scan finds Data\Source\Scripts and hands it in as an ordinary
            // auto dir — ahead of every mod that follows it. If it kept that slot, the vanilla copy of Actor.psc would
            // shadow SKSE's and every extended call would fail, which is exactly the bug the vanilla-last rule exists
            // to stop. It must be pulled back to LAST.
            var withData = CompileTools.BuildImports(scriptDir, fakeCompiler, null, new[] { fakeVanilla, autoA });
            Check(withData.Count == 3 && withData[^1].Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase)
                  && withData[1].Equals(autoA, StringComparison.OrdinalIgnoreCase),
                  "an auto dir that IS the vanilla folder (the Data loose root) is re-slotted to LAST, behind the mods");

            // A dir in BOTH the caller list and the scan keeps its CALLER slot (dedup keeps the first occurrence) —
            // otherwise defensively re-passing a folder you already have installed would demote it below the modlist.
            var overlap = CompileTools.BuildImports(scriptDir, fakeCompiler, autoA, new[] { autoB, autoA });
            Check(overlap.Count == 4 && overlap[1].Equals(autoA, StringComparison.OrdinalIgnoreCase)
                  && overlap[2].Equals(autoB, StringComparison.OrdinalIgnoreCase),
                  "a dir passed BOTH by the caller and by the scan keeps the caller slot");

            // Null/empty auto list ⇒ byte-identical to the pre-#200 behaviour (the default-off path and every old call).
            var noAuto = CompileTools.BuildImports(scriptDir, fakeCompiler, userB, null);
            var emptyAuto = CompileTools.BuildImports(scriptDir, fakeCompiler, userB, Array.Empty<string>());
            Check(noAuto.SequenceEqual(emptyAuto, StringComparer.OrdinalIgnoreCase) && noAuto.Count == 3,
                  "no auto dirs (null or empty) → the original three-rank list, unchanged");

            // VanillaSourceDir is the SINGLE definition both the assembly and the render's labelling read. Derived
            // twice they could disagree, and the render would then label the vanilla slot as a mod.
            Check(CompileTools.VanillaSourceDir(fakeCompiler) == fakeVanilla, "VanillaSourceDir resolves <game>\\Data\\Source\\Scripts");
            Check(CompileTools.VanillaSourceDir(Path.Combine(fake, "nogame", "Papyrus Compiler", "PapyrusCompiler.exe")) is null,
                  "VanillaSourceDir returns null when the folder isn't there (no phantom import dir)");

            // ---- PlanImports: the LABELS the render prints, on the real assembled path ----
            // A dir can belong to two origins at once, and the label decides what the user is told about a shadowed
            // script. Both collisions are ordinary, not exotic: you routinely compile a .psc sitting in a mod's own
            // Source\Scripts (own folder == an auto root), and the scan always reaches the game's Data folder
            // (an auto root == vanilla). Driven through the REAL PlanImports, not a hand-built plan.
            PapyrusSourceRoot Root(string provider, string dir) => new(provider, dir, @"Source\Scripts");
            // PlanImports narrows the scan to what the target REFERENCES, so the arm needs a real target: it names a
            // script that autoA provides and says nothing about autoB, which must therefore be dropped.
            var targetPsc = Path.Combine(scriptDir, "HCPlanTarget.psc");
            File.WriteAllText(targetPsc, "Scriptname HCPlanTarget extends Quest\n\nFunction Go()\n    HCSkseHelper.Ping()\nEndFunction\n");
            File.WriteAllText(Path.Combine(autoA, "HCSkseHelper.psc"), "Scriptname HCSkseHelper\n");
            File.WriteAllText(Path.Combine(autoB, "HCUnrelated.psc"), "Scriptname HCUnrelated\n");
            var labelled = CompileTools.PlanImports(
                targetPsc, scriptDir, fakeCompiler, new[] { userB },
                new[] { Root("Data", fakeVanilla), Root("SKSE", autoA), Root("PapyrusUtil", autoB), Root("Me", scriptDir) },
                autoEnabled: true, importSetName: null, warning: null);
            Check(labelled.Dirs.Any(d => d.Equals(autoA, StringComparison.OrdinalIgnoreCase)),
                  "a scanned folder the script REFERENCES is kept");
            Check(!labelled.Dirs.Any(d => d.Equals(autoB, StringComparison.OrdinalIgnoreCase)),
                  "a scanned folder the script never references is DROPPED (the narrowing that makes the scan usable at modlist scale)");
            Check(labelled.AutoScanned == 3 && labelled.AutoProviders.Count == 1,
                  "the plan reports BOTH numbers — 3 folders scanned, 1 kept — so a dropped dependency reads as dropped, not overlooked");
            string LabelOf(string dir) => labelled.Entries.First(e => e.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)).Origin;
            Check(LabelOf(scriptDir) == CompileTools.ImportPlan.OwnFolder,
                  "a dir that is BOTH the script's own folder and a scanned mod is labelled the own folder (it holds that slot)");
            Check(LabelOf(fakeVanilla) == CompileTools.ImportPlan.Vanilla,
                  "the game's Data root comes back from the scan as a mod, but is labelled VANILLA (it is the vanilla slot)");
            Check(LabelOf(autoA) == CompileTools.ImportPlan.AutoPrefix + "SKSE", "a scanned mod is labelled with its providing mod folder");
            Check(LabelOf(userB) == CompileTools.ImportPlan.CallerDirs, "a caller dir is labelled import_dirs=");
            Check(labelled.Dirs.SequenceEqual(CompileTools.BuildImports(scriptDir, fakeCompiler, userB,
                      new[] { fakeVanilla, autoA, scriptDir }), StringComparer.OrdinalIgnoreCase),
                  "PlanImports' dirs ARE BuildImports' output over the KEPT folders — the labels describe the path that actually ran");
            Check(labelled.CallerCount == 1 && labelled.AutoProviders.SequenceEqual(new[] { "SKSE" }),
                  "the summary counts are derived from the FINAL entries: 1 caller, 1 auto (Data and the own folder are not counted as mods)");

            // PR #296 review: a folder the caller passed EXPLICITLY must keep its caller identity even when the scan
            // also knows it. This is the recovery path the tool itself prints — a failure says "pass that folder via
            // import_dirs=", the user does, and the next run must not report it back as one the script references.
            // Both derived counts went wrong at once before this: CallerCount missed it, AutoProviders claimed it.
            var overlap2 = CompileTools.PlanImports(
                targetPsc, scriptDir, fakeCompiler, new[] { userB, autoB },
                new[] { Root("SKSE", autoA), Root("PapyrusUtil", autoB) },
                autoEnabled: true, importSetName: null, warning: null);
            string Label2(string dir) => overlap2.Entries.First(e => e.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)).Origin;
            Check(Label2(autoB) == CompileTools.ImportPlan.CallerDirs,
                  "a DROPPED candidate that the caller passed is labelled import_dirs=, not MO2:<mod> (it reached the path only because the caller asked)");
            Check(overlap2.CallerCount == 2 && overlap2.AutoProviders.SequenceEqual(new[] { "SKSE" }),
                  "…and the counts follow: 2 caller dirs, 1 from the scan — the 'kept the N this script REFERENCES BY NAME' number no longer over-counts by the overlap");

            var overlap3 = CompileTools.PlanImports(
                targetPsc, scriptDir, fakeCompiler, new[] { autoA },
                new[] { Root("SKSE", autoA) }, autoEnabled: true, importSetName: null, warning: null);
            Check(overlap3.CallerCount == 1 && overlap3.AutoProviders.Count == 0,
                  "a KEPT candidate the caller also passed counts as the caller's — it would be on the path with the scan switched off");
            // …and the mirror of that (PR #296 re-review): taking the caller SLOT must not erase the fact that the
            // WALK matched it. AutoProviders answers "why is this folder on the path"; Referenced answers "what does
            // this script reference" — the number the summary and the banner quote. One field doing both made a
            // genuine match render as "matched the 0 this script REFERENCES BY NAME".
            var overlapRef = CompileTools.PlanImports(
                targetPsc, scriptDir, fakeCompiler, new[] { autoA },
                new[] { Root("SKSE", autoA), Root("PapyrusUtil", autoB) },
                autoEnabled: true, importSetName: null, warning: null);
            Check(overlapRef.Referenced.SequenceEqual(new[] { "SKSE" }),
                  "a folder the walk matched stays in the REFERENCED count even when the caller also passed it (slot ≠ reference)");
            Check(overlapRef.AutoProviders.Count == 0 && overlapRef.CallerCount == 1,
                  "…while the SLOT attribution still credits the caller (the two questions answered separately)");
            Check(CompileTools.ImportSummary(overlapRef).Contains("matched 1 of 2"),
                  "…and the summary quotes the walk's number, not the slot's");

            // THE REGRESSION THE RE-REVIEW CAUGHT. Splitting the game's Data sources out of the mod candidates is only
            // safe if something still fills the vanilla slot. With a compiler whose own <game>\Data\Source\Scripts does
            // not exist — a hand-set compiler path, or a Stock Game whose sources live under MO2's data dir — the fold
            // left the path with NO vanilla at all, and every compile would fail on Form/Quest/ObjectReference while
            // the banner blamed a missing mod.
            var noSrcCompiler = Path.Combine(fake, "nogame", "Papyrus Compiler", "PapyrusCompiler.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(noSrcCompiler)!);
            Check(CompileTools.VanillaSourceDir(noSrcCompiler) is null, "control: this compiler has no vanilla sources beside it");
            var rescued = CompileTools.PlanImports(
                targetPsc, scriptDir, noSrcCompiler, Array.Empty<string>(),
                new[] { Root("SKSE", autoA) }, autoEnabled: true, importSetName: null, warning: null,
                gameDataSources: fakeVanilla);
            Check(rescued.Dirs.Any(d => d.Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase)),
                  "the modlist's own Data sources fill the vanilla slot when the compiler-relative folder is missing");
            Check(rescued.Entries[^1].Dir.Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase)
                  && rescued.Entries[^1].Origin == CompileTools.ImportPlan.Vanilla,
                  "…in the vanilla slot: LAST, and labelled vanilla rather than as a mod");
            Check(!rescued.VanillaMissing, "…and the plan does not claim vanilla is missing");
            // ONE decision, not two: the plan's VanillaMissing and the assembled path must be the same answer. They
            // were computed by two copies of the same expression, so a change to either could have left the plan
            // reporting a vanilla slot the path did not carry.
            Check(rescued.VanillaMissing == !rescued.Entries.Any(e => e.Origin == CompileTools.ImportPlan.Vanilla),
                  "VanillaMissing agrees with the assembled path (the vanilla dir is resolved ONCE and handed down)");
            Check(CompileTools.BuildImports(scriptDir, noSrcCompiler, null, null, fakeVanilla)[^1]
                      .Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase),
                  "BuildImports takes the caller's RESOLVED vanilla dir rather than re-deriving its own");

            // NeedsModlistScan — auto_imports=false must actually SKIP the scan. Fetching the vanilla fallback made
            // the call unconditional for one commit, which quietly cost the flag its only real use (the scan forces
            // the asset build and probes two layouts under every enabled mod) while three render strings still said
            // the mods had not been scanned. The vanilla-only branch is the single exception, and it is narrow.
            Check(CompileTools.NeedsModlistScan(true, fakeCompiler), "auto_imports=true scans");
            Check(CompileTools.NeedsModlistScan(true, noSrcCompiler), "…scans regardless of whether the compiler has vanilla beside it");
            Check(!CompileTools.NeedsModlistScan(false, fakeCompiler),
                  "auto_imports=false with the compiler's vanilla present does NOT touch the modlist — the flag's promise, and its whole point");
            Check(CompileTools.NeedsModlistScan(false, noSrcCompiler),
                  "auto_imports=false WITHOUT compiler vanilla still reads the modlist — the only place left to find any (a vanilla-less path resolves nothing)");

            // …and the strings printed on that path must not claim a scan verdict they no longer hold. The old
            // "your enabled mods were NOT scanned" was printed at the very moment the scan had just run.
            var offPlan = CompileTools.PlanImports(
                targetPsc, scriptDir, fakeCompiler, Array.Empty<string>(),
                Array.Empty<PapyrusSourceRoot>(), autoEnabled: false, importSetName: null, warning: null);
            var offText = CompileTools.ImportSummary(offPlan);
            Check(!offText.Contains("NOT scanned") && offText.Contains("NOT on the import path"),
                  "the auto_imports=false line states where the mods ARE NOT (the path), not what houseCARL did or didn't read");

            var stranded = CompileTools.PlanImports(
                targetPsc, scriptDir, noSrcCompiler, Array.Empty<string>(),
                new[] { Root("SKSE", autoA) }, autoEnabled: true, importSetName: null, warning: null,
                gameDataSources: null);
            Check(stranded.VanillaMissing && !stranded.Dirs.Any(d => d.Equals(fakeVanilla, StringComparison.OrdinalIgnoreCase)),
                  "with vanilla sources genuinely nowhere, the plan says so rather than shipping a silently vanilla-less path");
            Check(CompileTools.ImportSummary(stranded).Contains("NO vanilla Papyrus sources"),
                  "…and the render leads with it (Q3 — otherwise the missing-imports banner blames a mod)");
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

                // #200: the same proof for an AUTO-DISCOVERED dir. The modlist scan feeds dirs through a different
                // parameter and a LOWER rank than import_dirs=, so "caller beats vanilla" does not transitively prove
                // "a scanned mod beats vanilla" — and that is the case the new default actually relies on.
                var autoImports = CompileTools.BuildImports(work, compiler, null, new[] { extDir });
                var auto = HousecarlCore.PapyrusCompile.CompileObject(compiler, "HCImportOrderTarget", autoImports, outDir);
                Check(auto.Success && auto.PexPath is not null,
                      "an AUTO-DISCOVERED mod's extended copy also beats vanilla" +
                      (auto.Success ? "" : $" (first error: {(auto.Diagnostics.Count > 0 ? auto.Diagnostics[0].ToString() : auto.Stderr.Trim())})"));
            }
            finally { try { Directory.Delete(work, recursive: true); } catch { /* in-dir scratch; non-fatal */ } }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
