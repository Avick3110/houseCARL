using System.Text.RegularExpressions;
using SetupProgram = HousecarlSetup.Program; // alias: the generator's own top-level Program shadows it otherwise

namespace HousecarlGenerator;

/// <summary>
/// Setup update-lock guard (HCBR-2026-06-15-01 item 9.1 / PR-M). Re-running houseCARL-Setup over a LIVE
/// install used to copy over the running housecarl-mcp.exe (CopyDirectory's File.Copy overwrite:true), throw
/// mid-copy, and leave a half-updated tree behind a generic "setup did not complete". The fix pre-flights the
/// lock at every destination BEFORE any copy and refuses with actionable guidance, with a mid-copy
/// sharing-violation catch as defense in depth. This drives the REAL, now-probeable
/// <see cref="HousecarlSetup.Program.TryInstall"/> over a synthetic package + a temp home (no real ~/.claude,
/// ~/.codex, or %LOCALAPPDATA% touched):
///
///   T1 (control)  a clean first install (no destination exe yet) SUCCEEDS for Claude AND Codex — the
///                 pre-flight never false-blocks a fresh machine — and the Codex umbrella skill lands at
///                 ~/.agents/skills/housecarl. Both halves of that path pair are pinned: the fixture the
///                 installer reads from is the package path READ OUT of scripts/build-plugin.ps1, so a
///                 packager that moves the umbrella and an installer that does not turn this red instead
///                 of letting the installer's Directory.Exists guard skip the copy in silence.
///   T2 (Claude)   with the installed server exe held open like a running session, the re-install refuses:
///                 ServerInUse, RefusedBeforeAnyCopy (the PRE-FLIGHT path, not the catch), a deleted sentinel
///                 file is STILL absent (no copy ran), and the held exe is byte-intact.
///   T3 (Codex)    the same pre-flight refusal at the Codex destination (both destinations covered).
///   T4 (catch)    a held sibling DLL (exe FREE, so the pre-flight passes) makes the copy hit a sharing
///                 violation, which the defense-in-depth catch turns into ServerInUse (NOT before-copy, and
///                 NOT a raw throw).
///
/// Self-contained: synthetic files in temp, no game data / no MO2 / no real host config. RED before the fix:
/// remove the pre-flight and T2/T3's RefusedBeforeAnyCopy flips false (the request reaches the mid-copy catch)
/// or the call throws; remove the catch and T4 throws (uncaught IOException) instead of a clean ServerInUse.
///
/// Run: dotnet run --project src/housecarl-generator setup-update-lock-guard
/// </summary>
internal static class SetupUpdateLockProbe
{
    [CiProbe("setup-update-lock-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" setup update-lock guard — pre-flight the locked-server overwrite (PR-M)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        // Keep the Codex destination hermetic: TryInstall honours CODEX_HOME for config.toml, so a dev box
        // that happens to set it would otherwise write into the real ~/.codex. We pass an explicit temp home,
        // and clear CODEX_HOME for this short-lived probe process so config.toml lands under the temp home too.
        Environment.SetEnvironmentVariable("CODEX_HOME", null);

        string root = Path.Combine(Path.GetTempPath(), "hc-setup-lock-" + Guid.NewGuid().ToString("N"));
        string pkg  = Path.Combine(root, "package");   // the unzipped package dir
        string src  = Path.Combine(pkg, "housecarl");  // pluginSrc (beside it lives codex/skills/housecarl)
        string home = Path.Combine(root, "home");      // stand-in for the user profile

        // The destinations TryInstall computes, asked of the installer's OWN path helpers rather than rebuilt
        // here: a guard holding a second copy of the paths would follow the installer wherever it went.
        string claudeExe = SetupProgram.ClaudeDestExe(home);
        string claudeDll = Path.Combine(Path.GetDirectoryName(claudeExe)!, "Mutagen.Bethesda.dll");
        string codexExe  = SetupProgram.CodexDestExe(home, home);
        string sentinel  = Path.Combine(SetupProgram.ClaudeSkillsDest(home), "skills", "demo-skill", "SKILL.md");
        // The Codex umbrella's install destination: the packager path and the installer path must agree,
        // or InstallForCodex's Directory.Exists guard skips the copy and says nothing.
        string umbrella  = Path.Combine(SetupProgram.CodexSkillsRoot(home), "housecarl", "SKILL.md");
        // Where the PACKAGER puts it, read from the packaging script rather than restated here - restating
        // it would pin the installer against this file's opinion, not against what actually ships.
        var (codexPkgPath, codexPathWhy) = CodexPackagePath();
        Check(codexPkgPath is not null, "packager's Codex umbrella path read from " + PackagingScript + ": " + codexPathWhy);
        string[] codexSegs = (codexPkgPath ?? "codex/skills").Split('/');

        byte[] exeV1 = { 1, 1, 1, 1 };
        byte[] exeV2 = { 2, 2, 2, 2 }; // a different "version", so a copy that ran WOULD change the on-disk bytes

        try
        {
            // ---- a synthetic package the installer can copy from ----
            WriteFile(Path.Combine(src, ".claude-plugin", "plugin.json"), "{}");
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), exeV1);
            WriteFile(Path.Combine(src, "server", "Mutagen.Bethesda.dll"), new byte[] { 9 }); // a sibling DLL (T4)
            WriteFile(Path.Combine(src, "skills", "demo-skill", "SKILL.md"), "demo");
            WriteFile(Path.Combine(new[] { pkg }.Concat(codexSegs).Append("housecarl").Append("SKILL.md").ToArray()), "umbrella");

            // ===================================================== T1: a clean first install succeeds
            Console.WriteLine("--- T1: a clean first install succeeds (pre-flight never false-blocks a fresh machine) ---");
            var clean = SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home);
            Check(clean.Outcome == SetupProgram.InstallOutcome.Installed, "clean install (Both) => Installed");
            Check(File.Exists(claudeExe), "Claude server exe landed at ~/.claude/skills/housecarl/server");
            Check(File.Exists(codexExe),  "Codex server exe landed under the (test) data dir");
            Check(File.Exists(umbrella),  $"Codex umbrella skill packed at {codexPkgPath ?? "codex/skills"}/housecarl landed at ~/.agents/skills/housecarl");

            // ===================================================== T2: locked Claude exe => pre-flight refuses, before any copy
            Console.WriteLine();
            Console.WriteLine("--- T2: re-install with the Claude server exe HELD (a running session) refuses BEFORE any copy ---");
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), exeV2); // a newer "version" waiting to land
            File.Delete(sentinel);                                              // gone unless a copy re-creates it
            SetupProgram.InstallResult lockedClaude;
            using (HoldLikeRunning(claudeExe))
                lockedClaude = SetupProgram.TryInstall(SetupProgram.Target.Claude, src, home, home);
            Check(lockedClaude.Outcome == SetupProgram.InstallOutcome.ServerInUse, "locked Claude exe => ServerInUse (refused, not thrown)");
            Check(lockedClaude.RefusedBeforeAnyCopy, "refused at PRE-FLIGHT (RefusedBeforeAnyCopy) — not the mid-copy catch");
            Check(!File.Exists(sentinel), "no copy ran: the deleted sentinel file is still absent");
            Check(File.ReadAllBytes(claudeExe).AsSpan().SequenceEqual(exeV1),
                  "the held server exe is byte-intact (still the installed bytes, not the newer source)");
            WriteFile(sentinel, "demo");                                        // restore for the later targets
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), exeV1);

            // ===================================================== T3: locked Codex exe => same pre-flight refusal
            Console.WriteLine();
            Console.WriteLine("--- T3: the same pre-flight refusal at the Codex destination (both destinations covered) ---");
            SetupProgram.InstallResult lockedCodex;
            using (HoldLikeRunning(codexExe))
                lockedCodex = SetupProgram.TryInstall(SetupProgram.Target.Codex, src, home, home);
            Check(lockedCodex.Outcome == SetupProgram.InstallOutcome.ServerInUse, "locked Codex exe => ServerInUse");
            Check(lockedCodex.RefusedBeforeAnyCopy, "Codex refusal is at pre-flight too");

            // ===================================================== T4: held sibling DLL => defense-in-depth mid-copy catch
            Console.WriteLine();
            Console.WriteLine("--- T4: a held sibling DLL (exe FREE) is caught mid-copy as ServerInUse (defense in depth) ---");
            SetupProgram.InstallResult lockedDll;
            using (HoldLikeRunning(claudeDll))
                lockedDll = SetupProgram.TryInstall(SetupProgram.Target.Claude, src, home, home);
            Check(lockedDll.Outcome == SetupProgram.InstallOutcome.ServerInUse,
                  "held DLL (exe free) => ServerInUse (the copy's sharing violation is caught, not a raw throw)");
            Check(!lockedDll.RefusedBeforeAnyCopy, "this is the MID-COPY catch, NOT the pre-flight (the two paths stay distinct)");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The script that assembles the package — the authority on where the Codex umbrella ships.</summary>
    private const string PackagingScript = "scripts/build-plugin.ps1";

    /// <summary>The Codex skills root the PACKAGER writes, relative to the package root, read out of
    /// <see cref="PackagingScript"/>'s text ($CodexRoot / $CodexSkills, each a Join-Path of the one above it).
    /// Read rather than run: the answer is two lines of the script, and running a packaging build to learn it
    /// would cost minutes. Returns null with a reason when the script cannot be read or either assignment is
    /// gone — the probe fails on that rather than certifying the path pair from its own restatement.</summary>
    internal static (string? Path, string Why) CodexPackagePath()
    {
        if (!File.Exists(PackagingScript))
            return (null, $"not readable from '{Directory.GetCurrentDirectory()}' — run ci-all from the repo root");
        string text = File.ReadAllText(PackagingScript);
        var root  = Regex.Match(text, @"^\s*\$CodexRoot\s*=\s*Join-Path\s+\$PkgRoot\s+'([^']+)'", RegexOptions.Multiline);
        var skills = Regex.Match(text, @"^\s*\$CodexSkills\s*=\s*Join-Path\s+\$CodexRoot\s+'([^']+)'", RegexOptions.Multiline);
        if (!root.Success)   return (null, "no '$CodexRoot = Join-Path $PkgRoot ...' assignment found");
        if (!skills.Success) return (null, "no '$CodexSkills = Join-Path $CodexRoot ...' assignment found");
        return ($"{root.Groups[1].Value}/{skills.Groups[1].Value}", $"{root.Groups[1].Value}/{skills.Groups[1].Value}");
    }

    /// <summary>Open a file the way a running image holds it: readable + share-read, write DENIED — so the
    /// installer's write-open / File.Copy overwrite fails with a sharing violation, exactly as for a live exe.</summary>
    private static FileStream HoldLikeRunning(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
