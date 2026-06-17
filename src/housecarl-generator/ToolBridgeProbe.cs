using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// External-tool bridge — step-1 proof (EXTERNAL_TOOL_BRIDGE_PLAN). Exercises the PURE core pieces the
/// housecarl_set_tool_path tool + the riders ride, with NO MO2 / NO server, so it's a cheap, deterministic green/red gate:
///
///   1. <see cref="UserConfigStore"/> CLOBBER-SAFETY (the load-bearing claim) — two independent writers (the MO2 instance
///      dir + a tool path) share one houseCARL.user.json and must NOT overwrite each other's field, in either order;
///      hardened per the 2026-06-12 hunt (F3): a corrupt file is BACKED UP + REPORTED (never silently blank — the old
///      path wiped every saved setting on the next Update), writes are atomic (temp + rename, no residue), and TWO
///      STORE INSTANCES on one file (the CLI + desktop two-process shape) serialize on the named cross-process mutex
///      instead of losing each other's read-modify-write.
///   2. <see cref="ToolBridge.Validate"/> — rejects a missing exe / wrong-named exe / missing dir; accepts a real exe + dir.
///   3. <see cref="ToolBridge.RenderMissingPrompt"/> — the forcing function names the tool key AND the resolving call, AND
///      (6.2) when auto-detect had a candidate to check, NAMES where it looked — a missed compiler game-dir anchor tells
///      the user which path failed, while keeping the tool-key + resolving-call contract intact.
///   4. <see cref="ToolBridge.TryParse"/> — wire names round-trip; junk is rejected.
///   5. <see cref="ToolBridge.Probe"/> (6.2 compiler auto-detect) — WITH a gameDirHint the compiler probe hits a synthetic
///      &lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe and misses when that exe is absent (a Stock-Game copy); WITHOUT a
///      hint it yields no candidate (null), the pre-6.2 behavior; BSArch has no canonical home (always null).
///   6. <see cref="ToolBridge.Inspect"/> — the status-surface resolve (housecarl_load_order_status' log-folder section): a
///      saved+valid path → Saved; an invalid/absent one with no canonical home → Unset; PURE (takes the saved path,
///      persists nothing — a ReadOnly status read never mutates config).
///   7. CROSS-INSTANCE SHARING (6.2/7.1 "share across instances") — tool paths live in ONE global houseCARL.user.json keyed
///      by tool, and an MO2-instance switch (SetInstance) only writes Mo2InstanceDir; so repeated instance-dir rewrites
///      (the switch shape) leave the saved compiler + bsarch paths untouched. A regression-lock on that by-construction
///      coexistence (the same store the clobber-safety arm proves), framed in the gap's own "set once, reuse" terms.
///
/// The cross-restart persistence + live-server forcing-function proof is the Aaron-empirical follow-up (needs his install).
/// Run: dotnet run --project src/housecarl-generator tool-bridge
/// </summary>
public static class ToolBridgeProbe
{
    public static int Run(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" external-tool bridge — step 1: shared-config clobber-safety + validate + prompt + auto-detect");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        int fail = 0;
        void Check(bool cond, string label)
        {
            Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + label);
            if (!cond) fail++;
        }

        // ---------------------------------------------------------------- 1) STORE clobber-safety (the core claim)
        Console.WriteLine("--- 1: UserConfigStore — two writers share one file without clobbering ---");
        var tmp = Path.Combine(Path.GetTempPath(), "houseCARL.user.test." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UserConfigStore(tmp);
            Check(store.Load().Mo2InstanceDir is null && store.Load().ToolPaths is null, "absent file loads blank");

            store.Update(c => c.Mo2InstanceDir = @"C:\MO2\Instance");
            store.Update(c => (c.ToolPaths ??= new())["bsarch"] = @"C:\Tools\bsarch.exe");
            var a = store.Load();
            Check(a.Mo2InstanceDir == @"C:\MO2\Instance", "MO2 dir survives a later tool-path write");
            Check(a.ToolPaths is { } tp && tp.TryGetValue("bsarch", out var bp) && bp == @"C:\Tools\bsarch.exe",
                  "tool path persisted alongside the MO2 dir");

            store.Update(c => c.Mo2InstanceDir = @"D:\Other");                 // reverse order
            var b = store.Load();
            Check(b.Mo2InstanceDir == @"D:\Other" && b.ToolPaths!["bsarch"] == @"C:\Tools\bsarch.exe",
                  "tool path survives a later MO2-dir write (no clobber, both directions)");

            store.Update(c => (c.ToolPaths ??= new())["papyrus_compiler"] = @"C:\CK\PapyrusCompiler.exe");
            var c2 = store.Load();
            Check(c2.ToolPaths!.Count == 2 && c2.Mo2InstanceDir == @"D:\Other", "a second tool path merges; MO2 dir intact");
            Check(!File.Exists(tmp + ".tmp"), "atomic write leaves no .tmp residue");

            // CORRUPT = LOUD (hunt F3, hunter-proven silent clobber): blank-with-note + backup, never silently blank.
            File.WriteAllText(tmp, "{ this is not valid json");
            var blank = store.Load(out var loadNote);
            Check(blank.Mo2InstanceDir is null, "corrupt file loads blank, no throw (Q3)");
            Check(loadNote is not null && loadNote.Contains(".corrupt.bak"), "corrupt load is REPORTED, naming the backup");
            Check(File.Exists(tmp + ".corrupt.bak") && File.ReadAllText(tmp + ".corrupt.bak") == "{ this is not valid json",
                  "the corrupt original is backed up byte-for-byte beside the file");
            var (upOk, upErr, upNote) = store.Update(c => c.Mo2InstanceDir = @"E:\Fresh");
            Check(upOk && upErr is null && upNote is not null, "Update over a corrupt file succeeds AND reports the recovery");
            var fresh = store.Load(out var freshNote);
            Check(fresh.Mo2InstanceDir == @"E:\Fresh" && freshNote is null, "the fresh file holds the new setting and reads clean");

            // CROSS-PROCESS shape (hunt F3): TWO store instances on ONE file — each with its own process-local gate, the
            // way the CLI plugin + desktop app share houseCARL.user.json — hammer different fields concurrently. Without
            // the named mutex the read-modify-write races and one concern's last value is clobbered (hunter-measured).
            const int rounds = 200;
            var s1 = new UserConfigStore(tmp);
            var s2 = new UserConfigStore(tmp);
            var t1 = Task.Run(() => { for (int i = 1; i <= rounds; i++) s1.Update(c => c.Mo2InstanceDir = @"C:\Race\" + i); });
            var t2 = Task.Run(() => { for (int i = 1; i <= rounds; i++) s2.Update(c => (c.ToolPaths ??= new())["bsarch"] = @"C:\Race\bsarch" + i + ".exe"); });
            Task.WaitAll(t1, t2);
            var final = s1.Load(out var raceNote);
            Check(raceNote is null, "no corruption under two-store contention (every write atomic + serialized)");
            Check(final.Mo2InstanceDir == @"C:\Race\" + rounds
                  && final.ToolPaths is { } rtp && rtp.TryGetValue("bsarch", out var rb) && rb == @"C:\Race\bsarch" + rounds + ".exe",
                  "BOTH concerns' LAST values survive two-store concurrent updates (no cross-process clobber)");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temp cleanup, non-fatal */ }
            try { File.Delete(tmp + ".corrupt.bak"); } catch { /* temp cleanup, non-fatal */ }
        }

        // ---------------------------------------------------------------- 2) VALIDATE
        Console.WriteLine();
        Console.WriteLine("--- 2: ToolBridge.Validate — loud on a wrong/missing path, accepts the real thing ---");
        var ghost = Path.Combine(Path.GetTempPath(), "ghost-" + Guid.NewGuid().ToString("N") + ".exe");
        Check(!ToolBridge.Validate(ToolDependency.Bsarch, ghost).ok, "rejects a non-existent exe");

        var goodExe = Path.Combine(Path.GetTempPath(), "bsarch.test." + Guid.NewGuid().ToString("N") + ".exe");
        var wrongExe = Path.Combine(Path.GetTempPath(), "notepad.test." + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(goodExe, "stub"); File.WriteAllText(wrongExe, "stub");
        try
        {
            Check(ToolBridge.Validate(ToolDependency.Bsarch, goodExe).ok, "accepts an existing bsarch*.exe");
            Check(!ToolBridge.Validate(ToolDependency.Bsarch, wrongExe).ok, "rejects an exe whose name isn't the expected tool");
            Check(ToolBridge.Validate(ToolDependency.PapyrusLogs, Path.GetTempPath()).ok, "accepts an existing log directory");
            Check(!ToolBridge.Validate(ToolDependency.PapyrusLogs, ghost).ok, "rejects a non-existent log directory");
        }
        finally { try { File.Delete(goodExe); File.Delete(wrongExe); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------------- 3) MISSING-DEPENDENCY PROMPT (forcing function)
        Console.WriteLine();
        Console.WriteLine("--- 3: ToolBridge.RenderMissingPrompt — scripts the ask + the resolving call (+ where it looked) ---");
        var prompt = ToolBridge.RenderMissingPrompt(ToolDependency.Bsarch);
        Check(prompt.Contains("housecarl_set_tool_path"), "prompt names the resolving call");
        Check(prompt.Contains("bsarch"), "prompt names the tool key");
        Check(!prompt.Contains("looked for it automatically"), "BSArch (no candidates) prompt names NO looked-here location");

        // 6.2 "better message": with a game-dir hint, the compiler prompt NAMES the candidate it checked (so a Stock-Game
        // miss says WHERE houseCARL looked) — WITHOUT losing the tool-key + resolving-call contract.
        var hintDir = Path.Combine(Path.GetTempPath(), "hc-no-such-game-" + Guid.NewGuid().ToString("N"));
        var expectedCandidate = Path.Combine(hintDir, "Papyrus Compiler", "PapyrusCompiler.exe");
        var compPrompt = ToolBridge.RenderMissingPrompt(ToolDependency.PapyrusCompiler, hintDir);
        Check(compPrompt.Contains(expectedCandidate), "compiler prompt (hinted) names the exact path it auto-checked");
        Check(compPrompt.Contains("housecarl_set_tool_path") && compPrompt.Contains("papyrus_compiler"),
              "compiler prompt keeps the tool-key + resolving-call contract alongside the looked-here note");
        Check(!ToolBridge.RenderMissingPrompt(ToolDependency.PapyrusCompiler).Contains("looked for it automatically"),
              "compiler prompt with NO hint names no location (nothing was auto-checked)");

        // ---------------------------------------------------------------- 4) PARSE
        Console.WriteLine();
        Console.WriteLine("--- 4: ToolBridge.TryParse — wire names round-trip; junk rejected ---");
        Check(ToolBridge.TryParse("papyrus_compiler", out var d1) && d1 == ToolDependency.PapyrusCompiler, "parses papyrus_compiler");
        Check(ToolBridge.TryParse("CRASH_LOGS", out var d2) && d2 == ToolDependency.CrashLogs, "parses case-insensitively");
        Check(!ToolBridge.TryParse("nonsense", out _), "rejects an unknown tool");

        // ---------------------------------------------------------------- 5) AUTO-DETECT (6.2 compiler game-dir anchor)
        Console.WriteLine();
        Console.WriteLine("--- 5: ToolBridge.Probe — compiler auto-detects under the game-dir hint; bsarch never does ---");
        // A synthetic game dir with the CK's canonical compiler layout: <game>\Papyrus Compiler\PapyrusCompiler.exe.
        var synthGame = Path.Combine(Path.GetTempPath(), "hc-synthgame-" + Guid.NewGuid().ToString("N"));
        var synthCompilerDir = Path.Combine(synthGame, "Papyrus Compiler");
        var synthCompiler = Path.Combine(synthCompilerDir, "PapyrusCompiler.exe");
        Directory.CreateDirectory(synthCompilerDir);
        File.WriteAllText(synthCompiler, "stub");
        try
        {
            Check(ToolBridge.Probe(ToolDependency.PapyrusCompiler, synthGame) == synthCompiler,
                  "compiler probe (with game-dir hint) hits <game>\\Papyrus Compiler\\PapyrusCompiler.exe");
            Check(ToolBridge.Probe(ToolDependency.PapyrusCompiler) is null,
                  "compiler probe with NO hint yields no candidate (pre-6.2 behavior — falls through to the prompt)");
            var emptyGame = Path.Combine(Path.GetTempPath(), "hc-emptygame-" + Guid.NewGuid().ToString("N"));
            Check(ToolBridge.Probe(ToolDependency.PapyrusCompiler, emptyGame) is null,
                  "compiler probe misses when the hinted game dir has no Papyrus Compiler\\ (a Stock-Game copy)");
            Check(ToolBridge.Probe(ToolDependency.Bsarch, synthGame) is null, "bsarch has no canonical home (always prompts, hint ignored)");
        }
        finally { try { Directory.Delete(synthGame, recursive: true); } catch { /* non-fatal */ } }
        // (papyrus_logs / crash_logs probe the user's Documents — environment-dependent, so not asserted here.)

        // ---------------------------------------------------------------- 6) INSPECT (status surface: saved → auto-detect → unset, PURE)
        Console.WriteLine();
        Console.WriteLine("--- 6: ToolBridge.Inspect — status-surface resolve (saved/auto-detected/unset), persists NOTHING ---");
        var realExe = Path.Combine(Path.GetTempPath(), "bsarch.inspect." + Guid.NewGuid().ToString("N") + ".exe");
        var realDir = Path.Combine(Path.GetTempPath(), "logs.inspect." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(realExe, "stub"); Directory.CreateDirectory(realDir);
        try
        {
            var savedExe = ToolBridge.Inspect(ToolDependency.Bsarch, realExe);
            Check(savedExe.source == ToolPathSource.Saved && savedExe.path == realExe, "a saved + valid exe path resolves as Saved");

            var savedDir = ToolBridge.Inspect(ToolDependency.PapyrusLogs, realDir);
            Check(savedDir.source == ToolPathSource.Saved && savedDir.path == realDir, "a saved + valid log dir resolves as Saved");

            // Bsarch has NO canonical probe home, so an invalid/absent saved path falls through to Unset deterministically
            // (a LOG dep would probe the user's Documents here — environment-dependent — so Bsarch is used for the assertion).
            var ghost2 = Path.Combine(Path.GetTempPath(), "ghost-" + Guid.NewGuid().ToString("N") + ".exe");
            Check(ToolBridge.Inspect(ToolDependency.Bsarch, ghost2).source == ToolPathSource.Unset,
                  "an invalid saved path + no probe home → Unset (falls through, like the runtime resolver)");
            Check(ToolBridge.Inspect(ToolDependency.Bsarch, null).source == ToolPathSource.Unset,
                  "no saved path + no probe home → Unset");
            // PURE by construction: Inspect is static, takes the saved path as an arg, and has no store to write — the
            // runtime ToolPathResolver owns persistence, so a ReadOnly status read can never mutate config.
        }
        finally { try { File.Delete(realExe); Directory.Delete(realDir); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------------- 7) CROSS-INSTANCE SHARING (6.2/7.1: set once, reuse)
        Console.WriteLine();
        Console.WriteLine("--- 7: tool paths persist across MO2-instance switches (one global file; SetInstance writes only Mo2InstanceDir) ---");
        var tmp7 = Path.Combine(Path.GetTempPath(), "houseCARL.user.share." + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UserConfigStore(tmp7);
            // The user sets the compiler + bsarch ONCE (housecarl_set_tool_path writes ToolPaths).
            store.Update(c => (c.ToolPaths ??= new())["papyrus_compiler"] = @"C:\CK\Papyrus Compiler\PapyrusCompiler.exe");
            store.Update(c => (c.ToolPaths ??= new())["bsarch"] = @"C:\Tools\bsarch.exe");
            // Now they "jump around" instances — each SetInstance writes ONLY Mo2InstanceDir (PersistInstanceDir), never
            // ToolPaths. Replay that shape: repeated instance-dir rewrites must not disturb the saved tool paths.
            for (int i = 1; i <= 5; i++) store.Update(c => c.Mo2InstanceDir = @"C:\MO2\Instance" + i);
            var after = store.Load();
            Check(after.Mo2InstanceDir == @"C:\MO2\Instance5", "the last instance switch is recorded");
            Check(after.ToolPaths is { } tps
                  && tps.TryGetValue("papyrus_compiler", out var cp) && cp == @"C:\CK\Papyrus Compiler\PapyrusCompiler.exe"
                  && tps.TryGetValue("bsarch", out var bs) && bs == @"C:\Tools\bsarch.exe",
                  "BOTH tool paths survive every instance switch unchanged (set once → shared across instances)");
        }
        finally { try { File.Delete(tmp7); } catch { /* non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
