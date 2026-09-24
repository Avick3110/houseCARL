using System.Runtime.CompilerServices;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Point the auto-spill store at a directory for the scope, then put the prior value back. The store is a
/// process-global, so a test that takes one runs in <see cref="SerialCollection"/>, where nothing else runs beside
/// it; the one that needs to see EXACTLY the file its own call wrote takes one, so "the file this call spilled" is a
/// Single(), not a guess.</summary>
public sealed class ResultsDirScope : IDisposable
{
    readonly string? _prior;
    public string Dir { get; }

    public ResultsDirScope(string dir, bool create = true)
    {
        Dir = dir;
        if (create) Directory.CreateDirectory(dir);
        _prior = ResultsStore.OverrideDirForTests;
        ResultsStore.OverrideDirForTests = dir;
    }

    public void Dispose() => ResultsStore.OverrideDirForTests = _prior;
}

/// <summary>Every other test spills into one directory for the whole process, set before any test runs, so no
/// truncating call writes an artifact into the build output.</summary>
static class TestResultsDir
{
    [ModuleInitializer]
    internal static void Set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-test-results-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        ResultsStore.OverrideDirForTests = dir;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(dir, true); } catch { /* best-effort */ } };
    }
}
