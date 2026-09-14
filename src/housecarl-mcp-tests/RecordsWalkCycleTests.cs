using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The plugin from the bug report (#726): a leveled item A whose entries point at A and at B, and B whose entries
/// point back at A — a self-reference and a two-node loop, the two simplest cycles there are. Alongside them an
/// acyclic seed whose two branches meet on one record, so a diamond cannot pass for a loop.
/// </summary>
public sealed class WalkCycleWorld : IDisposable
{
    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The cyclic seed: entries point at itself and at <see cref="LoopPartner"/>.</summary>
    public string SelfAndLoopSeed { get; }
    /// <summary>The record the cyclic seed loops through, whose own entry points back at the seed.</summary>
    public string LoopPartner { get; }
    /// <summary>The acyclic seed: two branches that meet on one shared record and stop.</summary>
    public string DiamondSeed { get; }
    /// <summary>A densely cyclic seed: <see cref="Hubs"/> form lists each pointing at every one of them, itself
    /// included, so the walk finds cycles by the hundred off one seed.</summary>
    public string DenseSeed { get; }

    /// <summary>Hubs in the dense fan. A DFS over n mutually-linking records reports n(n+1)/2 back edges, so this
    /// is set past what it takes to trip the per-seed cycle-search cap.</summary>
    public const int Hubs = 22;

    readonly string _priorCorpusPath;

    public WalkCycleWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-walkcycle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcWalkCycleMaster", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var a = master.LeveledItems.AddNew();
        a.EditorID = "HcCycleA";
        var b = master.LeveledItems.AddNew();
        b.EditorID = "HcCycleB";
        a.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(a.FormKey) } },
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(b.FormKey) } },
        };
        b.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(a.FormKey) } },
        };
        SelfAndLoopSeed = a.FormKey.ToString();
        LoopPartner = b.FormKey.ToString();

        // The acyclic shape: seed -> left and right, both of which point at the same ammo. Two paths to one record
        // is a re-convergence, not a loop, and it is the case a visited-set count of revisits gets wrong.
        var shared = master.Ammunitions.AddNew();
        shared.EditorID = "HcCycleShared";
        var left = master.LeveledItems.AddNew();
        left.EditorID = "HcCycleLeft";
        var right = master.LeveledItems.AddNew();
        right.EditorID = "HcCycleRight";
        foreach (var branch in new[] { left, right })
            branch.Entries = new Noggog.ExtendedList<LeveledItemEntry>
            {
                new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(shared.FormKey) } },
            };
        var diamond = master.LeveledItems.AddNew();
        diamond.EditorID = "HcCycleDiamond";
        diamond.Entries = new Noggog.ExtendedList<LeveledItemEntry>
        {
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(left.FormKey) } },
            new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1, Reference = new FormLink<IItemGetter>(right.FormKey) } },
        };
        DiamondSeed = diamond.FormKey.ToString();

        // The dense fan: every hub points at every hub, itself included. One seed, cycles by the hundred.
        var hubs = new List<FormList>(Hubs);
        for (int i = 0; i < Hubs; i++)
        {
            var hub = master.FormLists.AddNew();
            hub.EditorID = "HcCycleHub" + i;
            hubs.Add(hub);
        }
        foreach (var hub in hubs)
            foreach (var other in hubs) hub.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(other.FormKey));
        var fanSeed = master.FormLists.AddNew();
        fanSeed.EditorID = "HcCycleFanSeed";
        foreach (var hub in hubs) fanSeed.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(hub.FormKey));
        DenseSeed = fanSeed.FormKey.ToString();

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "WalkCycleMod"));
        master.BeginWrite.ToPath(Path.Combine(mods, "WalkCycleMod", MasterName))
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
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+WalkCycleMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared cycle world. One build per collection.</summary>
public sealed class WalkCycleFixture : IDisposable
{
    public WalkCycleWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>Its own collection: <c>CorpusRulebook.CorpusPath</c> is a process-global and one world owns it at a time.</summary>
[CollectionDefinition("walk-cycle")]
public sealed class WalkCycleCollection : ICollectionFixture<WalkCycleFixture> { }

/// <summary>
/// What the chain form says about cycles (#726). A forward closure walk over the reported plugin terminated
/// correctly and reported no cycles at all, because a cycle was only ever recorded on a named-follow walk — the
/// closure walk deduped its revisits on the visited set and said nothing. The walk now records its edges and the
/// cycles are found from them, which is also what tells a loop apart from a diamond.
/// </summary>
[Collection("walk-cycle")]
[Trait("tier", "integration")]
public sealed class RecordsWalkCycleTests
{
    readonly WalkCycleWorld _w;
    public RecordsWalkCycleTests(WalkCycleFixture f) => _w = f.W;

    LoadOrderService Svc => _w.Svc;
    static RecordsTools.RecordsProject Chain => new() { form = "chain" };
    static RecordsTools.RecordsWalk Deep => new() { depth = 1000, max_nodes = 100000 };

    /// <summary>The self-reference and the two-node loop, both named as the loop they are.</summary>
    [Fact]
    public void AForwardWalkReportsASelfReferenceAndATwoNodeLoop()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.SelfAndLoopSeed }, walk: Deep, project: Chain);

        Assert.DoesNotContain("error:", response);
        // A -> A, and A -> B -> A. Each line names its loop and closes on the record it started from.
        Assert.Contains("cycle: ", response);
        Assert.Contains("(HcCycleA) -> ", response);
        Assert.Contains("(HcCycleB) -> ", response);
        var cycleLines = response.Split('\n').Where(l => l.Contains("cycle: ")).ToList();
        Assert.Equal(2, cycleLines.Count);
        Assert.Single(cycleLines, l => l.Split(" -> ").Length == 2 && l.Contains("HcCycleA"));   // A -> A
        Assert.Single(cycleLines, l => l.Split(" -> ").Length == 3 && l.Contains("HcCycleB"));   // A -> B -> A
    }

    /// <summary>The count the form reports, which was 0 on this plugin.</summary>
    [Fact]
    public void TheChainFormCountsTheCyclesItFound()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.SelfAndLoopSeed }, walk: Deep, project: Chain,
                                            counts_only: true);

        Assert.Contains("cycles=2", response);
    }

    /// <summary>A densely cyclic seed must not price its own nodes out of the render. The cycle list is bounded by
    /// nothing but the walked fanout, and reserving it whole beside every node line sent the seed back out with
    /// nothing said — no nodes, no cycles, no cut notice naming the remedy.</summary>
    [Fact]
    public void ADenselyCyclicSeedStillRendersItsNodesAndSaysWhatItHeldBack()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.DenseSeed }, walk: Deep, project: Chain,
                                            max_chars: 4000);

        Assert.DoesNotContain("error:", response);
        Assert.Contains("HcCycleHub", response);                      // its nodes are there
        Assert.Contains("more cycle(s) held back", response);         // and it says what did not fit
        Assert.DoesNotContain("rendered 0 of 1 seeds", response);
    }

    /// <summary>The json lane owns the same unbounded list and had no guard: it wrote every cycle whole, so one
    /// oversized row returned past max_chars with truncated=false — no artifact written, and nothing saying
    /// anything had been left out.</summary>
    [Fact]
    public void AJsonChainRowHoldsItsCyclesToMaxCharsAndSaysTheResponseWasCut()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.DenseSeed }, walk: Deep, project: Chain,
                                            format: "json", max_chars: 4000);

        using var doc = JsonDocument.Parse(response);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        var cycles = doc.RootElement.GetProperty("rows")[0].GetProperty("cycles");
        Assert.Contains(cycles.EnumerateArray(), e => e.GetString()!.Contains("more cycle(s) held back"));
    }

    /// <summary>The search itself is capped, and a seed that hit the cap says so rather than passing a stopped
    /// search off as a finished one — the count is a floor, and this is when the reader must know it.</summary>
    [Fact]
    public void ASeedWhoseCycleSearchHitItsCapSaysSo()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.DenseSeed }, walk: Deep, project: Chain,
                                            counts_only: true);

        Assert.Contains($"cycles={LoadOrderService.WalkCycleCap}", response);
        Assert.Contains($"{LoadOrderService.WalkCycleCap}-cycle search cap", response);
    }

    /// <summary>counts_only lists no seed, so it must not tell the reader to look under one.</summary>
    [Fact]
    public void ACountsOnlyChainDoesNotPointAtSeedsItDoesNotList()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.SelfAndLoopSeed }, walk: Deep, project: Chain,
                                            counts_only: true);

        Assert.Contains("cycles=2", response);
        Assert.DoesNotContain("listed under its seed", response);
    }

    /// <summary>"None means none" holds only over a walk that finished. A seed cut at its depth cap reports
    /// cycles=0 for a graph it never read to the end, and zero is exactly the answer that reads as proof — so the
    /// cut is stated beside the count, on counts_only too, where no seed row carries the cap note.</summary>
    [Fact]
    public void ACutWalkDoesNotLetItsZeroCycleCountReadAsProofOfNone()
    {
        var response = RecordsTools.Records(Svc, formids: new[] { _w.DenseSeed },
                                            walk: new RecordsTools.RecordsWalk { depth = 1 },
                                            project: Chain, counts_only: true);

        Assert.Contains("cycles=0", response);
        Assert.Contains("not proof of none", response);
    }

    /// <summary>Two paths to one record is a re-convergence, not a loop: an acyclic walk still reports none.</summary>
    [Fact]
    public void AWalkWithNoLoopReportsNoCycles()
    {
        var counts = RecordsTools.Records(Svc, formids: new[] { _w.DiamondSeed }, walk: Deep, project: Chain,
                                          counts_only: true);
        Assert.Contains("cycles=0", counts);

        var response = RecordsTools.Records(Svc, formids: new[] { _w.DiamondSeed }, walk: Deep, project: Chain);
        Assert.DoesNotContain("error:", response);
        Assert.DoesNotContain("cycle: ", response);
    }
}
