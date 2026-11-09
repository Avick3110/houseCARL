using Xunit;

namespace HousecarlMcpTests;

/// <summary>A profile file another process holds reads as the transient it is on both OSes: the Win32 sharing and lock
/// violations on Windows, and the raw EWOULDBLOCK errno .NET raises for the same refusal on Linux.</summary>
[Trait("tier", "unit")]
public sealed class HeldProfileFileTests
{
    [Theory]
    [InlineData(32, true, true)]
    [InlineData(33, true, true)]
    [InlineData(unchecked((int)0x80070020), true, true)]
    [InlineData(11, false, true)]
    [InlineData(11, true, false)]
    [InlineData(32, false, false)]
    [InlineData(2, false, false)]
    public void AHeldFileIsNamedByItsOwnOsErrorOnly(int hresult, bool isWindows, bool held) =>
        Assert.Equal(held, Mo2LoadOrder.IsHeld(hresult, isWindows));
}
