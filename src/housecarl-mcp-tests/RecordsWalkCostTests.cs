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

    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

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

    // ---- and it stops when the client stops waiting -------------------------------------------------

    /// <summary>The walk's hop loop honours the call's cancellation token, like every other loop that reads a body
    /// (#582). Its engine entry took no token at all before.</summary>
    [Fact]
    public void AWalkStopsWhenTheClientCancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var q = Svc.CrossQuery(Npc, null, null, false, new[] { _w.MasterName }, null, int.MaxValue);
        Assert.Null(q.Error);
        var seeds = q.Keys.Select(k => k.ToString()).ToArray();

        Assert.Throws<OperationCanceledException>(() =>
            Svc.WalkForwardBatch(seeds, new[] { "Template" }, "Template", 16, 2000,
                                 Array.Empty<(string, bool)>(), null, out _, out _, cts.Token));
    }
}
