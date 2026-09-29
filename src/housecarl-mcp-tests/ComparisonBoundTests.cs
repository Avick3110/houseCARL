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

    /// <summary>A whole-record REFR tree measured 96 ms a row quiet and 154 under load, at 1.52 versions a row.
    /// Twelve thousand rows is about 19 to 31 minutes: refused, at a price within a factor of two of both.</summary>
    [Fact]
    public void AReferenceScaleTreeIsRefusedAtAPriceNearItsMeasuredCost()
    {
        var shape = Shape(12_000, 1.52, contained: 12_000, tree: true);
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

    /// <summary>Narrowed NPC_ comparisons on ARR, every measured run: rows, versions a row, megabytes walked and
    /// seconds. Each price sits in the band render-budget.md states, from about 1.7 times low to 2.3 times high.</summary>
    [Theory]
    [InlineData(1_000, 1.0, 7_992, 3.52)]
    [InlineData(1_000, 1.0, 7_992, 2.88)]
    [InlineData(10_000, 1.66, 27_761, 9.48)]
    [InlineData(10_000, 1.66, 27_761, 8.09)]
    [InlineData(65_748, 1.36, 204_967, 199.7)]
    [InlineData(65_748, 1.36, 204_967, 215.7)]
    [InlineData(65_748, 1.36, 204_967, 158.6)]
    [InlineData(65_748, 1.36, 204_967, 173.2)]
    [InlineData(65_748, 1.20, 178_020, 184.1)]   // the delta against previous_provider
    public void ANarrowedComparisonIsPricedByThePluginsItWalks(int rows, double versions, double megabytes, double seconds)
    {
        var walked = Shape(rows, versions, narrowed: true, tree: true) with { MegabytesWalked = megabytes };
        Assert.InRange(walked.MillisPerRow * rows / 1000, seconds / 1.7, seconds * 2.3);
        Assert.Null(Refuse(walked, "tree"));
    }

    /// <summary>A narrowed tree that walks six times the all-NPC_ megabytes is refused, and says what it walks.</summary>
    [Fact]
    public void ANarrowedRefusalNamesThePluginsItWalks()
    {
        var r = Refuse(Shape(65_748, 1.36, narrowed: true, tree: true) with { MegabytesWalked = 6 * 204_967 }, "tree");
        Assert.NotNull(r);
        Assert.Contains("walking 1,229.8 GB of provider plugins", r);
    }

    /// <summary>A plugin asked for a key it lacks is walked to its end: 8,174 MB of HearthFires.esm walked that way by
    /// a delta over every NPC_ took 38 s, which the hit-walk price alone would put at 5.</summary>
    [Fact]
    public void AMissWalkIsPricedAtItsOwnRate()
    {
        var hit = Shape(65_748, 42.0 / 65_748, narrowed: true) with { MegabytesWalked = 8_174 };
        Assert.InRange(hit.MillisPerRow * 65_748 / 1000, 0, 38.25 / 2);
        var miss = Shape(65_748, 42.0 / 65_748, narrowed: true) with { MegabytesMissWalked = 8_174 };
        Assert.InRange(miss.MillisPerRow * 65_748 / 1000, 38.25 / 1.7, 38.25 * 2.3);
        Assert.Contains("to their end for records they lack", Refuse(miss with { MegabytesMissWalked = 200_000 })!);
    }

    /// <summary>A refusal on the floor, before providers and contained records are counted, says its bound is an upper one.</summary>
    [Fact]
    public void AFloorRefusalSaysItsBoundIsAtMost()
    {
        var floor = RenderBudget.ComparisonShape.Floor(8_000_000, tree: false, narrowed: true, replaysOverlay: false);
        var r = Refuse(floor);
        Assert.NotNull(r);
        Assert.Contains("at least", r);
        Assert.Contains("rows that shape could fit at most before its providers and contained records are counted", r);
        var counted = Refuse(Shape(4_000_000, 2, narrowed: true))!;
        Assert.DoesNotContain("at most", counted);
        Assert.DoesNotContain("at least", counted);
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
    public void ATreeAgainstANamedVersusReadsThatVersionToo()
    {
        var mid = System.Text.Json.JsonDocument.Parse("\"" + _w.MidName + "\"").RootElement.Clone();
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 100 },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, versus: mid, project: Tree()));
        Assert.StartsWith("error:", r);
        Assert.Contains("0.2 s a row", r);                 // three providers and the versus= version at 50 ms each
        Assert.Contains("4 versions read a record", r);
    }

    /// <summary>A tree against a named versus= that holds no version stops at the winner: CellG's two providers are
    /// not read past it, so one whole version fits a 60 ms budget that three would not.</summary>
    [Fact]
    public void ATreeAgainstAVersusThatHoldsNothingReadsOnlyTheWinner()
    {
        var view = _w.Svc.CaptureView();
        Assert.Equal(2, view.TouchingPlugins(_w.CellG)!.Count);
        Assert.DoesNotContain(_w.TopName, view.TouchingPlugins(_w.CellG)!);
        var top = System.Text.Json.JsonDocument.Parse("\"" + _w.TopName + "\"").RootElement.Clone();
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 60 },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellG) }, versus: top, project: Tree()));
        Assert.False(r.StartsWith("error:"), r);
        var counted = ComparisonCount.Count(new FormKey?[] { _w.CellG }, view, tree: true, RecordReads.PoleSpec.Winner,
                                            new RecordReads.PoleSpec(RecordReads.PoleKind.Named, _w.TopName), null, null, null);
        Assert.Equal(1, counted.Reads);
        double topMb = new FileInfo(Directory.GetFiles(_w.Root, _w.TopName, SearchOption.AllDirectories)[0]).Length / 1_000_000.0;
        Assert.Equal(topMb, counted.MegabytesMissWalked, 6);   // the versus= plugin, walked to its end for a record it lacks
    }

    /// <summary>A delta whose subject holds no version of a row refuses on the subject, so the reference plugin is
    /// neither walked nor charged: one plugin walk, counted as a walk to the subject plugin's end and nothing else.</summary>
    [Fact]
    public void ADeltaWhoseSubjectHoldsNothingDoesNotWalkTheReference()
    {
        var view = _w.Svc.CaptureView();
        Assert.DoesNotContain(_w.TopName, view.TouchingPlugins(_w.CellG)!);
        double mb = new FileInfo(Directory.GetFiles(_w.Root, _w.TopName, SearchOption.AllDirectories)[0]).Length / 1_000_000.0;
        var top = System.Text.Json.JsonDocument.Parse("\"" + _w.TopName + "\"").RootElement.Clone();
        var winner = System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone();
        var project = new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } };
        string Run(double perMb) => _w.Svc.WithBounds(b => b with { ComparisonMillis = RenderBudget.MillisPerNarrowComparisonRead + mb * perMb * 1.5 },
            () => RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellG) }, source: top, versus: winner, project: project));
        var counted = ComparisonCount.Count(new FormKey?[] { _w.CellG }, view, tree: false,
                                            new RecordReads.PoleSpec(RecordReads.PoleKind.Named, _w.TopName), RecordReads.PoleSpec.Winner,
                                            null, null, null);
        Assert.Equal(0, counted.Reads);
        Assert.Equal(0, counted.MegabytesWalked);
        Assert.Equal(mb, counted.MegabytesMissWalked, 6);
        long before = _w.Svc.Counters.CollectPasses;
        var r = Run(RenderBudget.MillisPerComparisonMegabyteMissWalked);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(1, _w.Svc.Counters.CollectPasses - before);
    }

    /// <summary>A scan of cells against a versus= plugin that lacks most of them prices that plugin's walk to its end,
    /// and says so; the scan lane has no floor, so the counted shape is what refuses.</summary>
    [Fact]
    public void AScanAgainstAVersusThatLacksItsRecordsIsChargedTheMissWalk()
    {
        var top = System.Text.Json.JsonDocument.Parse("\"" + _w.TopName + "\"").RootElement.Clone();
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 1e-9 },
                                  () => RecordsTools.Records(_w.Svc, types: new[] { "CELL" }, versus: top, project: Tree("EditorID")));
        Assert.StartsWith("error:", r);
        Assert.Contains("to their end for records they lack", r);
    }

    /// <summary>Rows the index settles as absent read no body, and the absent plugin is explained from the profile
    /// once for the call, not once a row.</summary>
    [Theory]
    [InlineData("tree")]
    [InlineData("delta")]
    public void AbsentIdsExplainTheirPluginOnce(string form)
    {
        var ids = Enumerable.Range(0x800, 50).Select(i => $"{i:X6}:HcAbsentComparison.esp").ToArray();
        var project = new RecordsTools.RecordsProject { form = form, fields = new[] { "EditorID" } };
        var winner = form == "delta" ? System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone() : (System.Text.Json.JsonElement?)null;
        long before = _w.Svc.Counters.AbsenceExplains;
        var r = RecordsTools.Records(_w.Svc, formids: ids, versus: winner, project: project, counts_only: true);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(1, _w.Svc.Counters.AbsenceExplains - before);
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

    /// <summary>A delta's chunk walks only its two poles' plugins: the weapon's winner (top) and the provider below it
    /// (mid). It fits a budget that covers those two and half the third provider, which a walk of every provider would not.</summary>
    [Fact]
    public void ANarrowedDeltaIsChargedOnlyForItsPolesPlugins()
    {
        double Mb(string name) => new FileInfo(Directory.GetFiles(_w.Root, name, SearchOption.AllDirectories)[0]).Length / 1_000_000.0;
        double budget = 2 * RenderBudget.MillisPerNarrowComparisonRead
                      + (Mb(_w.TopName) + Mb(_w.MidName) + Mb(_w.BaseName) / 2) * RenderBudget.MillisPerNarrowComparisonMegabyteWalked;
        var previous = System.Text.Json.JsonDocument.Parse("\"previous_provider\"").RootElement.Clone();
        var project = new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } };
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = budget },
                                  () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, versus: previous, project: project));
        Assert.False(r.StartsWith("error:"), r);
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
        Assert.Contains("could fit at most before its providers and contained records are counted", r);
    }

    [Fact]
    public void ANarrowedPostStateDeltaIsPricedWithTheReplay()
    {
        var post = System.Text.Json.JsonDocument.Parse("{\"overlay\":\"skypatcher\",\"state\":\"post\"}").RootElement.Clone();
        var winner = System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone();
        // 45.15 ms: the floor's one version and the replay fit, the delta's two versions do not.
        var r = _w.Svc.WithBounds(b => b with { ComparisonMillis = 45.15 },
            () => RecordsTools.Records(_w.Svc, formids: new[] { Weapon }, source: post, versus: winner,
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
                                       project: new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } },
                                       counts_only: true));
        Assert.StartsWith("error:", r);
        Assert.Contains("10 ms a row", r);                 // the file's version alone: the winner holds none of them
        Assert.Contains($"{RenderCostWorld.OffOrderRefs:N0} of them records a cell or topic contains", r);
    }

    /// <summary>A tree reads only in-order providers, and a new record has none, so a tree over a switched-off
    /// file's 10,000 new placed references reads nothing and is not refused.</summary>
    [Fact]
    public void ATreeOverAFilesNewRecordsReadsNothingAndIsNotRefused()
    {
        var source = System.Text.Json.JsonDocument.Parse("\"" + _w.OffOrderCellName + "\"").RootElement.Clone();
        var r = RecordsTools.Records(_w.Svc, types: new[] { "REFR" }, source: source,
                                     project: new RecordsTools.RecordsProject { form = "tree" }, counts_only: true);
        Assert.False(r.StartsWith("error:"), r);
    }
}

/// <summary>
/// The formids= comparison lane counts its price on one captured build and reads on another capture; a load-order
/// change between the two refuses rather than mixing builds. Its own world, since the test changes a plugin on disk.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBoundSeamTests
{
    [Theory]
    [InlineData("delta")]
    [InlineData("tree")]
    public void AnOrderChangeBetweenPricingAndReadingRefuses(string form)
    {
        using var w = new OwnedChildWorld();
        var weapon = OwnedChildWorld.Fid(w.Weapon);
        var project = new RecordsTools.RecordsProject { form = form, fields = new[] { "EditorID" } };
        var previous = form == "delta" ? System.Text.Json.JsonDocument.Parse("\"previous_provider\"").RootElement.Clone() : (System.Text.Json.JsonElement?)null;
        Assert.False(RecordsTools.Records(w.Svc, formids: new[] { weapon }, versus: previous, project: project).StartsWith("error:"));
        w.Svc.ReadArea.AfterComparisonPriceForGuard = () =>
        {
            w.Svc.ReadArea.AfterComparisonPriceForGuard = null;
            File.SetLastWriteTimeUtc(w.MidPath, File.GetLastWriteTimeUtc(w.MidPath).AddHours(1));
        };
        var r = RecordsTools.Records(w.Svc, formids: new[] { weapon }, versus: previous, project: project);
        Assert.StartsWith("error:", r);
        Assert.Contains("the load order changed between deriving the selection", r);
    }
}
