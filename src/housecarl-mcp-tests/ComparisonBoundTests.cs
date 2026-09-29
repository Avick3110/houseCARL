using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The delta and tree bound prices the shape being run (#932). The shapes below are the ones measured on the
/// 3,254-plugin order; the figures they are checked against are in docs/architecture/render-budget.md.
/// </summary>
[Trait("tier", "unit")]
public sealed class ComparisonBoundTests
{
    const string Lever = "pass fewer formids= entries.";

    static string? Refuse(RenderBudget.ComparisonShape shape, string form = "delta") =>
        RenderBudget.RefuseComparison(RenderBounds.Default, shape, form, Lever);

    /// <summary>The blind battery's call: a SkyPatcher-post versus winner delta over 1,276 ARMO/WEAP ids, whole
    /// records. It measured 298 s under load, inside the ten-minute budget, so it runs rather than being refused.</summary>
    [Fact]
    public void TheBatterysPostStateDeltaOverACatalogueIsNotRefused() =>
        Assert.Null(Refuse(new(1_276, 0, Narrowed: false, ReplaysOverlay: true)));

    /// <summary>A comparison narrowed by fields= on top-level records costs a fraction of a millisecond a row, so a
    /// catalogue many times the battery's fits.</summary>
    [Fact]
    public void ANarrowedComparisonAtCatalogueScaleIsNotRefused() =>
        Assert.Null(Refuse(new(100_000, 0, Narrowed: true, ReplaysOverlay: false)));

    /// <summary>A whole-record REFR tree measured 96 ms a row quiet and 154 under load. Twelve thousand rows is
    /// about 19 to 31 minutes: refused, at a price within a factor of two of both.</summary>
    [Fact]
    public void AReferenceScaleTreeIsRefusedAtAPriceNearItsMeasuredCost()
    {
        var shape = new RenderBudget.ComparisonShape(12_000, 12_000, Narrowed: false, ReplaysOverlay: false);
        var r = Refuse(shape, "tree");
        Assert.NotNull(r);
        Assert.Contains("minutes", r);
        Assert.InRange(shape.MillisPerRow, 96 / 2.0, 96 * 2.0);
        Assert.InRange(shape.MillisPerRow, 154 / 2.0, 154 * 2.0);
    }

    /// <summary>A narrowed row on a record a cell contains measured 14 ms, not the top-level fraction, so a big
    /// narrowed REFR comparison is refused where the same count of top-level records is not.</summary>
    [Fact]
    public void ANarrowedComparisonOverContainedRecordsIsChargedTheirOwnPrice()
    {
        Assert.Null(Refuse(new(100_000, 0, Narrowed: true, ReplaysOverlay: false)));
        var r = Refuse(new(100_000, 100_000, Narrowed: true, ReplaysOverlay: false));
        Assert.NotNull(r);
        Assert.Contains("a cell or topic contains", r);
    }

    /// <summary>A whole-record row costs the same contained or not, so its refusal does not name the contained count.</summary>
    [Fact]
    public void AWholeRecordRefusalDoesNotNameContainedRecords()
    {
        var r = Refuse(new(12_000, 12_000, Narrowed: false, ReplaysOverlay: false), "tree");
        Assert.NotNull(r);
        Assert.DoesNotContain("a cell or topic contains", r);
    }

    /// <summary>The refusal names the shape it priced, and still ends on the lane's lever.</summary>
    [Fact]
    public void TheRefusalNamesTheShapeItPricedAndTheLever()
    {
        var r = Refuse(new(5_000, 0, Narrowed: false, ReplaysOverlay: true));
        Assert.NotNull(r);
        Assert.Contains("priced for", r);
        Assert.Contains("replaying the SkyPatcher layer", r);
        Assert.EndsWith(Lever, r);
    }
}

/// <summary>
/// The same bound reached through <c>housecarl_records</c> with the derived bound in force (no row override), so the
/// shape the call site builds is what is priced: fields=, contained records, and a post-state pole. The world is
/// small, so the budget is lowered to 5 ms: one narrowed top-level row (0.2 ms) fits, and each dearer shape does not.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBoundCallSiteTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    public ComparisonBoundCallSiteTests(OwnedChildFixture f) => _w = f.W;

    string Weapon => OwnedChildWorld.Fid(_w.Weapon);
    string PlacedRef => OwnedChildWorld.Fid(new FormKey(_w.CellA.ModKey, 0xC10));

    static RecordsTools.RecordsProject Tree(params string[] fields) =>
        new() { form = "tree", fields = fields.Length > 0 ? fields : null };

    string Call(Func<string> call) => _w.Svc.WithBounds(b => b with { ComparisonMillis = 5 }, call);

    [Fact]
    public void ANarrowedTreeOnATopLevelRecordFits() =>
        Assert.False(Call(() => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, project: Tree("EditorID")))
                         .StartsWith("error:"));

    [Fact]
    public void AWholeRecordTreeIsPricedAsWholeRecords()
    {
        var r = Call(() => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, project: Tree()));
        Assert.StartsWith("error:", r);
        Assert.Contains("0.1 s a row", r);
    }

    [Fact]
    public void ANarrowedTreeOnAPlacedReferenceIsPricedAsContained()
    {
        var r = Call(() => RecordsTools.Records(_w.Svc, formids: new[] { PlacedRef }, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("15 ms a row", r);
    }

    [Fact]
    public void ANarrowedPostStateDeltaIsPricedWithTheReplay()
    {
        var post = System.Text.Json.JsonDocument.Parse("{\"overlay\":\"skypatcher\",\"state\":\"post\"}").RootElement.Clone();
        var winner = System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone();
        var r = Call(() => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, source: post, versus: winner,
                                                project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } }));
        Assert.StartsWith("error:", r);
        Assert.Contains("45.2 ms a row", r);
    }
}
