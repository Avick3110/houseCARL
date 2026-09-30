using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="RenderCap.Hold"/> on renders shaped to reach its two edges no covered render reaches today.</summary>
[Trait("tier", "unit")]
public sealed class RenderFloorHoldTests
{
    /// <summary>A floor that grows with every cap it is measured at never settles: the call is refused saying no cap was
    /// found, never shipped over the cap it was given.</summary>
    [Fact]
    public void AFloorThatNeverSettlesIsRefusedNotShippedOverTheCap()
    {
        static string Render(int cap) => new('x', cap + 1);

        var r = RenderCap.Hold(Render(100), 100, Render);

        Assert.True(RenderFloorAssert.IsFloorRefusal(r), r);
        Assert.Contains("no max_chars up to", r);
        Assert.DoesNotContain("\n", r);
    }

    /// <summary>The cap a records or asset_status refusal names still fits a next call that prints its read timing three
    /// digits wider and its spill file's name with a -NN counter in three places.</summary>
    [Fact]
    public void TheNamedCapFitsANextCallThatPrintsItsTimingAndSpillNameWider()
    {
        const int whole = 500;
        static string Render(int cap) => new('x', whole);   // an answer as wide as its floor, whatever the cap

        var refused = RenderCap.Hold(Render(100), 100, Render, RenderCap.NextCallGrowth);
        int nextCall = whole + 3 + 3 * 3;

        Assert.True(nextCall <= RenderFloorAssert.Named(refused), $"{refused} does not hold a next call {nextCall} wide");
    }
}
