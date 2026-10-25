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
        Assert.Contains("issues/1256", said, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', said);
        Assert.EndsWith(".", said, StringComparison.Ordinal);
    }

    /// <summary>On Windows nothing is refused, so the start is unchanged.</summary>
    [Fact]
    public void AWindowsStartIsNotRefused() =>
        Assert.Null(SupportedPlatform.Refusal(isWindows: true));
}
