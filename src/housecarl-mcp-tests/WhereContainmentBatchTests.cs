using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1147: a <c>*parent</c> scan gathers a chunk's containing records in ONE bulk fetch, typed by the
/// child, instead of one untyped whole-plugin seek per distinct parent; only verdicts outlive the chunk.</summary>
[Trait("tier", "unit")]
public sealed class WhereContainmentBatchTests
{
    readonly SkyrimMod _mod = new(new ModKey("ParentBatch", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly Dictionary<FormKey, FormKey> _parents = new();
    readonly Dictionary<FormKey, IMajorRecordGetter> _bodies = new();
    readonly List<int> _gatherSizes = new();
    readonly List<FormKey> _gathered = new();
    readonly List<Type> _gatherTypes = new();
    int _singleFetches;

    Cell NewCell(string eid)
    {
        var c = new Cell(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = eid };
        _bodies[c.FormKey] = c;
        return c;
    }

    PlacedObject NewPlaced(string eid, IMajorRecordGetter parent)
    {
        var r = new PlacedObject(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = eid };
        _bodies[r.FormKey] = r;
        _parents[r.FormKey] = parent.FormKey;
        return r;
    }

    FieldPredicateSet Bind(string clause, bool batched)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        set!.BindResolution(fk => _bodies.ContainsKey(fk) ? "ParentBatch.esm" : null,
                            fk => { _singleFetches++; return _bodies.GetValueOrDefault(fk); },
                            fk => _parents.TryGetValue(fk, out var p) ? p : null,
                            batched
                                ? wanted =>
                                {
                                    _gatherSizes.Add(wanted.Count);
                                    var got = new Dictionary<FormKey, IMajorRecordGetter>();
                                    foreach (var (pk, childType, hops) in wanted)
                                    {
                                        _gathered.Add(pk);
                                        _gatherTypes.AddRange(ContainmentIndex.ContainerGetters(childType, hops));
                                        if (_bodies.TryGetValue(pk, out var b)) got[pk] = b;
                                    }
                                    return got;
                                }
                                : null);
        return set;
    }

    /// <summary>Run the set over the candidates a chunk at a time, as the scan lanes do; chunk 0 means unbatched.</summary>
    static List<FormKey> Scan(FieldPredicateSet set, IReadOnlyList<IMajorRecordGetter> candidates, int chunk)
    {
        var hits = new List<FormKey>();
        int size = chunk == 0 ? candidates.Count : chunk;
        for (int start = 0; start < candidates.Count; start += size)
        {
            var part = candidates.Skip(start).Take(size).ToList();
            if (chunk > 0) set.HoldParents(part);
            foreach (var c in part) if (set.Matches(c)) hits.Add(c.FormKey);
            set.ReleaseParents();
        }
        Assert.Null(set.FatalError);
        return hits;
    }

    /// <summary>Many children of few parents: each parent is fetched once, all of them in one gather, and never
    /// through the one-at-a-time fetch. Fails on the per-parent fetch, which makes four single calls.</summary>
    [Fact]
    public void AChunkOfChildrenFetchesEachParentOnceInOneGather()
    {
        var cells = Enumerable.Range(0, 4).Select(i => NewCell($"PbCell{i}")).ToList();
        var placed = Enumerable.Range(0, 200).Select(i => (IMajorRecordGetter)NewPlaced($"PbRef{i}", cells[i % 4])).ToList();

        var set = Bind("*parent.EditorID = PbCell2", batched: true);
        var hits = Scan(set, placed, chunk: 1000);

        Assert.Equal(50, hits.Count);
        Assert.Equal(new[] { 4 }, _gatherSizes);                      // one gather, the four distinct parents
        Assert.Equal(cells.Select(c => c.FormKey).OrderBy(k => k.ID), _gathered.OrderBy(k => k.ID));
        Assert.Equal(0, _singleFetches);
        Assert.Equal(0, set.ParentBodiesHeld);
    }

    /// <summary>The gather is typed by the child: a placed reference's container is a cell, so the walk reads the
    /// cell groups and not the whole plugin from the top.</summary>
    [Fact]
    public void TheGatherIsTypedByTheChildsContainer()
    {
        var cell = NewCell("PbTyped");
        var placed = Enumerable.Range(0, 3).Select(i => (IMajorRecordGetter)NewPlaced($"PbTypedRef{i}", cell)).ToList();

        Scan(Bind("*parent.EditorID = PbTyped", batched: true), placed, chunk: 10);

        Assert.Equal(new[] { typeof(ICellGetter) }, _gatherTypes.Distinct());
        Assert.Equal(new[] { typeof(IDialogTopicGetter) }, ContainmentIndex.ContainerGetters(typeof(DialogResponses), 1));
        Assert.Equal(new[] { typeof(IWorldspaceGetter) }, ContainmentIndex.ContainerGetters(typeof(PlacedObject), 2));
    }

    /// <summary>A chunk boundary in the middle of one parent's children: the second chunk reuses the first chunk's
    /// VERDICT, never its body, and the answer is the unbatched one.</summary>
    [Fact]
    public void AChunkBoundaryInsideOneParentsChildrenGivesTheSameAnswer()
    {
        var cells = Enumerable.Range(0, 4).Select(i => NewCell($"PbSplit{i}")).ToList();
        // Grouped by cell, 50 each, so a 30-row chunk splits every cell's run of children.
        var placed = Enumerable.Range(0, 200).Select(i => (IMajorRecordGetter)NewPlaced($"PbSplitRef{i}", cells[i / 50])).ToList();

        var unbatched = Scan(Bind("*parent.EditorID = PbSplit1", batched: false), placed, chunk: 0);
        int singleUnbatched = _singleFetches;
        var batched = Scan(Bind("*parent.EditorID = PbSplit1", batched: true), placed, chunk: 30);

        Assert.Equal(50, unbatched.Count);
        Assert.Equal(unbatched, batched);
        Assert.Equal(4, singleUnbatched);
        Assert.Equal(singleUnbatched, _singleFetches);              // the batched pass made no single fetch of its own
        Assert.Equal(4, _gathered.Count);                           // each parent gathered once across all seven chunks
        Assert.Equal(_gathered.Count, _gathered.Distinct().Count());
    }

    /// <summary>A hop that dead-ends above its first step still names what it dead-ended on, read through the
    /// gather rather than one fetch per dead end.</summary>
    [Fact]
    public void AChainThatDeadEndsIsReadThroughTheGather()
    {
        var placed = new List<IMajorRecordGetter>();
        for (int i = 0; i < 10; i++)
        {
            var cell = NewCell($"PbLone{i}");
            for (int r = 0; r < 3; r++) placed.Add(NewPlaced($"PbLoneRef{i}_{r}", cell));
        }

        var set = Bind("*parent.*parent.EditorID = PbNowhere", batched: true);
        Assert.Empty(Scan(set, placed, chunk: 100));
        Assert.Equal(0, _singleFetches);
        Assert.Equal(new[] { 10 }, _gatherSizes);
        Assert.Contains("Cell", set.AccountingNote());
    }

    /// <summary>A parent outside any held chunk is fetched alone through the one-at-a-time fetch, as before the
    /// gather, so its fault surfaces as the row's own. Fails when a lone miss goes through the bulk gather.</summary>
    [Fact]
    public void ALoneMissIsFetchedOneAtATime()
    {
        var cell = NewCell("PbAlone");
        var placed = NewPlaced("PbAloneRef", cell);

        var set = Bind("*parent.EditorID = PbAlone", batched: true);
        Assert.True(set.Matches(placed));
        Assert.Equal(1, _singleFetches);
        Assert.Empty(_gatherSizes);
    }
}

/// <summary>The same against real plugins: a <c>*parent</c> scan makes no per-record seek, so no parent fetch
/// walks its winner plugin from the top. Fails on the per-parent untyped <c>GetRecord</c>.</summary>
[Trait("tier", "integration")]
public sealed class RecordsContainmentBatchTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;

    public RecordsContainmentBatchTests(OwnedChildFixture f) => _w = f.W;

    [Theory]
    [InlineData("PlacedObject", "*parent.EditorID = HcOcCellA")]
    [InlineData("DialogResponses", "*parent.EditorID = HcOcTopic")]
    [InlineData("PlacedObject", "*parent.*parent.EditorID = HcOcWrld")]
    public void AParentScanMakesNoPerRecordSeek(string type, string clause)
    {
        var seeks = _w.Svc.Counters.BodySeeks;
        var r = RecordsTools.Records(_w.Svc, types: new[] { type }, where: new[] { clause },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" },
                                     counts_only: true);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        Assert.Equal(seeks, _w.Svc.Counters.BodySeeks);
    }

    long[] Costs() { var c = _w.Svc.Counters; return new[] { c.BodySeeks, c.TypedSeeks, c.CollectPasses, c.TypedCollectPasses }; }
    long[] Since(long[] before) => Costs().Zip(before, (a, b) => a - b).ToArray();

    /// <summary>The scan's gather reaches the resolver typed, one container walk per winner plugin, never one per
    /// distinct cell. Fails on an untyped <c>GatherContainers</c>, and without the type lane's <c>HoldParents</c>.</summary>
    [Theory]
    [InlineData("PlacedObject", "*parent.EditorID = HcOcCellA", 3)]           // cells won by Base, Mid and Top
    [InlineData("DialogResponses", "*parent.EditorID = HcOcTopic", 1)]
    [InlineData("PlacedObject", "*parent.*parent.EditorID = HcOcWrld", 3)]
    public void AParentScanGathersTyped(string type, string clause, int walks)
    {
        var before = Costs();
        var r = RecordsTools.Records(_w.Svc, types: new[] { type }, where: new[] { clause },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" },
                                     counts_only: true);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        var d = Since(before);
        Assert.Equal(new long[] { 0, 0, walks, walks }, d);        // no lone seek; every gather walk typed
    }

    /// <summary>The formid-set lane gathers a chunk's containing records once: one walk per winner plugin for the
    /// bodies and one for the containers, never one per distinct cell. Fails without the lane's <c>HoldParents</c>.</summary>
    [Fact]
    public void AFormidSetParentScanGathersEachChunksParentsOnce()
    {
        var list = Path.Combine(_w.Root, "refs-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var w = RecordsTools.Records(_w.Svc, types: new[] { "PlacedObject" }, to_file: list);
        Assert.False(w.StartsWith("error", StringComparison.Ordinal), w);

        var before = Costs();
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "@" + list }, where: new[] { "*parent.EditorID = HcOcCellA" },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" },
                                     counts_only: true);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        var d = Since(before);
        Assert.Equal(0, d[0]);                                      // no record seeks its cell alone
        Assert.Equal(3, d[3]);                                      // one typed container walk per winner plugin: Base, Mid, Top
    }

    /// <summary>A <c>fields=["*parent.EditorID"]</c> projection takes the render chunk's containers in one typed
    /// gather, so no row seeks its cell alone. Fails when <c>Chunk.Parent</c> holds nothing.</summary>
    [Fact]
    public void AParentProjectionGathersTheChunksContainers()
    {
        var before = Costs();
        var r = RecordsTools.Records(_w.Svc, types: new[] { "PlacedObject" }, project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "*parent.EditorID" } },
                                     format: "json", limit: 1000);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        Assert.Contains("HcOcCellA", r);
        var d = Since(before);
        Assert.Equal(0, d[0]);                                      // no row seeks its cell alone
        Assert.True(d[2] > 0 && d[2] == d[3], string.Join(",", d));   // the gathers ran, every one typed
    }

    /// <summary>The second hop of a projection, which the chunk does not gather, seeks its container typed.
    /// Fails on an untyped <c>FetchContainer</c>.</summary>
    [Fact]
    public void ASecondHopProjectionSeeksTyped()
    {
        var before = Costs();
        var r = RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(_w.WorldCellRef) },
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "*parent.*parent.EditorID" } });
        Assert.Contains("HcOcWrld", r);
        var d = Since(before);
        Assert.True(d[0] > 0, string.Join(",", d));
        Assert.Equal(d[0], d[1]);                                   // every seek typed
    }

    /// <summary>A walk's <c>*parent</c> hop seeks the containing cell typed. Fails on the untyped walk fetch.</summary>
    [Fact]
    public void AWalksParentHopSeeksTyped()
    {
        var before = Costs();
        var r = RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(_w.WorldCellRef) },
                                     walk: new RecordsTools.RecordsWalk { seed_paths = new[] { "*parent.*parent" }, depth = 1 },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.Contains(OwnedChildWorld.Fid(_w.Worldspace), r);
        Assert.Equal(1, Since(before)[1]);                          // the one hop, to the cell
    }

    /// <summary>A projection's first hop gathers the rows' containers without walking every row's source plugin
    /// first: reading one Base row's parent walks Base alone for bodies. Fails when the gather reads each row's
    /// body to learn its type (Base, Mid and Top walked).</summary>
    [Fact]
    public void AProjectionHopWalksOnlyTheSourcePluginsItsRowsRead()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        var keys = AllPlaced(pin.View);
        var chunk = BodyPrefetch.Gather(pin.View, session, _w.Svc.Counters, keys, 0, keys.Count, _ => null, null, default);
        var row = keys.First(k => pin.View.ResolveWinner(k)!.Value.WinnerPlugin == _w.BaseName);

        var before = Costs();
        var body = chunk.Body(row)!;
        Assert.NotNull(chunk.Parent(body, pin.View.ParentOf(row)!.Value));
        var d = Since(before);
        Assert.Equal(1, d[2] - d[3]);                               // one untyped body walk, Base's
    }

    /// <summary>A projection row whose containing record's winner would not open answers with the gather's fault
    /// and does not open that plugin again. Fails when the fault is dropped and each row re-seeks its parent.</summary>
    [Fact]
    public void AProjectionHopAnswersTheGathersFaultWithoutReopening()
    {
        var pin = _w.Svc.CapturePin();
        using var session = pin.Resolver.OpenSession();
        // Base's references in CellG and CellH, whose cells Mid wins.
        var keys = AllPlaced(pin.View).Where(k => pin.View.ResolveWinner(k)!.Value.WinnerPlugin == _w.BaseName
                                                  && pin.View.ParentOf(k) is { } p && (p == _w.CellG || p == _w.CellH)).ToList();
        Assert.True(keys.Count >= 2);
        var chunk = BodyPrefetch.Gather(pin.View, session, _w.Svc.Counters, keys, 0, keys.Count, _ => null, null, default);
        var bodies = keys.Take(2).Select(k => chunk.Body(k)!).ToList();

        using (new FileStream(_w.PluginPaths[1], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<PluginUnreadableException>(() => chunk.Parent(bodies[0], pin.View.ParentOf(keys[0])!.Value));
            var opens = _w.Svc.Counters.SessionOverlayOpens;
            var fault = Assert.Throws<PluginUnreadableException>(() => chunk.Parent(bodies[1], pin.View.ParentOf(keys[1])!.Value));
            Assert.Equal(_w.MidName, fault.PluginName);
            Assert.Equal(opens, _w.Svc.Counters.SessionOverlayOpens);   // no second open of Mid
        }
    }

    static List<FormKey> AllPlaced(LoadOrderResolver.IndexView view) =>
        view.WinnerRecordsOfType(new[] { typeof(IPlacedObjectGetter) }).Select(x => x.fk).ToList();
}
