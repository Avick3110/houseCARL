using System.Diagnostics;
using System.Text;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The start-up refusal on a non-Windows OS (#1256). CI runs on Windows, so the OS fact is passed in rather than
/// read; the Windows side is also covered by every stdio test, which boots the real server.
/// </summary>
[Trait("tier", "unit")]
public sealed class SupportedPlatformTests
{
    /// <summary>Off Windows the server says one plain sentence: what is wrong, what to try, and the issue.</summary>
    [Fact]
    public void ANonWindowsStartIsRefusedInOneSentence()
    {
        var said = SupportedPlatform.Refusal(isWindows: false);

        Assert.NotNull(said);
        Assert.Contains("Windows only", said, StringComparison.Ordinal);
        Assert.Contains("run it on a Windows PC instead", said, StringComparison.Ordinal);
        Assert.Contains("issues/1256", said, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', said);
        Assert.EndsWith(".", said, StringComparison.Ordinal);
    }

    /// <summary>On Windows nothing is refused, so the start is unchanged.</summary>
    [Fact]
    public void AWindowsStartIsNotRefused() =>
        Assert.Null(SupportedPlatform.Refusal(isWindows: true));
}

/// <summary>
/// The real entry point, run in-process with the OS fact swapped, so dropping the gate from Program.cs fails here
/// on Windows CI. Serial: it swaps the process-wide console streams and the OS seam.
/// </summary>
[Trait("tier", "unit")]
[Collection(SerialCollection.Name)]
public sealed class SupportedPlatformEntryTests
{
    /// <summary>Off Windows the start writes the sentence to stderr, nothing to stdout, and exits 1 before the host builds.</summary>
    [Fact]
    public void TheEntryPointRefusesANonWindowsStart()
    {
        var entry = ToolSurface.Assembly.EntryPoint!;
        var (realOut, realErr, realOs) = (Console.Out, Console.Error, SupportedPlatform.IsWindows);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            SupportedPlatform.IsWindows = () => false;
            // Without the gate the server serves the runner's raw stdin and returns only at its EOF; the 30 s wait bounds that.
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var run = Task.Run(() => entry.Invoke(null, [Array.Empty<string>()]) switch
            {
                Task<int> t => t.GetAwaiter().GetResult(),
                int code => code,
                var other => throw new InvalidOperationException($"unexpected entry result {other}"),
            });
            Assert.True(run.Wait(TimeSpan.FromSeconds(30)), "the entry point did not return within 30 s");
            Assert.Equal(1, run.Result);
        }
        finally
        {
            SupportedPlatform.IsWindows = realOs;
            Console.SetOut(realOut);
            Console.SetError(realErr);
        }

        Assert.Equal(SupportedPlatform.Refusal(isWindows: false) + Environment.NewLine, stderr.ToString());
        Assert.Empty(stdout.ToString());
    }
}

/// <summary>
/// The built exe, started as its own process with the OS fact swapped by a .NET startup hook (<see cref="StartupHook"/>,
/// in this test assembly), so the raw stdout pipe a client reads is what is measured, not <c>Console.Out</c>.
/// </summary>
[Trait("tier", "unit")]
public sealed class SupportedPlatformProcessTests
{
    /// <summary>Off Windows nothing reaches the raw stdout, the client's JSON-RPC channel.</summary>
    [Fact]
    public void ANonWindowsStartWritesNothingToTheRawStdout()
    {
        var (exit, stdout, stderr) = BootAsNonWindows(new Dictionary<string, string?>());

        Assert.Equal(1, exit);
        Assert.Equal(SupportedPlatform.Refusal(isWindows: false) + Environment.NewLine, stderr);
        Assert.Empty(stdout);
    }

    /// <summary>Start the real exe under the hook with this environment, feed it nothing, and collect the raw bytes it wrote.</summary>
    internal static (int Exit, byte[] Stdout, string Stderr) BootAsNonWindows(IReadOnlyDictionary<string, string?> environment)
    {
        var exe = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "bin",
                               HarnessPaths.Configuration, HarnessPaths.TargetFramework, "housecarl-mcp.exe");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_STARTUP_HOOKS"] = typeof(StartupHook).Assembly.Location;
        psi.Environment.Remove(SchemaDepthCap.Variable);
        foreach (var (name, value) in environment)
            if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value;

        using var proc = Process.Start(psi)!;
        var raw = new MemoryStream();
        var stdout = proc.StandardOutput.BaseStream.CopyToAsync(raw);
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardInput.Close();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail("The server started as non-Windows did not exit within 30 s; it must refuse the start, not serve.");
        }
        stdout.GetAwaiter().GetResult();
        return (proc.ExitCode, raw.ToArray(), stderr.GetAwaiter().GetResult());
    }
}
