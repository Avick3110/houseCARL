using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>DialogueInfoOrder.Compute</c> on synthesised lists: an unread contributor, an untrusted move
/// baseline, a cycle an earlier plugin already placed, and the two ceilings. From the retired
/// <c>dialogue-info-order-guard</c> probe.</summary>
[Trait("tier", "unit")]
public class InfoOrderDegradeTests
{
    static readonly FormKey A = FormKey.Factory("000801:hcInfoMaster.esp");
    static readonly FormKey B = FormKey.Factory("000802:hcInfoMaster.esp");
    static readonly FormKey C = FormKey.Factory("000803:hcInfoMaster.esp");

    static InfoLine Plain(FormKey fk) => new(fk, null, false);

    static List<InfoLine> Numbered(int n, string plugin, bool chained = false) =>
        Enumerable.Range(1, n).Select(i => new InfoLine(FormKey.Factory($"{i:X6}:{plugin}"),
            chained && i < n ? FormKey.Factory($"{i + 1:X6}:{plugin}") : null, false)).ToList();

    // UNREAD-PARTIAL: one plugin read and one not is incomplete, not a single-plugin topic, and the note names the unread one.
    [Fact]
    public void OneReadAndOneUnreadPluginIsIncompleteAndNamesTheUnreadOne()
    {
        var io = DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("readable.esp", new[] { Plain(A), Plain(B) }) },
            _ => null, new[] { "locked.esp" });
        Assert.False(io.Complete);
        Assert.Equal(2, io.Order.Count);
        Assert.Single(io.UnreadContributors);
        Assert.Contains("locked.esp", io.Note ?? "");
    }

    // UNREAD-TOTAL: nothing read at all is incomplete with no lines, not an empty complete topic.
    [Fact]
    public void NothingReadIsIncompleteWithNoLines()
    {
        var io = DialogueInfoOrder.Compute(new List<(string, IReadOnlyList<InfoLine>)>(), _ => null, new[] { "locked.esp" });
        Assert.False(io.Complete);
        Assert.Empty(io.Order);
        Assert.Single(io.UnreadContributors);
    }

    // UNREAD-BASELINE: with the defining plugin unread, move analysis is SKIPPED; the control shows the move is there.
    [Fact]
    public void AnUnreadDefinerSkipsMoveAnalysis()
    {
        var groups = new List<(string, IReadOnlyList<InfoLine>)>
        {
            ("definer.esp", new[] { Plain(A), Plain(B), Plain(C) }),
            ("patch.esp",   new[] { Plain(A) }),
        };
        var control = DialogueInfoOrder.Compute(groups, _ => null);
        var shifted = DialogueInfoOrder.Compute(groups, _ => null, new[] { "definer.esp" }, originIsDefiningPlugin: false);
        Assert.Single(control.Moved);
        Assert.Empty(shifted.Moved);
        Assert.False(shifted.MovesComputed);
        Assert.Contains("SKIPPED", shifted.Note ?? "");
    }

    // CYCLE-PREPLACED: a PNAM cycle whose members an earlier plugin already placed is still reported as a cycle.
    [Fact]
    public void ACycleBetweenAlreadyPlacedLinesIsReported()
    {
        var x = FormKey.Factory("000901:hcInfoMaster.esp");
        var y = FormKey.Factory("000902:hcInfoMaster.esp");
        var io = DialogueInfoOrder.Compute(new List<(string, IReadOnlyList<InfoLine>)>
        {
            ("base.esp",  new[] { Plain(x), Plain(y) }),
            ("patch.esp", new[] { new InfoLine(x, y, false), new InfoLine(y, x, false) }),
        }, _ => null);
        Assert.Equal(2, io.Order.Count);
        Assert.Contains("PNAM cycle", io.Note ?? "");
    }

    // DEGRADE-CEILINGS (move analysis): past 400 lines the order stays whole and the note says move analysis did not run.
    [Fact]
    public void PastTheLineCeilingTheOrderIsWholeAndMoveAnalysisIsSkipped()
    {
        var big = DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", Numbered(401, "big.esp")) }, _ => null);
        Assert.Equal(401, big.Order.Count);
        Assert.False(big.MovesComputed);
        Assert.Contains("move analysis", big.Note ?? "");

        // At the ceiling itself the analysis still runs, so the boundary is pinned from both sides.
        var atCeiling = DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", Numbered(400, "big.esp")) }, _ => null);
        Assert.True(atCeiling.MovesComputed);
        Assert.Null(atCeiling.Note);
    }

    // DEGRADE-CEILINGS (hop ceiling): a 600-deep forward PNAM chain places every line and the note names the 'hop ceiling'.
    [Fact]
    public void AForwardPnamChainPastTheHopCeilingDegradesAndSaysSo()
    {
        var deep = DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("chain.esp", Numbered(600, "chain.esp", chained: true)) }, _ => null);
        Assert.Equal(600, deep.Order.Count);
        Assert.Contains("hop ceiling", deep.Note ?? "");
    }
}
