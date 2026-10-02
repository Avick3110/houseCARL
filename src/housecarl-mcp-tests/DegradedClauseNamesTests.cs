using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The text clause names the plugins a build lost (#1036), on <see cref="EpochWorld"/>, whose HcEpBad.esp
/// fails to load.</summary>
[Collection("epoch")]
[Trait("tier", "integration")]
public sealed class DegradedClauseNamesTests
{
    readonly EpochWorld W;
    public DegradedClauseNamesTests(EpochFixture f) => W = f.W;

    static RecordsTools.RecordsProject Eid => new() { form = "fields", fields = new[] { "EditorID" } };

    static string Named => $"1 plugin(s) excluded (load failure): {EpochWorld.BadName} — reason in housecarl_load_order_status";

    [Fact]
    public void TheRecordsTextHeadNamesTheExcludedPluginAndWhereTheReasonIs()
    {
        var fid = $"{W.Weapon.ID:X6}:{W.Weapon.ModKey.FileName}";
        var text = RecordsTools.Records(W.Svc, formids: new[] { fid }, project: Eid);

        Assert.Contains($"epoch={W.Svc.Stats().epoch} · {Named}", text);
    }

    [Fact]
    public void TheCheckErrorsHeadNamesTheExcludedPlugin()
    {
        Assert.Contains(Named, CheckTools.CheckTool(W.Svc));
    }
}

/// <summary>The clause itself: capped with +N more, one line, silent on a healthy order.</summary>
public sealed class DegradedClauseCapTests
{
    [Fact]
    public void TheClauseShowsThreeNamesThenCountsTheRest()
    {
        var clause = OrderDegraded.Clause(new[] { "e.esp", "B.esp", "d.esp", "a.esp", "c.esp" });

        Assert.Equal(" · 5 plugin(s) excluded (load failure): a.esp, B.esp, c.esp +2 more — reason in housecarl_load_order_status",
                     clause);
        Assert.DoesNotContain('\n', clause);
    }

    [Fact]
    public void TheClauseIsEmptyOnAHealthyOrder()
    {
        Assert.Equal("", OrderDegraded.Clause(Array.Empty<string>()));
        Assert.Equal("", OrderStamp.For("e1", Array.Empty<string>()).Clause);
    }
}
