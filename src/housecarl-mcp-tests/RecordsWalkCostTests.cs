using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A world whose point is the SHAPE a walk pays for: many seeds sitting in one plugin that also holds many other
/// records. A walk's old per-node body read was a whole-plugin seek, so its cost was the seed count TIMES the
/// plugin's record count; a handful of records in a small plugin cannot show that, and the shared worlds are both.
/// </summary>
public sealed class WalkCostWorld : IDisposable
{
    /// <summary>NPC seeds, each on the same template chain.</summary>
    public const int Seeds = 300;

    /// <summary>Distinct items per seed, so a closure walk's first hop reaches records nothing else reaches.</summary>
    public const int ItemsPerSeed = 3;

    /// <summary>Hubs in the revisit fan, and the fresh terminal each hub also carries.</summary>
    public const int Hubs = 40;

    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The seed of the revisit fan: a list of <see cref="Hubs"/> hubs that each point back at every hub and
    /// at one terminal of their own, so hop 2's frontier is half revisits and half records still to reach.</summary>
    public string RevisitSeed { get; }

    readonly string _priorCorpusPath;

    public WalkCostWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-walkcost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcWalkCostMaster", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var top = master.Npcs.AddNew();
        top.EditorID = "HcWalkTemplateTop";
        var mid = master.Npcs.AddNew();
        mid.EditorID = "HcWalkTemplateMid";
        mid.Template.SetTo(top);
        mid.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Stats;

        for (int i = 0; i < Seeds; i++)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = "HcWalkSeed" + i;
            n.Template.SetTo(mid);
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Stats;
            n.Items = new Noggog.ExtendedList<ContainerEntry>();
            for (int j = 0; j < ItemsPerSeed; j++)
            {
                var item = master.Ammunitions.AddNew();
                item.EditorID = $"HcWalkItem{i}_{j}";
                n.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = item.ToLink(), Count = 1 } });
            }
        }

        // The revisit fan: every hub points at every hub (itself included) and at one terminal of its own, so a
        // closure walk's hop 2 frontier is the hubs again — already visited — ahead of the terminals it can record.
        var hubs = new List<FormList>(Hubs);
        for (int i = 0; i < Hubs; i++)
        {
            var hub = master.FormLists.AddNew();
            hub.EditorID = "HcWalkHub" + i;
            hubs.Add(hub);
        }
        for (int i = 0; i < Hubs; i++)
        {
            foreach (var other in hubs) hubs[i].Items.Add(new FormLink<ISkyrimMajorRecordGetter>(other.FormKey));
            var terminal = master.FormLists.AddNew();
            terminal.EditorID = "HcWalkTerminal" + i;
            hubs[i].Items.Add(new FormLink<ISkyrimMajorRecordGetter>(terminal.FormKey));
        }
        var fanSeed = master.FormLists.AddNew();
        fanSeed.EditorID = "HcWalkFanSeed";
        foreach (var hub in hubs) fanSeed.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(hub.FormKey));
        RevisitSeed = fanSeed.FormKey.ToString();

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "WalkCostMod"));
        master.BeginWrite.ToPath(Path.Combine(mods, "WalkCostMod", MasterName))
              .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+WalkCostMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared walk-cost world. One build per collection.</summary>
public sealed class WalkCostFixture : IDisposable
{
    public WalkCostWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>Its own collection: <c>CorpusRulebook.CorpusPath</c> is a process-global and one world owns it at a time.</summary>
[CollectionDefinition("walk-cost")]
public sealed class WalkCostCollection : ICollectionFixture<WalkCostFixture> { }

/// <summary>
/// What a walk over a big selection costs (#556). A scan-scoped selection feeding the chain walk died with an
/// internal OutOfMemoryException at 2,235 NPC seeds, because the walk read each node's body with the whole-plugin
/// seek #582 took out of the scan and batch lanes: one walk of the winning plugin per seed and per reached node, so
/// the call's cost was the seed count times the plugin's record count.
/// </summary>
[Collection("walk-cost")]
[Trait("tier", "integration")]
public sealed class RecordsWalkCostTests
{
    readonly WalkCostWorld _w;
    public RecordsWalkCostTests(WalkCostFixture f) => _w = f.W;

    LoadOrderService Svc => _w.Svc;
    static readonly string[] Npc = { "NPC_" };
    static RecordsTools.RecordsProject Chain() => new() { form = "chain" };
    static RecordsTools.RecordsProject Fields() => new() { form = "fields", fields = new[] { "EditorID" } };
    RecordsTools.RecordsScope Scope() => new() { names = new[] { _w.MasterName } };

    /// <summary>The template chain, the shape the issue was reported on.</summary>
    static RecordsTools.RecordsWalk TemplateWalk() =>
        new() { follow = "Template", seed_paths = new[] { "Template" } };

    // ---- the cost itself ---------------------------------------------------------------------------

    /// <summary>The seeds are known before the walk starts, so their bodies come from one enumeration per source
    /// plugin. Before, each seed cost a whole-plugin seek of its own.</summary>
    [Fact]
    public void AWalkGathersItsSeedBodiesOncePerPluginNotOncePerSeed()
    {
        var before = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(),
                                            project: Chain(), counts_only: true);
        var seeks = LoadOrderResolver.BodySeeks - before;

        Assert.Contains($"seeds={WalkCostWorld.Seeds + 2}", response);
        // The gather, plus the template report's own re-read of the chain, which the walk no longer pins (#719).
        // That re-read is the CHAIN's length, not the seed count — the reports share one cache — so the claim this
        // test makes is unchanged: a constant, not one walk per seed.
        Assert.True(seeks <= 3, $"{WalkCostWorld.Seeds + 2} walk seeds cost {seeks} per-record plugin walks.");
    }

    /// <summary>Every seed advances one hop together, so a hop's reached nodes are one gather too. Before, a closure
    /// walk paid a whole-plugin seek per distinct node as well as per seed — the same call without seed_paths, which
    /// is how the issue was also reproduced.</summary>
    [Fact]
    public void AWalkGathersEachHopsBodiesTogetherNotOnePerNode()
    {
        var before = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                            walk: new RecordsTools.RecordsWalk { depth = 1 },
                                            project: Chain(), counts_only: true);
        var seeks = LoadOrderResolver.BodySeeks - before;

        Assert.Contains($"reached={WalkCostWorld.Seeds * (WalkCostWorld.ItemsPerSeed + 1) + 1}", response);
        Assert.True(seeks <= 1, $"a closure walk over {WalkCostWorld.Seeds} seeds cost {seeks} per-record plugin walks.");
    }

    /// <summary>What the seeks meant in memory. A walk's allocation now scales with the SEEDS, not with the seeds
    /// times the plugin behind them: 9 MB over these 302 seeds, where the per-node seek spent 240 MB on the same
    /// call and rose with every record added to the plugin. The bound is allocated bytes, which a run reports
    /// exactly — no clock, so no flaky timing.</summary>
    [Fact]
    public void AWalkCostsWithItsSeedsNotWithThePluginBehindThem()
    {
        // The same call once first: the index build and the JIT of everything below are one-time costs of the
        // process, not of a walk, and measuring them would make the number depend on which test ran first.
        string Walk() => RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(),
                                              project: Chain(), counts_only: true);
        Walk();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        Walk();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        const long perSeedCeiling = 128 * 1024;
        var ceiling = perSeedCeiling * (WalkCostWorld.Seeds + 2);
        Assert.True(allocated < ceiling,
                    $"the walk allocated {allocated / 1048576} MB over {WalkCostWorld.Seeds + 2} seeds — past the {ceiling / 1048576} MB this world's seed count allows.");
    }

    /// <summary>A reached node costs its row, not its body. A record getter is a slice of its whole GRUP's byte
    /// array and pins it, so caching every reached node's body until the call ended pinned one array per source
    /// GRUP per plugin — 270 KB a reached node on a real order, and an OOM at a raised budget (#719). Bodies now
    /// live for the gather pass that read them, and the reached set is gone before anything renders.</summary>
    [Fact]
    public void AWalkHoldsNoReachedBodiesPastTheGatherThatReadThem()
    {
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                            walk: new RecordsTools.RecordsWalk { depth = 1 },
                                            project: Chain(), counts_only: true);

        var reached = WalkCostWorld.Seeds * (WalkCostWorld.ItemsPerSeed + 1) + 1;
        Assert.Contains($"reached={reached}", response);
        Assert.Equal(0, LoadOrderService.WalkBodiesHeldAtReturn);
        Assert.True(LoadOrderService.WalkBodyHighWater <= BodyPrefetch.ChunkRows,
                    $"the walk held {LoadOrderService.WalkBodyHighWater} bodies at once — past the {BodyPrefetch.ChunkRows} one gather pass allows.");
    }

    /// <summary>The same walk with a pass small enough to SPLIT — every seed slice and every hop runs several
    /// passes, so the release, the level snapshot, a seed skipped because an earlier one filled the pass, and the
    /// per-pass node budget are all exercised. Building a world with a 2,000-link hop to meet the real pass size is
    /// not a test; the pass size is the knob, the same way the render bound is.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    public void AWalkSplitAcrossPassesReachesTheSameSetAndHoldsOnePass(int passRows)
    {
        var reached = WalkCostWorld.Seeds * (WalkCostWorld.ItemsPerSeed + 1) + 1;
        string Walk() => RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                              walk: new RecordsTools.RecordsWalk { depth = 1 },
                                              project: Chain(), counts_only: true);

        var whole = Walk();
        var split = WithPassRows(passRows, Walk);

        Assert.Equal(whole, split);
        Assert.Contains($"reached={reached}", split);
        Assert.Equal(0, LoadOrderService.WalkBodiesHeldAtReturn);
        Assert.True(LoadOrderService.WalkBodyHighWater <= passRows,
                    $"a walk passing {passRows} keys at a time held {LoadOrderService.WalkBodyHighWater} bodies at once.");
    }

    /// <summary>The node budget is spent across passes, not per pass: a seed capped at one node records one node and
    /// says the cap cut it, whether the hop it cut ran in one pass or twenty.</summary>
    [Fact]
    public void ASeedsNodeBudgetIsSpentAcrossPassesNotPerPass()
    {
        var response = WithPassRows(3, () =>
            RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                 walk: new RecordsTools.RecordsWalk { depth = 2, max_nodes = 1 },
                                 project: Chain(), counts_only: true));

        Assert.Contains($"reached={WalkCostWorld.Seeds + 1}", response);
        Assert.Equal(0, LoadOrderService.WalkBodiesHeldAtReturn);
    }

    /// <summary>A severity 'refuse' still names the first seed in seed order at the shallowest hop, and the pass it
    /// abandoned is released rather than left to the collector, now that a hop is interleaved across passes.</summary>
    [Fact]
    public void ARefusingWalkSplitAcrossPassesRefusesTheSameWayAndHoldsNothing()
    {
        var refuse = new[] { new RecordsTools.RecordsWalkExclusion { match = "Ammunition", severity = "refuse" } };
        string Walk() => RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                              walk: new RecordsTools.RecordsWalk { depth = 1, exclusions = refuse },
                                              project: Chain(), counts_only: true);

        var whole = Walk();
        var split = WithPassRows(5, Walk);

        Assert.StartsWith("error:", whole);
        Assert.Equal(whole, split);
        Assert.Equal(0, LoadOrderService.WalkBodiesHeldAtReturn);
    }

    // ---- the node budget's hard upper bound ---------------------------------------------------------

    /// <summary>The budget has a hard upper bound, so "no cap" cannot be spelled as a huge number and walked into
    /// the ground. The refusal names the bound and what to pass instead.</summary>
    [Fact]
    public void ANodeBudgetPastTheCeilingIsRefusedNamingTheBound()
    {
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                            walk: new RecordsTools.RecordsWalk { max_nodes = RecordsTools.RecordsWalk.Ceiling + 1 },
                                            project: Chain(), counts_only: true);

        Assert.StartsWith("error:", response);
        Assert.Contains("walk.max_nodes=" + (RecordsTools.RecordsWalk.Ceiling + 1), response);
        Assert.Contains(RecordsTools.RecordsWalk.Ceiling.ToString(), response);
    }

    /// <summary>And the bound itself is a legal budget, not one off it — the refusal is above, not at.</summary>
    [Fact]
    public void ANodeBudgetAtTheCeilingWalks()
    {
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                            walk: new RecordsTools.RecordsWalk { depth = 1, max_nodes = RecordsTools.RecordsWalk.Ceiling },
                                            project: Chain(), counts_only: true);

        Assert.DoesNotContain("error:", response);
        Assert.Contains($"reached={WalkCostWorld.Seeds * (WalkCostWorld.ItemsPerSeed + 1) + 1}", response);
    }

    /// <summary>A seed at its node budget reads no further. The gather used to take every seed's whole hop frontier
    /// before the budget was consulted on dequeue, so a walk capped at one node still read — and pinned — every
    /// link off every seed to keep one of them. The budget now rides into the gather, and the cap itself is
    /// unchanged: what is listed is reached and proved, and the truncation note still says so.</summary>
    [Fact]
    public void ACappedSeedGathersNoBodiesPastItsCap()
    {
        var beforeKeys = BodyPrefetch.KeysWanted;
        var beforeSeeks = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, types: Npc, plugins: Scope(),
                                            walk: new RecordsTools.RecordsWalk { depth = 2, max_nodes = 1 },
                                            project: Chain(), counts_only: true);
        var wanted = BodyPrefetch.KeysWanted - beforeKeys;
        var seeks = LoadOrderResolver.BodySeeks - beforeSeeks;

        // Every seed but the top of the chain proves exactly one node, which is what the cap allows.
        Assert.Contains($"reached={WalkCostWorld.Seeds + 1}", response);
        // The seeds themselves, plus at most one body per seed for the one node each may still record. Before the
        // budget rode into the gather this was the seeds plus their whole first frontier — a template link and
        // ItemsPerSeed items each.
        var ceiling = 2 * (WalkCostWorld.Seeds + 2);
        Assert.True(wanted <= ceiling,
                    $"a walk capped at one node per seed asked the gather for {wanted} bodies over {WalkCostWorld.Seeds + 2} seeds — past the {ceiling} its caps allow.");
        // And trimming the gather did not push the walk back onto the per-record seek for what it does read.
        Assert.True(seeks <= 1, $"a capped walk cost {seeks} per-record plugin walks.");
    }

    /// <summary>A key the seed already visited is not gathered. The fill loop screened on the cross-seed set only,
    /// so a hop whose frontier leads with revisits spent the seed's whole remaining budget on bodies the dequeue
    /// throws away — and the nodes the seed did record, sitting past that prefix, fell back on the whole-plugin
    /// seek this walk exists to remove.</summary>
    [Fact]
    public void AHopDoesNotSpendItsGatherOnNodesTheSeedAlreadyVisited()
    {
        var before = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, formids: new[] { _w.RevisitSeed },
                                            walk: new RecordsTools.RecordsWalk { depth = 2, max_nodes = 2 * WalkCostWorld.Hubs },
                                            project: Chain(), counts_only: true);
        var seeks = LoadOrderResolver.BodySeeks - before;

        // The hubs at hop 1, their terminals at hop 2 — the same answer either way; only the cost differed.
        Assert.Contains($"reached={2 * WalkCostWorld.Hubs}", response);
        Assert.True(seeks <= 1,
                    $"a hop whose frontier leads with {WalkCostWorld.Hubs} revisits cost {seeks} per-record plugin walks.");
    }

    // ---- what the reached set costs to RENDER --------------------------------------------------------

    /// <summary>The walk lane returns above the scan's render bound, because the seed count is not the rendered
    /// count. The reached count is, so a reading form that consumes the reached set is measured against the same
    /// bound — otherwise a walk that no longer runs out of memory hands the list lane seeds times walk.max_nodes
    /// bodies with no ceiling at all. The remedy is the walk's own, not the scan's window.</summary>
    [Fact]
    public void AReachedSetTooBigToRenderRefusesNamingTheChainFormAndNarrowerSeeds()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(), project: Fields()));

        Assert.StartsWith("error:", response);
        Assert.Contains("project.form='chain'", response);
        Assert.Contains("walk.max_nodes", response);
        Assert.Contains("walk.depth", response);
        Assert.DoesNotContain("re-scans", response);          // the scan's window is not what moves a walk
        Assert.DoesNotContain("does not combine", response);
    }

    /// <summary>Every reading form the walk description names is held to the bound, not just the three that read
    /// the caller's own field paths: summary and aggregate read a cheap leaf, but they read it off a BODY per row,
    /// so a walk feeding them the reached set costs the same unbounded render the others do.</summary>
    [Theory]
    [InlineData("summary")]
    [InlineData("aggregate")]
    public void EveryReadingFormOverTheReachedSetIsHeldToTheBound(string form)
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(),
                                 project: new RecordsTools.RecordsProject
                                 { form = form, group_by = form == "aggregate" ? "type" : null }));

        Assert.StartsWith("error:", response);
        Assert.Contains("project.form='chain'", response);
    }

    /// <summary>A walk is seeded from the formids= lane as well as from a scan, and there the scan terms are not
    /// part of the call at all — so the refusal names the seeds without them, rather than opening on three
    /// parameters the caller did not pass and could not pass alongside formids=.</summary>
    [Fact]
    public void AFormidsSeededWalkIsRefusedWithoutNamingTheScanTerms()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, formids: new[] { _w.RevisitSeed },
                                 walk: new RecordsTools.RecordsWalk { depth = 2 }, project: Fields()));

        Assert.StartsWith("error:", response);
        Assert.Contains("the seeds you passed", response);
        Assert.Contains("walk.max_nodes", response);
        Assert.DoesNotContain("types=", response);
        Assert.DoesNotContain("plugins=", response);
        Assert.DoesNotContain("where=", response);
    }

    /// <summary>The chain form stays exempt: it renders the walk's own rows and reads no body PER RENDERED ROW, so
    /// the same call under the same bound serves. The walk still reads one per reached node, which is the cost the
    /// bound is not measuring.</summary>
    [Fact]
    public void TheChainFormIsNotHeldToTheBodyRenderBound()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(),
                                 project: Chain(), counts_only: true));

        Assert.DoesNotContain("error:", response);
        Assert.Contains($"seeds={WalkCostWorld.Seeds + 2}", response);
    }

    /// <summary>Under the bound nothing changes: the same reading form serves the set the walk reached.</summary>
    [Fact]
    public void AReachedSetUnderTheBoundRendersAsBefore()
    {
        var response = WithBound(WalkCostWorld.Seeds * 4, () =>
            RecordsTools.Records(Svc, types: Npc, plugins: Scope(), walk: TemplateWalk(), project: Fields()));

        Assert.DoesNotContain("error:", response);
    }

    // ---- and it stops when the client stops waiting -------------------------------------------------

    /// <summary>The engine entry takes the call's token and hands it to the body gather, so a walk cancelled before
    /// it starts stops in the first gather rather than reading every seed. Its entry took no token at all before.
    /// The hop loop's own polls are what <see cref="AWalkStopsBetweenHopsWhenTheClientCancels"/> proves.</summary>
    [Fact]
    public void AWalkCancelledBeforeItStartsStopsInTheSeedGather()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Svc.WalkForwardBatch(Seeds(), new[] { "Template" }, "Template", 16, 2000,
                                 Array.Empty<(string, bool)>(), null, out _, out _, cts.Token));
    }

    /// <summary>The cancel that arrives once the walk is already running: the hop loop polls between seeds and
    /// between hops, like every other loop that reads a body (#582). The token is tripped from inside the walk —
    /// the exclusion list is read once per reached node, so enumerating it cancels after hop 1 has begun — and the
    /// poll at the top of the next seed's turn is what throws.</summary>
    [Fact]
    public void AWalkStopsBetweenHopsWhenTheClientCancels()
    {
        using var cts = new CancellationTokenSource();
        var exclusions = new CancelOnFirstRead(cts);

        Assert.Throws<OperationCanceledException>(() =>
            Svc.WalkForwardBatch(Seeds(), new[] { "Template" }, "Template", 16, 2000,
                                 exclusions, null, out _, out _, cts.Token));
        Assert.True(exclusions.WasRead, "the walk never reached a node, so the hop loop is not what stopped it.");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Run one call with the named-fields render bound moved, restored whatever happens — building
    /// 300,000 reachable records to meet the real one is not a test.</summary>
    static string WithBound(int rows, Func<string> call)
    {
        var prior = RenderBudget.MaxRenderRows;
        RenderBudget.MaxRenderRows = rows;
        try { return call(); }
        finally { RenderBudget.MaxRenderRows = prior; }
    }

    /// <summary>Run one call with the walk's gather pass shrunk, restored whatever happens.</summary>
    static string WithPassRows(int rows, Func<string> call)
    {
        var prior = LoadOrderService.WalkPassRows;
        LoadOrderService.WalkPassRows = rows;
        try { return call(); }
        finally { LoadOrderService.WalkPassRows = prior; }
    }

    /// <summary>Every NPC in the world, as walk seeds.</summary>
    string[] Seeds()
    {
        var q = Svc.CrossQuery(Npc, null, null, false, new[] { _w.MasterName }, null, int.MaxValue);
        Assert.Null(q.Error);
        return q.Keys.Select(k => k.ToString()).ToArray();
    }

    /// <summary>An empty exclusion list that cancels the moment the walk reads it — which the walk does once per
    /// node it reaches, inside the hop loop. No production seam: the parameter is a list and this is a list.</summary>
    sealed class CancelOnFirstRead : IReadOnlyList<(string Match, bool Refuse)>
    {
        readonly CancellationTokenSource _cts;
        public CancelOnFirstRead(CancellationTokenSource cts) => _cts = cts;
        public bool WasRead { get; private set; }
        public int Count => 0;
        public (string Match, bool Refuse) this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<(string Match, bool Refuse)> GetEnumerator()
        {
            WasRead = true;
            _cts.Cancel();
            yield break;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
