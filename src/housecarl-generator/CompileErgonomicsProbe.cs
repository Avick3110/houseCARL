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
        var okMsg = CompileTools.Render(ok, Plan(autoCount: 3, callerCount: 1), userChoseOutputDir: false);
        Check(okMsg.Contains("imports: 6 dir(s) searched"),
              "success renders the import summary with the TOTAL (own + 1 caller + 3 auto + vanilla = 6)");
        Check(okMsg.Contains("1 from import_dirs=") && okMsg.Contains("3 auto-discovered from MO2"),
              "the summary splits caller dirs from auto-discovered ones");
        Check(okMsg.Contains("modA") && okMsg.Contains("modC"), "the summary NAMES the providing mods");
        Check(okMsg.Contains("vanilla sources last"), "the summary states that vanilla ranks last");

        // E2: counts are DERIVED from the entries — a plan whose auto block is empty must not claim providers.
        var offMsg = CompileTools.Render(ok, Plan(autoEnabled: false), userChoseOutputDir: false);
        Check(offMsg.Contains("auto_imports=false") && !offMsg.Contains("auto-discovered from MO2"),
              "auto_imports=false is stated, and no auto-discovery is claimed");

        // E3: the display cap NAMES what it dropped — a truncated provider list must never read as the whole list.
        var manyMsg = CompileTools.Render(ok, Plan(autoCount: 12), userChoseOutputDir: false);
        Check(manyMsg.Contains("12 auto-discovered from MO2") && manyMsg.Contains("+4 more"),
              "over 8 providers: the full COUNT is stated and the tail is named '+4 more' (no silent truncation)");

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
        var scanned = CompileTools.Render(missingImports, Plan(autoCount: 3), userChoseOutputDir: false);
        var unscanned = CompileTools.Render(missingImports, Plan(autoEnabled: false), userChoseOutputDir: false);
        Check(scanned.Contains("inside a BSA") && scanned.Contains("3 mod source folder(s)"),
              "auto_imports ON: the banner names the scan's reach and the causes it cannot fix (BSA / not installed / subfolder)");
        Check(unscanned.Contains("auto_imports=false") && unscanned.Contains("re-run with auto_imports=true"),
              "auto_imports OFF: the banner's first remedy is to turn the scan ON");
        Check(!unscanned.Contains("inside a BSA"),
              "the two remedies are exclusive — the OFF branch does not also claim a scan happened");

        // E6: a discovery WARNING (unreadable profile) rides the output — losing the ergonomic default is never silent (Q3).
        var warned = CompileTools.Render(ok, Plan(warning: "auto_imports: could not read the MO2 modlist (boom)"), userChoseOutputDir: false);
        Check(warned.Contains("could not read the MO2 modlist"), "an auto-discovery warning is surfaced on a SUCCESSFUL compile too");

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

            // Best-effort by contract: a missing / nonsense root costs one candidate, never the scan.
            bool threw = false;
            try { PapyrusSourceRoots.Discover(new (string, string)[] { ("bad", "\0not a path"), ("gone", Path.Combine(gRoot, "nope")), ("blank", "") }); }
            catch { threw = true; }
            Check(!threw, "an unusable root is skipped, never thrown on (a lost import dir must not cost the compile)");
        }
        finally { try { Directory.Delete(gRoot, recursive: true); } catch { /* non-fatal */ } }

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
                                        string? setName = null, string? warning = null)
    {
        var e = new List<(string Dir, string Origin)> { (@"C:\work", CompileTools.ImportPlan.OwnFolder) };
        for (int i = 0; i < callerCount; i++) e.Add(($@"C:\caller{i}", CompileTools.ImportPlan.CallerDirs));
        for (int i = 0; i < autoCount; i++)
            e.Add(($@"C:\MO2\mods\mod{(char)('A' + i)}\Source\Scripts", CompileTools.ImportPlan.AutoPrefix + "mod" + (char)('A' + i)));
        e.Add((@"C:\Game\Data\Source\Scripts", CompileTools.ImportPlan.Vanilla));
        return new CompileTools.ImportPlan(e, autoEnabled, setName, warning);
    }
}
