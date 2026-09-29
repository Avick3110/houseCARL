using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>ToolBridge</c>, the pure core under <c>housecarl_set_tool_path</c> (migrated from the <c>tool-bridge</c> probe, arms
/// 2 to 6): validate a candidate path, render the missing-dependency prompt, parse a wire name, auto-detect the compiler
/// under the game-dir hints, and resolve a status-surface source without persisting anything.
/// </summary>
[Trait("tier", "unit")]
public sealed class ToolBridgeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-toolbridge-tests-" + Guid.NewGuid().ToString("N"));

    public ToolBridgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    string Stub(string name)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "stub");
        return p;
    }

    string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // Probe 2: "rejects a non-existent exe". Strengthened: the ghost carries the bsarch stem, so only the existence check can refuse it.
    [Fact]
    public void ValidateRejectsANonExistentExe()
    {
        var (ok, error) = ToolBridge.Validate(ToolDependency.Bsarch, Path.Combine(_root, "bsarch-ghost.exe"));
        Assert.False(ok);
        Assert.Contains("no such file", error);
    }

    // Probe 2: "accepts an existing bsarch*.exe".
    [Fact]
    public void ValidateAcceptsAnExistingBsarchExe()
    {
        Assert.True(ToolBridge.Validate(ToolDependency.Bsarch, Stub("bsarch.test.exe")).ok);
    }

    // Probe 2: "rejects an exe whose name isn't the expected tool".
    [Fact]
    public void ValidateRejectsAnExeWithTheWrongName()
    {
        var (ok, error) = ToolBridge.Validate(ToolDependency.Bsarch, Stub("notepad.test.exe"));
        Assert.False(ok);
        Assert.Contains("'bsarch'", error);
    }

    // Probe 2: "accepts an existing log directory".
    [Fact]
    public void ValidateAcceptsAnExistingLogDirectory()
    {
        Assert.True(ToolBridge.Validate(ToolDependency.PapyrusLogs, Dir("logs")).ok);
    }

    // Probe 2: "rejects a non-existent log directory".
    [Fact]
    public void ValidateRejectsANonExistentLogDirectory()
    {
        var (ok, error) = ToolBridge.Validate(ToolDependency.PapyrusLogs, Path.Combine(_root, "no-such-logs"));
        Assert.False(ok);
        Assert.Contains("no such folder", error);
    }

    // Probe 3: "prompt names the resolving call", "prompt names the tool key", and "BSArch (no candidates) prompt names NO
    // looked-here location".
    [Fact]
    public void TheBsarchPromptNamesTheCallAndKeyAndNoLookedHereNote()
    {
        var prompt = ToolBridge.RenderMissingPrompt(ToolDependency.Bsarch);
        Assert.Contains("housecarl_set_tool_path", prompt);
        Assert.Contains("tool='bsarch'", prompt);
        Assert.DoesNotContain("looked for it automatically", prompt);
    }

    // Probe 3: "compiler prompt names EVERY game-dir candidate it auto-checked (load-order dir + Steam install)" and "compiler
    // prompt keeps the tool-key + resolving-call contract alongside the looked-here note". Strengthened: the note itself is
    // asserted, so the no-hints arm below cannot pass on a reworded note.
    [Fact]
    public void TheHintedCompilerPromptNamesEveryCandidateAndKeepsTheCall()
    {
        var hintA = Path.Combine(_root, "stockgame");
        var hintB = Path.Combine(_root, "steam");
        var prompt = ToolBridge.RenderMissingPrompt(ToolDependency.PapyrusCompiler, new[] { hintA, hintB });

        Assert.Contains("looked for it automatically", prompt);
        Assert.Contains(Path.Combine(hintA, "Papyrus Compiler", "PapyrusCompiler.exe"), prompt);
        Assert.Contains(Path.Combine(hintB, "Papyrus Compiler", "PapyrusCompiler.exe"), prompt);
        Assert.Contains("housecarl_set_tool_path", prompt);
        Assert.Contains("tool='papyrus_compiler'", prompt);
    }

    // Probe 3: "compiler prompt with NO hints names no location (nothing was auto-checked)".
    [Fact]
    public void TheUnhintedCompilerPromptNamesNoLocation()
    {
        Assert.DoesNotContain("looked for it automatically", ToolBridge.RenderMissingPrompt(ToolDependency.PapyrusCompiler));
    }

    // Probe 4: "parses papyrus_compiler".
    [Fact]
    public void TryParseReadsPapyrusCompiler()
    {
        Assert.True(ToolBridge.TryParse("papyrus_compiler", out var dep));
        Assert.Equal(ToolDependency.PapyrusCompiler, dep);
    }

    // Probe 4: "parses case-insensitively".
    [Fact]
    public void TryParseIsCaseInsensitive()
    {
        Assert.True(ToolBridge.TryParse("CRASH_LOGS", out var dep));
        Assert.Equal(ToolDependency.CrashLogs, dep);
    }

    // Probe 4: "rejects an unknown tool".
    [Fact]
    public void TryParseRejectsAnUnknownTool()
    {
        Assert.False(ToolBridge.TryParse("nonsense", out _));
    }

    (string stockGame, string steamGame, string steamCompiler) Games()
    {
        var stockGame = Dir("stock");
        var steamGame = Dir("steam");
        Directory.CreateDirectory(Path.Combine(steamGame, "Papyrus Compiler"));
        var steamCompiler = Path.Combine(steamGame, "Papyrus Compiler", "PapyrusCompiler.exe");
        File.WriteAllText(steamCompiler, "stub");
        return (stockGame, steamGame, steamCompiler);
    }

    // Probe 5: "compiler probe (single game-dir hint) hits <game>\Papyrus Compiler\PapyrusCompiler.exe".
    [Fact]
    public void TheCompilerProbeHitsUnderASingleHint()
    {
        var (_, steam, compiler) = Games();
        Assert.Equal(compiler, ToolBridge.Probe(ToolDependency.PapyrusCompiler, new[] { steam }));
    }

    // Probe 5: "Stock-Game case: load-order dir misses, located Steam install hits (ordered multi-dir search — the fix)".
    [Fact]
    public void TheCompilerProbeFallsThroughAMissToTheNextHint()
    {
        var (stock, steam, compiler) = Games();
        Assert.Equal(compiler, ToolBridge.Probe(ToolDependency.PapyrusCompiler, new[] { stock, steam }));
    }

    // Probe 5: "compiler probe with NO hints yields no candidate (pre-6.2 behavior — falls through to the prompt)".
    [Fact]
    public void TheCompilerProbeWithNoHintsIsNull()
    {
        Assert.Null(ToolBridge.Probe(ToolDependency.PapyrusCompiler));
    }

    // Probe 5: "compiler probe misses when the only hinted dir has no Papyrus Compiler\ (a Stock-Game copy, no CK)".
    [Fact]
    public void TheCompilerProbeMissesAHintWithNoCompiler()
    {
        var (stock, _, _) = Games();
        Assert.Null(ToolBridge.Probe(ToolDependency.PapyrusCompiler, new[] { stock }));
    }

    // Probe 5: "bsarch has no canonical home (always prompts, hints ignored)".
    [Fact]
    public void TheBsarchProbeIgnoresHints()
    {
        var (_, steam, _) = Games();
        Assert.Null(ToolBridge.Probe(ToolDependency.Bsarch, new[] { steam }));
    }

    // Probe 6: "a saved + valid exe path resolves as Saved".
    [Fact]
    public void ASavedValidExeResolvesAsSaved()
    {
        var exe = Stub("bsarch.inspect.exe");
        var (path, source) = ToolBridge.Inspect(ToolDependency.Bsarch, exe);
        Assert.Equal(ToolPathSource.Saved, source);
        Assert.Equal(exe, path);
    }

    // Probe 6: "a saved + valid log dir resolves as Saved".
    [Fact]
    public void ASavedValidLogDirResolvesAsSaved()
    {
        var dir = Dir("logs.inspect");
        var (path, source) = ToolBridge.Inspect(ToolDependency.PapyrusLogs, dir);
        Assert.Equal(ToolPathSource.Saved, source);
        Assert.Equal(dir, path);
    }

    // Probe 6: "an invalid saved path + no probe home → Unset (falls through, like the runtime resolver)".
    [Fact]
    public void AnInvalidSavedPathWithNoProbeHomeIsUnset()
    {
        Assert.Equal(ToolPathSource.Unset, ToolBridge.Inspect(ToolDependency.Bsarch, Path.Combine(_root, "bsarch-ghost.exe")).source);
    }

    // Probe 6: "no saved path + no probe home → Unset".
    [Fact]
    public void NoSavedPathWithNoProbeHomeIsUnset()
    {
        Assert.Equal(ToolPathSource.Unset, ToolBridge.Inspect(ToolDependency.Bsarch, null).source);
    }
}
