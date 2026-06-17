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
        Check(LoadOrderService.ScriptOutputContract(@"C:\MO2\mods\MyPatch", mods, data).deployWarning is null,
              "a path under the MO2 mods folder is deployable (no warning)");
        Check(LoadOrderService.ScriptOutputContract(data, mods, data).deployWarning is null,
              "a path under the game's Data folder is deployable (no warning)");
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
        }
        finally { try { Directory.Delete(bRoot, recursive: true); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
