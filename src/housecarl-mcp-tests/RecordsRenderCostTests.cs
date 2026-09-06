using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A one-plugin world with enough records that a per-row cost is visible: 60 weapons in one master. The shared
/// worlds carry a handful of records each and every count in their tests is pinned to that handful, so a world
/// whose point is VOLUME is its own.
/// </summary>
public sealed class RenderCostWorld : IDisposable
{
    public const int Weapons = 60;

    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    readonly string _priorCorpusPath;

    public RenderCostWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-rendercost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcCostMaster", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Weapons; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = "HcCostSword" + i;
            w.Name = "Cost Sword " + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
        }

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "CostMasterMod"));
        master.BeginWrite.ToPath(Path.Combine(mods, "CostMasterMod", MasterName))
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
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+CostMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public string Scratch(string name)
    {
        var dir = Path.Combine(Root, "scratch");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The shared render-cost world. One build per collection.</summary>
public sealed class RenderCostFixture : IDisposable
{
    public RenderCostWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>Its own collection, for the reason every world here has one: <c>CorpusRulebook.CorpusPath</c> is a
/// process-global and only one world may own it at a time.</summary>
[CollectionDefinition("render-cost")]
public sealed class RenderCostCollection : ICollectionFixture<RenderCostFixture> { }

/// <summary>
/// What a <c>housecarl_records</c> scan's RENDER costs, what it says about that cost, and what a client abort does
/// to it (#582).
///
/// <para>The scan terms bound the scan; every rendered row of a body form then reads a record body, and that half
/// was neither cheap, declared, nor stoppable: a whole-overlay seek per row, no cost in the accounting, no bound,
/// and no cancellation.</para>
/// </summary>
[Collection("render-cost")]
[Trait("tier", "integration")]
public sealed class RecordsRenderCostTests
{
    readonly RenderCostWorld _w;
    public RecordsRenderCostTests(RenderCostFixture f) => _w = f.W;

    LoadOrderService Svc => _w.Svc;
    static readonly string[] Weap = { "WEAP" };
    static readonly string[] Paths = { "EditorID", "Name", "BasicStats.Damage" };

    static RecordsTools.RecordsProject Fields() => new() { form = "fields", fields = Paths };

    // ---- the cost itself ---------------------------------------------------------------------------

    /// <summary>A detail render maps each plugin ONCE for the whole call. Before the render shared one session and
    /// gathered bodies per plugin, it opened an overlay per ROW — the count scaled with rows, not with plugins, and
    /// each open was followed by a whole-overlay seek for the one record.</summary>
    [Fact]
    public void ADetailRenderOpensOneOverlayPerPluginNotPerRow()
    {
        var before = LoadOrderResolver.SessionOverlayOpens;
        var response = RecordsTools.Records(Svc, types: Weap, format: "dense", limit: RenderCostWorld.Weapons,
                                            project: Fields(), max_chars: 4_000_000);
        var opens = LoadOrderResolver.SessionOverlayOpens - before;

        Assert.Equal(RenderCostWorld.Weapons, Doc(response).GetProperty("rendered").GetInt32());
        Assert.True(opens <= 1, $"one plugin in the order and {RenderCostWorld.Weapons} rendered rows cost {opens} overlay opens.");
    }

    // ---- what the response says the cost was -------------------------------------------------------

    [Fact]
    public void TheDenseAccountingReportsTheRowsRenderedAndWhatTheRenderCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "dense", limit: 10, project: Fields()));
        Assert.Equal(10, doc.GetProperty("rendered").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    [Fact]
    public void TheJsonAccountingReportsTheRowsRenderedAndWhatTheRenderCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", limit: 10, project: Fields()));
        Assert.Equal(10, doc.GetProperty("rendered").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    [Fact]
    public void TheTextAccountingReportsTheRowsRenderedAndWhatTheRenderCost()
    {
        var text = RecordsTools.Records(Svc, types: Weap, limit: 10, project: Fields());
        Assert.Contains("rendered 10 rows in ", text);
        Assert.Contains(" ms", text);
    }

    // ---- the bound ---------------------------------------------------------------------------------

    /// <summary>Over the bound the call refuses BEFORE reading a body, and the sentence carries the three things a
    /// caller needs to pick a shape that fits: narrow the scan, window it, or write the whole set — with the two
    /// caveats that decide between the last two.</summary>
    [Fact]
    public void ARenderOverTheBoundRefusesNamingTheWindowAndTheWholeSetShapes()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, limit: RenderCostWorld.Weapons, project: Fields()));

        Assert.StartsWith("error:", response);
        Assert.Contains("limit=", response);
        Assert.Contains("offset=", response);
        Assert.Contains("to_file=", response);
        Assert.Contains("re-scans", response);          // why a deep window costs more
        Assert.Contains("does not combine", response);  // to_file= and offset= are exclusive
    }

    /// <summary>The bound is on the RENDER, so a to_file= call — which renders every selected row into the
    /// artifact — is held to it too, and refuses without writing the file.</summary>
    [Fact]
    public void AToFileCallOverTheBoundRefusesAndWritesNoArtifact()
    {
        var path = _w.Scratch("over-bound.jsonl");
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, project: Fields(), to_file: path));

        Assert.StartsWith("error:", response);
        Assert.False(File.Exists(path), "the refused call wrote its artifact anyway");
    }

    /// <summary>Under the bound nothing changes: the same call serves its rows.</summary>
    [Fact]
    public void ARenderUnderTheBoundIsUntouched()
    {
        var doc = Doc(WithBound(RenderCostWorld.Weapons, () =>
            RecordsTools.Records(Svc, types: Weap, format: "json", limit: RenderCostWorld.Weapons, project: Fields())));
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rendered").GetInt32());
    }

    /// <summary>The census reads no bodies, so it is not bound by the render's cost.</summary>
    [Fact]
    public void TheCensusIsNotHeldToTheRenderBound()
    {
        var response = WithBound(1, () =>
            RecordsTools.Records(Svc, types: Weap, format: "json", project: Fields(), counts_only: true));
        Assert.Equal(RenderCostWorld.Weapons, Doc(response).GetProperty("total").GetInt32());
    }

    // ---- cancellation ------------------------------------------------------------------------------

    /// <summary>A client abort stops the render inside one row: rows before the cancel are read, and the very next
    /// one raises rather than carrying on to the end of the selection.</summary>
    [Fact]
    public void ACancelStopsTheRenderWithinOneRow()
    {
        using var cts = new CancellationTokenSource();
        var q = Svc.CrossQuery(Weap, null, null, false, null, null, RenderCostWorld.Weapons);
        Assert.Null(q.Error);

        using var reader = new ScanDetailReader(Svc, q, Paths, 1, false, false, null, null, cts.Token);
        for (int i = 0; i < 5; i++) Assert.Null(reader.Row(i).Error);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Row(5));
    }

    /// <summary>A cancelled to_file= call leaves nothing behind: the rows exist only in the writer's buffer until
    /// the artifact is saved in one atomic move, so a stop before that writes no file at all — never a partial
    /// artifact that would pass for a whole one on re-entry.</summary>
    [Fact]
    public void ACancelledToFileCallLeavesNoArtifact()
    {
        var path = _w.Scratch("cancelled.jsonl");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            RecordsTools.Records(Svc, types: Weap, project: Fields(), to_file: path, ct: cts.Token));
        Assert.False(File.Exists(path), "a cancelled call left an artifact on disk");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    /// <summary>The tool body's own guard hands a real cancellation on rather than naming it an internal failure —
    /// otherwise a client that aborted would get a bug report instead of its own cancel.</summary>
    [Fact]
    public void ACancelIsNotReportedAsAnInternalFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string? served = null;
        var ex = Record.Exception(() =>
            served = RecordsTools.Records(Svc, types: Weap, project: Fields(), limit: RenderCostWorld.Weapons, ct: cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Null(served);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Run one call with the render bound moved, restored whatever happens — building 300,000 records to
    /// reach the real one is not a test.</summary>
    static string WithBound(int rows, Func<string> call)
    {
        var prior = RenderBudget.MaxRenderRows;
        RenderBudget.MaxRenderRows = rows;
        try { return call(); }
        finally { RenderBudget.MaxRenderRows = prior; }
    }

    static System.Text.Json.JsonElement Doc(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}
