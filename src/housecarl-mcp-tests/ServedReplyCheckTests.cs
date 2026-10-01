using System.Reflection;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A capped text reply is checked against its cap on the render actually served, and sizing a spill block is
/// only a hint (#986): the checks after the write, the flag that travels with the served render, and the sizing passes
/// that read no more than they must.</summary>
public sealed class ServedReplyCheckTests : IClassFixture<WideCutWorld>
{
    readonly WideCutWorld _w;
    public ServedReplyCheckTests(WideCutWorld w) => _w = w;

    string[] TopicIds => _w.Topics.Select(t => $"{t.ID:X6}:{t.ModKey.FileName}").ToArray();

    static readonly KeyValuePair<string, string>[] NoEcho = Array.Empty<KeyValuePair<string, string>>();

    static string Block(SpillInfo s) => Wire.SpillText(SpillState.Spilled(s, manifestOnly: false));

    /// <summary>The scan render's truncated flag is the served render's: false for a whole answer, and for a refusal the
    /// cut of the render at the cap the refusal was decided on, never of a grow round or a whole pass.</summary>
    [Fact]
    public void TruncatedTravelsWithTheRenderTheReplyComesFrom()
    {
        var q = _w.Svc.CrossQuery(new[] { "DIAL" }, null, null, false, null, null, 1);

        var whole = Wire.RenderCrossQuery(_w.Svc, q, null, 80_000, false, false, 1, null, out bool wholeCut);
        var refused = Wire.RenderCrossQuery(_w.Svc, q, null, 40, false, false, 1, null, out bool refusedCut);

        Assert.False(RenderFloorAssert.IsFloorRefusal(whole), whole);
        Assert.False(wholeCut, "a whole answer came back truncated");
        Assert.True(RenderFloorAssert.IsFloorRefusal(refused), refused);
        Assert.True(refusedCut, "a refusal decided on a render that cut its row came back untruncated");
    }

    /// <summary>A render that cannot stop early lays its whole answer once per call, however many grow rounds the refusal
    /// takes: a floor that prints the cap back takes two here.</summary>
    [Fact]
    public void ARenderThatCannotStopLaysItsWholeAnswerOncePerCall()
    {
        int wholes = 0, renders = 0;
        string At(int n)
        {
            renders++;
            if (n == RenderCap.Whole) wholes++;
            return new string('x', 995) + " max_chars=" + n;
        }

        var r = RenderCap.CappedOnce(100, At);

        Assert.True(RenderFloorAssert.IsFloorRefusal(r), r);
        Assert.True(renders > 2, $"{renders} renders: the refusal took no grow round, so the test proves nothing");
        Assert.Equal(1, wholes);
    }
}
