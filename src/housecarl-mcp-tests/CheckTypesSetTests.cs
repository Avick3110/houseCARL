using HousecarlMcp;
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

    /// <summary>A BLANK entry names no type, so it is refused SAYING it is blank rather than quoted back as an
    /// unknown type ''. An empty set is a different thing (no narrowing) and is not this.</summary>
    [Fact]
    public void ABlankTypeInTheSetIsRefusedAsBlank()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "" });

        Assert.Contains("blank record type", r.Error);
        Assert.DoesNotContain("unknown record type", r.Error);
        Assert.Empty(r.Reports);
    }

    /// <summary>One rule, not two: the records surface refuses the same blank entry with the same sentence, so a
    /// caller cannot learn one grammar on one surface and meet another on the other.</summary>
    [Fact]
    public void TheRecordsSurfaceRefusesABlankTypeTheSameWay()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "" });

        Assert.Contains("blank record type", r, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown record type", r, StringComparison.Ordinal);
    }

    /// <summary>limit= is ONE listing budget spent in the order the types stream, so a two-type sweep with room for
    /// one finding lists the NPC_ and never reaches the WEAP. The response says so: absent from the listing means
    /// unlisted, not clean.</summary>
    [Fact]
    public void ACutMultiTypeListingSaysTheBudgetWasSpentInTypeOrder()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "Npc", "Weapon" });
        var text = CheckErrorsFixtures.Text(r, 80_000);

        Assert.Equal(2, r.TotalDangling);                                  // the totals are never capped
        Assert.Single(r.Reports.SelectMany(p => p.Dangling));              // the listing carried one of them
        Assert.Contains("types=[Npc, Weapon]", text, StringComparison.Ordinal);
        Assert.Contains("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>A budget that dropped nothing leaves the listing complete for every type in the scope, so the rule
    /// is not stated — a warning about a hole that is not there reads as one that is.</summary>
    [Fact]
    public void AnUncutMultiTypeListingStatesNoTypeOrderRule()
    {
        var text = CheckErrorsFixtures.Text(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" }), 80_000);

        Assert.DoesNotContain("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>With ONE type in scope the budget cannot have stopped before another type, so a cut listing states
    /// no type-order rule either — the rule is about the set, not about the cut.</summary>
    [Fact]
    public void ACutSingleTypeListingStatesNoTypeOrderRule()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 0, types: new[] { "Npc" });
        var text = CheckErrorsFixtures.Text(r, 80_000);

        Assert.Equal(1, r.TotalDangling);
        Assert.Empty(r.Reports.SelectMany(p => p.Dangling));                // the budget listed none of it
        Assert.DoesNotContain("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>The json twin carries the same fact under the same test, so the two transports cannot disagree
    /// about which budget was spent how.</summary>
    [Fact]
    public void TheJsonTwinCarriesTheTypeOrderTheBudgetWasSpentIn()
    {
        var json = CheckErrorsFixtures.Json(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "Npc", "Weapon" }), 80_000);

        Assert.Equal("Npc, Weapon",
                     CheckErrorsFixtures.ErrorsFamily(json).GetProperty("accounting")
                                        .GetProperty("limit_spent_in_type_order").GetString());
    }
}
