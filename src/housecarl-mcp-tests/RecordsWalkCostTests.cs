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
        Assert.True(seeks <= 1, $"{WalkCostWorld.Seeds + 2} walk seeds cost {seeks} per-record plugin walks.");
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
