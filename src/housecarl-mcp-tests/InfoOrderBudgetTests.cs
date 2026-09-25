using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A topic whose listing is wider than max_chars comes back as a partial listing inside the cap, not dropped whole.</summary>
public sealed class InfoOrderBudgetTests
{
    /// <summary>Forty lines, line 1 re-listed by a later plugin, one touching plugin unread: a moved lead and a note to close on.</summary>
    static InfoOrderView BigOrder()
    {
        var lines = Enumerable.Range(1, 40).Select(i => new InfoLine(FormKey.Factory($"{i:X6}:big.esp"), null, false)).ToList();
        return DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", lines), ("patch.esp", new[] { lines[0] }) },
            _ => null, new[] { "locked.esp" });
    }

    [Fact]
    public void AnOversizedTopicRendersSomeLinesTheMarkerTheLeadAndTheNoteInsideTheCap()
    {
        const int cap = 1_500;
        var row = new LoadOrderService.InfoOrderRow("000001:big.esp", "DialogTopic", "HcBigTopic", "patch.esp", BigOrder(), null);
        var r = RecordsTools.RenderRecordsInfoOrder(new[] { row }, 1, 1, 0, "records  form=info_order", null, cap, null,
                                                    out bool truncated);

        Assert.True(truncated);
        Assert.True(r.Length <= cap, $"the render is {r.Length} chars, over max_chars={cap}");
        Assert.DoesNotContain("[rendered 0 of 1 rows", r);
        Assert.Contains("    #1  000002:big.esp", r);
        Assert.DoesNotContain("    #40  ", r);
        Assert.Contains("    ... [truncated at max_chars]", r);
        Assert.Contains("as far as could be read, 1 line sits at a different position", r);
        Assert.Contains("[!] INFO order — 1 plugin(s) that TOUCH this topic could not be read", r);
    }
}
