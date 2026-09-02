using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The bridge (RUN_ORDER amendment 2026-09-02 (7), residue item (i)): one test that runs the OLD harness,
/// so `dotnet test` is the single entry point for the whole guard estate during the two-harness window.
///
/// It shells the built generator's `ci-all` and fails on a non-zero exit. The bridge shrinks on its own as
/// families convert, and disappears when the residue counter reaches zero.
///
/// Tier `bridge` because it is neither cheap nor isolated: it runs ~1.5 minutes and duplicates a step CI
/// already has. CI excludes this tier from its `dotnet test` step for exactly that reason (see ci.yml);
/// a local `dotnet test` with no filter runs it, which is the point.
/// </summary>
[Trait("tier", "bridge")]
public sealed class HarnessBridgeTests
{
    readonly ITestOutputHelper _out;
    public HarnessBridgeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheOldProbeHarnessPassesToo_CiAllRunFromDotnetTest()
    {
        var dll = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-generator", "bin",
                               HarnessPaths.Configuration, "net9.0", "housecarl-generator.dll");

        // A missing binary is RED, never a skip: a bridge that quietly passes when it cannot find the
        // thing it bridges to is the fails-toward-green shape this test exists to close.
        Assert.True(File.Exists(dll),
            $"The generator is not built at '{dll}'. Build the solution in {HarnessPaths.Configuration} " +
            "first — the bridge runs the same binary CI runs, and cannot substitute a different one.");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = HarnessPaths.RepoRoot,   // same cwd CI gives it
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("ci-all");

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        // ci-all is ~1.5 min warm. The cap is generous but finite: a hung probe must report as a failure
        // with its output, not as a test run that never ends.
        if (!p.WaitForExit(milliseconds: 20 * 60 * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail("ci-all did not finish within 20 minutes and was killed.");
        }

        var output = stdout.Result + stderr.Result;
        _out.WriteLine(Tail(output, 40));

        Assert.True(p.ExitCode == 0,
            $"ci-all exited {p.ExitCode}. Its last lines:\n{Tail(output, 40)}");
    }

    static string Tail(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
    }
}
