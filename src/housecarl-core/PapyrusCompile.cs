using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>One compiler diagnostic — a file/line/col + message parsed off PapyrusCompiler's stderr. Its
/// <see cref="ToString"/> is the compact "name(line,col): message" the AI fix-loop reads (file basename, since the
/// full path is long and the AI already knows which script it compiled).</summary>
public sealed record PapyrusDiagnostic(string File, int Line, int Col, string Message)
{
    public override string ToString() => $"{System.IO.Path.GetFileName(File)}({Line},{Col}): {Message}";
}

/// <summary>The outcome of one compile. <see cref="RunError"/> non-null ⇒ the compiler could NOT be run at all (bad
/// path / timeout) — distinct from <see cref="Success"/>=false, which is a compile that RAN but produced no .pex.
/// <see cref="Success"/> is decided by THIS run WRITING the .pex — NOT the exit code (the CK compiler returns 0 even on
/// a usage error), and NOT the absence of diagnostics: a .pex that compiled WITH warnings is a success and the warnings
/// ride along in <see cref="Diagnostics"/>.</summary>
public sealed record CompileResult(
    bool Success, string ObjectName, string? PexPath, IReadOnlyList<PapyrusDiagnostic> Diagnostics,
    string Stdout, string Stderr, int ExitCode, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>
/// Drives the Creation Kit's PapyrusCompiler.exe as a subprocess to compile a .psc → .pex (the engine behind
/// housecarl_compile_script). Mutagen cannot compile Papyrus, so this is a bounded ProcessStartInfo + an output parser.
///
/// The shipped CK compiler's behaviour this code models:
///   • Invocation: `PapyrusCompiler &lt;object&gt; -f="flags.flg" -i="dir;dir" -o="out"`.
///   • Errors print to STDERR, one per line, as `&lt;fullpath&gt;(line,col): message`.
///   • A success prints `Batch compile … N succeeded, M failed.` to stdout and writes &lt;object&gt;.pex to -o.
///   • The EXIT CODE is unreliable (0 on a usage error), so success = THIS run WROTE the .pex (warnings are fine).
/// </summary>
public static class PapyrusCompile
{
    static readonly Regex DiagLine = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.*)$", RegexOptions.Compiled);

    // The "symbol/type could not be resolved" message fragments — the signature of a MISSING IMPORT (a dependency's
    // Source\Scripts folder not on the import path), as opposed to a syntax error. These match the CK compiler's exact
    // wording: "unknown type X", "variable X is undefined", "none is not a known user-defined type", "X is not a
    // function or does not exist". Syntax errors are NOT this class ("no viable alternative", "missing EOF",
    // "mismatched input", "unknown user flag") — a code defect the import path can't fix.
    static readonly string[] UnresolvedSymbolFragments =
    {
        "unknown type ",
        " is undefined",
        "is not a known user-defined type",
        "is not a function or does not exist",
    };

    /// <summary>True if a compiler diagnostic message is the "symbol/type could not be resolved" class — the signature of
    /// a MISSING IMPORT (the script is fine; a dependency's Source\Scripts folder just isn't on the import path), as
    /// distinct from a syntax error. Used to LEAD a dominated failure with the import-path hint instead of letting the AI
    /// read an avalanche of resolution errors as code bugs. Matches the compiler's wording (see
    /// <see cref="UnresolvedSymbolFragments"/>); case-insensitive.</summary>
    public static bool IsUnresolvedSymbol(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var frag in UnresolvedSymbolFragments)
            if (message.Contains(frag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Parse PapyrusCompiler stderr into diagnostics. The format is `&lt;fullpath&gt;(line,col): message`, one per
    /// line. Lines that don't match the shape are ignored (non-diagnostic noise).</summary>
    public static IReadOnlyList<PapyrusDiagnostic> ParseDiagnostics(string? stderr)
    {
        var list = new List<PapyrusDiagnostic>();
        if (string.IsNullOrEmpty(stderr)) return list;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = DiagLine.Match(line);
            if (!m.Success) continue;
            list.Add(new PapyrusDiagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["msg"].Value.Trim()));
        }
        return list;
    }

    /// <summary>Compile ONE object (a script name, no extension — its .psc must be findable in <paramref name="importDirs"/>)
    /// to a .pex in <paramref name="outputDir"/>. NON-DESTRUCTIVE: a prior &lt;object&gt;.pex is LEFT in place — a failed
    /// recompile must never destroy the last good build. So instead of deleting it first, success = the .pex was WRITTEN
    /// by this run (it newly appeared, or its write-time advanced), a signal that never touches the file. Reads both
    /// output streams asynchronously to avoid the classic pipe deadlock. A start failure or a timeout returns a RunError
    /// (the compiler couldn't run), never a thrown exception.</summary>
    public static CompileResult CompileObject(
        string compilerExe, string objectName, IReadOnlyList<string> importDirs, string outputDir,
        string flagsFile = "TESV_Papyrus_Flags.flg", int timeoutMs = 120_000)
    {
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        // Note the prior .pex's write-time (null ⇒ none) so we can tell "this run wrote it" from "a stale one was already
        // here" — WITHOUT deleting it. A multi-second compile always advances the write-time past this baseline.
        DateTime? pexBeforeUtc = File.Exists(pexPath) ? File.GetLastWriteTimeUtc(pexPath) : null;

        var psi = new ProcessStartInfo
        {
            FileName = compilerExe,
            WorkingDirectory = Path.GetDirectoryName(compilerExe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(objectName);
        psi.ArgumentList.Add($"-f={flagsFile}");
        // One arg: ';' is the CK compiler's import-dir LIST separator, not a path char. .NET quotes the whole value so
        // paths-with-spaces survive; a literal ';' INSIDE a path is the one thing that does NOT (it would split that
        // path at the separator) — but import directories effectively never contain one, so it's not a real concern.
        psi.ArgumentList.Add($"-i={string.Join(";", importDirs)}");
        psi.ArgumentList.Add($"-o={outputDir}");

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"could not run the Papyrus compiler at '{compilerExe}': {ex.Message}");
        }

        const int StreamDrainMs = 5000;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"the Papyrus compiler did not finish within {timeoutMs / 1000}s (killed).");
        }
        // The PROCESS exited, but a grandchild inheriting the stdout/stderr pipe could keep it open and hang the stream
        // reads forever (WaitForExit(int) does NOT flush async readers, unlike the parameterless overload). Bound the
        // post-exit drain: on a stuck pipe kill the tree to force the handles closed and use what was captured rather
        // than blocking indefinitely. producedNow is decided on the .pex's mtime, independent of the streams.
        bool drained; try { drained = Task.WaitAll(new Task[] { outTask, errTask }, StreamDrainMs); } catch { drained = false; }
        if (!drained) { try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
        var stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";

        var diags = ParseDiagnostics(stderr);
        // Success = THIS run WROTE the .pex. Warnings are NON-FATAL: if the compiler still emitted a .pex the
        // diagnostics ride along as warnings. NOT proc.ExitCode (unreliable) and NOT "no diagnostics" (that would fail
        // a compiled-with-warnings build).
        bool producedNow = File.Exists(pexPath)
            && (pexBeforeUtc is null || File.GetLastWriteTimeUtc(pexPath) > pexBeforeUtc.Value);
        return new CompileResult(producedNow, objectName, producedNow ? pexPath : null, diags, stdout, stderr, proc.ExitCode, null);
    }
}
