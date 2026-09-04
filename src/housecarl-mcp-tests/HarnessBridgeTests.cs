using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>One test that shells the built generator's <c>ci-all</c> and fails on a non-zero exit, so
/// <c>dotnet test</c> is the single entry point for both harnesses. Tier <c>bridge</c> because it is neither
/// cheap nor isolated — it runs about 1.5 minutes and duplicates a step CI already has, so CI excludes the
/// tier from its <c>dotnet test</c> step (see ci.yml) while a local unfiltered run includes it.</summary>
[Trait("tier", "bridge")]
public sealed class HarnessBridgeTests
{
    readonly ITestOutputHelper _out;
    public HarnessBridgeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task TheOldProbeHarnessPassesToo_CiAllRunFromDotnetTest()
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

        // The pipes can outlive ci-all itself: it spawns housecarl-mcp.exe (the stdio guards), and a
        // grandchild still holding the inherited handle keeps ReadToEndAsync pending after the parent has
        // exited. Blocking on .Result there would hang `dotnet test` silently and forever instead of
        // failing, so the reads are bounded and a timeout degrades to a missing tail, never a hang.
        string output;
        try
        {
            var both = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromMinutes(2));
            output = both[0] + both[1];
        }
        catch (TimeoutException)
        {
            output = "(ci-all exited but its output pipes did not close within 2 minutes — a child process " +
                     "is still holding them, so its last lines are unavailable here.)";
        }

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
