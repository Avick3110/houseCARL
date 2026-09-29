using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The delta and tree bound sizes its estimate from the measured row cost of the shape being run (#932). The
/// shapes below are the ones measured on the 3,254-plugin order; the figures they are checked against are in
/// docs/architecture/render-budget.md.
/// </summary>
[Trait("tier", "unit")]
public sealed class ComparisonBoundTests
{
    const string Lever = "pass fewer formids= entries.";

    static string? Refuse(RenderBudget.ComparisonShape shape, string form = "delta") =>
        RenderBudget.RefuseComparison(RenderBounds.Default, shape, form, Lever);

    /// <summary>The blind battery's call: a SkyPatcher-post versus winner delta over 1,276 ARMO/WEAP ids, whole
    /// records. It measured 298 s warm, inside the ten-minute budget, so it runs rather than being refused.</summary>
    [Fact]
    public void TheBatterysPostStateDeltaOverACatalogueIsNotRefused() =>
        Assert.Null(Refuse(new(1_276, 0, Narrowed: false, ReplaysOverlay: true)));

    /// <summary>A comparison narrowed by fields= on top-level records costs about a millisecond a row, so a catalogue
    /// many times the battery's fits.</summary>
    [Fact]
    public void ANarrowedComparisonAtCatalogueScaleIsNotRefused() =>
        Assert.Null(Refuse(new(100_000, 0, Narrowed: true, ReplaysOverlay: false)));

    /// <summary>A whole-record tree over 5,798 REFRs measured 895 s. It is still refused, and the estimate it quotes
    /// is within a factor of two of that.</summary>
    [Fact]
    public void AReferenceScaleTreeIsRefusedWithAnEstimateNearItsMeasuredCost()
    {
        var shape = new RenderBudget.ComparisonShape(5_798, 5_798, Narrowed: false, ReplaysOverlay: false);
        var r = Refuse(shape, "tree");
        Assert.NotNull(r);
        Assert.Contains("minutes", r);
        var estimate = shape.Rows * shape.MillisPerRow;
        Assert.InRange(estimate, 895_000 / 2.0, 895_000 * 2.0);
    }

    /// <summary>A narrowed row on a record a cell contains measured 21 ms, not the top-level millisecond, so a big
    /// narrowed REFR comparison is refused where the same count of top-level records is not.</summary>
    [Fact]
    public void ANarrowedComparisonOverContainedRecordsIsChargedTheirOwnCost()
    {
        Assert.Null(Refuse(new(100_000, 0, Narrowed: true, ReplaysOverlay: false)));
        var r = Refuse(new(100_000, 100_000, Narrowed: true, ReplaysOverlay: false));
        Assert.NotNull(r);
        Assert.Contains("a cell or topic contains", r);
    }

    /// <summary>The refusal names the shape its per-row figure was measured for, and still ends on the lane's lever.</summary>
    [Fact]
    public void TheRefusalNamesTheShapeItMeasuredAndTheLever()
    {
        var r = Refuse(new(5_000, 0, Narrowed: false, ReplaysOverlay: true));
        Assert.NotNull(r);
        Assert.Contains("replaying the SkyPatcher layer", r);
        Assert.EndsWith(Lever, r);
    }
}
