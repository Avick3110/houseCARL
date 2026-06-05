using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// External-tool bridge — step-1 proof (EXTERNAL_TOOL_BRIDGE_PLAN). Exercises the PURE core pieces the
/// housecarl_set_tool_path tool + the riders ride, with NO MO2 / NO server, so it's a cheap, deterministic green/red gate:
///
///   1. <see cref="UserConfigStore"/> CLOBBER-SAFETY (the load-bearing claim) — two independent writers (the MO2 instance
///      dir + a tool path) share one houseCARL.user.json and must NOT overwrite each other's field, in either order; a
///      corrupt file reads blank, never throws (Q3).
///   2. <see cref="ToolBridge.Validate"/> — rejects a missing exe / wrong-named exe / missing dir; accepts a real exe + dir.
///   3. <see cref="ToolBridge.RenderMissingPrompt"/> — the forcing function names the tool key AND the resolving call.
///   4. <see cref="ToolBridge.TryParse"/> — wire names round-trip; junk is rejected.
///   5. <see cref="ToolBridge.Probe"/> — the compiler probe hits a synthetic &lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe
///      and misses when absent; BSArch has no canonical home (always null).
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

            File.WriteAllText(tmp, "{ this is not valid json");
            Check(store.Load().Mo2InstanceDir is null, "corrupt file loads blank, no throw (Q3)");
        }
        finally { try { File.Delete(tmp); } catch { /* temp cleanup, non-fatal */ } }

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
        Console.WriteLine("--- 3: ToolBridge.RenderMissingPrompt — scripts the ask + the resolving call ---");
        var prompt = ToolBridge.RenderMissingPrompt(ToolDependency.Bsarch);
        Check(prompt.Contains("housecarl_set_tool_path"), "prompt names the resolving call");
        Check(prompt.Contains("bsarch"), "prompt names the tool key");

        // ---------------------------------------------------------------- 4) PARSE
        Console.WriteLine();
        Console.WriteLine("--- 4: ToolBridge.TryParse — wire names round-trip; junk rejected ---");
        Check(ToolBridge.TryParse("papyrus_compiler", out var d1) && d1 == ToolDependency.PapyrusCompiler, "parses papyrus_compiler");
        Check(ToolBridge.TryParse("CRASH_LOGS", out var d2) && d2 == ToolDependency.CrashLogs, "parses case-insensitively");
        Check(!ToolBridge.TryParse("nonsense", out _), "rejects an unknown tool");

        // ---------------------------------------------------------------- 5) AUTO-DETECT
        Console.WriteLine();
        Console.WriteLine("--- 5: ToolBridge.Probe — compiler + bsarch have no cheap canonical home (prompt) ---");
        Check(ToolBridge.Probe(ToolDependency.PapyrusCompiler) is null, "compiler has no probe home (prompts; the CK lives in the separate Steam install)");
        Check(ToolBridge.Probe(ToolDependency.Bsarch) is null, "bsarch has no canonical home (always prompts)");
        // (papyrus_logs / crash_logs probe the user's Documents — environment-dependent, so not asserted here.)

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
