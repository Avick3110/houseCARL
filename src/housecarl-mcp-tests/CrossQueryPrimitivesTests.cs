using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The service-layer scan primitives: defined_in, list-valued references, group_by, offset paging and
/// where_source=winner, over a master and a replacer that overrides one weapon and defines new ones.</summary>
[Trait("tier", "integration")]
public sealed class CrossQueryPrimitivesTests : IClassFixture<CrossQueryPrimitivesTests.World>
{
    /// <summary>Master: keywords KA, KB; weapons W1[KA] (Damage 10), W2[KB]; armor A1. Replacer: overrides W1
    /// (Damage 15, EditorID hcbpSword1Winner) and defines W3[KA,KB] and A2.</summary>
    public sealed class World : IDisposable
    {
        public const string MasterName = "hcbpMaster.esp";
        public const string ReplName = "hcbpRepl.esp";
        readonly string _dir;
        readonly LoadOrderResolver _resolver;
        public LoadOrderService Svc { get; }
        public FormKey Ka { get; }
        public FormKey Kb { get; }
        public FormKey W1 { get; }
        public FormKey W2 { get; }
        public FormKey W3 { get; }

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hc-cross-query-primitives-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var masterPath = Path.Combine(_dir, MasterName);
            var replPath = Path.Combine(_dir, ReplName);

            var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
            var ka = master.Keywords.AddNew(); ka.EditorID = "hcbpKwA"; Ka = ka.FormKey;
            var kb = master.Keywords.AddNew(); kb.EditorID = "hcbpKwB"; Kb = kb.FormKey;
            var w1 = master.Weapons.AddNew(); w1.EditorID = "hcbpSword1"; w1.BasicStats = new WeaponBasicStats { Damage = 10 };
            w1.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(Ka) };
            W1 = w1.FormKey;
            var w2 = master.Weapons.AddNew(); w2.EditorID = "hcbpSword2"; w2.BasicStats = new WeaponBasicStats { Damage = 20 };
            w2.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(Kb) };
            W2 = w2.FormKey;
            master.Armors.AddNew().EditorID = "hcbpArmor1";
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var repl = new SkyrimMod(ModKey.FromNameAndExtension(ReplName), SkyrimRelease.SkyrimSE);
            var w1ov = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(repl, w1);
            w1ov.BasicStats = new WeaponBasicStats { Damage = 15 };
            w1ov.EditorID = "hcbpSword1Winner";
            var w3 = repl.Weapons.AddNew(); w3.EditorID = "hcbpSword3"; w3.BasicStats = new WeaponBasicStats { Damage = 30 };
            w3.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
                { new FormLink<IKeywordGetter>(Ka), new FormLink<IKeywordGetter>(Kb) };
            W3 = w3.FormKey;
            repl.Armors.AddNew().EditorID = "hcbpArmor2";
            repl.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            _resolver = LoadOrderResolver.Build(new[] { masterPath, replPath });
            Svc = LoadOrderService.ForGuard(_resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        }

        public void Dispose()
        {
            _resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    const string Master = World.MasterName, Repl = World.ReplName;
    readonly World _w;
    readonly RecordReads _reads;

    public CrossQueryPrimitivesTests(World w) { _w = w; _reads = w.Svc.ReadArea; }

    static Dictionary<string, int> GroupMap(CrossQueryOutcome q) =>
        (q.Groups ?? Array.Empty<GroupCount>()).ToDictionary(g => g.Key, g => g.Count, StringComparer.Ordinal);

    // Probe: "plugins=[Repl] type=Weapon TOUCHES 2 (W1 override + W3 def)" and "defined_in=true DEFINES 1 (only W3)".
    [Fact]
    public void DefinedIn_NarrowsAPluginScopeFromTouchedRecordsToRecordsItDefines()
    {
        var touches = _reads.CrossQuery("Weapon", null, null, false, new[] { Repl }, null, 500);
        Assert.Equal(2, touches.Total);
        Assert.Contains(_w.W1, touches.Keys);

        var defined = _reads.CrossQuery("Weapon", null, null, false, new[] { Repl }, null, 500, definedIn: true);
        Assert.Equal(1, defined.Total);
        Assert.Equal(new[] { _w.W3 }, defined.Keys);
        Assert.Equal(Repl, defined.ScopeLabel);
    }

    // Probe: "defined_in=true WITHOUT plugins= is REFUSED loud (not silently ignored)".
    [Fact]
    public void DefinedIn_WithoutAPluginScope_IsRefusedNamingPlugins()
    {
        var q = _reads.CrossQuery("Weapon", null, null, false, null, null, 500, definedIn: true);
        Assert.Contains("plugins=", q.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "references=[KA,KB] over Weapons matches 3" and "each match records WHICH it hit", in input order.
    [Fact]
    public void ListReferences_OrsTheTargetsAndRecordsWhichTargetEachMatchHit()
    {
        var q = _reads.CrossQuery("Weapon", new[] { _w.Ka, _w.Kb }, null, false, null, null, 500);
        Assert.Equal(3, q.Total);
        Assert.NotNull(q.MatchedTargets);
        var matchOf = q.Keys.Select((k, i) => (k, m: q.MatchedTargets![i])).ToDictionary(x => x.k, x => x.m);
        Assert.Equal(_w.Ka.ToString(), matchOf[_w.W1]);
        Assert.Equal(_w.Kb.ToString(), matchOf[_w.W2]);
        Assert.Equal($"{_w.Ka}, {_w.Kb}", matchOf[_w.W3]);
    }

    // Probe: "references=[KA] alone matches 2 (W1,W3)" and "single-target references= adds NO matches= noise".
    [Fact]
    public void SingleReference_MatchesItsCarriersWithoutMatchedTargets()
    {
        var ka = _reads.CrossQuery("Weapon", new[] { _w.Ka }, null, false, null, null, 500);
        Assert.Equal(new[] { _w.W1, _w.W3 }.OrderBy(k => k.ToString()), ka.Keys.OrderBy(k => k.ToString()));
        Assert.Null(ka.MatchedTargets);
        var kb = _reads.CrossQuery("Weapon", new[] { _w.Kb }, null, false, null, null, 500);
        Assert.Equal(new[] { _w.W2, _w.W3 }.OrderBy(k => k.ToString()), kb.Keys.OrderBy(k => k.ToString()));
    }

    // Probe: "group_by=winner over Weapons: Repl=2 & master=1" and "groups are sorted by count desc".
    [Fact]
    public void GroupByWinner_CountsEveryMatchPerWinnerLargestFirst()
    {
        var q = _reads.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner");
        Assert.Equal(3, q.Total);
        Assert.Equal("winner", q.GroupBy);
        Assert.Equal(new[] { (Repl, 2), (Master, 1) }, q.Groups!.Select(g => (g.Key, g.Count)));
    }

    // Probe: "group_by=defined_in over Weapons: master=2 (W1,W2) & Repl=1 (W3)".
    [Fact]
    public void GroupByDefinedIn_CountsByTheOriginPlugin()
    {
        var q = _reads.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "defined_in");
        var g = GroupMap(q);
        Assert.Equal(3, q.Total);
        Assert.Equal(2, g[Master]);
        Assert.Equal(1, g[Repl]);
    }

    // Probe: "group_by=type over plugins=[master,Repl]: Weapon=3, Armor=2, Keyword=2" and "counts MATCH a hand tally".
    [Fact]
    public void GroupByType_MatchesAHandTallyOfTheSameScope()
    {
        var scope = new[] { Master, Repl };
        var byType = _reads.CrossQuery((string?)null, null, null, false, scope, null, 500, groupBy: "type");
        var plain = _reads.CrossQuery((string?)null, null, null, false, scope, null, 5000);
        var tally = plain.Prefilled!.GroupBy(s => s.Type).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        Assert.Equal(7, byType.Total);
        Assert.Equal(new Dictionary<string, int> { ["Weapon"] = 3, ["Armor"] = 2, ["Keyword"] = 2 }, GroupMap(byType));
        Assert.Equal(tally, GroupMap(byType));
    }

    // Probe: "conflicts_only + group_by=winner (no body scope): the 1 contested record (W1) → Repl=1".
    [Fact]
    public void ConflictsOnlyGroupByWinner_AggregatesThePureIndexBranch()
    {
        var q = _reads.CrossQuery((string?)null, null, null, true, null, null, 500, groupBy: "winner");
        Assert.Equal(1, q.Total);
        Assert.Equal(1, GroupMap(q)[Repl]);
    }

    // Probe: "group_by=type without type=/plugins= is REFUSED" and "group_by=<unknown key> is REFUSED loud".
    [Fact]
    public void GroupBy_TypeWithoutABodyScopeAndAnUnknownKey_AreRefused()
    {
        Assert.Contains("group_by=type", _reads.CrossQuery((string?)null, null, null, true, null, null, 500, groupBy: "type").Error,
                        StringComparison.OrdinalIgnoreCase);
        Assert.Contains("group_by", _reads.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "bogus").Error,
                        StringComparison.OrdinalIgnoreCase);
    }

    const string CfMasterName = "hcbpcfMaster.esp";

    // Probe #248: "case-variant master spellings MERGE into one group of count 2" and "the merged group key is a real master spelling".
    [Fact]
    public void GroupByDefinedIn_MergesCaseVariantSpellingsOfOneMaster()
    {
        var q = CaseVariantDefinedInGroups(masterInOrder: true);
        Assert.Equal(2, q.Total);
        var g = Assert.Single(q.Groups!);
        Assert.Equal(2, g.Count);
        Assert.Equal(CfMasterName, g.Key, ignoreCase: true);
    }

    // #248 with the master outside the order: no canonical spelling is published for it, so the group table's own
    // case-insensitive keys are the only thing merging the two spellings.
    [Fact]
    public void GroupByDefinedIn_MergesCaseVariantSpellingsOfAMasterOutsideTheOrder()
    {
        var q = CaseVariantDefinedInGroups(masterInOrder: false);
        Assert.Equal(2, q.Total);
        var g = Assert.Single(q.Groups!);
        Assert.Equal(2, g.Count);
        Assert.Equal(CfMasterName, g.Key, ignoreCase: true);
    }

    /// <summary>A overrides one master weapon listing the master as written; B overrides another listing it in
    /// lowercase. The query groups plugins=[A,B] by defined_in.</summary>
    static CrossQueryOutcome CaseVariantDefinedInGroups(bool masterInOrder)
    {
        const string cfMasterName = CfMasterName, cfAName = "hcbpcfA.esp", cfBName = "hcbpcfB.esp";
        var dir = Path.Combine(Path.GetTempPath(), "hc-cross-query-casefold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfMaster = new SkyrimMod(ModKey.FromNameAndExtension(cfMasterName), SkyrimRelease.SkyrimSE);
            var cw1 = cfMaster.Weapons.AddNew(); cw1.EditorID = "hcbpcfSword1";
            var cw2 = cfMaster.Weapons.AddNew(); cw2.EditorID = "hcbpcfSword2";
            cfMaster.BeginWrite.ToPath(Path.Combine(dir, cfMasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var cfA = new SkyrimMod(ModKey.FromNameAndExtension(cfAName), SkyrimRelease.SkyrimSE);
            _ = WriteEngine.GenericGetOrAddAsOverride(cfA, cw1);
            cfA.BeginWrite.ToPath(Path.Combine(dir, cfAName)).WithLoadOrder(new ISkyrimModGetter[] { cfMaster }).Write();

            // B lists the same master in lowercase, so the origin the scan reports for B's record is lowercase.
            var cfMasterLc = new SkyrimMod(ModKey.FromNameAndExtension(cfMasterName.ToLowerInvariant()), SkyrimRelease.SkyrimSE);
            var cw2Lc = new Weapon(new FormKey(cfMasterLc.ModKey, cw2.FormKey.ID), SkyrimRelease.SkyrimSE) { EditorID = "hcbpcfSword2" };
            var cfB = new SkyrimMod(ModKey.FromNameAndExtension(cfBName), SkyrimRelease.SkyrimSE);
            _ = WriteEngine.GenericGetOrAddAsOverride(cfB, cw2Lc);
            cfB.BeginWrite.ToPath(Path.Combine(dir, cfBName)).WithLoadOrder(new ISkyrimModGetter[] { cfMasterLc }).Write();

            var order = masterInOrder ? new[] { cfMasterName, cfAName, cfBName } : new[] { cfAName, cfBName };
            using var resolver = LoadOrderResolver.Build(order.Select(n => Path.Combine(dir, n)).ToList());
            var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));
            return svc.ReadArea.CrossQuery((string?)null, null, null, false, new[] { cfAName, cfBName }, null, 500, groupBy: "defined_in");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }

    // Probe #223: "the 3 windows tile the full enumeration EXACTLY", "total still", "offset in the outcome", "capped only when matches lie beyond the window".
    [Fact]
    public void Offset_WindowsTileTheTypeEnumerationExactly()
    {
        var full = _reads.CrossQuery("Weapon", null, null, false, null, null, 500);
        var paged = new List<FormKey>();
        for (int off = 0; off < 3; off++)
        {
            var win = _reads.CrossQuery("Weapon", null, null, false, null, null, 1, offset: off);
            Assert.Single(win.Keys);
            Assert.Equal(full.Total, win.Total);
            Assert.Equal(off, win.Offset);
            Assert.Equal(off < 2, win.Capped);
            paged.AddRange(win.Keys);
        }
        Assert.Equal(full.Keys, paged);
    }

    // Probe: "offset past the last match: 0 rows, exact total, not capped".
    [Fact]
    public void Offset_PastTheEndIsAnHonestEmptyWindow()
    {
        var past = _reads.CrossQuery("Weapon", null, null, false, null, null, 500, offset: 10);
        Assert.Empty(past.Keys);
        Assert.Equal(3, past.Total);
        Assert.False(past.Capped);
    }

    // Probe: "offset=-1 is REFUSED loud" and "offset + group_by is REFUSED loud (never silently ignored)".
    [Fact]
    public void Offset_NegativeAndUnderGroupBy_AreRefused()
    {
        Assert.Contains("offset", _reads.CrossQuery("Weapon", null, null, false, null, null, 500, offset: -1).Error,
                        StringComparison.OrdinalIgnoreCase);
        Assert.Contains("group_by", _reads.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner", offset: 5).Error,
                        StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "plugins=[master,Repl] windows tile the de-dup'd enumeration EXACTLY" and "sources stay parallel to keys".
    [Fact]
    public void Offset_WindowsTileThePluginScopeEnumerationWithSourcesParallel()
    {
        var scope = new[] { Master, Repl };
        var full = _reads.CrossQuery((string?)null, null, null, false, scope, null, 5000);
        var paged = new List<FormKey>();
        for (int off = 0; off < full.Total; off += 3)
        {
            var win = _reads.CrossQuery((string?)null, null, null, false, scope, null, 3, offset: off);
            Assert.Equal(win.Keys.Count, win.Sources!.Count);
            paged.AddRange(win.Keys);
        }
        Assert.Equal(7, full.Total);
        Assert.Equal(full.Keys, paged);
    }

    // Probe: "conflicts_only offset: window 0 = the 1 contested record; offset=1 = honest empty window, not capped".
    [Fact]
    public void Offset_PagesTheConflictsOnlyBranch()
    {
        var full = _reads.CrossQuery((string?)null, null, null, true, null, null, 500);
        var win0 = _reads.CrossQuery((string?)null, null, null, true, null, null, 500, offset: 0);
        var win1 = _reads.CrossQuery((string?)null, null, null, true, null, null, 500, offset: 1);
        Assert.Equal(new[] { _w.W1 }, full.Keys);
        Assert.Equal(full.Keys, win0.Keys);
        Assert.Empty(win1.Keys);
        Assert.Equal(1, win1.Total);
        Assert.False(win1.Capped);
    }

    // Probe #233: "where=[Damage=10] default (scoped) → W1" and "THE FIX: where_source=winner → 0 (W1's live winner is 15)".
    [Fact]
    public void WhereSourceWinner_DecidesTheMatchOnTheLiveWinnerNotTheScopedBody()
    {
        var scoped = _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, new[] { "BasicStats.Damage = 10" }, 500);
        Assert.Equal(new[] { _w.W1 }, scoped.Keys);
        Assert.False(scoped.WhereWinner);

        var winner10 = _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, new[] { "BasicStats.Damage = 10" }, 500, whereSource: "winner");
        Assert.Null(winner10.Error);
        Assert.Equal(0, winner10.Total);
        Assert.True(winner10.WhereWinner);

        var winner15 = _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "winner");
        Assert.Equal(new[] { _w.W1 }, winner15.Keys);
    }

    // Probe: "defined_in=true + where_source=winner → W1" and "W1 counted ONCE despite living in both scoped plugins".
    [Fact]
    public void WhereSourceWinner_ComposesWithDefinedInAndCountsARecordOnce()
    {
        var defined = _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, new[] { "BasicStats.Damage = 15" }, 500,
                                        definedIn: true, whereSource: "winner");
        Assert.Equal(new[] { _w.W1 }, defined.Keys);
        var both = _reads.CrossQuery("Weapon", null, null, false, new[] { Master, Repl }, new[] { "BasicStats.Damage = 15" }, 500,
                                     whereSource: "winner");
        Assert.Equal(1, both.Total);
        Assert.Equal(new[] { _w.W1 }, both.Keys);
    }

    // Probe: "type=-only where_source=winner → matches W1, carries the REDUNDANT note, same result as plain".
    [Fact]
    public void WhereSourceWinner_UnderATypeOnlyScopeIsAcceptedWithARedundantNote()
    {
        var winner = _reads.CrossQuery("Weapon", null, null, false, null, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "winner");
        var plain = _reads.CrossQuery("Weapon", null, null, false, null, new[] { "BasicStats.Damage = 15" }, 500);
        Assert.Equal(new[] { _w.W1 }, winner.Keys);
        Assert.True(winner.WhereWinner);
        Assert.Contains("redundant", winner.WhereSourceNote);
        Assert.Equal(plain.Keys, winner.Keys);
    }

    // Probe: "where_source='bogus' REFUSED naming 'scoped' and 'winner'" and "where_source=winner WITHOUT a body filter REFUSED".
    [Fact]
    public void WhereSource_UnknownValueAndNoBodyFilter_AreRefused()
    {
        var bad = _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, new[] { "BasicStats.Damage = 15" }, 500, whereSource: "bogus");
        Assert.Contains("scoped", bad.Error);
        Assert.Contains("winner", bad.Error);
        Assert.Contains("body filter", _reads.CrossQuery("Weapon", null, null, false, new[] { Master }, null, 500, whereSource: "winner").Error);
    }

    // Probe: "editorid_contains='Winner' default (scoped) → 0" and "where_source=winner → W1 — the body-filter widening beyond where=".
    [Fact]
    public void WhereSourceWinner_RetargetsEditorIdContainsToo()
    {
        var scoped = _reads.CrossQuery("Weapon", null, "Winner", false, new[] { Master }, null, 500);
        Assert.Equal(0, scoped.Total);
        var winner = _reads.CrossQuery("Weapon", null, "Winner", false, new[] { Master }, null, 500, whereSource: "winner");
        Assert.Equal(new[] { _w.W1 }, winner.Keys);
        Assert.True(winner.WhereWinner);
    }
}
