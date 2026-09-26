using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
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

    /// <summary>Keywords on every weapon, so a rows-form fold has this many lines to fold per body.</summary>
    public const int KeywordsPerWeapon = 3;

    /// <summary>A second population, spread one small plugin at a time: the gather's unit is a PLUGIN, so a claim
    /// about which plugins a render walks needs rows whose winners live in more than one. Its own record type, so
    /// the weapon counts every other test pins stay what they were.</summary>
    public const int Spread = 12;
    public const int AmmoPerPlugin = 5;

    /// <summary>A CONTESTED population: armors the master defines and every overrider below re-states, so a tree
    /// row has a provider stack rather than one node, and the same stack row after row. A tree's cost is per
    /// (row, provider), so that is what a gather claim needs to stand in.</summary>
    public const int Contested = 40;
    public const int Overriders = 4;

    /// <summary>How many of the contested armors the TOP overrider re-states. The rest are won by the one below it,
    /// so that plugin is the winner for some rows of a chunk and a lower provider for others — the shape a real
    /// order is full of, and the one a per-role walk pays twice for.</summary>
    public const int TopCovers = 20;

    public string Root { get; }
    public string MasterName { get; }

    /// <summary>The spread plugins' names, in load order.</summary>
    public IReadOnlyList<string> SpreadNames { get; }

    /// <summary>The overriding plugins' names, in load order (the last one wins the armors it re-states).</summary>
    public IReadOnlyList<string> OverriderNames { get; }

    /// <summary>The contested armors the TOP overrider re-states, in creation order, as FormID tokens — so armor
    /// n of this list carries Value base 100+n.</summary>
    public IReadOnlyList<string> TopCoveredIds { get; }

    /// <summary>The contested armors it does NOT, in creation order; armor n here carries base 100+TopCovers+n.</summary>
    public IReadOnlyList<string> PlainContestedIds { get; }

    /// <summary>A plugin in a switched-OFF mod folder: on disk, locatable, outside the load order. The off-order
    /// scan lane has its own cancellation path and its own catch-all, so it needs a world to run in.</summary>
    public string OffOrderName { get; }

    public LoadOrderService Svc { get; }

    public RenderCostWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-rendercost-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcCostMaster", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        // A list field with more than one element per record, so a rows-form fold has something to fold — the seam
        // between "bodies read" and "lines rendered".
        var kwds = new List<FormKey>();
        for (int k = 0; k < KeywordsPerWeapon; k++)
        {
            var kw = master.Keywords.AddNew();
            kw.EditorID = "HcCostKeyword" + k;
            kwds.Add(kw.FormKey);
        }
        for (int i = 0; i < Weapons; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = "HcCostSword" + i;
            w.Name = "Cost Sword " + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
            w.Keywords = Linked(kwds);
        }

        var contested = new List<IArmorGetter>();
        for (int i = 0; i < Contested; i++)
        {
            var a = master.Armors.AddNew();
            a.EditorID = "HcCostArmor" + i;
            a.Name = "Cost Armor " + i;
            a.Value = (uint)(100 + i);
            contested.Add(a);
        }

        var offKey = new ModKey("HcCostOff", ModType.Plugin);
        OffOrderName = offKey.FileName.String;
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        var offKwds = new List<FormKey>();
        for (int k = 0; k < KeywordsPerWeapon; k++)
        {
            var kw = off.Keywords.AddNew();
            kw.EditorID = "HcOffKeyword" + k;
            offKwds.Add(kw.FormKey);
        }
        for (int i = 0; i < OffOrderWeapons; i++)
        {
            var w = off.Weapons.AddNew();
            w.EditorID = "HcOffSword" + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(5 + i), Weight = 1 };
            w.Keywords = Linked(offKwds);
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

        // Each overrider re-states every contested armor, so all Contested rows share one provider stack of
        // Overriders+1 plugins — one plugin walk each for a whole chunk of rows, or one walk per (row, provider)
        // without the gather.
        var overriders = new List<string>();
        for (int op = 0; op < Overriders; op++)
        {
            var key = new ModKey("HcCostOver" + op, ModType.Plugin);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            // The top overrider states only the first TopCovers, so the one below it WINS the rest.
            foreach (var a in (op == Overriders - 1 ? contested.Take(TopCovers) : contested))
            {
                var ov = mod.Armors.GetOrAddAsOverride(a);
                ov.Value = (uint)(1000 * (op + 1)) + a.Value;
            }
            var folder = "CostOverMod" + op;
            Directory.CreateDirectory(Path.Combine(mods, folder));
            mod.BeginWrite.ToPath(Path.Combine(mods, folder, key.FileName.String))
               .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();
            overriders.Add(key.FileName.String);
        }
        OverriderNames = overriders;
        TopCoveredIds = contested.Take(TopCovers).Select(a => a.FormKey.ToString()).ToList();
        PlainContestedIds = contested.Skip(TopCovers).Select(a => a.FormKey.ToString()).ToList();

        Directory.CreateDirectory(Path.Combine(mods, "CostOffMod"));
        off.BeginWrite.ToPath(Path.Combine(mods, "CostOffMod", OffOrderName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        var active = new[] { MasterName }.Concat(SpreadNames).Concat(OverriderNames).ToList();
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", active) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", active.Select(n => "*" + n)) + "\r\n");
        // modlist.txt is read bottom-up: the master's mod last, so it stays lowest in the order.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "# header\r\n-CostOffMod\r\n"
            + string.Join("", Enumerable.Range(0, Overriders).Reverse().Select(p => $"+CostOverMod{p}\r\n"))
            + string.Join("", Enumerable.Range(0, Spread).Reverse().Select(p => $"+CostSpreadMod{p}\r\n"))
            + "+CostMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    /// <summary>The keyword links a weapon carries, in the list shape the record wants.</summary>
    static Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> Linked(IEnumerable<FormKey> keys)
    {
        var list = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        foreach (var k in keys) list.Add(new FormLink<IKeywordGetter>(k));
        return list;
    }

    public string Scratch(string name)
    {
        var dir = Path.Combine(Root, "scratch");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public void Dispose()
    {
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

/// <summary>One collection, sharing one <see cref="RenderCostWorld"/>. Serial for the reason <see cref="SerialCollection"/> is (#903).</summary>
[CollectionDefinition("render-cost", DisableParallelization = true)]
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

    static RecordsTools.RecordsProject Tree() => new() { form = "tree" };

    static readonly string[] Armo = { "ARMO" };

    static RecordsTools.RecordsProject DeltaForm() => new() { form = "delta" };

    /// <summary>Every contested armor as formids — rows whose provider stack is the same five plugins.</summary>
    string[] AllArmorIds => Svc.CrossQuery(Armo, null, null, false, null, null, RenderCostWorld.Contested)
                               .Keys.Select(k => k.ToString()).ToArray();

    // ---- the tree lane gathers its provider bodies a plugin at a time ------------------------------

    /// <summary>A tree row read each of its providers with a whole-plugin seek, so forty rows over a five-deep
    /// stack cost two hundred walks of the same five plugins (#765). The providers are gathered a chunk of rows at
    /// a time now: the same two hundred bodies, but one walk per provider plugin per chunk. Invisible in the
    /// answer — which is asserted here to be the same shape either way — so the walks are the claim.</summary>
    [Fact]
    public void ATreeGathersItsProviderBodiesPerPluginNotPerRow()
    {
        var ids = AllArmorIds;
        Assert.Equal(RenderCostWorld.Contested, ids.Length);

        var beforeBodies = RecordReads.TreeBodiesRead;
        var beforePasses = LoadOrderResolver.CollectPasses;
        var beforeSeeks = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, formids: ids, project: Tree(), max_chars: 4_000_000);
        var bodies = RecordReads.TreeBodiesRead - beforeBodies;
        var passes = LoadOrderResolver.CollectPasses - beforePasses;
        var seeks = LoadOrderResolver.BodySeeks - beforeSeeks;

        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), response);
        int stack = RenderCostWorld.Overriders + 1;
        // Every row carries the four always-present providers; the first TopCovers carry the top one as well.
        Assert.Equal(RenderCostWorld.Contested * RenderCostWorld.Overriders + RenderCostWorld.TopCovers, bodies);
        Assert.Equal(0, seeks);
        int chunks = (RenderCostWorld.Contested + RecordReads.ComparisonChunkRows - 1) / RecordReads.ComparisonChunkRows;
        Assert.True(passes <= stack * chunks,
                    $"{RenderCostWorld.Contested} rows over a {stack}-deep stack cost {passes} plugin walks.");

        // The ANSWER, not just its cost: each node diffed against its own row's winner, in priority order. A gather
        // handing a row another row's body, or a provider's body to the wrong node, lands here.
        Assert.Contains("HcCostArmor3\n", response);
        Assert.Contains("HcCostArmor20\n", response);
        InOrder(response,
                "HcCostMaster.esm: Value=103 (HcCostOver3.esp 4103)",
                "HcCostOver0.esp: Value=1103 (HcCostOver3.esp 4103)",
                "HcCostOver1.esp: Value=2103 (HcCostOver3.esp 4103)",
                "HcCostOver2.esp: Value=3103 (HcCostOver3.esp 4103)");
        InOrder(response,
                "HcCostMaster.esm: Value=120 (HcCostOver2.esp 3120)",
                "HcCostOver0.esp: Value=1120 (HcCostOver2.esp 3120)",
                "HcCostOver1.esp: Value=2120 (HcCostOver2.esp 3120)");
    }

    /// <summary>The same plugin wins some rows of a chunk and sits mid-stack for others — the common case on a real
    /// order. Split by role, the walk paid for it twice; one pass in descending load order walks it once, because a
    /// row's winner is the highest-priority of its providers and therefore arrives first either way.</summary>
    [Fact]
    public void ATreeWalksAPluginOnceWhenItWinsSomeRowsAndLosesOthers()
    {
        // One chunk, both kinds of row in it: HcCostOver2 wins the plain ones and is a lower provider on the rest.
        var ids = _w.TopCoveredIds.Take(RecordReads.ComparisonChunkRows / 2)
                    .Concat(_w.PlainContestedIds.Take(RecordReads.ComparisonChunkRows / 2)).ToArray();
        Assert.Equal(RecordReads.ComparisonChunkRows, ids.Length);

        var beforePasses = LoadOrderResolver.CollectPasses;
        var beforeSeeks = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, formids: ids, project: Tree(), max_chars: 4_000_000);
        var passes = LoadOrderResolver.CollectPasses - beforePasses;
        var seeks = LoadOrderResolver.BodySeeks - beforeSeeks;

        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), response);
        Assert.Equal(0, seeks);
        Assert.True(passes <= RenderCostWorld.Overriders + 1,
                    $"one chunk over {RenderCostWorld.Overriders + 1} distinct provider plugins cost {passes} walks.");
        Assert.Contains("HcCostOver2.esp: Value=3100 (HcCostOver3.esp 4100)", response);   // a row HcCostOver2 loses
        Assert.Contains("HcCostOver1.esp: Value=2120 (HcCostOver2.esp 3120)", response);   // a row it wins
    }

    /// <summary>A tree's versus= pole is one body per row out of ONE plugin, and it was read with a whole-plugin
    /// seek per row like the providers were. It is gathered on the fold's own chunk boundaries now.</summary>
    [Fact]
    public void ATreesNamedVersusPoleIsGatheredToo()
    {
        var ids = AllArmorIds;
        var beforeSeeks = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, formids: ids, project: Tree(), versus: Pole(_w.MasterName),
                                            max_chars: 4_000_000);
        var seeks = LoadOrderResolver.BodySeeks - beforeSeeks;

        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), response);
        Assert.Equal(0, seeks);
        // The named pole IS the reference: it carries no delta line of its own, and every other node — the winner
        // included — is diffed against it.
        Assert.Contains($"versus={_w.MasterName}", response);
        InOrder(response,
                $"HcCostOver0.esp: Value=1103 ({_w.MasterName} 103)",
                $"HcCostOver3.esp (winner): Value=4103 ({_w.MasterName} 103)");
        Assert.DoesNotContain($"{_w.MasterName}: Value=", response);
    }

    /// <summary>The delta lane pays the same per row, one body per pole: forty rows against the provider below
    /// each subject were eighty whole-plugin walks of the two plugins that actually hold them. Which plugin a pole
    /// reads a row from is an index fact, so the chunk is declared before a body is read and gathered per plugin
    /// (#765).</summary>
    [Fact]
    public void ADeltaGathersEachPolesBodiesPerPluginNotPerRow()
    {
        var ids = AllArmorIds;
        var beforePasses = LoadOrderResolver.CollectPasses;
        var beforeSeeks = LoadOrderResolver.BodySeeks;
        var response = RecordsTools.Records(Svc, formids: ids, project: DeltaForm(),
                                            versus: Pole("previous_provider"), max_chars: 4_000_000);
        var passes = LoadOrderResolver.CollectPasses - beforePasses;
        var seeks = LoadOrderResolver.BodySeeks - beforeSeeks;

        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), response);
        Assert.Equal(0, seeks);
        int chunks = (RenderCostWorld.Contested + RecordReads.ComparisonChunkRows - 1) / RecordReads.ComparisonChunkRows;
        // Two winner plugins and two previous-provider plugins across the chunk, one walk each.
        Assert.True(passes <= 4 * chunks,
                    $"{RenderCostWorld.Contested} delta rows over two poles cost {passes} plugin walks.");

        // Each row's own pair: a gather handing a row the other pole's plugin, or another row's body, lands here.
        Assert.Contains("- Value=4103 (HcCostOver2.esp 3103)", response);
        Assert.Contains("- Value=3120 (HcCostOver1.esp 2120)", response);
    }

    /// <summary>Assert each part appears, in this order.</summary>
    static void InOrder(string text, params string[] parts)
    {
        int at = 0;
        foreach (var part in parts)
        {
            int i = text.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(i >= 0, $"missing '{part}' after index {at} in:{Environment.NewLine}{text}");
            at = i + part.Length;
        }
    }

    // ---- the comparison forms: limit= bounds the READ, and a job past the bound announces itself ----

    /// <summary>A tree row reads every provider of its record, so a window that only trimmed the render made the
    /// first ten rows of a 17,727-row scan cost all 17,727 (#721). The window is applied to the keys now, and the
    /// bodies the fold reads are what proves it: the claim is invisible in the answer, which renders the same ten
    /// rows either way. Counted on the fold rather than on <c>BodySeeks</c>, because the providers are gathered a
    /// plugin at a time now (#765) and a gathered body is not a seek.</summary>
    [Fact]
    public void LimitBoundsWhatATreeOverAScanReads_NotOnlyWhatItRenders()
    {
        var before = RecordReads.TreeBodiesRead;
        var windowedResponse = RecordsTools.Records(Svc, types: Weap, project: Tree(), limit: 5);
        var windowed = RecordReads.TreeBodiesRead - before;

        before = RecordReads.TreeBodiesRead;
        RecordsTools.Records(Svc, types: Weap, project: Tree(), limit: RenderCostWorld.Weapons);
        var whole = RecordReads.TreeBodiesRead - before;

        Assert.False(windowedResponse.StartsWith("error:", StringComparison.Ordinal), windowedResponse);
        Assert.True(windowed * 2 < whole,
                    $"limit=5 read {windowed} bodies against {whole} for the whole {RenderCostWorld.Weapons}-row selection.");
    }

    /// <summary>And it says the rows outside the window were not read, so a windowed tree cannot be mistaken for
    /// the whole selection.</summary>
    [Fact]
    public void AWindowedTreeSaysOnlyTheseRowsWereRead() =>
        Assert.Contains("only these rows were read",
                        RecordsTools.Records(Svc, types: Weap, project: Tree(), limit: 5));

    /// <summary>Paging a tree lands on the rows the note names. The scan already skips offset= before it hands the
    /// keys over, so skipping again here would read a window further in than the one reported — and now that the
    /// window decides which bodies are read, that is rows silently never looked at rather than a misnumbered
    /// header.</summary>
    [Fact]
    public void PagingATreeReadsTheRowsTheWindowNoteNames()
    {
        var page1 = RecordsTools.Records(Svc, types: Weap, format: "json", project: Tree(), limit: 5);
        var page2 = RecordsTools.Records(Svc, types: Weap, format: "json", project: Tree(), limit: 5, offset: 5);
        var first = Doc(page1).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("formid").GetString()).ToList();
        var second = Doc(page2).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("formid").GetString()).ToList();

        Assert.Equal(5, first.Count);
        Assert.Equal(5, second.Count);
        Assert.Empty(first.Intersect(second));                       // consecutive windows, not overlapping or skipping
        var whole = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", project: Tree(),
                                             limit: RenderCostWorld.Weapons, max_chars: 4_000_000))
                    .GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("formid").GetString()).ToList();
        Assert.Equal(whole.Take(5), first);
        Assert.Equal(whole.Skip(5).Take(5), second);                 // the second page IS rows 6–10 of the selection
        Assert.Contains($"of {RenderCostWorld.Weapons}", page2);     // and the note's denominator is the selection
    }

    /// <summary>The refusal describes the form it is refusing: a delta reads two poles per record, not every
    /// override, and saying otherwise would be false about the work and would inflate the estimate.</summary>
    [Fact]
    public void TheBoundDescribesADeltasOwnWorkNotATrees()
    {
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, types: Weap, versus: System.Text.Json.JsonDocument.Parse("\"winner\"").RootElement.Clone(),
                                                                  project: new RecordsTools.RecordsProject { form = "delta" }, limit: 10));
        Assert.StartsWith("error:", r);
        Assert.Contains("two versions", r);
        Assert.DoesNotContain("every override", r);
    }

    /// <summary>The estimate up front (#716): before reading a body, the call knows the count and the form, and a
    /// selection past the bound refuses naming the count, what a tree reads, and what to try — rather than going
    /// quiet for twenty minutes.</summary>
    [Fact]
    public void ATreeOverTooManyRecordsRefusesWithTheCountAndWhatToTry()
    {
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, types: Weap, project: Tree(), limit: 10));
        Assert.StartsWith("error:", r);
        Assert.Contains("reads every override", r);
        Assert.Contains("10 records", r);
        Assert.Contains("limit=", r);
        Assert.Contains("where=", r);
    }

    /// <summary>The same bound on the formids= lane, where limit= is not a lever: this lane reads every id it was
    /// handed before the render window applies, so the sentence names the list instead.</summary>
    [Fact]
    public void ATreeOverTooManyFormidsRefusesNamingTheListAsTheLever()
    {
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, formids: AllWeaponIds, project: Tree()));
        Assert.StartsWith("error:", r);
        Assert.Contains("formids=", r);
    }

    /// <summary>The scan lever names a limit= AT OR BELOW the bound rather than "lower limit= further": the
    /// parameter defaults to 500, so a call that passed nothing is already windowed and cannot be told apart from
    /// an explicit limit=500 — telling either to lower one names a parameter one of them never carried.</summary>
    [Fact]
    public void TheScanLeverNamesALimitAtOrBelowTheBound_NotOneToLower()
    {
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, types: Weap, project: Tree()));
        Assert.StartsWith("error:", r);
        Assert.Contains("limit= at or below the bound", r);
        Assert.DoesNotContain("lower limit=", r);
    }

    /// <summary>A malformed call keeps its own precise refusal. The cost bound is charged after each form's shape
    /// checks, so a tree carrying source= is told to drop source= rather than to pass fewer ids — trimming the list
    /// would only reach the real error on the next call.</summary>
    [Fact]
    public void AMalformedTreeIsRefusedForItsShapeBeforeItsCost()
    {
        var src = System.Text.Json.JsonDocument.Parse("\"" + _w.MasterName + "\"").RootElement.Clone();
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, formids: AllWeaponIds, project: Tree(), source: src));
        Assert.StartsWith("error:", r);
        Assert.Contains("no subject", r);
        Assert.DoesNotContain("pass fewer formids=", r);
    }

    /// <summary>The smallest refusable job on this lane lands in the minute band, which every other bound starts
    /// above: 251 rows must not read "about 1 minutes".</summary>
    [Fact]
    public void TheEstimateReadsProperlyJustOverTheComparisonBound() =>
        Assert.DoesNotContain(" 1 minutes",
                              RenderBudget.ProjectedAt(RenderBudget.DefaultMaxComparisonRows + 1, RenderBudget.MillisPerComparisonRow));

    /// <summary>A census and a to_file= artifact cover the whole selection whatever limit= says, so the sentence
    /// they get names the scan terms and says limit= is not the lever.</summary>
    [Fact]
    public void ATreeCensusPastTheBoundSaysLimitIsNotItsLever()
    {
        var r = WithComparisonBound(2, () => RecordsTools.Records(Svc, types: Weap, project: Tree(), limit: 10, counts_only: true));
        Assert.StartsWith("error:", r);
        Assert.Contains("limit= does not lower what they read", r);
    }

    /// <summary>The ammo the FIRST spread plugin defines — a pole that holds a handful of a much longer list.</summary>
    string[] SpreadZeroAmmoIds => Svc.CrossQuery(Ammo, null, null, false, new[] { _w.SpreadNames[0] }, null,
                                                 RenderCostWorld.AmmoPerPlugin)
                                    .Keys.Select(k => k.ToString()).ToArray();

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

    /// <summary>rows_read is what was READ. The rows form folds each weapon's keyword list to one line per
    /// element, and those lines live inside the record's own row rather than becoming rows of their own, so the
    /// count beside the cost stays the bodies the clock measured — the count the bound is checked against.</summary>
    [Fact]
    public void AFoldedScanReportsTheBodiesItReadAndNotTheRowsTheFoldMade()
    {
        var rows = new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Keywords" } };
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", limit: RenderCostWorld.Weapons,
                                           project: rows, max_chars: 4_000_000));
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        // The fold really did produce a line per keyword (plus the root's own count line) — otherwise this pins
        // nothing: the point is that those lines live INSIDE one record's row and never become records of their own.
        Assert.Equal(RenderCostWorld.KeywordsPerWeapon + 1,
                     doc.GetProperty("records")[0].GetProperty("fields").GetArrayLength());
    }

    /// <summary>The off-order lane states the same count for the same reason.</summary>
    [Fact]
    public void AFoldedOffOrderScanReportsTheBodiesItReadToo()
    {
        var rows = new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Keywords" } };
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, source: Pole(_w.OffOrderName), format: "json",
                                           project: rows, max_chars: 4_000_000));
        Assert.Equal(RenderCostWorld.OffOrderWeapons, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.OffOrderWeapons, doc.GetProperty("rows_read").GetInt32());
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

    /// <summary>This lane reads its bodies before it renders a limit=/offset= window of them, so the count beside
    /// the cost is what it READ, not what the window showed (#607).</summary>
    [Fact]
    public void AWindowedFormidsRenderReportsTheBodiesItRead()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", limit: 5, project: Fields()));
        Assert.Equal(5, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);
    }

    /// <summary>The text transport states the same count.</summary>
    [Fact]
    public void AWindowedFormidsTextRenderReportsTheBodiesItRead()
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

    /// <summary>identity reads a body too, and by the dearest route the tool has: the resolver has no record type
    /// to seek the winner by, so each id is an untyped whole-plugin scan — 12.5–14 ms a row on the ARR order
    /// against 0.05–0.07 ms for the same ids read as named fields. So it is bounded, on its own tier (#607).</summary>
    [Fact]
    public void AnIdentityReadOverItsOwnBoundRefuses()
    {
        var response = WithIdentityBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds,
                                 project: new RecordsTools.RecordsProject { form = "identity" }));

        Assert.StartsWith("error:", response);
        Assert.Contains("UNTYPED", response);                        // what actually costs here
        Assert.Contains("project.form='summary'", response);         // the shape that answers the same question cheaply
        Assert.Contains("fewer formids=", response);                 // this lane's own lever
        Assert.DoesNotContain("narrow the scan terms", response);    // it has no scan terms
    }

    /// <summary>Its bound is its own: a named-fields bound tighter than the list does not refuse it, and an
    /// identity bound tighter than the list does not refuse a fields read.</summary>
    [Fact]
    public void TheIdentityBoundAndTheFieldsBoundAreSeparate()
    {
        var identity = new RecordsTools.RecordsProject { form = "identity" };
        var doc = Doc(WithBound(1, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", project: identity)));
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("count").GetInt32());

        var fields = Doc(WithIdentityBound(1, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", limit: 5, project: Fields())));
        Assert.Equal(5, fields.GetProperty("rendered").GetInt32());
    }

    /// <summary>And it reports what it cost, because a bound calibrated on one machine is only checkable where the
    /// truth comes back — the count being the bodies it read, not the window's rows.</summary>
    [Fact]
    public void AnIdentityReadReportsWhatResolvingItsFormidsCost()
    {
        var identity = new RecordsTools.RecordsProject { form = "identity" };
        var doc = Doc(RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", limit: 5, project: identity));
        Assert.Equal(5, doc.GetProperty("rendered").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
        Assert.True(doc.GetProperty("render_ms").GetInt64() >= 0);

        var text = RecordsTools.Records(Svc, formids: AllWeaponIds, limit: 5, project: identity);
        Assert.Contains($"read {RenderCostWorld.Weapons} record bodies in ", text);
    }

    /// <summary>An id a named source= pole has no version of refuses per item WITHOUT reading a body, so the count
    /// is the bodies read and not the list's length. Claiming the list would divide the clock by rows that cost
    /// nothing — a 2,000-id list over a pole touching three would report the tier at a thousandth of its declared
    /// cost, which is the opposite of what the accounting is for (#607).</summary>
    [Fact]
    public void AFormidsPoleReadCountsOnlyTheBodiesThePoleHeld()
    {
        var ids = SpreadZeroAmmoIds.Concat(AllWeaponIds).ToArray();
        var doc = Doc(RecordsTools.Records(Svc, formids: ids, source: Pole(_w.SpreadNames[0]), format: "json",
                                           project: Fields()));
        Assert.Equal(ids.Length, doc.GetProperty("count").GetInt32());
        Assert.Equal(RenderCostWorld.AmmoPerPlugin, doc.GetProperty("rows_read").GetInt32());

        var text = RecordsTools.Records(Svc, formids: ids, source: Pole(_w.SpreadNames[0]), project: Fields());
        Assert.Contains($"read {RenderCostWorld.AmmoPerPlugin} record bodies in ", text);
    }

    /// <summary>The same on the identity lane, whose skip is a malformed token: ResolveRefs never reaches a read
    /// for one, so it is not a body the accounting may claim.</summary>
    [Fact]
    public void AnIdentityReadCountsOnlyTheIdsThatResolved()
    {
        var ids = new[] { "not-a-formid", "also bad", "123456:NoSuchPlugin.esp" }.Concat(AllWeaponIds).ToArray();
        var doc = Doc(RecordsTools.Records(Svc, formids: ids, format: "json",
                                           project: new RecordsTools.RecordsProject { form = "identity" }));
        Assert.Equal(ids.Length, doc.GetProperty("count").GetInt32());
        Assert.Equal(RenderCostWorld.Weapons, doc.GetProperty("rows_read").GetInt32());
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

    /// <summary>A walk's census is held to the bound for the same reason the formids= lane's is: the walk hands its
    /// reached set to the LIST lane, which reads a body per id before it counts anything. Exempting counts_only
    /// there left the biggest shape on the tool — a wide walk counted rather than rendered — bounded by nothing.</summary>
    [Fact]
    public void AWalkCensusIsHeldToTheBoundBecauseTheListLaneStillReadsEveryBody()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, walk: new RecordsTools.RecordsWalk(), project: Fields(),
                                 counts_only: true));

        Assert.StartsWith("error:", response);
        Assert.Contains("project.form='chain'", response);   // the walk lane's own remedy, not the scan's
    }

    /// <summary>And it says so in the words of the call that got it. A counts_only call renders NOTHING, so a lead
    /// written for a render states the one fact that does not explain the refusal and leaves out the one that does:
    /// the list lane reads every reached body BEFORE it counts them. Its remedy has to fit a census too — telling a
    /// caller to window a render they never asked for is not a lever (#607).</summary>
    [Fact]
    public void AWalkCensusRefusalSaysItCountsBodiesRatherThanRendersRows()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, walk: new RecordsTools.RecordsWalk(), project: Fields(),
                                 counts_only: true));

        Assert.Contains("reads a record body before it is counted", response);
        Assert.Contains("counts_only= does not lower it", response);
        Assert.DoesNotContain("renders", response);
        Assert.DoesNotContain("window the render", response);
    }

    /// <summary>The formids= lane's own gate hands out the same lead, so its census says the same true thing.</summary>
    [Fact]
    public void AFormidsCensusRefusalSaysItCountsBodiesRatherThanRendersRows()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, formids: AllWeaponIds, project: Fields(), counts_only: true));

        Assert.Contains("reads a record body before it is counted", response);
        Assert.Contains("counts_only= does not lower what it costs", response);
        Assert.DoesNotContain("renders", response);
        Assert.DoesNotContain("window the render", response);
    }

    /// <summary>A rendered walk keeps the render's words and the render's lever — the census arm is an arm, not a
    /// rewrite.</summary>
    [Fact]
    public void ARenderedWalkRefusalStillSpeaksOfItsRender()
    {
        var response = WithBound(10, () =>
            RecordsTools.Records(Svc, types: Weap, walk: new RecordsTools.RecordsWalk(), project: Fields()));

        Assert.Contains("renders", response);
        Assert.Contains("window the render and not the walk", response);
        Assert.DoesNotContain("before it is counted", response);
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
    /// reserved on disk by <c>ResultsStore.Reserve</c> before the write. (The cancel this asserts lands in the
    /// render; a cancel landing inside the spill write itself is a window no test can time, and is covered by the
    /// reservation deleting its own file when the spill's <c>using</c> disposes it unwritten.)</summary>
    [Fact]
    public void ACancelledCallLeavesNothingInTheResultsDirectory()
    {
        var dir = SpillFolders.Emptied(Svc);   // emptied, so the claim is "empty"
        Assert.Empty(Directory.GetFiles(dir));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            RecordsTools.Records(Svc, types: Weap, limit: RenderCostWorld.Weapons, project: Fields(),
                                 max_chars: 600, ct: cts.Token));

        Assert.Empty(Directory.GetFiles(dir));
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
    string WithBound(int rows, Func<string> call)
    {
        var prior = Svc.Bounds;
        Svc.Bounds = prior with { Rows = rows };
        try { return call(); }
        finally { Svc.Bounds = prior; }
    }

    /// <summary>The same for the comparison forms' own bound, which is small enough in production that a world of
    /// 60 records could reach it — moved anyway, so the test says which number it is about.</summary>
    string WithComparisonBound(int rows, Func<string> call)
    {
        var prior = Svc.Bounds;
        Svc.Bounds = prior with { ComparisonRows = rows };
        try { return call(); }
        finally { Svc.Bounds = prior; }
    }

    /// <summary>The same for the whole-record lane's own bound.</summary>
    string WithWholeRecordBound(int rows, Func<string> call)
    {
        var prior = Svc.Bounds;
        Svc.Bounds = prior with { WholeRecordRows = rows };
        try { return call(); }
        finally { Svc.Bounds = prior; }
    }

    /// <summary>And for the identity lane's own bound.</summary>
    string WithIdentityBound(int rows, Func<string> call)
    {
        var prior = Svc.Bounds;
        Svc.Bounds = prior with { IdentityRows = rows };
        try { return call(); }
        finally { Svc.Bounds = prior; }
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
