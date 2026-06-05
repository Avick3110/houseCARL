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
/// path / timeout) — distinct from <see cref="Success"/>=false, which is a compile that RAN and reported errors.
/// <see cref="Success"/> is decided by the .pex existing AND no error diagnostics — NOT the exit code (the CK
/// compiler returns 0 even on a usage error; measured 2026-06-05).</summary>
public sealed record CompileResult(
    bool Success, string ObjectName, string? PexPath, IReadOnlyList<PapyrusDiagnostic> Diagnostics,
    string Stdout, string Stderr, int ExitCode, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>
/// Drives the Creation Kit's PapyrusCompiler.exe as a subprocess to compile a .psc → .pex (the engine behind
/// housecarl_compile_script). NOT Mutagen — Mutagen cannot compile Papyrus (verified 2026-06-05), so this is a bounded
/// ProcessStartInfo + an output parser, nothing more.
///
/// Everything here is grounded in the REAL compiler's behaviour, measured 2026-06-05 against the shipped CK compiler:
///   • Invocation (Bethesda's own ScriptCompile.bat): `PapyrusCompiler &lt;object&gt; -f="flags.flg" -i="dir;dir" -o="out"`.
///   • Errors print to STDERR, one per line, as `&lt;fullpath&gt;(line,col): message`.
///   • A success prints `Batch compile … N succeeded, M failed.` to stdout and writes &lt;object&gt;.pex to -o.
///   • The EXIT CODE is unreliable (0 on a usage error), so success = the .pex exists AND no error diagnostics.
/// Pure (no DI): the build-time probe drives this exact code against the real compiler.
/// </summary>
public static class PapyrusCompile
{
    static readonly Regex DiagLine = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.*)$", RegexOptions.Compiled);

    /// <summary>Parse PapyrusCompiler stderr into diagnostics. The format is `&lt;fullpath&gt;(line,col): message`, one per
    /// line (measured against the real compiler). Lines that don't match the shape are ignored (non-diagnostic noise).</summary>
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
    /// to a .pex in <paramref name="outputDir"/>. Deletes any stale &lt;object&gt;.pex first so the success check (the .pex
    /// exists) reflects THIS run honestly (Q3). Reads both output streams asynchronously to avoid the classic pipe
    /// deadlock. A start failure or a timeout returns a RunError (the compiler couldn't run), never a thrown exception.</summary>
    public static CompileResult CompileObject(
        string compilerExe, string objectName, IReadOnlyList<string> importDirs, string outputDir,
        string flagsFile = "TESV_Papyrus_Flags.flg", int timeoutMs = 120_000)
    {
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        try { if (File.Exists(pexPath)) File.Delete(pexPath); } catch { /* best-effort: a locked stale pex just means the success check is conservative */ }

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
        psi.ArgumentList.Add($"-i={string.Join(";", importDirs)}");   // one arg; .NET quotes it, so spaces/semicolons in paths survive
        psi.ArgumentList.Add($"-o={outputDir}");

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"could not run the Papyrus compiler at '{compilerExe}': {ex.Message}");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"the Papyrus compiler did not finish within {timeoutMs / 1000}s (killed).");
        }
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();

        var diags = ParseDiagnostics(stderr);
        bool pex = File.Exists(pexPath);
        bool success = pex && diags.Count == 0;   // NOT proc.ExitCode — unreliable (measured)
        return new CompileResult(success, objectName, success ? pexPath : null, diags, stdout, stderr, proc.ExitCode, null);
    }
}
