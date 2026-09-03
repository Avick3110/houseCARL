using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Proves <see cref="CheckErrorsWorld"/> is what its own doc claims, so a fixture drift fails here rather than in
/// every fact test that takes a total from it.
/// </summary>
[Trait("tier", "integration")]
[Collection("check-errors")]
public sealed class CheckErrorsWorldTests
{
    readonly CheckErrorsWorld W;
    public CheckErrorsWorldTests(CheckErrorsWorldFixture f) => W = f.W;

    [Fact]
    public void TheWorldSweepsThreePluginsAndFindsSixDanglingRefsOneMissingMasterAndOneUnparseablePlugin()
    {
        var r = W.Svc.CheckErrors(null, 1000, findings: null);

        Assert.Null(r.Error);
        Assert.Equal(CheckErrorsWorld.ScannedPlugins, r.PluginsScanned);
        Assert.Equal(CheckErrorsWorld.TotalDangling, r.TotalDangling);
        Assert.Equal(CheckErrorsWorld.BaselineDangling, r.BaselineDangling);
        Assert.Equal(1, r.TotalMissingMasters);
        Assert.Single(r.ExcludedPlugins);
        Assert.Contains(W.BadName, r.ExcludedPlugins.Keys);
        Assert.Equal(new[] { W.BaseName }, r.BaseMastersSwept);
    }
}
