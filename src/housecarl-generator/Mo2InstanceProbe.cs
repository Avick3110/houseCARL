using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Launch-arc item 3 proof — <see cref="Mo2Instance"/> derives the load-order roots + the ACTIVE profile from ONE path
/// (the MO2 instance folder), by reading ModOrganizer.ini. This probe proves the three that reproduce the old hardcoding
/// (ProfileDir / ModsDir / DataDir); OverwriteDir (the fourth root, hunt F9) is covered by overwrite-resolve-guard.
/// Deterministic, solo, touches no tracked file:
///
///   E5 (the real instance) — Resolve(Aaron's real `C:\MO2\Instance`) must reproduce EXACTLY the three paths
///       that were hand-typed into appsettings.json (the ground truth this replaces), and auto-detect the selected
///       profile. This is the load-bearing assertion: the derivation == the old hardcoding, by construction.
///   E6 (cheap profile detect) — ReadSelectedProfile() returns the active profile name alone (what the freshness check
///       reads after the ini's mtime moves, to learn a profile was switched).
///   E1–E4, E7 (Qt quirks + edges, synthetic) — a temp instance exercises the @ByteArray(...) unwrap, the doubled-
///       backslash unescape, a profile name with spaces, the base_directory override, a base_directory set-but-MISSING
///       (still usable via fallback, but NAMED — hunt H1), and the loud-fail paths (missing ini, missing/unset
///       selected_profile) — each NAMED in the problem list (Q3), never a silent half-derivation or silent fallback.
/// </summary>
public static class Mo2InstanceProbe
{
    const string RealInstance = @"C:\MO2\Instance";
    // Ground truth = the three paths appsettings.json hardcoded before this work (what the derivation must reproduce).
    const string ExpectProfileName = "Default";
    const string ExpectProfileDir  = @"C:\MO2\Instance\profiles\Default";
    const string ExpectModsDir      = @"C:\MO2\Instance\mods";
    const string ExpectDataDir      = @"C:\MO2\Instance\Stock Game\Data";

    public static int RunProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" launch-arc item 3: derive load-order roots from ONE MO2 instance path (ModOrganizer.ini)");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        bool allPass = true;

        // ---------------------------------------------------------- E5: the REAL instance == the old hardcoding
        Console.WriteLine("--- E5: Resolve(real instance) reproduces the three appsettings paths + auto-detects the profile ---");
        if (Directory.Exists(RealInstance) && File.Exists(Mo2Instance.IniPath(RealInstance)))
        {
            try
            {
                var p = Mo2Instance.Resolve(RealInstance);
                Console.WriteLine($"   instance : {p.InstanceDir}");
                Console.WriteLine($"   profile  : {p.ProfileName}");
                Console.WriteLine($"   ProfileDir: {p.ProfileDir}");
                Console.WriteLine($"   ModsDir   : {p.ModsDir}");
                Console.WriteLine($"   DataDir   : {p.DataDir}  (gamePath {p.GamePath})");
                Pass(ref allPass, "E5a profile auto-detected == selected_profile", p.ProfileName == ExpectProfileName);
                Pass(ref allPass, "E5b ProfileDir == old hardcoded", SamePath(p.ProfileDir, ExpectProfileDir));
                Pass(ref allPass, "E5c ModsDir == old hardcoded", SamePath(p.ModsDir, ExpectModsDir));
                Pass(ref allPass, "E5d DataDir == old hardcoded (gamePath + \\Data)", SamePath(p.DataDir, ExpectDataDir));
            }
            catch (Exception ex) { Console.WriteLine($"   !! Resolve threw: {ex.Message}"); allPass = false; }

            // E6 — cheap profile-only read (the switch-detect signal).
            var sel = Mo2Instance.ReadSelectedProfile(RealInstance);
            Console.WriteLine($"   ReadSelectedProfile() -> {sel ?? "(null)"}");
            Pass(ref allPass, "E6 cheap profile detect == selected_profile", sel == ExpectProfileName);
        }
        else
        {
            Console.WriteLine($"   (real instance not present at '{RealInstance}' — E5/E6 skipped; synthetic edges still run)");
        }
        Console.WriteLine();

        // ---------------------------------------------------------- synthetic temp instance for the Qt quirks + edges
        var tmp = Path.Combine(Path.GetTempPath(), "hc-mo2instance-probe");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        // ---------------------------------------------------------- E1: @ByteArray unwrap + \\ unescape + spaced profile
        Console.WriteLine("--- E1: a synthetic instance — @ByteArray(...) unwrap, doubled-backslash unescape, spaces in the profile ---");
        {
            var inst = Path.Combine(tmp, "e1");
            var game = Path.Combine(inst, "Stock Game");
            const string prof = "My Test Profile";
            MakeInstance(inst, game, prof, baseDirOverride: null);
            try
            {
                var p = Mo2Instance.Resolve(inst);
                Console.WriteLine($"   profile={p.ProfileName}  ProfileDir={p.ProfileDir}  DataDir={p.DataDir}");
                Pass(ref allPass, "E1a profile (with spaces) unwrapped from @ByteArray", p.ProfileName == prof);
                Pass(ref allPass, "E1b ProfileDir derived under the instance", SamePath(p.ProfileDir, Path.Combine(inst, "profiles", prof)));
                Pass(ref allPass, "E1c gamePath \\\\-unescaped → DataDir = gamePath\\Data", SamePath(p.DataDir, Path.Combine(game, "Data")));
                Pass(ref allPass, "E1d ModsDir = instance\\mods", SamePath(p.ModsDir, Path.Combine(inst, "mods")));
            }
            catch (Exception ex) { Console.WriteLine($"   !! Resolve threw: {ex.Message}"); allPass = false; }
        }
        Console.WriteLine();

        // ---------------------------------------------------------- E2: missing/unset selected_profile → loud fail
        Console.WriteLine("--- E2: @Invalid()/absent selected_profile → not usable, named in the problem list (Q3) ---");
        {
            var inst = Path.Combine(tmp, "e2");
            var game = Path.Combine(inst, "Stock Game");
            MakeInstance(inst, game, profile: null, baseDirOverride: null);   // writes selected_profile=@Invalid()
            var (ok, paths, problems) = Mo2Instance.Validate(inst);
            Console.WriteLine($"   ok={ok}  problems=[{string.Join(" | ", problems)}]");
            Pass(ref allPass, "E2 unset profile → not ok, paths null, profile-problem surfaced",
                !ok && paths is null && problems.Any(s => s.Contains("selected_profile")));
        }
        Console.WriteLine();

        // ---------------------------------------------------------- E3: no ModOrganizer.ini → loud fail
        Console.WriteLine("--- E3: a folder with no ModOrganizer.ini → not usable, points the user at the instance folder ---");
        {
            var inst = Path.Combine(tmp, "e3");
            Directory.CreateDirectory(inst);
            var (ok, _, problems) = Mo2Instance.Validate(inst);
            Console.WriteLine($"   ok={ok}  problems=[{string.Join(" | ", problems)}]");
            Pass(ref allPass, "E3 no ini → not ok, problem names ModOrganizer.ini",
                !ok && problems.Any(s => s.Contains("ModOrganizer.ini")));
        }
        Console.WriteLine();

        // ---------------------------------------------------------- E4: base_directory override moves mods/ + profiles/
        Console.WriteLine("--- E4: base_directory override → mods/ + profiles/ read from the base, Data still from gamePath ---");
        {
            var inst = Path.Combine(tmp, "e4");
            var baseDir = Path.Combine(tmp, "e4base");
            var game = Path.Combine(inst, "Stock Game");
            const string prof = "Default";
            MakeInstance(inst, game, prof, baseDirOverride: baseDir);   // mods/ + profiles/ created under baseDir, not inst
            try
            {
                var p = Mo2Instance.Resolve(inst);
                Console.WriteLine($"   ProfileDir={p.ProfileDir}  ModsDir={p.ModsDir}");
                Pass(ref allPass, "E4a ProfileDir under base_directory", SamePath(p.ProfileDir, Path.Combine(baseDir, "profiles", prof)));
                Pass(ref allPass, "E4b ModsDir under base_directory", SamePath(p.ModsDir, Path.Combine(baseDir, "mods")));
                Pass(ref allPass, "E4c DataDir still under gamePath", SamePath(p.DataDir, Path.Combine(game, "Data")));
            }
            catch (Exception ex) { Console.WriteLine($"   !! Resolve threw: {ex.Message}"); allPass = false; }
        }
        Console.WriteLine();

        // ---------------------------------------------------------- E7: base_directory SET but its folder is MISSING (hunt H1)
        Console.WriteLine("--- E7: base_directory set but the folder doesn't exist → still usable (falls back), but the discarded base_directory is NAMED (Q3) ---");
        {
            var inst = Path.Combine(tmp, "e7");
            var game = Path.Combine(inst, "Stock Game");
            const string prof = "Default";
            MakeInstance(inst, game, prof, baseDirOverride: null);          // a VALID instance: mods/ + profiles/ under the instance dir
            var ghostBase = Path.Combine(tmp, "e7-ghost-base");            // named by the ini but never created on disk
            File.AppendAllLines(Mo2Instance.IniPath(inst),
                new[] { "[Settings]", $"base_directory=@ByteArray({ghostBase.Replace(@"\", @"\\")})" });   // MO2's doubled-backslash form
            var (ok, paths, problems) = Mo2Instance.Validate(inst);
            Console.WriteLine($"   ok={ok}  problems=[{string.Join(" | ", problems)}]");
            Pass(ref allPass, "E7a still usable — silently falls back to the instance dir", ok && paths is not null);
            Pass(ref allPass, "E7b the discarded base_directory is NAMED, not silently dropped (Q3)",
                problems.Any(s => s.Contains("base_directory")));
        }
        Console.WriteLine();

        Directory.Delete(tmp, recursive: true);
        Console.WriteLine(allPass
            ? "=== PASS: one instance path → the load-order roots + active profile, by construction (real == old hardcoding); Qt quirks + loud-fail edges proven. ==="
            : "=== read the per-E lines above (a !! marks the thing to fix). ===");
        return allPass ? 0 : 1;

        static void Pass(ref bool all, string label, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "!! FAIL")}] {label}"); if (!ok) all = false; }
    }

    /// <summary>Build a synthetic MO2 instance on disk that mimics the format MEASURED in Aaron's real file: a
    /// ModOrganizer.ini with @ByteArray(...)-wrapped, doubled-backslash gamePath + selected_profile; the mods/ and
    /// profiles/&lt;profile&gt;/loadorder.txt under <paramref name="baseDirOverride"/> if given (and the ini carries
    /// base_directory) else under the instance; and a Stock Game\Data so the existence checks pass. profile=null writes
    /// selected_profile=@Invalid() (the unset case).</summary>
    static void MakeInstance(string instanceDir, string gamePath, string? profile, string? baseDirOverride)
    {
        Directory.CreateDirectory(instanceDir);
        Directory.CreateDirectory(Path.Combine(gamePath, "Data"));
        var basePath = baseDirOverride ?? instanceDir;
        Directory.CreateDirectory(Path.Combine(basePath, "mods"));
        if (profile is not null)
        {
            var profDir = Path.Combine(basePath, "profiles", profile);
            Directory.CreateDirectory(profDir);
            File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\nSkyrim.esm\n");
        }

        // Mimic Qt/QSettings: @ByteArray wrapper + doubled backslashes (what MO2 writes).
        var lines = new List<string>
        {
            "[General]",
            "gameName=Skyrim Special Edition",
            profile is null ? "selected_profile=@Invalid()" : $"selected_profile=@ByteArray({profile})",
            $"gamePath=@ByteArray({Escape(gamePath)})",
            "game_edition=Steam",
        };
        if (baseDirOverride is not null)
        {
            lines.Add("[Settings]");
            lines.Add($"base_directory=@ByteArray({Escape(baseDirOverride)})");
        }
        File.WriteAllLines(Mo2Instance.IniPath(instanceDir), lines);

        static string Escape(string p) => p.Replace(@"\", @"\\");   // MO2 stores paths with doubled backslashes
    }

    static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
