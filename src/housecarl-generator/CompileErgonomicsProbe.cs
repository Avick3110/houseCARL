using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Compile-rider ergonomics guard (HCBR-2026-06-15-01 / PR-J, items 6.2 + 6.3). The service-layer (housecarl-mcp) half of
/// the compiler/BSA ergonomics work — the pure-core ToolBridge half (auto-detect candidates, the looked-here prompt, the
/// cross-instance sharing lock) lives in <see cref="ToolBridgeProbe"/>. Two LoadOrderService seams, both asserted on
/// SYNTHETIC paths — no MO2 instance, no game data, no record index:
///
///   A  <see cref="LoadOrderService.GameDirOrNull"/> (6.2 auto-detect hint) — NULL-SAFE by contract: it feeds the compiler
///      auto-detect, so a failure must fall through to the forcing prompt, never throw and abort the compile.
///        • explicit-paths mode → DataDir's parent (the game dir), derived without an ini read;
///        • unconfigured → null (no _configured guard would otherwise hit EnsurePathsDerived's NotConfigured throw);
///        • an UNUSABLE instance dir → null, NOT a thrown exception (the rider's own config gate names the real problem).
///   B  <see cref="LoadOrderService.ScriptOutputContract"/> (6.3 output_dir=) — the DECIDED contract (Aaron 2026-06-16):
///      output_dir is a mod-folder ROOT and houseCARL appends Scripts\ (so the .pex deploys), with a double-Scripts guard
///      and a Q3 deployability warning. PURE (no I/O) so the riskiest change's only proof isn't punted:
///        • a bare root gets Scripts\ appended; a root already ending in Scripts\ does NOT get a second one (any case);
///        • a path under the MO2 mods dir or a game Data\Scripts is deployable (no warning); one outside any deploy root
///          still lands but carries the Q3 note (never a clean "done" for a .pex MO2 won't deploy);
///        • the output path is the chosen contract WITHOUT calling ResolvePatchModFolder (no houseCARL mod folder cut),
///          and the rider's RiderFolder is user-owned (CreatedFresh=false) so residue cleanup never deletes it.
///
/// Run: dotnet run --project src/housecarl-generator compile-ergonomics-guard
/// </summary>
internal static class CompileErgonomicsProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" compile-ergonomics guard — GameDirOrNull null-safety + output_dir= contract (PR-J)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var tmpStore = Path.Combine(Path.GetTempPath(), "hc-comperg-store-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UserConfigStore(tmpStore);

            // ---------------------------------------------------------- A) GameDirOrNull — the compiler auto-detect hint
            Console.WriteLine("--- A: LoadOrderService.GameDirOrNull — game dir derived; null-safe when it can't be ---");

            // explicit-paths mode: DataDir is set directly, so the game dir = DataDir's parent (no ini read, no instance).
            var explicitSvc = LoadOrderService.WithExplicitPaths(@"C:\Game\Skyrim Special Edition\Data", @"C:\Mods", @"C:\Profile", 0, store);
            Check(explicitSvc.GameDirOrNull() == @"C:\Game\Skyrim Special Edition",
                  "explicit mode: GameDirOrNull = DataDir's parent (the game install dir)");

            // unconfigured: no instance, not configured → null (not a throw).
            var unconfigured = LoadOrderService.WithInstance(null, 0, store);
            bool unconfThrew = false; string? unconfResult = null;
            try { unconfResult = unconfigured.GameDirOrNull(); } catch { unconfThrew = true; }
            Check(!unconfThrew && unconfResult is null, "unconfigured: GameDirOrNull returns null (never throws)");

            // an UNUSABLE instance dir: configured (non-blank), but EnsurePathsDerived throws on Resolve → caught → null.
            var badInstance = LoadOrderService.WithInstance(
                Path.Combine(Path.GetTempPath(), "hc-no-such-instance-" + Guid.NewGuid().ToString("N")), 0, store);
            bool badThrew = false; string? badResult = null;
            try { badResult = badInstance.GameDirOrNull(); } catch { badThrew = true; }
            Check(!badThrew && badResult is null,
                  "unusable instance: GameDirOrNull returns null, does NOT throw (best-effort hint — the rider's config gate reports the real problem)");

            // CompilerGameDirHints: the ordered auto-detect hint list. [0] = the load-order game dir; then the located real
            // Steam install (environment-dependent — absent on a CI runner with no Skyrim, verified on Aaron's rig). NULL-SAFE
            // end to end: the GameFinder/registry call is wrapped, so a hiccup yields fewer hints, never throws.
            bool hintsThrew = false; IReadOnlyList<string>? hints = null;
            try { hints = explicitSvc.CompilerGameDirHints(); } catch { hintsThrew = true; }
            Check(!hintsThrew && hints is not null && hints.Contains(@"C:\Game\Skyrim Special Edition"),
                  "CompilerGameDirHints includes the load-order game dir as the first hint, and never throws (locator is best-effort)");
            bool unconfHintsThrew = false; IReadOnlyList<string>? unconfHints = null;
            try { unconfHints = unconfigured.CompilerGameDirHints(); } catch { unconfHintsThrew = true; }
            Check(!unconfHintsThrew && unconfHints is not null,
                  "CompilerGameDirHints on an unconfigured service returns a list (no load-order hint; locator-only), never throws");
        }
        finally { try { File.Delete(tmpStore); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- B) output_dir= contract (6.3): pure double-Scripts guard
        Console.WriteLine();
        Console.WriteLine("--- B1: ScriptOutputContract (pure) — append Scripts\\ with the double-Scripts guard + deployability ---");
        const string mods = @"C:\MO2\mods", data = @"C:\Game\Skyrim Special Edition\Data";

        var bare = LoadOrderService.ScriptOutputContract(@"C:\MyMod", mods, data);
        Check(bare.scriptsDir == @"C:\MyMod\Scripts" && bare.appendedScripts, "a bare mod-folder root gets Scripts\\ appended");

        var already = LoadOrderService.ScriptOutputContract(@"C:\MyMod\Scripts", mods, data);
        Check(already.scriptsDir == @"C:\MyMod\Scripts" && !already.appendedScripts, "double-Scripts guard: a root already ending in Scripts is NOT doubled");

        var lower = LoadOrderService.ScriptOutputContract(@"C:\MyMod\scripts", mods, data);
        Check(lower.scriptsDir == @"C:\MyMod\scripts" && !lower.appendedScripts, "double-Scripts guard is case-insensitive (…\\scripts not doubled)");

        var trailing = LoadOrderService.ScriptOutputContract(@"C:\MyMod\Scripts\", mods, data);
        Check(trailing.scriptsDir == @"C:\MyMod\Scripts" && !trailing.appendedScripts, "double-Scripts guard tolerates a trailing separator");

        Console.WriteLine();
        Console.WriteLine("--- B2: ScriptOutputContract — deployability warning (Q3: never a clean done for a .pex that won't load) ---");
        Check(bare.deployWarning is not null, "a path under NEITHER mods nor Data carries the Q3 deploy warning");
        // DEPLOYABLE (no warning): exactly <mods>\<modFolder>\Scripts, or <data>\Scripts.
        Check(LoadOrderService.ScriptOutputContract(@"C:\MO2\mods\MyPatch", mods, data).deployWarning is null,
              "a real mod folder (<mods>\\MyPatch) is deployable (no warning)");
        Check(LoadOrderService.ScriptOutputContract(data, mods, data).deployWarning is null,
              "the game's Data folder (-> <data>\\Scripts) is deployable (no warning)");
        // NON-deployable (warns) — the tightened rule (review nit): "under mods" alone is not enough.
        Check(LoadOrderService.ScriptOutputContract(@"C:\MO2\mods", mods, data).deployWarning is not null,
              "the mods ROOT itself (-> <mods>\\Scripts, no mod folder) WARNS — MO2 won't deploy it (tightened nit)");
        Check(LoadOrderService.ScriptOutputContract(@"C:\MO2\mods\X\Sub", mods, data).deployWarning is not null,
              "a NESTED path (<mods>\\X\\Sub\\Scripts -> Data\\Sub\\Scripts) WARNS — not Data\\Scripts (tightened nit)");
        Check(LoadOrderService.ScriptOutputContract(@"C:\Game\Skyrim Special Edition\Data\Sub", mods, data).deployWarning is not null,
              "a nested Data path (<data>\\Sub\\Scripts) WARNS — the game loads only <data>\\Scripts");
        Check(LoadOrderService.ScriptOutputContract(@"C:\MO2\modsX\Foo", mods, data).deployWarning is not null,
              "segment-boundary safe: C:\\MO2\\modsX is NOT 'under' C:\\MO2\\mods (still warns)");
        // 2026-08-07 (#312's shared refactor): the MO2 OVERWRITE folder deploys too — it is mapped onto Data at top
        // priority and is where xEdit/CK/Synthesis output lands, so warning about it was a false alarm. Pinned BOTH
        // ways, because the overwrite root is a parameter: unknown root → the old warning still fires.
        const string over = @"C:\MO2\overwrite";
        Check(LoadOrderService.ScriptOutputContract(over, mods, data, over).deployWarning is null
              && LoadOrderService.ScriptOutputContract(over, mods, data).deployWarning is not null,
              "the MO2 overwrite folder deploys when its root is known, and is judged on that root (not by folder name)");
        // …and a drive ROOT keeps its separator: "C:\" trimmed to "C:" yields the drive-RELATIVE "C:Scripts".
        Check(LoadOrderService.ScriptOutputContract(@"C:\", mods, data).scriptsDir == @"C:\Scripts",
              $"a drive root appends correctly — got [{LoadOrderService.ScriptOutputContract(@"C:\", mods, data).scriptsDir}] (want C:\\Scripts, never C:Scripts)");

        // ---------------------------------------------------------- B3: ResolveExplicitScriptFolder — no patch folder cut
        Console.WriteLine();
        Console.WriteLine("--- B3: ResolveExplicitScriptFolder — user-owned, ResolvePatchModFolder NOT called, cleanup bypassed ---");
        var bRoot = Path.Combine(Path.GetTempPath(), "hc-comperg-b-" + Guid.NewGuid().ToString("N"));
        var tStore = Path.Combine(bRoot, "user.json");
        var tMods = Path.Combine(bRoot, "mods");
        var tData = Path.Combine(bRoot, "game", "Data");
        var tOut = Path.Combine(bRoot, "elsewhere", "MyMod");           // OUTSIDE the mods tree
        try
        {
            Directory.CreateDirectory(tMods); Directory.CreateDirectory(tData);
            var svc = LoadOrderService.WithExplicitPaths(tData, tMods, "", 0, new UserConfigStore(tStore));

            var rf = svc.ResolveExplicitScriptFolder(tOut, out var warn);
            Check(rf.OutputDir == Path.Combine(tOut, "Scripts"), "output path = output_dir\\Scripts (the chosen contract)");
            Check(!rf.CreatedFresh, "the folder is USER-OWNED (CreatedFresh=false), so residue cleanup never deletes it");
            Check(Directory.Exists(rf.OutputDir), "the Scripts\\ folder is created under output_dir");
            Check(!Directory.EnumerateFileSystemEntries(tMods).Any(),
                  "ResolvePatchModFolder was NOT called — no houseCARL patch folder cut under ModsDir");
            Check(warn is not null, "output_dir outside the mods tree carries the deploy warning");
            // The load-bearing bypass: on a failed compile RemoveOrNameRiderResidue must NOT delete the user's folder.
            // (Asserting it returns null alone is too weak — an EMPTY fresh folder also returns null because it gets
            // deleted; the real tooth is that a user-owned folder SURVIVES.)
            var residue = svc.RemoveOrNameRiderResidue(rf);
            Check(residue is null && Directory.Exists(rf.OutputDir),
                  "residue cleanup never deletes a user-owned output_dir folder (CreatedFresh=false: returns null, folder survives)");

            // A path UNDER the mods tree is deployable → no warning.
            var rfIn = svc.ResolveExplicitScriptFolder(Path.Combine(tMods, "MyPatch"), out var warnIn);
            Check(rfIn.OutputDir == Path.Combine(tMods, "MyPatch", "Scripts") && warnIn is null,
                  "an output_dir under the MO2 mods folder deploys cleanly (no warning)");

            // review nit #4: if <output_dir>\Scripts already exists AS A FILE, the create throws IOException — it must be
            // re-stamped as a friendly InvalidOperationException (which the rider renders as a clean "error: ...") rather
            // than escaping to Guard.Tool's generic "internal failure" wording.
            var tColl = Path.Combine(bRoot, "collision");
            Directory.CreateDirectory(tColl);
            File.WriteAllText(Path.Combine(tColl, "Scripts"), "a file where the Scripts folder should go");
            bool friendly = false;
            try { svc.ResolveExplicitScriptFolder(tColl, out _); }
            catch (InvalidOperationException) { friendly = true; }
            catch { /* any other exception type → not friendly */ }
            Check(friendly, "a file at <output_dir>\\Scripts yields a friendly InvalidOperationException, not a raw IOException");
        }
        finally { try { Directory.Delete(bRoot, recursive: true); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- C: success message matches the actual destination (Q3)
        Console.WriteLine();
        Console.WriteLine("--- C: CompileTools.Render — the success line names the RIGHT destination (review nit #1) ---");
        var ok = new HousecarlCore.CompileResult(
            Success: true, ObjectName: "MyScript", PexPath: @"C:\out\Scripts\MyScript.pex",
            Diagnostics: Array.Empty<HousecarlCore.PapyrusDiagnostic>(), Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var defaultMsg = CompileTools.Render(ok, Plan(), userChoseOutputDir: false);
        var outDirMsg = CompileTools.Render(ok, Plan(), userChoseOutputDir: true);
        Check(defaultMsg.Contains("houseCARL patch-mod folder") && defaultMsg.Contains("enable it in MO2"),
              "default destination: success names the houseCARL patch-mod folder + the MO2-enable step");
        Check(outDirMsg.Contains("output folder you chose") && !outDirMsg.Contains("houseCARL patch-mod folder"),
              "output_dir= destination: success names the user's chosen folder, NOT a houseCARL patch folder (no over-claim)");

        // ---------------------------------------------------------- D: missing-imports LEAD on a dominated failure (HCBR-2026-06-25)
        Console.WriteLine();
        Console.WriteLine("--- D: a failure dominated by unresolved-symbol errors LEADS with the import_dirs hint (not 'fix the code') ---");

        // D1: PapyrusCompile.IsUnresolvedSymbol on the REAL captured CK compiler wording (missing PO3/SkyUI/JContainers
        // sources, 2026-06-26) — the four resolution-error shapes are import-class; the syntax shapes are NOT.
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("unknown type po3_sksefunctions"), "import-class: 'unknown type …'");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("variable JValue is undefined"), "import-class: '… is undefined'");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("none is not a known user-defined type"), "import-class: '… is not a known user-defined type' (cascade)");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("HC_ImportOrderProbeExt is not a function or does not exist"), "import-class: '… is not a function or does not exist'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("no viable alternative at character '@'"), "syntax (NOT import): 'no viable alternative …'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("missing EOF at 'EndFunction'"), "syntax (NOT import): 'missing EOF …'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("Unknown user flag papyrus"), "syntax (NOT import): 'Unknown user flag …'");

        // D2: a FAILED compile whose diagnostics are the real missing-import avalanche → Render LEADS with the banner + count.
        HousecarlCore.PapyrusDiagnostic Diag(string msg) => new(@"C:\mod\Scripts\HCMissingImports.psc", 8, 1, msg);
        var missingImports = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCMissingImports", PexPath: null,
            Diagnostics: new[]
            {
                Diag("unknown type po3_sksefunctions"),
                Diag("unknown type ski_configbase"),
                Diag("variable PO3_SKSEFunctions is undefined"),
                Diag("none is not a known user-defined type"),
                Diag("variable JValue is undefined"),
                Diag("variable JMap is undefined"),
            },
            Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var miMsg = CompileTools.Render(missingImports, Plan(), userChoseOutputDir: false);
        Check(miMsg.Contains("INCOMPLETE import path"), "dominated failure LEADS with the 'INCOMPLETE import path' banner");
        Check(miMsg.Contains("6 of 6") && miMsg.IndexOf("INCOMPLETE import path", StringComparison.Ordinal) < miMsg.IndexOf("diagnostic(s)", StringComparison.Ordinal),
              "the banner names the count (6 of 6) and precedes the diagnostic list");

        // D3: a SYNTAX failure (the real captured broken-script wording) must NOT mislabel a code bug as a missing import —
        // no banner, and the generic import tail still rides along as the fallback hint.
        var syntaxFail = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCBad", PexPath: null,
            Diagnostics: new[]
            {
                Diag("no viable alternative at character '@'"),
                Diag("no viable alternative at input 'papyrus'"),
                Diag("Unknown user flag papyrus"),
                Diag("missing EOF at 'EndFunction'"),
            },
            Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var synMsg = CompileTools.Render(syntaxFail, Plan(), userChoseOutputDir: false);
        Check(!synMsg.Contains("INCOMPLETE import path"), "a syntax-only failure does NOT trigger the missing-imports banner (no false positive)");
        Check(synMsg.Contains("import path") && synMsg.Contains("import_dirs="), "a syntax-only failure keeps the generic import-path tail as the fallback hint");

        // Helper: build a failed compile from N unresolved + M syntax diagnostics, to pin the gate boundary exactly.
        HousecarlCore.CompileResult Fail(int unresolved, int syntax)
        {
            var ds = new List<HousecarlCore.PapyrusDiagnostic>();
            for (int i = 0; i < unresolved; i++) ds.Add(Diag($"unknown type frameworktype{i}"));
            for (int i = 0; i < syntax; i++) ds.Add(Diag($"no viable alternative at input 'tok{i}'"));
            return new HousecarlCore.CompileResult(false, "HCBoundary", null, ds, "", "", 0, null);
        }
        bool Banner(HousecarlCore.CompileResult r) =>
            CompileTools.Render(r, Plan(), userChoseOutputDir: false).Contains("INCOMPLETE import path");

        // D4: COUNT floor — 2 unresolved is below the >=3 minimum regardless of ratio, so no banner (a 1-2 error typo).
        Check(!Banner(Fail(unresolved: 2, syntax: 0)), "2 unresolved (100% but < 3) → no banner (the >=3 count floor)");

        // D5: the reviewer's boundary — a near-EVEN 3-of-6 split (half could be real syntax bugs) must NOT earn the
        // confident "not a bug" banner under the >=2/3 gate (3/6 = 50% < 66%). It falls to the generic import tail instead.
        Check(!Banner(Fail(unresolved: 3, syntax: 3)), "3-of-6 (50%) → NO banner: a near-even split is below the 2/3 supermajority bar");

        // D6: exactly AT the 2/3 bar (4-of-6) DOES fire — the gate is inclusive at two-thirds.
        Check(Banner(Fail(unresolved: 4, syntax: 2)), "4-of-6 (exactly 2/3) → banner fires (the gate is inclusive at two-thirds)");
        // …and just over keeps firing (the overwhelming real signature: ~all unresolved).
        Check(Banner(Fail(unresolved: 5, syntax: 1)), "5-of-6 (>2/3) → banner fires");

        // ---------------------------------------------------------- E: the import path is REPORTED (#200)
        Console.WriteLine();
        Console.WriteLine("--- E: Render prints WHAT WAS SEARCHED — summary on success, full ordered path on failure (#200) ---");

        // E1: a success no longer drops the import list on the floor (the pre-#200 render took it and never used it).
        // The 3-of-501 shape is the real one, measured on ARR 2.0 — reporting only the survivors would read as
        // "your modlist ships 3 source folders", wrong by two orders of magnitude.
        var okMsg = CompileTools.Render(ok, Plan(autoCount: 3, callerCount: 1, scanned: 501), userChoseOutputDir: false);
        Check(okMsg.Contains("imports: 6 dir(s) searched"),
              "success renders the import summary with the TOTAL (own + 1 caller + 3 auto + vanilla = 6)");
        Check(okMsg.Contains("1 from import_dirs=") && okMsg.Contains("the modlist scan matched 3 of 501 scanned mod source folder(s) referenced by this script"),
              "the summary splits caller dirs from the scan, and reports BOTH scan numbers (kept AND scanned)");
        Check(okMsg.Contains("modA") && okMsg.Contains("modC"), "the summary NAMES the providing mods");
        Check(okMsg.Contains("vanilla sources last"), "the summary states that vanilla ranks last");

        // E2: counts are DERIVED from the entries — a plan whose auto block is empty must not claim providers.
        var offMsg = CompileTools.Render(ok, Plan(autoEnabled: false), userChoseOutputDir: false);
        Check(offMsg.Contains("auto_imports=false") && !offMsg.Contains("scanned mod source folder"),
              "auto_imports=false is stated, and no scan is claimed");

        // E3: the display cap NAMES what it dropped — a truncated provider list must never read as the whole list.
        var manyMsg = CompileTools.Render(ok, Plan(autoCount: 12), userChoseOutputDir: false);
        Check(manyMsg.Contains("matched 12 of 12 scanned mod source folder(s)") && manyMsg.Contains("+4 more"),
              "over 8 providers: the full COUNT is stated and the tail is named '+4 more' (no silent truncation)");

        // E3b: hitting the reference-walk ceiling is DISCLOSED — a shortened path must never look like a complete one.
        var cappedMsg = CompileTools.Render(ok, Plan(autoCount: 2, scan: new PapyrusDependencyScan(
                Array.Empty<string>(), Indexed: 13235, FilesRead: PapyrusDependencyFilter.MaxFilesRead, BudgetExhausted: true)),
            userChoseOutputDir: false);
        Check(cappedMsg.Contains("reference walk stopped") && cappedMsg.Contains(PapyrusDependencyFilter.MaxFilesRead.ToString()),
              "an exhausted reference-walk budget is surfaced with its ceiling (Q3 — no silently shortened import path)");
        Check(!okMsg.Contains("reference walk stopped"), "…and a completed walk says nothing (no false alarm)");

        // E3c (PR #296 review): the caveat is written FOR the failing run — its own text ends "if the compile fails on
        // unresolved symbols, pass that folder via import_dirs=" — yet it lived only in ImportSummary, which Render
        // calls only when the compile SUCCEEDED. The failing run got the confident banner and no disclosure at all.
        var cappedPlan = Plan(autoCount: 2, scanned: 501, scan: new PapyrusDependencyScan(
            Array.Empty<string>(), Indexed: 13235, FilesRead: PapyrusDependencyFilter.MaxFilesRead, BudgetExhausted: true));
        var cappedFail = CompileTools.Render(missingImports, cappedPlan, userChoseOutputDir: false);
        Check(cappedFail.Contains("reference walk stopped"),
              "the truncation caveat reaches the FAILED render too — the run it was written for");
        Check(!cappedFail.Contains("REFERENCES BY NAME") && cappedFail.Contains("START WITH THE ⚠ NOTE BELOW"),
              "…and the banner stops asserting a complete narrowing, pointing at the caveat instead of at causes that exclude it");

        // E3d: the same for an UNREADABLE target. An empty result must not be rendered as a claim about the source's
        // contents when the source was never opened.
        var unreadPlan = Plan(autoCount: 0, scanned: 501, scan: new PapyrusDependencyScan(
            Array.Empty<string>(), Indexed: 13235, FilesRead: 0, BudgetExhausted: false, TargetUnreadable: true));
        var unreadOk = CompileTools.Render(ok, unreadPlan, userChoseOutputDir: false);
        Check(unreadOk.Contains("could not be READ") && !unreadOk.Contains("referenced by this script"),
              "an unreadable target is SAID, and the summary drops the 'referenced by this script' claim it never earned");
        Check(CompileTools.Render(missingImports, unreadPlan, userChoseOutputDir: false).Contains("could not be READ"),
              "…on the failed render too");

        // E4: a FAILURE prints the full ordered path with provenance — the summary line cannot answer "in what order".
        var failPlan = Plan(autoCount: 2, callerCount: 1);
        var failMsg = CompileTools.Render(syntaxFail, failPlan, userChoseOutputDir: false);
        Check(failMsg.Contains("import path searched, in order"), "a failure prints the full ordered import path");
        Check(failPlan.Entries.All(e => failMsg.Contains(e.Dir, StringComparison.Ordinal)),
              "failure detail lists EVERY dir on the path");
        Check(failMsg.Contains("[the script's own folder]") && failMsg.Contains("[MO2: modA]")
              && failMsg.Contains("[import_dirs=]") && failMsg.Contains("[vanilla sources]"),
              "every entry carries its provenance label (own / MO2:<mod> / import_dirs= / vanilla)");
        Check(failMsg.IndexOf("[the script's own folder]", StringComparison.Ordinal)
              < failMsg.IndexOf("[import_dirs=]", StringComparison.Ordinal)
              && failMsg.IndexOf("[import_dirs=]", StringComparison.Ordinal)
              < failMsg.IndexOf("[MO2: modA]", StringComparison.Ordinal)
              && failMsg.IndexOf("[MO2: modA]", StringComparison.Ordinal)
              < failMsg.IndexOf("[vanilla sources]", StringComparison.Ordinal),
              "the printed order IS the search order: own > import_dirs= > MO2 > vanilla");

        // E5: the missing-imports banner's REMEDY tracks whether the modlist was scanned. Telling someone to "list every
        // dependency" when 30 folders are already on the path is the wrong instruction and hides the causes a scan can't
        // fix (not installed / sources inside a BSA / nested a level down).
        var scanned = CompileTools.Render(missingImports, Plan(autoCount: 3, scanned: 501), userChoseOutputDir: false);
        var unscanned = CompileTools.Render(missingImports, Plan(autoEnabled: false), userChoseOutputDir: false);
        Check(scanned.Contains("inside a BSA") && scanned.Contains("found 501 mod source folder(s) and matched the 3"),
              "auto_imports ON: the banner names what the scan found AND kept, plus the causes it cannot fix (never named / not installed / BSA / subfolder)");
        Check(unscanned.Contains("auto_imports=false") && unscanned.Contains("re-run with auto_imports=true"),
              "auto_imports OFF: the banner's first remedy is to turn the scan ON");
        Check(!unscanned.Contains("inside a BSA"),
              "the two remedies are exclusive — the OFF branch does not also claim a scan happened");

        // E6: a discovery WARNING (unreadable profile) rides the output — losing the ergonomic default is never silent (Q3).
        var warnPlan = Plan(warning: "auto_imports: could not read the MO2 modlist (boom)");
        var warned = CompileTools.Render(ok, warnPlan, userChoseOutputDir: false);
        Check(warned.Contains("could not read the MO2 modlist"), "an auto-discovery warning is surfaced on a SUCCESSFUL compile too");
        Check(CompileTools.Render(syntaxFail, warnPlan, userChoseOutputDir: false).Contains("could not read the MO2 modlist"),
              "…and on a failure WITH diagnostics");

        // E6b (PR #296 review): the third render branch — a failure the diagnostic parser cannot split — appended the
        // import path but NOT the warning, so "houseCARL could not LOOK at your modlist" was discarded and the reader
        // was left to read a two-entry path as "nothing is installed".
        var unparseable = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCRaw", PexPath: null,
            Diagnostics: Array.Empty<HousecarlCore.PapyrusDiagnostic>(),
            Stdout: "something the parser cannot split", Stderr: "", ExitCode: 1, RunError: null);
        var rawMsg = CompileTools.Render(unparseable, warnPlan, userChoseOutputDir: false);
        Check(rawMsg.Contains("no per-line diagnostics were parsed"), "control: this really is the raw-output branch");
        Check(rawMsg.Contains("could not read the MO2 modlist"),
              "the discovery warning survives the no-parseable-diagnostics branch too (every branch that prints a path prints its caveats)");

        // E7 (PR #296 re-review, 3rd pass): a FAILED modlist read must not render as a scan that ran and concluded.
        // An empty root list from a read that threw is indistinguishable from a modlist with no source folders, and
        // the three statements collided: a "matched 0 of 0 … referenced by this script" conclusion, a claim that
        // houseCARL had looked under the MO2 data folder, and a tail asserting vanilla was on a single-entry path.
        var failWarning = "modlist scan: could not read the MO2 modlist to discover Papyrus source folders (boom) — " +
                          "none of your installed mods' source folders are on the import path for this compile.";
        var failedScan = new CompileTools.ImportPlan(
            new[] { (@"C:\work", CompileTools.ImportPlan.OwnFolder) },
            AutoEnabled: true, ImportSetName: null, Warning: failWarning,
            AutoScanned: 0, Scan: null, ReferencedProviders: null, VanillaMissing: true, ScanFailed: true);
        var failedText = CompileTools.Render(ok, failedScan, userChoseOutputDir: false);
        Check(!failedText.Contains("referenced by this script") && !failedText.Contains("matched 0 of 0"),
              "a failed modlist read draws NO scan conclusion — 'matched 0 of 0 … referenced by this script' is a verdict a read that threw never reached");
        Check(failedText.Contains("modlist could NOT be read"), "…it says the read failed instead");
        Check(failedText.Contains("could not read your MO2 modlist to look under the data folder")
              && !failedText.Contains("and none under your MO2 data folder"),
              "the vanilla caveat stops claiming houseCARL LOOKED under the data folder — which of the two it is changes the fix");
        Check(!failedText.Contains("vanilla sources are on the import path"),
              "…and no tail asserts a vanilla slot two lines under a caveat saying there is none");
        Check(!failedText.Contains("auto_imports:"),
              "the warning is labelled 'modlist scan', not 'auto_imports' — the rider reaches this read with auto_imports OFF too");

        // …and the same on the failure render, where the banner would otherwise blame the script.
        var failedBanner = CompileTools.Render(missingImports, failedScan, userChoseOutputDir: false);
        Check(failedBanner.Contains("The modlist could NOT be read") && !failedBanner.Contains("REFERENCES BY NAME"),
              "the missing-imports banner leads with the failed read, not with causes that presuppose a scan happened");

        // Control: a scan that RAN and genuinely matched nothing still says so — the flag must not swallow the real
        // zero, which is a legitimate conclusion.
        var realZero = CompileTools.Render(ok, Plan(autoCount: 0, scanned: 501), userChoseOutputDir: false);
        Check(realZero.Contains("matched 0 of 501") && realZero.Contains("referenced by this script"),
              "a scan that ran and matched nothing DOES report that conclusion (no false alarm)");

        // CLOSURE over the render's boolean state, REFLECTED rather than hand-listed. Three consecutive review passes
        // found the same shape — a path where the scan did not run, or did not finish, rendering as one that ran and
        // concluded — each time in a NEW flag. A hand-written array of the flags that already exist cannot catch the
        // next one, which is precisely the manual step it claims to remove (PR #296, 4th pass: the first version of
        // this block said it asserted the rule for any future flag while being a pair selected to pass).
        //
        // So: every bool on ImportPlan and PapyrusDependencyScan must be CLASSIFIED here. Adding one without a
        // decision fails the first arm — that is the part no hand-list can do. The classification then says what the
        // render owes that state, and both answers are asserted, so an exemption is a recorded judgement rather than
        // an omission that looks like one.
        //
        // SUPPRESS = the scan's answer is not authoritative, so the conclusion phrase must not be printed at all.
        // EXEMPT   = it may print, WITH the stated reason. Not every degraded state is a false claim: a truncated
        //            walk really did match the folders it names, so "referenced by this script" is a true lower
        //            bound, and only the set's completeness is in doubt — which its ⚠ states one line down.
        var classification = new Dictionary<string, (bool Suppress, string Why, CompileTools.ImportPlan? Sample)>
        {
            ["ScanFailed"] = (true,
                "the modlist read threw — there is no conclusion, and an empty root list looks exactly like a modlist with no source folders",
                failedScan),
            ["TargetUnreadable"] = (true,
                "the target's source was never opened, so nothing was resolved from its contents",
                Plan(autoCount: 0, scanned: 501, scan: new PapyrusDependencyScan(
                    Array.Empty<string>(), Indexed: 13235, FilesRead: 0, BudgetExhausted: false, TargetUnreadable: true))),
            ["BudgetExhausted"] = (false,
                "the walk DID match the folders it names — the claim is a true lower bound; only completeness is in doubt, and the ⚠ says so",
                Plan(autoCount: 2, scanned: 501, scan: new PapyrusDependencyScan(
                    Array.Empty<string>(), Indexed: 13235, FilesRead: PapyrusDependencyFilter.MaxFilesRead, BudgetExhausted: true))),
            ["VanillaMissing"] = (false,
                "a different axis — the vanilla SLOT, not the scan's answer; it carries its own ⚠ and says nothing about what the script references",
                null),
            ["AutoEnabled"] = (false,
                "not a degradation at all — it records what the caller asked for, and its own summary branch already replaces the phrase",
                null),
        };

        var stateFlags = typeof(CompileTools.ImportPlan).GetProperties()
            .Concat(typeof(PapyrusDependencyScan).GetProperties())
            .Where(pi => pi.PropertyType == typeof(bool))
            .Select(pi => pi.Name)
            .Distinct()
            .ToList();
        Check(stateFlags.Count >= 5, $"reflection found the state flags to classify (got {stateFlags.Count}: {string.Join(", ", stateFlags)})");
        foreach (var name in stateFlags)
            Check(classification.ContainsKey(name),
                  $"closure: bool state '{name}' is CLASSIFIED suppress-or-exempt — a new flag cannot reach the render undecided");

        foreach (var (name, (suppress, why, sample)) in classification)
        {
            if (sample is null) continue;                    // classified by judgement; no render shape of its own
            var okText = CompileTools.Render(ok, sample, userChoseOutputDir: false);
            var failText = CompileTools.Render(missingImports, sample, userChoseOutputDir: false);
            if (suppress)
            {
                Check(!okText.Contains("referenced by this script"),
                      $"closure [{name}] SUPPRESS: the conclusion phrase is withheld on the success render — {why}");
                Check(!failText.Contains("REFERENCES BY NAME"),
                      $"closure [{name}] SUPPRESS: …and the banner does not assert it either");
            }
            else
            {
                Check(okText.Contains("referenced by this script"),
                      $"closure [{name}] EXEMPT: the conclusion phrase still prints, deliberately — {why}");
            }
            Check(okText.Contains("⚠"), $"closure [{name}]: carries a ⚠ caveat either way");
        }

        // ---------------------------------------------------------- F: named import sets persist and coexist (#200)
        Console.WriteLine();
        Console.WriteLine("--- F: UserConfigStore import sets — round-trip, case handling, and the no-clobber contract ---");
        var fStore = Path.Combine(Path.GetTempPath(), "hc-importset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UserConfigStore(fStore);
            Check(store.GetImportSet("nope") is null, "an unknown set reads as null (never an empty set that would compile short)");
            Check(store.ImportSetNames().Count == 0, "no sets saved yet → no names");

            // The file's whole point is that four independent concerns share it. Seed the other three FIRST, so a save
            // that clobbered any of them fails here rather than in a user's config months later.
            store.Update(c => { c.Mo2InstanceDir = @"C:\MO2"; c.ToolPaths = new Dictionary<string, string> { ["papyrus_compiler"] = @"C:\CK\PapyrusCompiler.exe" }; });
            store.RecordInPlaceAcknowledged(@"C:\MO2\mods\X\Y.esp");

            var dirs = new[] { @"C:\proj\stubs", @"C:\proj\src" };
            var (savedOk, savedErr) = store.SaveImportSet("MyProject", dirs);
            Check(savedOk, $"a set saves ({savedErr})");
            var back = store.GetImportSet("MyProject");
            Check(back is not null && back.SequenceEqual(dirs), "the set round-trips in ORDER (order is compiler semantics)");
            Check(store.GetImportSet("myproject") is not null, "lookup is case-INSENSITIVE (JSON hands the dictionary back with an ordinal comparer)");
            Check(store.GetImportSet(" MyProject ") is not null, "lookup trims the name");

            var after = store.Load();
            Check(after.Mo2InstanceDir == @"C:\MO2", "saving a set does NOT clobber the MO2 instance dir");
            Check(after.ToolPaths is not null && after.ToolPaths.ContainsKey("papyrus_compiler"), "…nor the saved tool paths");
            Check(after.InPlaceAcknowledged is { Count: 1 }, "…nor the in-place acknowledgements");

            // Re-saving under a different CASE must REPLACE, not add a second set the case-insensitive read would then
            // pick between arbitrarily.
            store.SaveImportSet("myproject", new[] { @"C:\other" });
            Check(store.Load().ImportSets!.Count == 1, "re-saving with different case REPLACES (one set, not two)");
            var replaced = store.GetImportSet("MYPROJECT");
            Check(replaced is not null && replaced.Count == 1 && replaced[0] == @"C:\other", "the replacement's dirs win");

            store.SaveImportSet("alpha", new[] { @"C:\a" });
            Check(store.ImportSetNames().SequenceEqual(new[] { "alpha", "myproject" }), "names come back sorted (for the unknown-name 'saved sets:' list)");
        }
        finally { try { File.Delete(fStore); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- G: PapyrusSourceRoots — the modlist scan (#200)
        Console.WriteLine();
        Console.WriteLine("--- G: PapyrusSourceRoots.Discover — both layouts, .psc-gated, precedence-ordered, deduped ---");
        var gRoot = Path.Combine(Path.GetTempPath(), "hc-pyroots-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A fabricated MO2 loose-root tree: an SE-layout mod, an LE-layout mod, a mod with the folder but no .psc,
            // a mod with no source folder at all, and a root whose folder holds ONLY a longer-extension file.
            string Mk(string mod, string layout, params string[] files)
            {
                var d = Path.Combine(gRoot, mod, layout);
                Directory.CreateDirectory(d);
                foreach (var f in files) File.WriteAllText(Path.Combine(d, f), "Scriptname X\n");
                return d;
            }
            var seDir = Mk("SKSE", @"Source\Scripts", "Actor.psc", "Game.psc");
            var leDir = Mk("OldMod", @"Scripts\Source", "Legacy.psc");
            var emptyDir = Mk("PexOnly", @"Source\Scripts");                       // folder exists, no sources
            var longExt = Mk("LongExt", @"Source\Scripts", "NotASource.pscx");     // 8.3 pattern-match trap
            Directory.CreateDirectory(Path.Combine(gRoot, "NoSources"));

            var roots = new (string, string)[]
            {
                ("overwrite", Path.Combine(gRoot, "overwrite")),                   // does not exist at all
                ("SKSE", Path.Combine(gRoot, "SKSE")),
                ("PexOnly", Path.Combine(gRoot, "PexOnly")),
                ("OldMod", Path.Combine(gRoot, "OldMod")),
                ("LongExt", Path.Combine(gRoot, "LongExt")),
                ("NoSources", Path.Combine(gRoot, "NoSources")),
            };
            var found = PapyrusSourceRoots.Discover(roots);
            Check(found.Count == 2, $"only the two roots that actually HOLD .psc are returned (got {found.Count})");
            Check(found[0].Dir == seDir && found[0].Provider == "SKSE" && found[0].Layout == @"Source\Scripts",
                  "the SE layout (Source\\Scripts) is found and tagged with its providing mod");
            Check(found[1].Dir == leDir && found[1].Layout == @"Scripts\Source",
                  "the LE layout (Scripts\\Source) is found too");
            Check(found.All(f => f.Dir != emptyDir), "a source folder with NO .psc contributes nothing (would widen the path for free)");
            // NO TEETH HERE, and said so rather than left to look like a proof: removing HasSources' explicit
            // EndsWith re-check did NOT turn this arm red (falsified 2026-07-28), i.e. this machine's pattern matcher
            // never returned the .pscx in the first place — the 8.3 short-name quirk the re-check guards is
            // volume-dependent. Kept as a shape-check: it fails loudly if the .psc gate is ever dropped entirely.
            Check(!PapyrusSourceRoots.HasSources(longExt),
                  "a folder holding only '.pscx' is NOT a source folder (shape-check — the 8.3 quirk this guards did not reproduce here)");
            Check(found.Select(f => f.Provider).SequenceEqual(new[] { "SKSE", "OldMod" }),
                  "the GIVEN root order is preserved — that order IS MO2 precedence, and precedence decides which copy wins");

            // A root reached twice (an MO2 setup can point two roots at one folder) must occupy ONE slot: the first.
            var dupRoots = new (string, string)[] { ("SKSE", Path.Combine(gRoot, "SKSE")), ("Clone", Path.Combine(gRoot, "SKSE")) };
            var deduped = PapyrusSourceRoots.Discover(dupRoots);
            Check(deduped.Count == 1 && deduped[0].Provider == "SKSE", "the same folder twice → ONE entry, the higher-precedence root keeps it");

            // ExcludeGameData (PR #296 review): the game's own Data root is not a mod. The rider ALSO drops whatever
            // matches the COMPILER's vanilla dir — but on a Wabbajack Stock Game setup those are different folders by
            // design (the CK compiler lives in the real Steam install), so that check never fires there and the base
            // game would rank as an ordinary mod: labelled [MO2: Data], counted in the summary, and — because Data is
            // last in precedence, hence the fallback provider for every unshadowed vanilla name — dragging the
            // reference walk through the whole base game. The arm is built with the data dir DELIBERATELY unrelated to
            // any compiler path, which is the case the old check could not see.
            var stockData = Mk("StockGameData", @"Source\Scripts", "Form.psc", "Quest.psc");
            var stockRoots = new (string, string)[]
            {
                ("SKSE", Path.Combine(gRoot, "SKSE")),
                ("Data", Path.Combine(gRoot, "StockGameData")),
            };
            var withData = PapyrusSourceRoots.Discover(stockRoots);
            Check(withData.Any(r => r.Dir == stockData), "control: the scan DOES find the game's own Data sources (they hold .psc)");
            var (noData, gameData) = PapyrusSourceRoots.SplitGameData(withData, Path.Combine(gRoot, "StockGameData"));
            Check(noData.All(r => r.Dir != stockData) && noData.Count == withData.Count - 1,
                  "SplitGameData takes the game's Data sources out of the MOD candidates by PATH — no dependence on them coinciding with the compiler's vanilla dir");
            Check(noData.Any(r => r.Provider == "SKSE"), "…and takes nothing else");
            // The re-review regression: dropping it is only half right. Handed back, it can stand in as the vanilla
            // slot when the compiler-relative folder is missing; discarded, the path can end up with NO vanilla at all.
            Check(gameData == stockData, "…and HANDS IT BACK, so the caller can use it as the vanilla slot rather than losing it");
            var (blankMods, blankGame) = PapyrusSourceRoots.SplitGameData(withData, null);
            Check(blankMods.Count == withData.Count && blankGame is null,
                  "a blank data dir splits NOTHING (Path.Combine would otherwise resolve the layouts against the process CWD)");
            var (noneMods, noneGame) = PapyrusSourceRoots.SplitGameData(
                PapyrusSourceRoots.Discover(new (string, string)[] { ("SKSE", Path.Combine(gRoot, "SKSE")) }),
                Path.Combine(gRoot, "StockGameData"));
            Check(noneMods.Count == 1 && noneGame is null,
                  "a data dir whose sources the scan never found yields no game-Data folder (no phantom vanilla path)");

            // Best-effort by contract: a missing / nonsense root costs one candidate, never the scan.
            bool threw = false;
            try { PapyrusSourceRoots.Discover(new (string, string)[] { ("bad", "\0not a path"), ("gone", Path.Combine(gRoot, "nope")), ("blank", "") }); }
            catch { threw = true; }
            Check(!threw, "an unusable root is skipped, never thrown on (a lost import dir must not cost the compile)");
        }
        finally { try { Directory.Delete(gRoot, recursive: true); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- H: the reference walk (#200's load-bearing narrowing)
        Console.WriteLine();
        Console.WriteLine("--- H: PapyrusDependencyFilter.Relevant — direct, TRANSITIVE, precedence, and the unreferenced drop ---");
        var hRoot = Path.Combine(Path.GetTempPath(), "hc-pydeps-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Folder(string name, params (string File, string Body)[] scripts)
            {
                var d = Path.Combine(hRoot, name);
                Directory.CreateDirectory(d);
                foreach (var (f, b) in scripts) File.WriteAllText(Path.Combine(d, f + ".psc"), b);
                return d;
            }

            // own ──references──> Framework ──references──> Deep.  Unrelated is named by nobody.
            var own = Folder("own", ("Target", "Scriptname Target extends Quest\n\nFunction Go()\n    Framework.Ping()\nEndFunction\n"));
            var framework = Folder("framework", ("Framework", "Scriptname Framework\n\nDeep Function Get() global\n    return None\nEndFunction\n"));
            var deep = Folder("deep", ("Deep", "Scriptname Deep\n"));
            var unrelated = Folder("unrelated", ("Unrelated", "Scriptname Unrelated\n"));
            var target = Path.Combine(own, "Target.psc");
            var candidates = new[] { framework, deep, unrelated };

            var scan = PapyrusDependencyFilter.Relevant(target, new[] { own }, candidates);
            Check(scan.Folders.Contains(framework), "a folder the target names DIRECTLY is kept");
            Check(scan.Folders.Contains(deep),
                  "a folder reached only THROUGH that dependency is kept — the walk is transitive, because type-checking " +
                  "loads a dependency's own dependencies (one level would compile-fail on Deep)");
            Check(!scan.Folders.Contains(unrelated), "a folder nothing references is DROPPED");
            Check(scan.Folders.SequenceEqual(new[] { framework, deep }),
                  "kept folders come back in the CANDIDATE order (MO2 precedence), not discovery order");
            Check(!scan.Folders.Contains(own), "a SEED folder is indexed but never returned (the caller adds it unconditionally)");
            // 4 reads, not 3: the seed read, then Target again (its own `Scriptname Target` token resolves back to its
            // own folder), then Framework and Deep. The re-read is harmless — `seen` stops each NAME once — and costs
            // one file, so it is not worth special-casing; the number is asserted as what actually happens.
            Check(scan.Indexed == 4 && scan.FilesRead == 4 && !scan.BudgetExhausted,
                  $"the disclosed numbers are real: 4 indexed, 4 read (seed, Target, Framework, Deep) (got {scan.Indexed}/{scan.FilesRead})");

            // PRECEDENCE: two folders provide the same script NAME. The compiler takes the first match on the path, so
            // only that folder can ever be justified by that name — indexing both would put a shadowed copy on the path.
            var winner = Folder("winner", ("Shared", "Scriptname Shared\n"));
            var loser = Folder("loser", ("Shared", "Scriptname Shared\n"));
            var shTarget = Path.Combine(Folder("shOwn", ("ShTarget", "Scriptname ShTarget\n\nShared s\n")), "ShTarget.psc");
            var shScan = PapyrusDependencyFilter.Relevant(shTarget, new[] { Path.Combine(hRoot, "shOwn") }, new[] { winner, loser });
            Check(shScan.Folders.SequenceEqual(new[] { winner }),
                  "a name provided twice resolves to the HIGHER-precedence folder only — the loser's copy never joins the path");

            // Over-inclusion is the deliberate bias: a name mentioned in a COMMENT still earns its folder, because the
            // cost is one extra import dir and the opposite error is a compile that used to work and now doesn't.
            var cmtTarget = Path.Combine(Folder("cmtOwn", ("CmtTarget", "Scriptname CmtTarget\n; TODO: wire up Framework later\n")), "CmtTarget.psc");
            var cmtScan = PapyrusDependencyFilter.Relevant(cmtTarget, new[] { Path.Combine(hRoot, "cmtOwn") }, new[] { framework, deep, unrelated });
            Check(cmtScan.Folders.Contains(framework),
                  "a name appearing only in a comment still earns its folder (deliberate over-inclusion — a wrongly DROPPED folder is the costly error)");

            // A target that cannot be read yields nothing rather than throwing: the compile still runs and fails with
            // the compiler's own message, which is a better error than the filter's.
            bool blewUp = false;
            PapyrusDependencyScan? missing = null;
            try { missing = PapyrusDependencyFilter.Relevant(Path.Combine(hRoot, "no-such.psc"), new[] { own }, candidates); }
            catch { blewUp = true; }
            Check(!blewUp && missing is not null && missing.Folders.Count == 0,
                  "an unreadable target returns an empty set, never a throw (the compiler's own error is the better one)");
            // PR #296 review: empty-because-unreadable must be DISTINGUISHABLE from empty-because-nothing-matched, or
            // the render turns it into "0 of 501 referenced by this script" — a claim about contents never examined.
            Check(missing is { TargetUnreadable: true }, "…and it is FLAGGED unreadable, not passed off as a walk that found nothing");
            Check(!scan.TargetUnreadable, "…while a real walk is not flagged (no false alarm)");

            // A cycle (A references B references A) must terminate — scripts reference each other routinely.
            var cycA = Folder("cycA", ("CycA", "Scriptname CycA\n\nCycB b\n"));
            var cycB = Folder("cycB", ("CycB", "Scriptname CycB\n\nCycA a\n"));
            var cycTarget = Path.Combine(Folder("cycOwn", ("CycTarget", "Scriptname CycTarget\n\nCycA a\n")), "CycTarget.psc");
            var cycScan = PapyrusDependencyFilter.Relevant(cycTarget, new[] { Path.Combine(hRoot, "cycOwn") }, new[] { cycA, cycB });
            Check(cycScan.Folders.SequenceEqual(new[] { cycA, cycB }), "a reference CYCLE terminates and keeps both folders");
        }
        finally { try { Directory.Delete(hRoot, recursive: true); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>A synthetic <see cref="CompileTools.ImportPlan"/> for the render arms — a real one needs a compiler on
    /// disk and an MO2 profile, and these arms are about what Render PRINTS, not about how the path was assembled
    /// (<see cref="ImportOrderProbe"/> owns the assembly contract). Shaped exactly like a real plan: the script's own
    /// folder first, then caller dirs, then auto-discovered mods, then vanilla last.</summary>
    static CompileTools.ImportPlan Plan(bool autoEnabled = true, int autoCount = 0, int callerCount = 0,
                                        string? setName = null, string? warning = null, int scanned = -1,
                                        PapyrusDependencyScan? scan = null)
    {
        var e = new List<(string Dir, string Origin)> { (@"C:\work", CompileTools.ImportPlan.OwnFolder) };
        for (int i = 0; i < callerCount; i++) e.Add(($@"C:\caller{i}", CompileTools.ImportPlan.CallerDirs));
        var referenced = new List<string>();
        for (int i = 0; i < autoCount; i++)
        {
            var mod = "mod" + (char)('A' + i);
            e.Add(($@"C:\MO2\mods\{mod}\Source\Scripts", CompileTools.ImportPlan.AutoPrefix + mod));
            referenced.Add(mod);   // the ordinary case: every folder the walk matched also took an auto SLOT
        }
        e.Add((@"C:\Game\Data\Source\Scripts", CompileTools.ImportPlan.Vanilla));
        return new CompileTools.ImportPlan(e, autoEnabled, setName, warning, scanned < 0 ? autoCount : scanned, scan, referenced);
    }
}
