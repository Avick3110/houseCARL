using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A scan scoped to several plugins shows, and where= judges, the highest-loading scoped copy of each record,
/// whatever order plugins.names is in (#1098). The world's first weapon is 10 in the master, 50 in the mid plugin and
/// 99 in the override, which loads last.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScopeCopyOrderTests : RecordsTestBase
{
    public RecordsScopeCopyOrderTests(RecordsFixture f) : base(f) { }

    string Scan(string[] names, params string[] where) =>
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, plugins: Scope(names),
                             where: where.Length == 0 ? null : where, project: Fields("BasicStats.Damage"));

    // The render time is the one line two identical answers may differ on.
    static string Rows(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("rendered ", StringComparison.Ordinal)));

    [Fact]
    public void ATwoPluginScopeGivesTheSameRowsAndBodiesUnderEitherNameOrder()
    {
        var masterFirst = Scan(new[] { W.MasterName, W.OverrideName });
        var overrideFirst = Scan(new[] { W.OverrideName, W.MasterName });

        Assert.Equal(Rows(masterFirst), Rows(overrideFirst));
        Assert.Contains("Damage = 99", masterFirst);
        Assert.DoesNotContain("Damage = 10", masterFirst);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AScopeWithoutTheWinnerShowsItsHighestScopedCopy(bool swap)
    {
        var names = swap ? new[] { W.MidName, W.MasterName } : new[] { W.MasterName, W.MidName };

        var text = Scan(names);

        Assert.Contains("from " + W.MidName, text);
        Assert.Contains("Damage = 50", text);
        Assert.DoesNotContain("Damage = 99", text);
        Assert.DoesNotContain("Damage = 10", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFilterOnlyTheLowerScopedCopyPassesMatchesNothing(bool swap)
    {
        var names = swap ? new[] { W.OverrideName, W.MidName } : new[] { W.MidName, W.OverrideName };

        var text = Scan(names, "BasicStats.Damage < 60");

        Assert.Contains("scan: 0 matches", text);
        Assert.DoesNotContain(Fid(W.Weapons[0]), text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFilterTheShownCopyPassesReturnsThatCopy(bool swap)
    {
        var names = swap ? new[] { W.OverrideName, W.MidName } : new[] { W.MidName, W.OverrideName };

        var text = Scan(names, "BasicStats.Damage > 90");

        Served(text, Fid(W.Weapons[0]));
        Assert.Contains("from " + W.OverrideName, text);
        Assert.Contains("Damage = 99", text);
    }
}
