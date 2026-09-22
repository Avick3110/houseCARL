using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a lane says when <c>offset=</c> is asked of a count table (#810, SPEC §2.1): one sentence, the same
/// one everywhere — the table covers the complete selection, it caps with <c>limit=</c>, it does not page. The four
/// lanes used to spell that rule four ways, and <c>records</c> under <c>counts_only=</c> did not spell it at all: it
/// accepted <c>offset=</c> and windowed a render the census writes no rows into.</summary>
static class CountTableRefusal
{
    /// <summary>The distinguishing clauses of the shared sentence, with the knob that asked for the table named.</summary>
    internal static void SaysTheOneSentence(string text, string knob)
    {
        Assert.Contains(knob + " answers as a count table over the COMPLETE selection", text);
        Assert.Contains("caps with limit= and does not page", text);
        Assert.Contains("drop offset=", text);
        Assert.Contains("to_file=", text);
    }
}

/// <summary>The <c>housecarl_records</c> lanes: the aggregate form, and <c>counts_only=</c> on the scan and the list
/// lane.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsCountTableOffsetTests : RecordsTestBase
{
    public RecordsCountTableOffsetTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void TheAggregateFormRefusesOffsetWithTheSharedSentence() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, offset: 1,
                                 project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }),
            "the aggregate form");

    [Fact]
    public void CountsOnlyOnTheScanLaneRefusesOffsetWithTheSharedSentence() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, counts_only: true, offset: 1), "counts_only=");

    [Fact]
    public void CountsOnlyOnTheListLaneRefusesOffsetWithTheSharedSentence() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, formids: AllWeaponIds, counts_only: true, offset: 1), "counts_only=");

    /// <summary>The scan's own <c>group_by=</c>, refused in the service rather than at the tool door.</summary>
    [Fact]
    public void TheScansGroupByRefusesOffsetWithTheSharedSentence()
    {
        var outcome = Svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner", offset: 5);
        Assert.NotNull(outcome.Error);
        CountTableRefusal.SaysTheOneSentence(outcome.Error!, "group_by=");
    }
}

/// <summary>The OFF-ORDER scan's own <c>group_by=</c> — its own world, because the shared records world carries no
/// off-order file this lane can read.</summary>
[Collection("render-cost")]
[Trait("tier", "integration")]
public sealed class OffOrderCountTableOffsetTests
{
    readonly RenderCostWorld _w;
    public OffOrderCountTableOffsetTests(RenderCostFixture f) => _w = f.W;

    [Fact]
    public void TheOffOrderScansGroupByRefusesOffsetWithTheSharedSentence()
    {
        var pole = _w.Svc.ProbeSourceArm(_w.OffOrderName, null, out var perr);
        Assert.Null(perr);

        var outcome = _w.Svc.OffOrderQuery(pole!, new[] { "WEAP" }, null, null, null, false, null, 100,
                                          groupBy: "winner", offset: 5, formidSet: null, artifactDemands: null);

        Assert.NotNull(outcome.Error);
        CountTableRefusal.SaysTheOneSentence(outcome.Error!, "group_by=");
    }
}
