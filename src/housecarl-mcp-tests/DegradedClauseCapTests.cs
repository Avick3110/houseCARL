using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The degraded-order clause itself (#1036): capped with +N more, one line, silent on a healthy order. The
/// per-lane cases are in <see cref="DegradedOrderMarkerTests"/>.</summary>
[Trait("tier", "unit")]
public sealed class DegradedClauseCapTests
{
    [Fact]
    public void TheClauseShowsThreeNamesThenCountsTheRest()
    {
        var clause = OrderStamp.For("e1", new[] { "e.esp", "B.esp", "d.esp", "a.esp", "c.esp" }).Clause;

        Assert.Equal(" · 5 plugin(s) excluded (load failure): a.esp, B.esp, c.esp +2 more — reason in housecarl_load_order_status",
                     clause);
        Assert.DoesNotContain('\n', clause);
    }

    [Fact]
    public void TheClauseIsEmptyOnAHealthyOrder()
    {
        Assert.Equal("", OrderDegraded.Clause(Array.Empty<string>(), pointToStatus: false));
        Assert.Equal("", OrderStamp.For("e1", Array.Empty<string>()).Clause);
    }
}
