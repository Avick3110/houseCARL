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
///                 pre-flight never false-blocks a fresh machine.
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
        string src  = Path.Combine(pkg, "housecarl");  // pluginSrc (beside it lives codex/housecarl)
        string home = Path.Combine(root, "home");      // stand-in for the user profile

        // The destinations TryInstall computes (mirrored here for the lock-holding + assertions).
        string claudeExe = Path.Combine(home, ".claude", "skills", "housecarl", "server", "housecarl-mcp.exe");
        string claudeDll = Path.Combine(home, ".claude", "skills", "housecarl", "server", "Mutagen.Bethesda.dll");
        string codexExe  = Path.Combine(home, "houseCARL", "server", "housecarl-mcp.exe");
        string sentinel  = Path.Combine(home, ".claude", "skills", "housecarl", "skills", "demo-skill", "SKILL.md");

        byte[] exeV1 = { 1, 1, 1, 1 };
        byte[] exeV2 = { 2, 2, 2, 2 }; // a different "version", so a copy that ran WOULD change the on-disk bytes

        try
        {
            // ---- a synthetic package the installer can copy from ----
            WriteFile(Path.Combine(src, ".claude-plugin", "plugin.json"), "{}");
            WriteFile(Path.Combine(src, "server", "housecarl-mcp.exe"), exeV1);
            WriteFile(Path.Combine(src, "server", "Mutagen.Bethesda.dll"), new byte[] { 9 }); // a sibling DLL (T4)
            WriteFile(Path.Combine(src, "skills", "demo-skill", "SKILL.md"), "demo");
            WriteFile(Path.Combine(pkg, "codex", "housecarl", "SKILL.md"), "umbrella");

            // ===================================================== T1: a clean first install succeeds
            Console.WriteLine("--- T1: a clean first install succeeds (pre-flight never false-blocks a fresh machine) ---");
            var clean = SetupProgram.TryInstall(SetupProgram.Target.Both, src, home, home);
            Check(clean.Outcome == SetupProgram.InstallOutcome.Installed, "clean install (Both) => Installed");
            Check(File.Exists(claudeExe), "Claude server exe landed at ~/.claude/skills/housecarl/server");
            Check(File.Exists(codexExe),  "Codex server exe landed under the (test) data dir");

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
