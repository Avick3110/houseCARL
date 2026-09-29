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

    /// <summary>A counted shape: <paramref name="versions"/> read a row, the first <paramref name="contained"/> rows contained.</summary>
    static RenderBudget.ComparisonShape Shape(int rows, double versions, int contained = 0, bool narrowed = false,
                                              bool overlay = false, bool tree = false)
    {
        long reads = (long)Math.Round(rows * versions);
        return new(rows, reads, (long)Math.Round(contained * versions), contained, narrowed, overlay, tree, Counted: true);
    }

    /// <summary>The blind battery's call: a SkyPatcher-post versus winner delta over 1,276 ARMO/WEAP ids, whole
    /// records. It measured 298 s under load, inside the ten-minute budget, so it runs rather than being refused.</summary>
    [Fact]
    public void TheBatterysPostStateDeltaOverACatalogueIsNotRefused() =>
        Assert.Null(Refuse(Shape(1_276, 2, overlay: true)));

    /// <summary>A comparison narrowed by fields= on top-level records costs a fraction of a millisecond a row, so a
    /// catalogue many times the battery's fits.</summary>
    [Fact]
    public void ANarrowedComparisonAtCatalogueScaleIsNotRefused() =>
        Assert.Null(Refuse(Shape(100_000, 2, narrowed: true)));

    /// <summary>A whole-record REFR tree measured 96 ms a row quiet and 154 under load, at 1.54 versions a row.
    /// Twelve thousand rows is about 19 to 31 minutes: refused, at a price within a factor of two of both.</summary>
    [Fact]
    public void AReferenceScaleTreeIsRefusedAtAPriceNearItsMeasuredCost()
    {
        var shape = Shape(12_000, 1.54, contained: 12_000, tree: true);
        var r = Refuse(shape, "tree");
        Assert.NotNull(r);
        Assert.Contains("minutes", r);
        Assert.InRange(shape.MillisPerRow, 96 / 2.0, 96 * 2.0);
        Assert.InRange(shape.MillisPerRow, 154 / 2.0, 154 * 2.0);
    }

    /// <summary>A tree reads every provider of its record, so the same rows cost more the more versions each has:
    /// 6,000 NPC_ rows fit at one version and are refused at ten, and the refusal says how many it counted.</summary>
    [Fact]
    public void ATreeIsPricedPerVersionItReads()
    {
        Assert.Null(Refuse(Shape(6_000, 1, tree: true), "tree"));
        var r = Refuse(Shape(6_000, 10, tree: true), "tree");
        Assert.NotNull(r);
        Assert.Contains("10 versions read a record", r);
    }

    /// <summary>A narrowed row on a record a cell contains measured 14 ms, not the top-level fraction, so a big
    /// narrowed REFR comparison is refused where the same count of top-level records is not.</summary>
    [Fact]
    public void ANarrowedComparisonOverContainedRecordsIsChargedTheirOwnPrice()
    {
        Assert.Null(Refuse(Shape(100_000, 2, narrowed: true)));
        var r = Refuse(Shape(100_000, 2, contained: 100_000, narrowed: true));
        Assert.NotNull(r);
        Assert.Contains("a cell or topic contains", r);
    }

    /// <summary>A whole LAND or NAVM version measured about 100 ms, twice a top-level one, so a whole-record
    /// comparison over contained records is refused where the same count of top-level records is not.</summary>
    [Fact]
    public void AWholeRecordComparisonOverContainedRecordsIsChargedTheDearestMeasuredType()
    {
        Assert.Null(Refuse(Shape(7_000, 1, tree: true), "tree"));
        var r = Refuse(Shape(7_000, 1, contained: 7_000, tree: true), "tree");
        Assert.NotNull(r);
        Assert.Contains("7,000 of them records a cell or topic contains", r);
    }

    /// <summary>A narrowed tree over all 65,748 NPC_ walked 205 GB of provider plugins and took 216 s; its reads alone price it at 9 s.</summary>
    [Fact]
    public void ANarrowedComparisonIsPricedByThePluginsItWalks()
    {
        var reads = Shape(65_748, 1.36, narrowed: true, tree: true);
        Assert.InRange(reads.MillisPerRow * 65_748 / 1000, 0, 20);
        var walked = reads with { MegabytesWalked = 204_967 };
        Assert.InRange(walked.MillisPerRow * 65_748 / 1000, 216 / 2.0, 216 * 2.0);
        Assert.Null(Refuse(walked, "tree"));
        var r = Refuse(walked with { MegabytesWalked = 3 * 204_967 }, "tree");
        Assert.NotNull(r);
        Assert.Contains("walking 614.9 GB of provider plugins", r);
    }

    /// <summary>A refusal on the floor, before providers and contained records are counted, says its bound is an upper one.</summary>
    [Fact]
    public void AFloorRefusalSaysItsBoundIsAtMost()
    {
        var floor = RenderBudget.ComparisonShape.Floor(4_000_000, tree: false, namedVersus: false, narrowed: true, replaysOverlay: false);
        var r = Refuse(floor);
        Assert.NotNull(r);
        Assert.Contains("given at most, before its providers and contained records are counted", r);
        Assert.DoesNotContain("at most", Refuse(Shape(4_000_000, 2, narrowed: true))!);
    }

    /// <summary>The refusal names the shape it priced, and still ends on the lane's lever.</summary>
    [Fact]
    public void TheRefusalNamesTheShapeItPricedAndTheLever()
    {
        var r = Refuse(Shape(5_000, 2, overlay: true));
        Assert.NotNull(r);
        Assert.Contains("priced for", r);
        Assert.Contains("replaying the SkyPatcher layer", r);
        Assert.EndsWith(Lever, r);
    }
}

/// <summary>
/// The same bound reached through <c>housecarl_records</c> with the derived bound in force (no row override), so the
/// shape the call site builds is what is priced: fields=, contained records, and a post-state pole. The world is
/// small, so the budget is lowered to 5 ms: one narrowed top-level row fits, and each dearer shape does not.
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
        // 100 ms: one whole version (the floor) fits, the weapon's three providers do not.
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 100 },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, project: Tree()));
        Assert.StartsWith("error:", r);
        Assert.Contains("0.15 s a row", r);                // three providers at 50 ms a whole version
        Assert.Contains("3 versions read a record", r);
    }

    [Fact]
    public void ANarrowedTreeOnAPlacedReferenceIsPricedAsContained()
    {
        var r = Call(() => RecordsTools.Records(_w.Svc, formids: new[] { PlacedRef }, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("10 ms a row", r);
    }

    /// <summary>A narrowed row's cost is mostly the provider plugins its chunk walks, so the weapon's three plugins are
    /// charged by their size: refused at a budget that covers its three reads and half those megabytes.</summary>
    [Fact]
    public void ANarrowedTreeIsChargedForThePluginsItsChunkWalks()
    {
        double mb = new[] { _w.BaseName, _w.MidName, _w.TopName }
            .Sum(n => new FileInfo(Directory.GetFiles(_w.Root, n, SearchOption.AllDirectories)[0]).Length / 1_000_000.0);
        double budget = 3 * RenderBudget.MillisPerNarrowComparisonRead + mb * RenderBudget.MillisPerNarrowComparisonMegabyteWalked / 2;
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = budget },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("GB of provider plugins", r);
    }

    /// <summary>A scan counts its own matches' providers and containment exactly, so its refusal quotes the bound that shape is given.</summary>
    [Fact]
    public void ANarrowedScanOverPlacedReferencesIsCountedExactly()
    {
        var r = Call(() => RecordsTools.Records(_w.Svc, types: new[] { "REFR" }, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("of them records a cell or topic contains", r);
        Assert.DoesNotContain("at most", r);
    }

    /// <summary>The list lane refuses on its length before the index is built, and says that bound is an upper one.</summary>
    [Fact]
    public void AListRefusedOnItsLengthSaysTheBoundIsAtMost()
    {
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 0.05 },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("given at most, before its providers and contained records are counted", r);
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

/// <summary>
/// The off-order lane prices a file's new records by the file's own containment, which the active order's index
/// does not hold: new placed references in a switched-off plugin are charged as contained.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBoundOffOrderTests : IClassFixture<RenderCostFixture>
{
    readonly RenderCostWorld _w;
    public ComparisonBoundOffOrderTests(RenderCostFixture f) => _w = f.W;

    [Fact]
    public void AFilesNewPlacedReferencesArePricedAsContained()
    {
        var source = System.Text.Json.JsonDocument.Parse("\"" + _w.OffOrderCellName + "\"").RootElement.Clone();
        var winner = System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone();
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 5 },
            () => RecordsTools.Records(_w.Svc, types: new[] { "REFR" }, source: source, versus: winner,
                                       project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } }));
        Assert.StartsWith("error:", r);
        Assert.Contains($"{RenderCostWorld.OffOrderRefs} of them records a cell or topic contains", r);
    }
}
