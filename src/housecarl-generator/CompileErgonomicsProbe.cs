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
        var defaultMsg = CompileTools.Render(ok, Array.Empty<string>(), userChoseOutputDir: false);
        var outDirMsg = CompileTools.Render(ok, Array.Empty<string>(), userChoseOutputDir: true);
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
        var miMsg = CompileTools.Render(missingImports, Array.Empty<string>(), userChoseOutputDir: false);
        Check(miMsg.Contains("INCOMPLETE import_dirs"), "dominated failure LEADS with the 'INCOMPLETE import_dirs' banner");
        Check(miMsg.Contains("6 of 6") && miMsg.IndexOf("INCOMPLETE import_dirs", StringComparison.Ordinal) < miMsg.IndexOf("diagnostic(s)", StringComparison.Ordinal),
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
        var synMsg = CompileTools.Render(syntaxFail, Array.Empty<string>(), userChoseOutputDir: false);
        Check(!synMsg.Contains("INCOMPLETE import_dirs"), "a syntax-only failure does NOT trigger the missing-imports banner (no false positive)");
        Check(synMsg.Contains("import path") && synMsg.Contains("import_dirs="), "a syntax-only failure keeps the generic import-path tail as the fallback hint");

        // D4: the gate is a STRONG majority + count — 2 resolution errors mixed with 3 syntax errors is NOT dominated.
        var mixed = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCMixed", PexPath: null,
            Diagnostics: new[]
            {
                Diag("unknown type foo"), Diag("variable bar is undefined"),
                Diag("no viable alternative at input 'x'"), Diag("missing EOF at 'y'"), Diag("mismatched input 'z'"),
            },
            Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        Check(!CompileTools.Render(mixed, Array.Empty<string>(), userChoseOutputDir: false).Contains("INCOMPLETE import_dirs"),
              "2 resolution errors among 5 is NOT a strong majority → no banner (the gate needs >=3 AND a majority)");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
