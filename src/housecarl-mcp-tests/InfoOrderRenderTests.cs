using System.Text;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The merged INFO order's text block, <c>Wire.AppendInfoOrderView</c>, on synthesised views: the
/// INCOMPLETE banner, the header count, the SKIPPED note, the qualified moved lead, the two head placements. From
/// the retired <c>dialogue-info-order-guard</c> probe.</summary>
[Trait("tier", "unit")]
public class InfoOrderRenderTests
{
    static string Render(InfoOrderView io, int cap = Wire.DefaultMaxChars)
    {
        var sb = new StringBuilder();
        Wire.AppendInfoOrderView(sb, io, cap);
        return sb.ToString();
    }

    static InfoLine Plain(FormKey fk) => new(fk, null, false);

    static List<InfoLine> Forty() =>
        Enumerable.Range(1, 40).Select(i => Plain(FormKey.Factory($"{i:X6}:big.esp"))).ToList();

    static InfoOrderView Compute(IReadOnlyList<string>? unread, bool definerRead, params (string, IReadOnlyList<InfoLine>)[] groups)
        => DialogueInfoOrder.Compute(groups.ToList(), _ => null, unread, originIsDefiningPlugin: definerRead);

    // UNREAD-RENDER (partial): one plugin unread renders INCOMPLETE, never 'nothing merges here'.
    [Fact]
    public void APartialReadRendersIncompleteNotNothingMerges()
    {
        var a = FormKey.Factory("000801:hcInfoMaster.esp");
        var b = FormKey.Factory("000802:hcInfoMaster.esp");
        var r = Render(Compute(new[] { "locked.esp" }, true, ("readable.esp", new[] { Plain(a), Plain(b) })));
        Assert.Contains("INCOMPLETE", r);
        Assert.DoesNotContain("nothing merges here", r);
    }

    // UNREAD-RENDER (total): nothing read renders INCOMPLETE and says it is 'NOT an empty topic'.
    [Fact]
    public void ATotalReadFailureRendersIncompleteAndNotEmpty()
    {
        var r = Render(Compute(new[] { "locked.esp" }, true));
        Assert.Contains("INCOMPLETE", r);
        Assert.Contains("NOT an empty topic", r);
    }

    // RENDER-BIG-TOPIC: a big order with nothing moved lists every line.
    [Fact]
    public void ABigOrderWithNothingMovedListsEveryLine()
    {
        var many = Forty();
        var io = Compute(null, true, ("big.esp", many), ("patch.esp", many));
        Assert.Empty(io.Moved);
        Assert.Contains("    #40  ", Render(io));
    }

    // RENDER-CLAIM-GATES (header count): the header counts the plugins that TOUCH the topic, read or not.
    [Fact]
    public void TheHeaderCountsThePluginsThatTouchNotTheOnesRead()
    {
        var r = Render(Compute(new[] { "locked.esp" }, true, ("read.esp", Forty())));
        Assert.Contains("merged across 2 plugins that touch", r);
        Assert.DoesNotContain("merged across 1 plugins", r);
    }

    // RENDER-CLAIM-GATES (skipped): every row, then the SKIPPED note, and nothing between the header and row #1.
    [Fact]
    public void ASkippedMoveAnalysisListsEveryRowAndSaysSkipped()
    {
        var io = Compute(new[] { "definer.esp" }, false, ("read.esp", Forty()));
        Assert.False(io.MovesComputed);
        var r = Render(io);
        var lines = r.Split('\n');
        int header = Array.FindIndex(lines, l => l.Contains("effective INFO order", StringComparison.Ordinal));
        Assert.True(header >= 0, "no header line");
        Assert.StartsWith("    #1  ", lines[header + 1]);
        Assert.Contains("    #40  ", r);
        Assert.Contains("move analysis was SKIPPED", r);
    }

    // RENDER-CLAIM-GATES (skipped, cut): the same order cut by its cap still closes on the SKIPPED note, inside the cap.
    [Fact]
    public void ASkippedMoveAnalysisCutByTheCapStillClosesOnTheNote()
    {
        const int cap = 1_200;
        var r = Render(Compute(new[] { "definer.esp" }, false, ("read.esp", Forty())), cap);
        Assert.True(r.Length <= cap, $"the render is {r.Length} chars, over {cap}");
        Assert.Contains("    ... [truncated at max_chars]", r);
        Assert.DoesNotContain("    #40  ", r);
        Assert.Contains("move analysis was SKIPPED", r);
    }

    // RENDER-CLAIM-GATES (banner): an unread plugin after the definer keeps moves computed but says 'NOT authoritative'.
    [Fact]
    public void AnUnreadPluginAfterTheDefinerGetsTheIncompleteBanner()
    {
        var io = Compute(new[] { "locked.esp" }, true, ("read.esp", Forty()));
        Assert.True(io.MovesComputed);
        Assert.False(io.Complete);
        Assert.Contains("The sequence below is NOT authoritative", Render(io));
    }

    // RENDER-CLAIM-GATES (moved lead): a moved line over an incomplete read says 'as far as could be read'.
    [Fact]
    public void AMovedLeadOverAnIncompleteReadIsQualified()
    {
        var lines = Forty();
        var io = Compute(new[] { "locked.esp" }, true, ("definer.esp", lines), ("patch.esp", new[] { lines[0] }));
        Assert.NotEmpty(io.Moved);
        Assert.False(io.Complete);
        Assert.Contains("as far as could be read", Render(io));
    }

    // RENDER-HEAD-SPLIT: the zero marker's row says 'deliberate, not a fault'; the unresolvable one's says 'names no reachable line'.
    [Fact]
    public void TheFirstMarkerAndAnUnresolvablePnamRenderDifferentlyPerRow()
    {
        var marker = FormKey.Factory("00AA01:split.esp");
        var broken = FormKey.Factory("00AA02:split.esp");
        var view = new InfoOrderView(
            new[]
            {
                new InfoOrderEntry(marker, 0, "a.esp", InfoPlacement.HeadFirstMarker, 0, false, false),
                new InfoOrderEntry(broken, 1, "b.esp", InfoPlacement.HeadUnresolvable, 1, false, false),
            },
            new[] { "a.esp", "b.esp" }, Array.Empty<InfoOrderEntry>(), null);

        var rows = Render(view).Split('\n');
        var markerRow = rows.FirstOrDefault(l => l.Contains(marker.ToString(), StringComparison.Ordinal)) ?? "";
        var brokenRow = rows.FirstOrDefault(l => l.Contains(broken.ToString(), StringComparison.Ordinal)) ?? "";
        Assert.Contains("deliberate, not a fault", markerRow);
        Assert.DoesNotContain("names no reachable line", markerRow);
        Assert.Contains("names no reachable line", brokenRow);
        Assert.DoesNotContain("deliberate, not a fault", brokenRow);
    }
}
