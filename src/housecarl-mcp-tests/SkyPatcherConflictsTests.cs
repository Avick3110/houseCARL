using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>SkyPatcherConflicts: cross-file SET collisions (later file wins), cross-INI duplicates, the
/// intra-file dead-write (ITM) rules, the no-op scan's two shared rules, and the set/accumulate partition.
/// Migrated from the skypatcher-conflicts-guard probe; each test keeps the probe's check label as its comment.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherConflictsTests
{
    const string Target = "Skyrim.esm|12EB7";   // 012EB7:Skyrim.esm
    const string Other = "Skyrim.esm|13790";

    static readonly SkyPatcherCatalog Catalog = SkyPatcherCatalog.Load();
    static readonly SkyPatcherFieldMap FieldMap = SkyPatcherFieldMap.Load();
    static readonly SkyPatcherRecordCatalog WeapCat = Catalog.ForSubfolder("weapon")!;

    static SkyPatcherDiscovery.IniFile Ini(string name, params string[] lines) => new(
        RelPath: $"SKSE\\Plugins\\SkyPatcher\\weapon\\{name}", Subfolder: "weapon", SortKey: name,
        WinningProvider: "TestMod", LooseFilePath: null, ShadowedProviders: Array.Empty<string>(), GatePlugin: null,
        NotApplied: null, Lines: lines.Select(SkyPatcherParse.ParseLine).ToList());

    static SkyPatcherConflicts.Report Detect(params SkyPatcherDiscovery.IniFile[] files) =>
        SkyPatcherConflicts.Detect(new SkyPatcherDiscovery.FolderScan("weapon", WeapCat, PatchingEnabled: true, Files: files),
            Catalog, FieldMap);

    // The cross-file fixture: a/m/z/y apply in that order; gated.ini is filename-gated off.
    static readonly SkyPatcherConflicts.Report Cross = Detect(
        Ini("a.ini",
            $"filterByWeapons={Target}:attackDamage=40:weight=5",
            $"filterByWeapons={Target}:keywordsToAdd=Some.esp|100"),
        Ini("m.ini",
            $"filterByWeapons={Other}:attackDamage=99",
            $"filterByWeapons={Target}:weight=5",
            $"filterByWeapons={Target}:filterByKeywords=Some.esp|200:speed=2"),
        Ini("z.ini",
            $"filterByWeapons={Target}:attackDamage=60",
            "reach=1.5",
            $"filterByWeapons={Target}:keywordsToAdd=Some.esp|101",
            $"filterByWeapons={Target}:speed=9"),
        Ini("gated.ini", $"filterByWeapons={Target}:attackDamage=1") with { NotApplied = "filename-gated off" },
        Ini("y.ini", $"filterByWeapons={Target}:reach=2.5"));

    // The intra-file fixture for the dead-write rules.
    static readonly IReadOnlyList<SkyPatcherConflicts.SkyPatcherItm> Itms = Detect(
        Ini("g.ini",
            "attackDamage=1",
            "attackDamage=2"),
        Ini("gated2.ini",
            $"filterByWeapons={Target}:weight=1",
            $"filterByWeapons={Target}:weight=2") with { NotApplied = "filename-gated off" },
        Ini("i.ini",
            $"filterByWeapons={Target}:attackDamage=40",
            $"filterByWeapons={Target}:attackDamage=40",
            $"filterByWeapons={Target}:weight=5",
            "weight=9",
            "reach=1.0",
            $"filterByWeapons={Target}:reach=2.0",
            $"filterByWeapons={Target}:keywordsToAdd=Some.esp|100",
            $"filterByWeapons={Target}:keywordsToAdd=Some.esp|101"),
        Ini("multi.ini",
            $"filterByWeapons={Target},{Other}:attackDamage=40",
            $"filterByWeapons={Target}:attackDamage=60",
            $"filterByWeapons={Target},{Other}:weight=1",
            "weight=2"),
        Ini("cond.ini",
            $"filterByWeapons={Target}:speed=7",
            $"filterByWeapons={Target}:filterByKeywords=Some.esp|200:speed=9",
            $"filterByWeapons={Target}:filterByKeywords=Some.esp|200:reach=1.0",
            $"filterByWeapons={Target}:reach=2.0")).Itms;

    static SkyPatcherConflicts.SkyPatcherConflict? Conflict(string field) =>
        Cross.Conflicts.FirstOrDefault(c => c.Field == field);

    static SkyPatcherConflicts.SkyPatcherItm? Itm(string field, string file) =>
        Itms.FirstOrDefault(m => m.Field == field && Path.GetFileName(m.File) == file);

    // ---- cross-file conflicts and duplicates ----

    // SET-vs-SET same-target collision detected (attackDamage a vs z)
    [Fact]
    public void ASetVsSetSameTargetCollisionIsDetected()
    {
        var dmg = Conflict("BasicStats.Damage");
        Assert.NotNull(dmg);
        Assert.Equal(2, dmg!.Entries.Count);
    }

    // winner = the LATER file in apply order (z.ini, 60)
    [Fact]
    public void TheWinnerIsTheLaterFileInApplyOrder()
    {
        var winner = Conflict("BasicStats.Damage")!.Winner;
        Assert.Equal("SKSE\\Plugins\\SkyPatcher\\weapon\\z.ini", winner.File);
        Assert.Equal("60", winner.Value);
    }

    // the not-applied (gated) file's set did NOT participate
    [Fact]
    public void ANotAppliedFilesSetDoesNotParticipate() =>
        Assert.DoesNotContain(Conflict("BasicStats.Damage")!.Entries, e => e.File.Contains("gated"));

    // accumulating op (keywordsToAdd) is NOT a conflict
    [Fact]
    public void AnAccumulatingOpIsNotAConflict() =>
        Assert.DoesNotContain(Cross.Conflicts, c => c.Field == "Keywords");

    // same-value sets are NOT a conflict (weight 5 vs 5)
    [Fact]
    public void SameValueSetsAreNotAConflict() =>
        Assert.DoesNotContain(Cross.Conflicts, c => c.Field == "BasicStats.Weight");

    // same-value sets across files ARE a cross-INI DUPLICATE (weight 5 vs 5, a.ini + m.ini)
    [Fact]
    public void SameValueSetsAcrossFilesAreADuplicate()
    {
        var dup = Cross.Duplicates.FirstOrDefault(x => x.Field == "BasicStats.Weight");
        Assert.NotNull(dup);
        Assert.Equal(new[] { "a.ini", "m.ini" }, dup!.Entries.Select(e => Path.GetFileName(e.File)));
    }

    // a value-MIXED group stays a conflict only, never double-reported as a duplicate
    [Theory]
    [InlineData("BasicStats.Damage")]
    [InlineData("Data.Reach")]
    [InlineData("Data.Speed")]
    public void AValueMixedGroupIsNeverAlsoADuplicate(string field) =>
        Assert.DoesNotContain(Cross.Duplicates, x => x.Field == field);

    // a BROAD set collides with an explicit-target set (reach 1.5 vs 2.5)
    [Fact]
    public void ABroadSetCollidesWithAnExplicitTargetSet()
    {
        var reach = Conflict("Data.Reach");
        Assert.NotNull(reach);
        Assert.Equal(2, reach!.Entries.Count);
        Assert.Equal("2.5", reach.Winner.Value);
    }

    // an entry whose line carries EXTRA filters is flagged CONDITIONAL
    [Fact]
    public void AnEntryWithExtraFiltersIsFlaggedConditional()
    {
        var speed = Conflict("Data.Speed");
        Assert.NotNull(speed);
        Assert.True(speed!.Conditional);
        Assert.Contains(speed.Entries, e => e.Conditional);
        Assert.Contains(speed.Entries, e => !e.Conditional);
    }

    // different-target sets do NOT collide (no conflict lists 99)
    [Fact]
    public void DifferentTargetSetsDoNotCollide() =>
        Assert.DoesNotContain(Cross.Conflicts.SelectMany(c => c.Entries), e => e.Value == "99");

    // one INI writing the same field and target twice is an ITM only, never a cross-INI conflict or duplicate
    [Fact]
    public void ASingleFilesRewritesAreNeverACrossIniConflictOrDuplicate()
    {
        var solo = Detect(Ini("solo.ini",
            $"filterByWeapons={Target}:attackDamage=40",
            $"filterByWeapons={Target}:attackDamage=60",
            $"filterByWeapons={Target}:weight=5",
            $"filterByWeapons={Target}:weight=5"));
        Assert.Empty(solo.Conflicts);
        Assert.Empty(solo.Duplicates);
        Assert.Equal(2, solo.Itms.Count);
    }

    // cross-file-only writes are NOT ITMs (the conflict fixture yields zero)
    [Fact]
    public void CrossFileOnlyWritesAreNotItms() => Assert.Empty(Cross.Itms);

    // ---- intra-file dead writes (ITM) ----

    // same field/target written twice in ONE file IS a dead write — same value included, killer named
    [Fact]
    public void ASameFileRewriteIsDeadEvenAtTheSameValue()
    {
        var e = Assert.Single(Itm("BasicStats.Damage", "i.ini")!.Entries);
        Assert.Equal((1, "40"), (e.Line, e.Value));
        Assert.Equal(new[] { 2 }, e.KillerLines);
    }

    // explicit-target write killed by a later same-file BROAD write is dead
    [Fact]
    public void AnExplicitWriteKilledByALaterBroadWriteIsDead()
    {
        var e = Assert.Single(Itm("BasicStats.Weight", "i.ini")!.Entries);
        Assert.Equal(3, e.Line);
        Assert.Equal(new[] { 4 }, e.KillerLines);
    }

    // BROAD-then-explicit kills nothing (broad stays live for other records) — no i.ini reach ITM
    [Fact]
    public void BroadThenExplicitKillsNothing() => Assert.Null(Itm("Data.Reach", "i.ini"));

    // accumulating op (keywordsToAdd) twice is NOT an ITM
    [Fact]
    public void AnAccumulatingOpTwiceIsNotAnItm() => Assert.DoesNotContain(Itms, m => m.Field == "Keywords");

    // BROAD-vs-BROAD in one file IS a dead write (earlier broad dead)
    [Fact]
    public void BroadVsBroadInOneFileIsDead()
    {
        var g = Assert.Single(Itms, m => Path.GetFileName(m.File) == "g.ini");
        Assert.Equal("BasicStats.Damage", g.Field);
        var e = Assert.Single(g.Entries);
        Assert.Equal(1, e.Line);
        Assert.Equal(new[] { 2 }, e.KillerLines);
    }

    // a not-applied (gated) file's duplicates do NOT ITM
    [Fact]
    public void ANotAppliedFilesDuplicatesAreNotItms() => Assert.DoesNotContain(Itms, m => m.File.Contains("gated2"));

    // a MULTI-TARGET write partially overwritten is NOT dead (still live for the other target)
    [Fact]
    public void AMultiTargetWritePartiallyOverwrittenIsNotDead() => Assert.Null(Itm("BasicStats.Damage", "multi.ini"));

    // a MULTI-TARGET write fully re-covered is dead — reported ONCE (per write, not per token)
    [Fact]
    public void AMultiTargetWriteFullyReCoveredIsDeadOnce()
    {
        var e = Assert.Single(Itm("BasicStats.Weight", "multi.ini")!.Entries);
        Assert.Equal(3, e.Line);
        Assert.Equal(new[] { 4 }, e.KillerLines);
    }

    // a CONDITIONAL overwriter kills nothing (it may not fire) — no cond.ini speed ITM
    [Fact]
    public void AConditionalOverwriterKillsNothing() => Assert.DoesNotContain(Itms, m => m.Field == "Data.Speed");

    // a conditional EARLIER write killed unconditionally IS dead (flagged informational)
    [Fact]
    public void AConditionalEarlierWriteKilledUnconditionallyIsDead()
    {
        var e = Assert.Single(Itm("Data.Reach", "cond.ini")!.Entries);
        Assert.Equal(3, e.Line);
        Assert.True(e.Conditional);
        Assert.Equal(new[] { 4 }, e.KillerLines);
    }

    // ---- the no-op scan's two shared rules ----

    // target collection: FormID → FormKey, EditorIDs → raw strings, broad ops counted, Excluded-primary and op-less lines excluded
    [Fact]
    public void TargetCollectionSplitsFormIdsEditorIdsAndBroadLines()
    {
        var forms = new HashSet<FormKey>();
        var eids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int broad = 0;
        foreach (var line in new[]
        {
            $"filterByWeapons={Target}:attackDamage=40",
            "filterByWeapons=IronSword1H,SteelSword1H:attackDamage=40",
            "attackDamage=60",
            $"filterByWeaponsExcluded={Target}:reach=2",
            $"filterByWeapons={Target}",
            $"filterByWeaponsExcluded={Target}",   // op-less with no bare primary: not a broad write either
        })
            SkyPatcherConflicts.CollectExplicitPrimaryTargets(SkyPatcherParse.ParseLine(line), Catalog, WeapCat, forms, eids, ref broad);
        Assert.Single(forms);
        Assert.True(eids.SetEquals(new[] { "IronSword1H", "SteelSword1H" }), string.Join(",", eids));
        Assert.Equal(2, broad);
    }

    static SkyPatcherOverlay.SkyPatcherAppliedOp Ap(string op, string raw, string? before, string? after) =>
        new("x.ini", 1, op, op, raw, "p", before, after, null);

    static RecordMap WeapMap => FieldMap.ForSubfolder("weapon")[0];

    // no-op test: a SET-class op with before == after IS a no-op write
    [Fact]
    public void ASetOpWithBeforeEqualAfterIsANoOp() =>
        Assert.True(SkyPatcherConflicts.IsNoOpWrite(Ap("attackDamage", "40", "40", "40"), WeapMap));

    // no-op test: before != after is NOT
    [Fact]
    public void ASetOpThatChangesTheValueIsNotANoOp() =>
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("attackDamage", "60", "40", "60"), WeapMap));

    // no-op test: a deliberate 'none' leave-unchanged is NOT flagged
    [Fact]
    public void ADeliberateNoneIsNotANoOp() =>
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("setProtected", "none", "true", "true"), FieldMap.ForSubfolder("npc")[0]));

    // no-op test: an ACCUMULATING op with equal tokens is NOT this class (skypatcher_read's lane)
    [Fact]
    public void AnAccumulatingOpWithEqualTokensIsNotANoOp() =>
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("keywordsToAdd", "Some.esp|100", "2 entr(ies)", "2 entr(ies)"), WeapMap));

    // no-op test: an unknown/unmapped op or a null map is NOT flagged
    [Fact]
    public void AnUnknownOpOrANullMapIsNotANoOp()
    {
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("nonsenseOp", "1", "1", "1"), WeapMap));
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("attackDamage", "40", "40", "40"), null));
    }

    // no-op test: a null before (no static read) is NOT flagged
    [Fact]
    public void ANullBeforeIsNotANoOp() =>
        Assert.False(SkyPatcherConflicts.IsNoOpWrite(Ap("attackDamage", "40", null, null), WeapMap));

    // ---- the set/accumulate partition ----

    // every op semantic is classified set-class OR accumulating (none silently default)
    [Fact]
    public void EveryOpSemanticIsClassified() =>
        Assert.Empty(Enum.GetValues<SkyPatcherOpSemantic>()
            .Where(s => !SkyPatcherConflicts.SetClassSemantics.Contains(s) && !SkyPatcherConflicts.AccumulatingSemantics.Contains(s)));

    // the set/accumulate sets are disjoint
    [Fact]
    public void TheSetAndAccumulateSetsAreDisjoint() =>
        Assert.Empty(SkyPatcherConflicts.SetClassSemantics.Intersect(SkyPatcherConflicts.AccumulatingSemantics));
}
