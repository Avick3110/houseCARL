using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>One compiler diagnostic parsed off PapyrusCompiler's stderr; <see cref="ToString"/> renders it as "name(line,col): message" with the file's basename.</summary>
public sealed record PapyrusDiagnostic(string File, int Line, int Col, string Message)
{
    public override string ToString() => $"{System.IO.Path.GetFileName(File)}({Line},{Col}): {Message}";
}

/// <summary>The outcome of one compile; what <see cref="RunError"/> and <see cref="Success"/> each mean is in docs/architecture/papyrus.md.</summary>
public sealed record CompileResult(
    bool Success, string ObjectName, string? PexPath, IReadOnlyList<PapyrusDiagnostic> Diagnostics,
    string Stdout, string Stderr, int ExitCode, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>Drives the Creation Kit's PapyrusCompiler.exe as a subprocess to compile a .psc to a .pex; the invocation
/// and the success rule it models are in docs/architecture/papyrus.md.</summary>
public static class PapyrusCompile
{
    static readonly Regex DiagLine = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.*)$", RegexOptions.Compiled);

    // The compiler's own "symbol/type could not be resolved" wording, which a syntax error never uses.
    static readonly string[] UnresolvedSymbolFragments =
    {
        "unknown type ",
        " is undefined",
        "is not a known user-defined type",
        "is not a function or does not exist",
    };

    /// <summary>True if a diagnostic is the missing-import class rather than a syntax error, case-insensitively; pinned
    /// by <c>compile-ergonomics-guard</c>.</summary>
    public static bool IsUnresolvedSymbol(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var frag in UnresolvedSymbolFragments)
            if (message.Contains(frag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Parse PapyrusCompiler stderr into diagnostics, ignoring lines that do not match the shape.</summary>
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

    /// <summary>Compile ONE object to a .pex in <paramref name="outputDir"/>, non-destructively and without throwing;
    /// the success signal and the stream handling are in docs/architecture/papyrus.md.</summary>
    public static CompileResult CompileObject(
        string compilerExe, string objectName, IReadOnlyList<string> importDirs, string outputDir,
        string flagsFile = "TESV_Papyrus_Flags.flg", int timeoutMs = 120_000)
    {
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        // The prior .pex's write-time (null means none): the baseline "this run wrote it" is decided against.
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
        // One arg: ';' is the compiler's import-dir list separator, and .NET quotes the whole value.
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
        // The post-exit drain is bounded, because a grandchild holding the pipe would hang the stream reads.
        bool drained; try { drained = Task.WaitAll(new Task[] { outTask, errTask }, StreamDrainMs); } catch { drained = false; }
        if (!drained) { try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
        var stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";

        var diags = ParseDiagnostics(stderr);
        // Success = THIS run WROTE the .pex; warnings ride along.
        bool producedNow = File.Exists(pexPath)
            && (pexBeforeUtc is null || File.GetLastWriteTimeUtc(pexPath) > pexBeforeUtc.Value);
        return new CompileResult(producedNow, objectName, producedNow ? pexPath : null, diags, stdout, stderr, proc.ExitCode, null);
    }
}
