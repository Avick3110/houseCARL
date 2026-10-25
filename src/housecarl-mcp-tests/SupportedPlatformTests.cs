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
        var (realIn, realOut, realErr, realOs) = (Console.In, Console.Out, Console.Error, SupportedPlatform.IsWindows);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            SupportedPlatform.IsWindows = () => false;
            Console.SetIn(TextReader.Null);   // without the gate the stdio server reads EOF and stops, not hangs
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
            Console.SetIn(realIn);
            Console.SetOut(realOut);
            Console.SetError(realErr);
        }

        Assert.Equal(SupportedPlatform.Refusal(isWindows: false) + Environment.NewLine, stderr.ToString());
        Assert.Empty(stdout.ToString());
    }
}
