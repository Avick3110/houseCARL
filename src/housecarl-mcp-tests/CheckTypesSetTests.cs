using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_check</c>'s record-type scope is a SET: a sweep over N types is the sweep over each, findings
/// merged, and one type is a set of one. The two subjects are the master's NPC_ and WEAP, one dangling ref each.
/// </summary>
[Collection("type-arms")]
[Trait("tier", "integration")]
public sealed class CheckTypesSetTests
{
    readonly TypeArmWorld _w;
    public CheckTypesSetTests(TypeArmFixture f) => _w = f.W;

    /// <summary>The set is the union: both types' findings come back from one call.</summary>
    [Fact]
    public void ASweepOverTwoTypesReportsBothTypesFindings()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" });

        Assert.Null(r.Error);
        Assert.Equal(2, r.TotalDangling);
        var ids = r.Reports.SelectMany(p => p.Dangling).Select(d => d.SourceEditorId).ToList();
        Assert.Contains(TypeArmWorld.SweepNpc, ids);
        Assert.Contains(TypeArmWorld.SweepWeapon, ids);
    }

    /// <summary>One type is a set of one, and it still answers over that type alone — the arm that keeps the union
    /// above from passing on a scope that stopped narrowing.</summary>
    [Fact]
    public void ASweepOverOneTypeStillAnswersOverThatTypeAlone()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc" });

        Assert.Null(r.Error);
        Assert.Equal(1, r.TotalDangling);
        var ids = r.Reports.SelectMany(p => p.Dangling).Select(d => d.SourceEditorId).ToList();
        Assert.Contains(TypeArmWorld.SweepNpc, ids);
        Assert.DoesNotContain(TypeArmWorld.SweepWeapon, ids);
    }

    /// <summary>The applied narrowing is spelled with both types, so a count can never read as the wider claim.</summary>
    [Fact]
    public void TheRenderNamesEveryTypeInTheSet()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" });

        Assert.Contains("types=[Npc, Weapon]", r.FilterNote);
    }

    /// <summary>An unknown entry refuses the whole sweep by name, the way the records surface refuses one.</summary>
    [Fact]
    public void AnUnknownTypeInTheSetRefusesTheSweepByName()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "NOSUCHTYPE" });

        Assert.Contains("NOSUCHTYPE", r.Error);
        Assert.Empty(r.Reports);
    }
}
