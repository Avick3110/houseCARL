using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The delta and tree floor: each row priced at the cheapest row measured for its shape, so a count past ten minutes
/// even at that price refuses before any read and a feasible job never does (#932); the measurements are cited in
/// docs/architecture/render-budget.md. What passes the floor is metered: ComparisonMeterTests.
/// </summary>
[Trait("tier", "unit")]
public sealed class ComparisonBoundTests
{
    const string Lever = "pass fewer formids= entries.";

    static string? Refuse(int rows, bool narrowed, string form = "delta") =>
        RenderBudget.RefuseComparison(RenderBounds.Default, rows, narrowed, form, RenderBudget.ComparisonLever(Lever, narrowed));

    /// <summary>The blind battery's 1,276-id whole-record delta passes the floor.</summary>
    [Fact]
    public void TheBatterysDeltaPassesTheFloor() => Assert.Null(Refuse(1_276, false));

    /// <summary>The 5,798-REFR whole tree passes the floor: whether it fits is the meter's to measure.</summary>
    [Fact]
    public void AReferenceScaleWholeTreePassesTheFloor() => Assert.Null(Refuse(5_798, false, "tree"));

    /// <summary>A narrowed tree over all 65,748 NPC_ runs in about three minutes and passes the floor.</summary>
    [Fact]
    public void ANarrowedTreeOverEveryNpcPassesTheFloor() => Assert.Null(Refuse(65_748, true, "tree"));

    /// <summary>Past the whole-record floor the refusal says "at least" at the cheapest row, and leads its levers with
    /// fields= on the same call.</summary>
    [Fact]
    public void AWholeRecordCountPastTheFloorRefusesAtLeastAndNamesFields()
    {
        var r = Refuse(20_001, false, "tree");
        Assert.NotNull(r);
        Assert.Contains("at least about 10 minutes even at the cheapest measured row (30 ms a row comparing whole records)", r);
        Assert.Contains("(20,000 rows fit at that price)", r);
        Assert.Contains("fields= on this same call", r);
        Assert.EndsWith(Lever, r);
    }

    /// <summary>A narrowed count refuses only in the millions, and does not offer fields= it already has.</summary>
    [Fact]
    public void ANarrowedCountRefusesOnlyInTheMillions()
    {
        Assert.Null(Refuse(15_000_000, true));
        var r = Refuse(15_000_001, true);
        Assert.NotNull(r);
        Assert.Contains("0.04 ms a row comparing named fields", r);
        Assert.DoesNotContain("fields= on this same call", r);
    }

    /// <summary>A test's ComparisonRows override still bounds the floor.</summary>
    [Fact]
    public void TheRowOverrideStillBounds()
    {
        var bounds = RenderBounds.Default with { ComparisonRows = 2 };
        Assert.Null(RenderBudget.RefuseComparison(bounds, 2, false, "tree", Lever));
        Assert.Contains("(2 rows fit at that price)", RenderBudget.RefuseComparison(bounds, 3, false, "tree", Lever));
    }
}

/// <summary>
/// The floor as the tool derives it, with no row override: the shape comes from fields=. The ids need not exist; the
/// floor is on the list's length, before any body is read.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBoundCallSiteTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    public ComparisonBoundCallSiteTests(OwnedChildFixture f) => _w = f.W;

    static RecordsTools.RecordsProject Form(string form, bool narrowed) =>
        new() { form = form, fields = narrowed ? new[] { "EditorID" } : null };

    string[] Ids(int n) => Enumerable.Range(0x10000, n).Select(i => $"{i:X6}:{_w.BaseName}").ToArray();

    [Fact]
    public void AWholeRecordTreeRefusesAtTheFloorWhereTheSameNarrowedTreeRuns()
    {
        var ids = Ids(RenderBudget.ComparisonFloorRows(RenderBounds.Default, false) + 1);
        var whole = RecordsTools.Records(_w.Svc, formids: ids, project: Form("tree", false), counts_only: true);
        Assert.StartsWith("error:", whole);
        Assert.Contains("even at the cheapest measured row", whole);
        var narrowed = RecordsTools.Records(_w.Svc, formids: ids, project: Form("tree", true), counts_only: true);
        Assert.False(narrowed.StartsWith("error:"), narrowed);
    }
}
