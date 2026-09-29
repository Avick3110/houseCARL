using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The merged INFO order read off plugins on disk through <c>DialogueValidate.InfoOrders</c>: file order,
/// re-list to the tail, PNAM placement, cycles, deleted lines, the zero-PNAM axis, a batch of two topics, a foreign
/// PNAM target. From the retired <c>dialogue-info-order-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class InfoOrderMergeTests
{
    static InfoOrderWorld.Fixture W => InfoOrderWorld.World;

    // MERGE-FILE-ORDER: a topic only one plugin touches has that plugin's list as its order, not contested.
    [Fact]
    public void ATopicOnePluginTouchesKeepsThatPluginsOrder()
    {
        var io = W.Solo;
        Assert.NotNull(io);
        Assert.False(io.Contested);
        Assert.Equal(W.SoloLines, io.Order.Select(e => e.Info));
    }

    // NO-FALSE-MOVE: that uncontested topic reports zero moved lines.
    [Fact]
    public void AnUncontestedTopicReportsNoMovedLines()
    {
        var io = W.Solo;
        Assert.NotNull(io);
        Assert.Empty(io.Moved);
        Assert.DoesNotContain(io.Order, e => e.Moved);
    }

    // PNAM-CHAIN-NOOP: six lines re-listed in reverse, each carrying its PNAM, land back in their original order.
    [Fact]
    public void RelistedLinesCarryingTheirPnamKeepTheirOrder()
    {
        var io = W.Order;
        Assert.NotNull(io);
        var info = W.OrderLines;
        var expected = new[] { info[1], info[2], info[3], info[4], info[5], info[6], info[7], info[0] };
        Assert.Equal(expected, io.Order.Select(e => e.Info));
    }

    // REORDER-TO-TAIL: the last plugin re-lists line 1 with no PNAM, so it moves #1 -> #8, credited to that plugin.
    [Fact]
    public void ALineRelistedWithoutPnamMovesToTheTailCreditedToTheMover()
    {
        var io = W.Order;
        Assert.NotNull(io);
        Assert.True(io.Contested);
        var m = Assert.Single(io.Moved);
        Assert.Equal(W.OrderLines[0], m.Info);
        Assert.Equal(0, m.OriginIndex);
        Assert.Equal(7, m.Index);
        Assert.Equal(InfoOrderWorld.LastName, m.PlacedBy, ignoreCase: true);
    }

    // PNAM-HEAD: a PNAM naming a record no plugin defines is placed at the head as HeadUnresolvable.
    [Fact]
    public void APnamNamingNothingPlacesTheLineAtTheHeadAsUnresolvable()
    {
        var e = W.Head?.Order.FirstOrDefault(x => x.Info == W.HeadSecond);
        Assert.NotNull(e);
        Assert.Equal(0, e.Index);
        Assert.Equal(InfoPlacement.HeadUnresolvable, e.Placement);
    }

    // PNAM-CYCLE: two lines whose PNAMs name each other terminate and both appear (and the note names the cycle).
    [Fact]
    public void TwoLinesWhosePnamsNameEachOtherBothAppear()
    {
        var io = W.Cycle;
        Assert.NotNull(io);
        Assert.Equal(2, io.Order.Count);
        Assert.Equal(2, io.Order.Select(x => x.Info).Distinct().Count());
        Assert.Contains("PNAM cycle", io.Note ?? "");
    }

    // DELETED-KEPT: a deleted line keeps its slot, flagged deleted.
    [Fact]
    public void ADeletedLineKeepsItsSlot()
    {
        var io = W.Deleted;
        Assert.NotNull(io);
        Assert.Equal(3, io.Order.Count);
        var e = io.Order.FirstOrDefault(x => x.Info == W.DeletedLine);
        Assert.NotNull(e);
        Assert.True(e.Deleted);
        Assert.Equal(1, e.Index);
    }

    // PNAM-ZERO-AXIS: a present-but-zero PNAM on disk reads as the first marker and places the line at the head.
    [Fact]
    public void APresentZeroPnamOnDiskPlacesTheLineFirstAsTheMarker()
    {
        Assert.True(W.ZeroPatched > 0, "the fixture patched no PNAM subrecord");
        Assert.True(DialogueInfoOrder.PnamZeroIsDistinguishable);
        var e = W.Zero?.Order.FirstOrDefault(x => x.Info == W.ZeroMarkedLine);
        Assert.NotNull(e);
        Assert.Equal(InfoPlacement.HeadFirstMarker, e.Placement);
        Assert.Equal(0, e.Index);
    }

    // WRITER-DROPS-NULL: Mutagen's writer emits no PNAM for a null link, so the line reads back PNAM-absent: the tail.
    [Fact]
    public void ANullPnamWrittenByMutagenReadsBackAsAbsentAndGoesToTheTail()
    {
        var e = W.ZeroWriter?.Order.FirstOrDefault(x => x.Info == W.WriterNulledLine);
        Assert.NotNull(e);
        Assert.Equal(InfoPlacement.Tail, e.Placement);
        Assert.Equal(1, e.Index);
    }

    // BATCH-FAN-IN: two topics of one quest read in one batch each get their own contributors and their own lines.
    [Fact]
    public void TwoTopicsReadInOneBatchKeepTheirOwnContributorsAndLines()
    {
        var a = W.FanA; var b = W.FanB;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, a.ContributingPlugins.Count);
        Assert.Contains(InfoOrderWorld.MidName, a.ContributingPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, a.Order.Count);
        Assert.Single(b.ContributingPlugins);
        Assert.DoesNotContain(InfoOrderWorld.MidName, b.ContributingPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, b.Order.Count);
        // Identity, not only counts: the two topics are symmetric on the master side, so a swap keeps every count.
        Assert.Contains(a.Order, e => e.Info == W.FanA0);
        Assert.DoesNotContain(a.Order, e => e.Info == W.FanB0);
        Assert.Contains(b.Order, e => e.Info == W.FanB0);
        Assert.DoesNotContain(b.Order, e => e.Info == W.FanA0);
    }

    // PNAM-FOREIGN: a PNAM target in another topic is pulled in, credited to its own plugin, and the line lands after it.
    [Fact]
    public void APnamTargetInAnotherTopicIsPulledInAndCreditedToItsOwnPlugin()
    {
        var io = W.Foreign;
        Assert.NotNull(io);
        Assert.Equal(3, io.Order.Count);
        var e = io.Order.FirstOrDefault(x => x.Info == W.ForeignLine);
        var target = io.Order.FirstOrDefault(x => x.Info == W.FanA0);
        Assert.NotNull(e);
        Assert.NotNull(target);
        Assert.Equal(InfoPlacement.AfterTarget, e.Placement);
        Assert.Equal(InfoOrderWorld.MasterName, target.PlacedBy, ignoreCase: true);
        Assert.Equal(target.Index + 1, e.Index);
    }

    // PNAM-SELF: a PNAM naming its own record degrades to a placement and the note says 'OWN record'.
    [Fact]
    public void APnamNamingItsOwnRecordDegradesAndSaysSo()
    {
        var io = W.Self;
        Assert.NotNull(io);
        Assert.Equal(2, io.Order.Count);
        Assert.Contains("OWN record", io.Note ?? "");
    }
}
