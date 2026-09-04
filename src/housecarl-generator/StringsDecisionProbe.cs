using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI regression guard for the localized-strings redirect DECISION (HCBR 2026-06-24) — the pure
/// path logic the bug turned on, with ZERO game files. The end-to-end string RESOLUTION (does Mutagen find the
/// DLC strings in the game-Data BSA once redirected) is exercised by the manual strings-resolve-probe against
/// real DLC + Skyrim - Interface.bsa; THIS guard locks the two decision pieces most likely to silently regress
/// under a refactor:
///   FolderHasOwnStrings(path) — does the plugin's OWN folder carry a strings source FOR THIS PLUGIN (a loose table
///                              matching its name, or a .bsa beside it that embeds one)?
///                              true ⇒ keep the unchanged folder-adjacent default open; false ⇒ redirect to game-Data.
///   ComputeDataDir(nameToIdx, paths) — the game-Data fallback target = the resolved Skyrim.esm's directory.
///
/// Each assertion is its own RED-proof: a regression (e.g. dropping the .bsa check, or a case-sensitive Skyrim
/// lookup) flips a specific bool and fails the named case. Builds throwaway temp dirs; deletes them on exit.
///
/// Run: dotnet run --project src/housecarl-generator strings-decision-guard
/// </summary>
public static class StringsDecisionProbe
{
    [CiProbe("strings-decision-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("== strings redirect decision (FolderHasOwnStrings + ComputeDataDir), self-contained ==");
        var root = Path.Combine(Path.GetTempPath(), "hc-strings-decision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int fails = 0;
        try
        {
            // ---- FolderHasOwnStrings: the redirect decision ----
            // (a) bare folder (the .esm only — the "Cleaned Base Game Masters" shape) ⇒ NO own strings ⇒ redirect.
            var bare = MkFolder(root, "bare", esp: "Cleaned.esm");
            fails += Check("bare folder (.esm only) → no own strings (redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(bare, "Cleaned.esm")) == false);

            // (b) a loose Strings\ carrying a table for THIS plugin ⇒ has own strings ⇒ keep default.
            var loose = MkFolder(root, "loose", esp: "Mod.esp");
            Directory.CreateDirectory(Path.Combine(loose, "Strings"));
            File.WriteAllText(Path.Combine(loose, "Strings", "Mod_English.STRINGS"), "x");
            fails += Check("loose Strings\\ with this plugin's table → has own strings (no redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(loose, "Mod.esp")) == true);

            // (b2) #369: a Strings\ folder is shared by every plugin beside it, so an EMPTY one — and one holding only
            //      a NEIGHBOUR's tables — is not this plugin's source and must not suppress the redirect.
            var emptyLoose = MkFolder(root, "loose-empty", esp: "Mod.esp");
            Directory.CreateDirectory(Path.Combine(emptyLoose, "Strings"));
            fails += Check("empty Strings\\ → no own strings (redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(emptyLoose, "Mod.esp")) == false);

            var neighbour = MkFolder(root, "loose-neighbour", esp: "Mod.esp");
            Directory.CreateDirectory(Path.Combine(neighbour, "Strings"));
            File.WriteAllText(Path.Combine(neighbour, "Strings", "Mod_extras_English.STRINGS"), "x");
            fails += Check("Strings\\ holding only a NEIGHBOUR's table → no own strings (redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(neighbour, "Mod.esp")) == false);

            // (c) an UNREADABLE .bsa beside the plugin ⇒ "has own" ⇒ keep default. It may hold the tables and nothing
            //     here can rule that out, so the redirect is not taken past a source that was never ruled out. (An
            //     archive that PARSES is asked whether it embeds this plugin's tables — see LocalizedStringsSourceTests,
            //     which authors real archives both ways.)
            var bsa = MkFolder(root, "bsa", esp: "Mod.esp");
            File.WriteAllText(Path.Combine(bsa, "Mod.bsa"), "x");
            fails += Check("unreadable .bsa present → has own strings (no redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(bsa, "Mod.esp")) == true);

            // (d) BOTH ⇒ has own strings.
            var both = MkFolder(root, "both", esp: "M.esp");
            Directory.CreateDirectory(Path.Combine(both, "Strings"));
            File.WriteAllText(Path.Combine(both, "Strings", "M_English.STRINGS"), "x");
            File.WriteAllText(Path.Combine(both, "M.bsa"), "x");
            fails += Check("Strings\\ + .bsa → has own strings (no redirect)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(both, "M.esp")) == true);

            // (e) DEFENSIVE: a path whose folder doesn't exist ⇒ "has own" (true) so OpenOverlay keeps the
            //     unchanged default — we only ever REDIRECT on a clean, empty read, never on a bad one.
            fails += Check("nonexistent folder → defensive 'has own' (no redirect on a bad read)",
                LoadOrderResolver.FolderHasOwnStrings(Path.Combine(root, "does-not-exist", "X.esp")) == true);

            // ---- ComputeDataDir: the game-Data fallback target = Skyrim.esm's directory ----
            var dataDir = Path.Combine(root, "Data");
            Directory.CreateDirectory(dataDir);
            var skyrim = Path.Combine(dataDir, "Skyrim.esm");

            // (f) Skyrim.esm present ⇒ its directory.
            var withSky = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Update.esm"] = 0, ["Skyrim.esm"] = 1 };
            var withSkyPaths = new[] { Path.Combine(root, "x", "Update.esm"), skyrim };
            fails += Check("ComputeDataDir → resolved Skyrim.esm's dir",
                SamePath(LoadOrderResolver.ComputeDataDir(withSky, withSkyPaths), dataDir));

            // (g) case-insensitive lookup (the nameToIdx Build uses is OrdinalIgnoreCase) ⇒ still found.
            var lcSky = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["skyrim.esm"] = 0 };
            fails += Check("ComputeDataDir → case-insensitive Skyrim.esm key",
                SamePath(LoadOrderResolver.ComputeDataDir(lcSky, new[] { skyrim }), dataDir));

            // (h) no Skyrim.esm ⇒ null ⇒ OpenOverlay leaves the default open everywhere (no fallback target).
            var noSky = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Dragonborn.esm"] = 0 };
            fails += Check("ComputeDataDir → null when no Skyrim.esm",
                LoadOrderResolver.ComputeDataDir(noSky, new[] { Path.Combine(dataDir, "Dragonborn.esm") }) is null);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ } }

        Console.WriteLine(fails == 0
            ? "GUARD PASS — strings redirect decision + game-Data derivation locked."
            : $"GUARD FAIL — {fails} assertion(s) wrong.");
        return fails == 0 ? 0 : 1;
    }

    static string MkFolder(string root, string name, string esp)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, esp), "x");   // dummy — the decision helpers never OPEN it (path ops only)
        return dir;
    }

    static bool SamePath(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static int Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }
}
