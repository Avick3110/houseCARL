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
    public const int OffOrderWeapons = 12;

    /// <summary>A second population, spread one small plugin at a time: the gather's unit is a PLUGIN, so a claim
    /// about which plugins a render walks needs rows whose winners live in more than one. Its own record type, so
    /// the weapon counts every other test pins stay what they were.</summary>
    public const int Spread = 12;
    public const int AmmoPerPlugin = 5;

    public string Root { get; }
    public string MasterName { get; }

    /// <summary>The spread plugins' names, in load order.</summary>
    public IReadOnlyList<string> SpreadNames { get; }

    /// <summary>A plugin in a switched-OFF mod folder: on disk, locatable, outside the load order. The off-order
    /// scan lane has its own cancellation path and its own catch-all, so it needs a world to run in.</summary>
    public string OffOrderName { get; }

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

        var offKey = new ModKey("HcCostOff", ModType.Plugin);
        OffOrderName = offKey.FileName.String;
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < OffOrderWeapons; i++)
        {
            var w = off.Weapons.AddNew();
            w.EditorID = "HcOffSword" + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(5 + i), Weight = 1 };
        }

        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "CostMasterMod"));
        master.BeginWrite.ToPath(Path.Combine(mods, "CostMasterMod", MasterName))
              .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var spread = new List<string>();
        for (int p = 0; p < Spread; p++)
        {
            var key = new ModKey("HcCostSpread" + p, ModType.Plugin);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            for (int a = 0; a < AmmoPerPlugin; a++)
            {
                var ammo = mod.Ammunitions.AddNew();
                ammo.EditorID = $"HcCostArrow{p}_{a}";
                ammo.Name = $"Cost Arrow {p}-{a}";
            }
            var folder = "CostSpreadMod" + p;
            Directory.CreateDirectory(Path.Combine(mods, folder));
            mod.BeginWrite.ToPath(Path.Combine(mods, folder, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            spread.Add(key.FileName.String);
        }
        SpreadNames = spread;
        Directory.CreateDirectory(Path.Combine(mods, "CostOffMod"));
        off.BeginWrite.ToPath(Path.Combine(mods, "CostOffMod", OffOrderName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var active = new[] { MasterName }.Concat(SpreadNames).ToList();
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", active) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", active.Select(n => "*" + n)) + "\r\n");
        // modlist.txt is read bottom-up: the master's mod last, so it stays lowest in the order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n-CostOffMod\r\n"
            + string.Join("", Enumerable.Range(0, Spread).Reverse().Select(p => $"+CostSpreadMod{p}\r\n"))
            + "+CostMasterMod\r\n");

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
    static readonly string[] Ammo = { "AMMO" };
    static readonly string[] Paths = { "EditorID", "Name", "BasicStats.Damage" };

    static RecordsTools.RecordsProject Fields() => new() { form = "fields", fields = Paths };

    /// <summary>Every weapon in the world as formids, for the list lane's own reads.</summary>
    string[] AllWeaponIds => Svc.CrossQuery(Weap, null, null, false, null, null, RenderCostWorld.Weapons)
                                .Keys.Select(k => k.ToString()).ToArray();
    static RecordsTools.RecordsProject Everything() => new() { form = "everything" };

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

    /// <summary>A plugin is walked for the rows the render REACHES, not for the whole chunk. The gather's unit is a
    /// plugin and its chunk is 2,000 rows — bigger than the default 500-row window — so gathering the chunk up front
    /// walked every plugin the selection touched, including the ones whose rows a max_chars cut never reached, and
    /// each of those walks costs a full enumeration of that plugin.</summary>
    [Fact]
    public void AChunkWalksOnlyThePluginsTheRenderedRowsCameFrom()
    {
        var q = Svc.CrossQuery(Ammo, null, null, false, null, null, RenderCostWorld.Spread * RenderCostWorld.AmmoPerPlugin);
        Assert.Null(q.Error);
        Assert.Equal(RenderCostWorld.Spread * RenderCostWorld.AmmoPerPlugin, q.Keys.Count);

        var before = LoadOrderResolver.CollectPasses;
        using var reader = new ScanDetailReader(Svc, q, new[] { "EditorID" }, 1, false, false, null, null, default);
        for (int i = 0; i < RenderCostWorld.AmmoPerPlugin; i++) Assert.Null(reader.Row(i).Error);   // one plugin's rows
        var walks = LoadOrderResolver.CollectPasses - before;

        Assert.True(walks <= 1, $"reading {RenderCostWorld.AmmoPerPlugin} rows from one plugin walked {walks} plugins.");
    }

    /// <summary>The BODY lane pays the same. <c>project.form='everything'</c> takes the batch read rather than the
    /// scan render, and that lane fetched each row's body with a whole-plugin seek of its own — the very cost this
    /// issue is about, at the same scale, on the shape a whole-order catalogue is most likely to ask for.</summary>
    [Fact]
    public void AnEverythingRenderReadsEachPluginOnceNotOncePerRow()
    {
        var before = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, types: Weap, format: "json", limit: RenderCostWorld.Weapons,
                                            project: Everything(), max_chars: 4_000_000);
        var seeks = LoadOrderResolver.BodySeeks - before;

        Assert.Equal(RenderCostWorld.Weapons, Doc(response).GetProperty("rendered").GetInt32());
        Assert.True(seeks <= 1, $"{RenderCostWorld.Weapons} rows of form='everything' cost {seeks} per-record plugin walks.");
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

    /// <summary>The body lane reports its cost too: it reads a body per row exactly as the scan render does, and
    /// the bound is one number over both lanes, so a caller checking the estimate has to be able to see both.</summary>
    [Fact]
    public void TheEverythingAccountingReportsWhatReadingItsBodiesCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", limit: 10, project: Everything()));
        Assert.Equal(10, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>The shape that renders the MOST rows reports what they cost: a to_file= call renders every selected
    /// row into the artifact, so the number comes off that write rather than off an inline loop that never ran.</summary>
    [Fact]
    public void AToFileCallReportsWhatWritingItsRowsCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", project: Fields(),
                                           to_file: _w.Scratch("cost-manifest.jsonl")));
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rendered_to_file").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>The accounting line comes out of the row budget rather than being appended past it — the reserve
    /// the render holds back has to cover the line at its widest, or holding it back proves nothing.</summary>
    [Fact]
    public void TheAccountingLineIsReservedFromTheRowBudget()
    {
        Assert.True(RenderBudget.AccountingLine(int.MaxValue, long.MaxValue).Length <= RenderBudget.AccountingReserve);
        Assert.True(RenderBudget.BodiesLine(int.MaxValue, long.MaxValue).Length <= RenderBudget.AccountingReserve);
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

    /// <summary>The body lane has a bound of its OWN, because its row is a whole record: measured at ~30 ms against
    /// ~0.013 ms for a three-field projection on the same world, so one number over both lanes would either wave
    /// this one through or refuse the cheap one for nothing. The refusal names the move between them.</summary>
    [Fact]
    public void AnEverythingRenderIsBoundedByItsOwnWholeRecordCost()
    {
        var response = WithWholeRecordBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, limit: RenderCostWorld.Weapons, project: Everything()));

        Assert.StartsWith("error:", response);
        Assert.Contains("WHOLE record body", response);
        Assert.Contains("project.form='fields'", response);
    }

    /// <summary>And the two bounds are separate in the other direction: an 'everything' selection that fits its own
    /// bound is served, however tight the named-fields bound is set.</summary>
    [Fact]
    public void TheFieldsBoundDoesNotRefuseAnEverythingRenderThatFitsItsOwn()
    {
        var doc = Doc(WithBound(1, () =>
            RecordsTools.Records(Svc, types: Weap, format: "json", limit: 5, project: Everything())));
        Assert.Equal(5, doc.GetProperty("rendered").GetInt32());
    }

    /// <summary>The <c>formids=</c> lane is held to the bound as well, and it is the lane that most needs it: a
    /// list is any length by construction, and re-entering an artifact as <c>formids=["@&lt;file&gt;"]</c> is what the
    /// whole-record refusal and the <c>to_file=</c> description send the caller to do.</summary>
    [Fact]
    public void AFormidsReadOverTheBoundRefuses()
    {
        var response = WithWholeRecordBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds, project: Everything()));

        Assert.StartsWith("error:", response);
        Assert.Contains("formids=", response);
        Assert.DoesNotContain("narrow the scan terms", response);   // this lane has none to narrow
    }

    /// <summary>And <c>counts_only</c> does not exempt it: this lane reads a body per id whatever it renders, so
    /// the census costs exactly what the render does — unlike the scan lane, whose census reads no bodies.</summary>
    [Fact]
    public void AFormidsCensusIsHeldToTheBoundBecauseItStillReadsEveryBody()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds, project: Fields(), counts_only: true));

        Assert.StartsWith("error:", response);
    }

    /// <summary>Every READING form on this lane is held to it, not only the ones that name fields: summary routes
    /// through the same batch read and takes one cheap leaf off each body, so a list over the bound refuses on it
    /// exactly as it does on fields (#607).</summary>
    [Fact]
    public void AFormidsSummaryReadOverTheBoundRefuses()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds,
                                 project: new RecordsTools.RecordsProject { form = "summary" }));

        Assert.StartsWith("error:", response);
        Assert.Contains("fewer formids=", response);                // this lane's own lever
        Assert.DoesNotContain("narrow the scan terms", response);   // it has no scan terms
    }

    /// <summary>And the aggregate form, which reads the same leaf per id before it counts anything.</summary>
    [Fact]
    public void AFormidsAggregateReadOverTheBoundRefuses()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds,
                                 project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }));

        Assert.StartsWith("error:", response);
        Assert.Contains("fewer formids=", response);
        Assert.DoesNotContain("narrow the scan terms", response);
    }

    // ---- what the formids= lane says its bodies cost ------------------------------------------------

    /// <summary>This lane reads a body for every id and then renders a limit=/offset= window of them, so the count
    /// beside the cost is the LIST's, not the window's — the number the bound is measured against (#607).</summary>
    [Fact]
    public void AWindowedFormidsRenderReportsTheWholeListItRead()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", limit: 5, project: Fields()));
        Assert.Equal(5, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>The text transport states the same count.</summary>
    [Fact]
    public void AWindowedFormidsTextRenderReportsTheWholeListItRead()
    {
        var text = RecordsTools.Records(Svc, formids: AllWeaponIds, limit: 5, project: Fields());
        Assert.Contains($"read {RenderCostWorld.Weapons} record bodies in ", text);
        Assert.Contains(" ms", text);
    }

    /// <summary>summary is this lane's DEFAULT form and reads a body per id like the rest, so it reports what those
    /// bodies cost — a caller who was refused and sliced the list has the number to check the slice against.</summary>
    [Fact]
    public void AFormidsSummaryRenderReportsWhatItsBodiesCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", limit: 5));
        Assert.Equal(5, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>And on text.</summary>
    [Fact]
    public void AFormidsSummaryTextRenderReportsWhatItsBodiesCost()
    {
        var text = RecordsTools.Records(Svc, formids: AllWeaponIds, limit: 5);
        Assert.Contains($"read {RenderCostWorld.Weapons} record bodies in ", text);
    }

    /// <summary>The aggregate form reads the same leaf per id before it counts anything, so it states the same
    /// cost — on json and on text alike.</summary>
    [Fact]
    public void AFormidsAggregateRenderReportsWhatItsBodiesCost()
    {
        var agg = new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" };
        var doc = Doc(RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", project: agg));
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);

        var text = RecordsTools.Records(Svc, formids: AllWeaponIds, project: agg);
        Assert.Contains($"read {RenderCostWorld.Weapons} record bodies in ", text);
    }

    /// <summary>The bound is on what a row costs, not on where the row came from: an OFF-ORDER selection over it
    /// refuses before reading a body, the same as the in-order lane. Its rows read a body per row off a file
    /// outside the order, and the limit= description states the bound with no lane attached.</summary>
    [Fact]
    public void AnOffOrderRenderOverTheBoundRefusesToo()
    {
        var response = WithWholeRecordBound(2, () =>
            RecordsTools.Records(Svc, types: Weap, source: Pole(_w.OffOrderName), project: Everything()));

        Assert.StartsWith("error:", response);
        Assert.Contains("WHOLE record body", response);
    }

    /// <summary>And it reports what those bodies cost, like every other body lane — a bound calibrated on one
    /// machine is only checkable where the truth comes back.</summary>
    [Fact]
    public void AnOffOrderBodyRenderReportsWhatItsBodiesCost()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, source: Pole(_w.OffOrderName), format: "json",
                                           project: Everything()));
        Assert.Equal(RenderCostWorld.OffOrderWeapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>A walk is measured on what it RENDERS — the set it reached — and not on the size of the scan that
    /// seeded it, which is a different count and a different lane. So the refusal that comes back is the walk
    /// lane's, naming the walk's own levers, and never the scan window's.</summary>
    [Fact]
    public void AWalkIsMeasuredOnWhatItReachedAndNotOnItsSeedScan()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, walk: new RecordsTools.RecordsWalk(), project: Fields()));

        Assert.StartsWith("error:", response);
        Assert.Contains("project.form='chain'", response);
        Assert.DoesNotContain("re-scans", response);          // the scan's window is not what moves a walk
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

    /// <summary>The body lane stops on a cancel as well. It resolves its rows through the batch read rather than the
    /// scan render, and that read polled a token no caller supplied — so <c>form='everything'</c> carried on after
    /// the client had given up.</summary>
    [Fact]
    public void ACancelStopsAnEverythingRenderToo()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string? served = null;
        var ex = Record.Exception(() =>
            served = RecordsTools.Records(Svc, types: Weap, project: Everything(), limit: RenderCostWorld.Weapons, ct: cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Null(served);
    }

    /// <summary>The batch read the body forms take stops inside one record on a cancel. It polled a token no caller
    /// supplied, so <c>form='everything'</c> and <c>form='rows'</c> — which read their bodies here, not through the
    /// scan render — carried on after the client had given up.</summary>
    [Fact]
    public void ABatchBodyReadStopsWhenTheClientCancels()
    {
        var ids = Svc.CrossQuery(Weap, null, null, false, null, null, RenderCostWorld.Weapons)
                     .Keys.Select(k => k.ToString()).ToList();
        Assert.Equal(RenderCostWorld.Weapons, ids.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Svc.ResolveBatch(ids, null, false, ct: cts.Token));
    }

    /// <summary>A cancelled OFF-ORDER scan finishes as a cancellation, not as a refusal saying the file could not be
    /// read: the file is perfectly readable, and naming it as the fault sends the caller after nothing.</summary>
    [Fact]
    public void ACancelledOffOrderScanIsNotReportedAsAFileFault()
    {
        var pole = Svc.ProbeSourceArm(_w.OffOrderName, null, out var perr);
        Assert.Null(perr);
        Assert.False(pole!.InOrder);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Record.Exception(() =>
            Svc.OffOrderQuery(pole, Weap, null, null, null, false, null, 100, null, 0, null, null, ct: cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>The SkyPatcher overlay read stops on a cancel as well. It replays each winner through the INI layer
    /// and reads its whole body, and it was the one body lane in this tool still without the token — so whether a
    /// call stopped depended on which <c>source=</c> arm it took.</summary>
    [Fact]
    public void ACancelStopsTheOverlayPostRead()
    {
        var ids = AllWeaponIds;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string? served = null;
        var ex = Record.Exception(() =>
            served = RecordsTools.Records(Svc, formids: ids, source: Overlay("post"), project: Everything(),
                                          ct: cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Null(served);
    }

    /// <summary>The off-order lane's BODY read stops on a cancel as well, and finishes as a cancellation rather
    /// than as a refusal blaming the file. The scan honoured the token; the read after it — the half that
    /// materialises each record — polled nothing and ran to the end of the selection.</summary>
    [Fact]
    public void ACancelStopsTheOffOrderBodyRead()
    {
        var pole = Svc.ProbeSourceArm(_w.OffOrderName, null, out var perr);
        Assert.Null(perr);
        var ids = Svc.OffOrderQuery(pole!, Weap, null, null, null, false, null, 100, null, 0, null, null)
                     .Keys.Select(k => k.ToString()).ToList();
        Assert.Equal(RenderCostWorld.OffOrderWeapons, ids.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Svc.ResolveBatchFromPole(ids, _w.OffOrderName, null, null, 1, false, null,
                                     out _, out _, out _, ct: cts.Token));
    }

    /// <summary>A cancelled call leaves nothing in the RESULTS directory either — the auto-spill path, whose name is
    /// reserved on disk by <c>ResultsStore.NextPath</c> before the write. (The cancel this asserts lands in the
    /// render; a cancel landing inside the spill write itself is a window no test can time, and is covered by the
    /// release the spill now makes on the way out.)</summary>
    [Fact]
    public void ACancelledCallLeavesNothingInTheResultsDirectory()
    {
        var dir = ResultsStore.Dir;
        var before = Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            RecordsTools.Records(Svc, types: Weap, limit: RenderCostWorld.Weapons, project: Fields(),
                                 max_chars: 600, ct: cts.Token));

        var after = Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;
        Assert.Equal(before, after);
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

    /// <summary>The same for the whole-record lane's own bound.</summary>
    static string WithWholeRecordBound(int rows, Func<string> call)
    {
        var prior = RenderBudget.MaxWholeRecordRows;
        RenderBudget.MaxWholeRecordRows = rows;
        try { return call(); }
        finally { RenderBudget.MaxWholeRecordRows = prior; }
    }

    /// <summary>A bare plugin-name source pole.</summary>
    static System.Text.Json.JsonElement Pole(string name) =>
        System.Text.Json.JsonDocument.Parse("\"" + name + "\"").RootElement.Clone();

    /// <summary>The SkyPatcher overlay pole at a named state.</summary>
    static System.Text.Json.JsonElement Overlay(string state) =>
        System.Text.Json.JsonDocument.Parse("{\"overlay\": \"skypatcher\", \"state\": \"" + state + "\"}")
                        .RootElement.Clone();

    static System.Text.Json.JsonElement Doc(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}
