using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A live MO2 order for the reach term: HcReach.esp holds two races (the human one carries a keyword), NPCs on
/// fixed levels, a PC-level-mult NPC, an NPC that inherits its race through its template and one whose template is a
/// leveled actor, an outfit holding an armour, weapons, books and a spell; one SPID and one KID file feed the replay.</summary>
sealed class ReachWorld : IDisposable
{
    public const string Plugin = "HcReach.esp";
    public readonly string Root;
    public readonly LoadOrderService Svc;

    public static string Fk(uint id) => $"{id:X6}:{Plugin}";
    public const uint HumanoidKw = 0x801, HumanRace = 0x810, BeastRace = 0x811, Faction = 0x812, Spell = 0x813, Lvln = 0x814,
        Boots = 0x815, Outfit = 0x816, Ench = 0x817, Mgef = 0x818,
        Guard = 0x820, Bodyguard = 0x821, Beast = 0x822, TemplateBase = 0x823, Templated = 0x824, LeveledTemplated = 0x825, Mult = 0x826, Pick = 0x827,
        IronSword = 0x830, FireSword = 0x831, WoodSword = 0x832, Journal = 0x840, Tome = 0x841, ReachSpell = 0x850,
        SpellA = 0x851, SpellB = 0x852, KidA = 0x853, KidB = 0x854;

    public const string Spid =
        "Keyword = HcGivenKw|HcKwHumanoid\r\n"                         // :1 gives HcGivenKw to every NPC whose race carries HcKwHumanoid
        + "Keyword = HcChanceKw|HcGuard|NONE|NONE|NONE|NONE|50\r\n"    // :2 gives HcChanceKw to HcGuard, at chance 50
        + "ExclusiveGroup = HcPair|HcSpellA,HcSpellB\r\n";             // :3 HcBodyguard already holds HcSpellB
    public const string Kid = "Keyword = HcBookTypeJournal|Book|*journal\r\n"   // :1 the BOOBIES shape: a keyword KID creates, by part of a name
        + "ExclusiveGroup = HcKidPair|HcKidA,HcKidB\r\n";                            // :2 HcIronSword already holds HcKidB

    /// <summary>A second plugin, loaded after the first, holding one NPC of each bucket for a scoped call.</summary>
    public const string More = "HcReachMore.esp", First = "HcReachFirst.esp", Off = "HcReachOff.esp";
    public static string MoreFk(uint id) => $"{id:X6}:{More}";
    public const uint MoreHuman = 0x800, MoreLeveled = 0x801;

    public ReachWorld(bool more = false, string spid = "", string kid = "", bool pick = false,
                      Action<SkyrimMod>? extra = null, bool first = false, bool off = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-reach-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var iniMod = Path.Combine(mods, "HcReachIni");
        foreach (var d in new[] { profileDir, Path.Combine(Root, "game", "Data"), Path.Combine(mods, "HcReachPlugins"), iniMod })
            Directory.CreateDirectory(d);
        SkyPatcherSwitchesFile.WriteTweaks(iniMod);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace('\\', '/') + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(Plugin), SkyrimRelease.SkyrimSE);
        FormKey K(uint id) => new(mod.ModKey, id);
        var kw = new Keyword(K(HumanoidKw), SkyrimRelease.SkyrimSE) { EditorID = "HcKwHumanoid" };
        mod.Keywords.Add(kw);
        mod.Races.Add(new Race(K(HumanRace), SkyrimRelease.SkyrimSE)
            { EditorID = "HcHumanRace", Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kw.ToLink() } });
        mod.Races.Add(new Race(K(BeastRace), SkyrimRelease.SkyrimSE) { EditorID = "HcBeastRace" });
        mod.Factions.Add(new Faction(K(Faction), SkyrimRelease.SkyrimSE) { EditorID = "HcFaction" });
        mod.Spells.Add(new Spell(K(Spell), SkyrimRelease.SkyrimSE) { EditorID = "HcSpell" });
        mod.Spells.Add(new Spell(K(SpellA), SkyrimRelease.SkyrimSE) { EditorID = "HcSpellA" });
        mod.Spells.Add(new Spell(K(SpellB), SkyrimRelease.SkyrimSE) { EditorID = "HcSpellB" });
        mod.Keywords.Add(new Keyword(K(KidA), SkyrimRelease.SkyrimSE) { EditorID = "HcKidA" });
        var kidB = new Keyword(K(KidB), SkyrimRelease.SkyrimSE) { EditorID = "HcKidB" };
        mod.Keywords.Add(kidB);
        mod.LeveledNpcs.Add(new LeveledNpc(K(Lvln), SkyrimRelease.SkyrimSE) { EditorID = "HcLvln" });
        var boots = new Armor(K(Boots), SkyrimRelease.SkyrimSE) { EditorID = "HcBoots" };
        mod.Armors.Add(boots);
        mod.Outfits.Add(new Outfit(K(Outfit), SkyrimRelease.SkyrimSE)
            { EditorID = "HcOutfit", Items = new ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { boots.ToLink<IOutfitTargetGetter>() } });
        mod.ObjectEffects.Add(new ObjectEffect(K(Ench), SkyrimRelease.SkyrimSE) { EditorID = "HcEnch" });
        mod.MagicEffects.Add(new MagicEffect(K(Mgef), SkyrimRelease.SkyrimSE) { EditorID = "HcMgef", Flags = MagicEffect.Flag.Hostile });

        Npc N(uint id, string eid, uint race, short level, bool female = false)
        {
            var n = new Npc(K(id), SkyrimRelease.SkyrimSE) { EditorID = eid, Name = eid };
            n.Race.SetTo(K(race));
            n.Configuration.Level = new NpcLevel { Level = level };
            if (female) n.Configuration.Flags |= NpcConfiguration.Flag.Female;
            mod.Npcs.Add(n);
            return n;
        }
        var guard = N(Guard, "HcGuard", HumanRace, 10, female: true);
        guard.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(K(Faction)), Rank = 0 });
        guard.DefaultOutfit.SetTo(K(Outfit));
        N(Bodyguard, "HcBodyguard", HumanRace, 20).ActorEffect = new ExtendedList<IFormLinkGetter<ISpellRecordGetter>> { new FormLink<ISpellRecordGetter>(K(SpellB)) };
        N(Beast, "HcBeast", BeastRace, 5);
        N(TemplateBase, "HcTemplateBase", HumanRace, 8);
        // Its own race is the beast's; the Traits category it inherits says human.
        var templated = N(Templated, "HcTemplated", BeastRace, 8);
        templated.Template.SetTo(K(TemplateBase));
        templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var leveled = N(LeveledTemplated, "HcLeveledTemplated", BeastRace, 8);
        leveled.Template.SetTo(K(Lvln));
        leveled.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var mult = N(Mult, "HcMult", BeastRace, 1);
        mult.Configuration.Level = new PcLevelMult { LevelMult = 1 };
        mult.Configuration.CalcMinLevel = 1; mult.Configuration.CalcMaxLevel = 50;
        // Templated on a leveled list with no category inherited: its own data decides, but SPID matches its pick too.
        if (pick) N(Pick, "HcPick", BeastRace, 3).Template.SetTo(K(Lvln));

        Weapon W(uint id, string eid) { var w = new Weapon(K(id), SkyrimRelease.SkyrimSE) { EditorID = eid, Name = eid }; mod.Weapons.Add(w); return w; }
        W(IronSword, "HcIronSword").Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kidB.ToLink() };
        W(FireSword, "HcFireSword").ObjectEffect.SetTo(K(Ench));
        W(WoodSword, "HcWoodSword");
        mod.Books.Add(new Book(K(Journal), SkyrimRelease.SkyrimSE) { EditorID = "HcJournal", Name = "Old journal" });
        mod.Books.Add(new Book(K(Tome), SkyrimRelease.SkyrimSE) { EditorID = "HcTome", Name = "Tome" });
        var spell = new Spell(K(ReachSpell), SkyrimRelease.SkyrimSE) { EditorID = "HcReachSpell" };
        spell.Effects.Add(new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(K(Mgef)), Data = new EffectData() });
        mod.Spells.Add(spell);
        extra?.Invoke(mod);
        mod.BeginWrite.ToPath(Path.Combine(mods, "HcReachPlugins", Plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var active = new List<string> { Plugin };
        if (first)
        {
            new SkyrimMod(ModKey.FromFileName(First), SkyrimRelease.SkyrimSE).BeginWrite
                .ToPath(Path.Combine(mods, "HcReachPlugins", First)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            active.Insert(0, First);
        }
        if (more) active.Add(More);
        if (more)
        {
            var add = new SkyrimMod(ModKey.FromFileName(More), SkyrimRelease.SkyrimSE);
            var human = new Npc(new FormKey(add.ModKey, MoreHuman), SkyrimRelease.SkyrimSE) { EditorID = "HcMoreHuman" };
            human.Race.SetTo(K(HumanRace));
            add.Npcs.Add(human);
            var lv = new Npc(new FormKey(add.ModKey, MoreLeveled), SkyrimRelease.SkyrimSE) { EditorID = "HcMoreLeveled" };
            lv.Race.SetTo(K(BeastRace));
            lv.Template.SetTo(K(Lvln));
            lv.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
            add.Npcs.Add(lv);
            add.BeginWrite.ToPath(Path.Combine(mods, "HcReachPlugins", More)).WithLoadOrder(new ISkyrimModGetter[] { mod }).Write();
        }
        // An inactive plugin in the mods folder, outside the load order.
        if (off)
        {
            var o = new SkyrimMod(ModKey.FromFileName(Off), SkyrimRelease.SkyrimSE);
            o.Npcs.Add(new Npc(new FormKey(o.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcOffNpc" });
            o.BeginWrite.ToPath(Path.Combine(mods, "HcReachPlugins", Off)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        File.WriteAllText(Path.Combine(iniMod, "HcReach_DISTR.ini"), Spid + spid);
        File.WriteAllText(Path.Combine(iniMod, "HcReach_KID.ini"), Kid + kid);
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + string.Concat(active.Select(p => p + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), string.Concat(active.Select(p => "*" + p + "\r\n")));
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+HcReachIni\r\n+HcReachPlugins\r\n");
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>records where=["spid reaches …"] / ["kid reaches …"]: each pick of the ruled design (DECISIONS 2026-11-17 (3)),
/// each bucket, KID's pass order, and the refusals.</summary>
[Trait("tier", "integration")]
public sealed class RecordReachTests : IDisposable
{
    readonly ReachWorld _w = new();
    public void Dispose() => _w.Dispose();

    string Records(string term, string[]? formids = null, string[]? types = null, bool countsOnly = false)
        => RecordsTools.Records(_w.Svc, formids: formids, types: types, where: new[] { term }, counts_only: countsOnly);

    static string F(uint id) => ReachWorld.Fk(id);

    /// <summary>A record's summary row, which the reach note's examples never spell.</summary>
    static string Row(uint id) => "  " + F(id) + "  runtime=";

    [Fact]
    public void ARaceLineReadsATemplatedNpcThroughItsChain()
    {
        // Q2 (b): HcTemplated's own race is the beast's, but it takes Traits from a human template.
        var r = Records("spid reaches Spell = HcSpell||HcHumanRace");
        Assert.Contains(Row(ReachWorld.Templated), r);
        Assert.Contains(Row(ReachWorld.Guard), r);
        Assert.DoesNotContain(Row(ReachWorld.Beast), r);
    }

    [Fact]
    public void AChainEndingAtALeveledActorIsNotDecidable()
    {
        var r = Records("spid reaches Spell = HcSpell||HcHumanRace", countsOnly: true);
        Assert.Contains("not decidable 1 (its template chain ends at a leveled actor: 1", r);
        Assert.DoesNotContain(Row(ReachWorld.LeveledTemplated), Records("spid reaches Spell = HcSpell||HcHumanRace"));
    }

    [Fact]
    public void AKeywordAKeywordLineGivesCounts()
    {
        // Q3 (b): HcGivenKw exists on no record; SPID's :1 gives it to every NPC of a race carrying HcKwHumanoid.
        var r = Records("spid reaches Spell = HcSpell|HcGivenKw");
        Assert.Contains("scan: 4 matches", r);   // HcGuard, HcBodyguard, HcTemplateBase, and HcTemplated through its chain
        Assert.Contains(Row(ReachWorld.Bodyguard), r);
        Assert.DoesNotContain(Row(ReachWorld.Beast), r);
        Assert.Contains("Keyword line(s) of the layer were replayed first", r);
        // Only the line giving a keyword it reads is replayed; HcChanceKw's line is not.
        Assert.Contains("SPID's 1 Keyword line(s) of the layer were replayed first, so a keyword they give counts; the other 1 give none it reads.", r);
    }

    [Fact]
    public void AKeywordAChanceLineGivesNeedsTheRunningGame()
    {
        var r = Records("spid reaches Spell = HcSpell|HcChanceKw", countsOnly: true);
        Assert.Contains("yes 0,", r);
        Assert.Contains("needs the running game 2 (a Keyword line that gives a keyword it reads (chance 50): 2", r);   // HcGuard, and the chain-undecided one
    }

    [Fact]
    public void EachBucketIsCountedApart()
    {
        // The dev shapes: Immersive Death Cycle_DISTR.ini:2 (a race filter) and Mysticism Restoration_DISTR.ini:98 (chance 20).
        var race = Records("spid reaches Spell = HcSpell|NONE|HcHumanRace,HcBeastRace|NONE|NONE|NONE|NONE", countsOnly: true);
        Assert.Contains("scan: 6 matches", race);
        Assert.Contains("yes 6, not decidable 1", race);
        var chance = Records("spid reaches Spell = HcSpell|NONE|HcHumanRace|NONE|NONE|NONE|20", countsOnly: true);
        Assert.Contains("scan: 0 matches", chance);
        Assert.Contains("needs the running game 5 (chance 20: 5", chance);   // the chain-undecided one too: the running game outranks not decidable
        Assert.Contains("The line's chance is 20 as written", chance);
    }

    [Fact]
    public void APcLevelMultLevelThatStraddlesTheRangeIsNotDecidable()
    {
        var r = Records("spid reaches Spell = HcSpell|||5/10", countsOnly: true);
        Assert.Contains("a PC-level-mult NPC's level, which the player's level sets: 1", r);
        Assert.Contains(Row(ReachWorld.Guard), Records("spid reaches Spell = HcSpell|||5/10"));
        Assert.DoesNotContain(Row(ReachWorld.Bodyguard), Records("spid reaches Spell = HcSpell|||5/10"));
    }

    [Fact]
    public void StringsTraitsAndFormsFilterAsSpidDoes()
    {
        Assert.Contains("scan: 2 matches", Records("spid reaches Spell = HcSpell|*Guard", countsOnly: true));   // HcGuard, HcBodyguard
        Assert.Contains("scan: 1 match", Records("spid reaches Spell = HcSpell|*Guard,-HcBodyguard", countsOnly: true));
        Assert.Contains("scan: 1 match", Records("spid reaches Spell = HcSpell|*Guard|||F", countsOnly: true));
        Assert.Contains("scan: 1 match", Records("spid reaches Spell = HcSpell||HcFaction", countsOnly: true));
        Assert.Contains("scan: 7 matches", Records("spid reaches Spell = HcSpell||HcReach.esp", countsOnly: true));
    }

    [Fact]
    public void AnOutfitIsNotFollowedToWhatItHolds()
    {
        // Q1 (a): the line hands HcOutfit to HcGuard; the armour it holds is no NPC, and the reach term reads NPCs only.
        Assert.Contains(Row(ReachWorld.Guard), Records("spid reaches Outfit = HcOutfit|HcGuard"));
        Assert.Contains("scan: 0 matches", Records("spid reaches Outfit = HcOutfit|HcGuard", formids: new[] { F(ReachWorld.Boots) }));
    }

    [Fact]
    public void AKidLineSeesAKeywordAnEarlierKidLineGives()
    {
        // The dev BOOBIES chain: BOOBIES_GENERAL_KID.ini:16 reads BookTypeJournal, which BOOBIES_ImmersiveIcons_KID.ini:13 gives by '*journal'.
        var r = Records("kid reaches Keyword = HcBoobies|Book|HcBookTypeJournal");
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(Row(ReachWorld.Journal), r);
        Assert.Contains("other Book line(s) of the layer were replayed first", r);
    }

    [Fact]
    public void KidTraitHOnASpellIsNotDecidable()
    {
        // Q4 (a): HcReachSpell's one effect is hostile, but KID's H on a spell stays not decidable.
        var r = Records("kid reaches Keyword = HcAbsorb|Spell|NONE|-H", countsOnly: true);
        Assert.Contains("yes 0, not decidable 4 (trait H on a spell", r);
    }

    [Fact]
    public void AKidTraitReadsTheItemsOwnField()
    {
        var r = Records("kid reaches Keyword = HcTag|Weapon|NONE|E");
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(Row(ReachWorld.FireSword), r);
        Assert.Contains("scan: 2 matches", Records("kid reaches Keyword = HcTag|Weapon|*Sword,-HcWoodSword", countsOnly: true));
    }

    [Fact]
    public void KidsChanceRollIsTheFixedHash()
    {
        // The same keyword and item draw the same number every time, and different keywords draw differently.
        Assert.Equal(KidReach.Roll("OCF_WeapTypeSword1H", 0x00012EB7), KidReach.Roll("OCF_WeapTypeSword1H", 0x00012EB7));
        Assert.NotEqual(KidReach.Roll("OCF_WeapTypeSword1H", 0x00012EB7), KidReach.Roll("OCF_WeapMelee", 0x00012EB7));
        Assert.InRange(KidReach.Roll("OCF_WeapMelee", 0x00012EB7), 0.0, 1.0);
    }

    [Fact]
    public void KidsChanceIsRolledPerItemOffTheFiles()
    {
        // KID's chance is a fixed function of the keyword's EditorID and the item's runtime FormID, so it is decided here.
        var r = Records("kid reaches Keyword = HcTag|Weapon|NONE|NONE|50", countsOnly: true);
        int expected = new uint[] { ReachWorld.IronSword, ReachWorld.FireSword, ReachWorld.WoodSword }.Count(id => KidReach.Roll("HcTag", id) <= 0.5f);
        Assert.Contains($"scan: {expected} match", r);
        Assert.Contains("needs the running game 0", r);
        Assert.NotEqual(3, expected);   // the fixture's draws leave at least one sword out, so a skipped roll would show
    }

    [Fact]
    public void AnExclusiveGroupSiblingKeepsTheLineOff()
    {
        // SPID: HcBodyguard holds HcSpellB, HcSpellA's group sibling. KID: HcIronSword holds HcKidB, HcKidA's.
        Assert.Contains("scan: 1 match", Records("spid reaches Spell = HcSpellA|*Guard", countsOnly: true));
        var kid = Records("kid reaches Keyword = HcKidA|Weapon|*Sword");
        Assert.Contains("scan: 2 matches", kid);
        Assert.DoesNotContain(Row(ReachWorld.IronSword), kid);
    }

    [Fact]
    public void AnItemThatHoldsTheKeywordIsSkipped()
    {
        var r = Records("kid reaches Keyword = HcKidB|Weapon|*Sword", countsOnly: true);
        Assert.Contains("yes 2,", r);
        Assert.Contains("1 already hold the keyword, which KID skips", r);
    }

    [Fact]
    public void ATypesThatContradictsTheLineIsRefused()
    {
        var r = Records("spid reaches Spell = HcSpell", types: new[] { "WEAP" });
        Assert.Contains("names no Npc record", r);
    }

    [Fact]
    public void ALineTheFormatSkipsIsRefusedByName()
    {
        Assert.Contains("SPID skips it", Records("spid reaches Spell = HcSpell|a|b|c|d|e|f|g|h"));
        Assert.Contains("KID skips it", Records("kid reaches nothing here"));
    }

    [Fact]
    public void ALineTheFormatDropsReachesNothing()
    {
        var r = Records("spid reaches Spell = HcNoSuchSpell", countsOnly: true);
        Assert.Contains("scan: 0 matches", r);
        Assert.Contains("SPID drops the line", r);
        // Under types= too the empty selection answers 0, never every NPC of the type.
        Assert.Contains("scan: 0 matches", Records("spid reaches Spell = HcNoSuchSpell", types: new[] { "NPC_" }, countsOnly: true));
    }

    [Fact]
    public void AScopedCallCountsTheBucketsWithinItsScope()
    {
        // HcReachMore.esp adds a human NPC (yes) and one templated on a leveled actor (not decidable).
        using var w = new ReachWorld(more: true);
        var r = RecordsTools.Records(w.Svc, plugins: new RecordsTools.RecordsScope { names = new[] { ReachWorld.Plugin } },
                                     where: new[] { "spid reaches Spell = HcSpell||HcHumanRace" }, counts_only: true);
        Assert.Contains("scan: 4 matches", r);
        Assert.Contains("over 9 NPC record(s): within the call's other terms yes 4, not decidable 1 (its template chain ends at a leveled actor: 1; e.g. "
                        + F(ReachWorld.LeveledTemplated) + "), needs the running game 0; over all 9, yes 5, not decidable 2, needs the running game 0.", r);
        Assert.DoesNotContain(ReachWorld.MoreFk(ReachWorld.MoreLeveled), r);
        // Unscoped, the note counts every NPC once, as before.
        var all = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell||HcHumanRace" }, counts_only: true);
        Assert.Contains("over 9 NPC record(s): yes 5, not decidable 2 (its template chain ends at a leveled actor: 2;", all);
    }

    [Fact]
    public void AReachTermBoundsAGroupByTypeCountTable()
    {
        var byType = new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" };
        var r = RecordsTools.Records(_w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard" }, project: byType);
        Assert.DoesNotContain("error:", r);
        Assert.Contains("grouped by type — 2 matches across 1 group", r);
        Assert.Contains("  Npc = 2", r);
        // Without a reach term the same aggregate still has no body-bearing scope and is refused as before.
        Assert.Contains("error: project.group_by='type' counts each match's record TYPE",
                        RecordsTools.Records(_w.Svc, conflicts_only: true, project: byType));
    }

    [Fact]
    public void AMalformedFieldTermIsRefusedBeforeTheReachPass()
    {
        // The reach line is one SPID skips, so a reach pass run first would refuse with its sentence instead.
        var r = RecordsTools.Records(_w.Svc, where: new[] { "spid reaches Spell = HcSpell|a|b|c|d|e|f|g|h", "editorid" });
        var parse = RecordsTools.Records(_w.Svc, types: new[] { "NPC_" }, where: new[] { "editorid" });
        Assert.DoesNotContain("SPID skips it", r);
        Assert.DoesNotContain("scan:", parse);
        Assert.Equal(parse, r);
    }

    // The reach line below is one SPID skips, so a reach pass run before the other refusals would answer with its sentence.
    const string Skipped = "spid reaches Spell = HcSpell|a|b|c|d|e|f|g|h";

    [Fact]
    public void ATypesNamingNoneOfTheLinesTypesIsRefusedBeforeTheReachPass()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, where: new[] { Skipped });
        Assert.DoesNotContain("SPID skips it", r);
        Assert.Contains("names no Npc record", r);
    }

    [Fact]
    public void AnUnknownTypeIsRefusedBeforeTheReachPass()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "Nppc" }, where: new[] { Skipped });
        Assert.DoesNotContain("SPID skips it", r);
        Assert.Contains("Nppc", r);
    }

    [Fact]
    public void AWrongFieldPathIsRefusedBeforeTheReachPass()
    {
        var r = RecordsTools.Records(_w.Svc, where: new[] { Skipped, "Bogus.Path = 1" });
        Assert.DoesNotContain("SPID skips it", r);
        Assert.Contains("Bogus", r);
    }

    [Fact]
    public void AStaleArtifactIsRefusedBeforeTheReachPass()
    {
        var q = _w.Svc.CrossQuery(null, null, null, false, null, new[] { Skipped }, 10,
                                  artifactDemands: new[] { new ArtifactDemand("stale.json", "deadbeef") });
        Assert.Contains("artifact 'stale.json' was captured at epoch=deadbeef", q.Error);
    }

    [Fact]
    public void AReachTermComposesWithAFieldTerm()
    {
        var r = RecordsTools.Records(_w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard", "editorid = HcBodyguard" });
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(Row(ReachWorld.Bodyguard), r);
    }

    [Fact]
    public void AGroupOfCreatedKeywordsKeepsTheSiblingLineOff()
    {
        // ARR's OCF and Bestiary shape: SPID creates both keywords, and :4 gives HcMadeA to every human NPC first.
        using var w = new ReachWorld(spid: "Keyword = HcMadeA|HcKwHumanoid\r\nExclusiveGroup = HcMadePair|HcMadeA,HcMadeB\r\n");
        var r = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Keyword = HcMadeB|*Hc" }, counts_only: true);
        // Only HcBeast and HcMult (beast race) lack HcMadeA; HcLeveledTemplated's race is its leveled template's.
        Assert.Contains("yes 2, not decidable 1", r);
    }

    [Fact]
    public void AKeywordAKidRaceLineGivesIsNotDecidable()
    {
        // ARR's ABA_KW-OCF_RACE_KID.ini:25 shape: KID tags a race, and a SPID string filter reads the tag.
        using var w = new ReachWorld(kid: "Keyword = HcRaceTag|Race|HcBeastRace\r\n");
        var r = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|HcRaceTag" }, counts_only: true);
        Assert.Contains("yes 0, not decidable 3 (a KID Race line may tag its race, which is not replayed: 2;", r);   // HcBeast, HcMult
        Assert.Contains("KID's Race lines may tag 1 race(s) with a keyword", r);
    }

    [Fact]
    public void AKidRaceLineIsNotedOnlyForALineReadingItsKeyword()
    {
        // ARR's ABB_KW-OCF_NPC_DISTR.ini:10 shape: a line reading no string, beside a KID Race line.
        using var w = new ReachWorld(kid: "Keyword = HcRaceTag|Race|HcBeastRace\r\nKeyword = HcOtherTag|Race|HcHumanRace\r\n");
        var none = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell||HcBeastRace" }, counts_only: true);
        Assert.DoesNotContain("KID's Race lines", none);
        Assert.Contains("yes 2,", none);
        var other = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|HcKwHumanoid" }, counts_only: true);
        Assert.DoesNotContain("KID's Race lines", other);
        // A partial string reading one tag notes it, and only the races that tag is given to.
        var part = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|*RaceT" }, counts_only: true);
        Assert.Contains("KID's Race lines may tag 1 race(s) with a keyword it reads", part);
        Assert.Contains("a KID Race line may tag its race, which is not replayed: 2", part);
    }

    [Fact]
    public void AnEarlierLineOfTheSameFormIsRunFirst()
    {
        // The ARR Bestiary shape (:32 = :154): HcGuard gets HcSpell from :4, so :5 does not reach her.
        using var w = new ReachWorld(spid: "Spell = HcSpell|HcGuard\r\nSpell = HcSpell|*Guard\r\n",
                                     kid: "Keyword = HcTag|Weapon|HcIronSword\r\nKeyword = HcTag|Weapon|*Sword\r\n");
        var second = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard" }, counts_only: true);
        Assert.Contains("yes 1,", second);
        Assert.Contains("1 already have it, on their record or from an earlier line of the same form, which SPID skips", second);
        var first = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|HcGuard" }, counts_only: true);
        Assert.Contains("yes 1,", first);
        Assert.DoesNotContain("earlier line", first);
        var kid = RecordsTools.Records(w.Svc, where: new[] { "kid reaches Keyword = HcTag|Weapon|*Sword" }, counts_only: true);
        Assert.Contains("yes 2,", kid);
        Assert.Contains("1 already hold the keyword, which KID skips", kid);
    }

    [Fact]
    public void ALayerLineSpacedOrCasedOtherwiseIsTheSameLine()
    {
        using var w = new ReachWorld(spid: "Spell = HcSpell|HcGuard\r\nSpell = HcSpell|*Guard\r\n",
                                     kid: "Keyword = HcTag|Weapon|HcIronSword\r\nKeyword = HcTag|Weapon|*Sword\r\n");
        string Note(string term) { var r = RecordsTools.Records(w.Svc, where: new[] { term }, counts_only: true); if (!r.Contains("reach:")) Assert.Fail(r); return r[r.IndexOf("reach:")..].Split('\'')[2]; }
        Assert.Equal(Note("spid reaches Spell = HcSpell|*Guard"), Note("spid reaches Spell =  HCSPELL | *GUARD"));
        Assert.Equal(Note("kid reaches Keyword = HcTag|Weapon|*Sword"), Note("kid reaches Keyword  =  HCTAG|Weapon|*SWORD"));
        Assert.Contains("yes 1,", Note("spid reaches Spell =  HCSPELL | *GUARD"));
        // A line the layer does not hold still runs every same-form line first: both reach one of its two NPCs.
        var off = Note("spid reaches Spell = HcSpell|*Guard,-HcNobody");
        Assert.Contains("yes 0,", off);
        Assert.Contains("2 already have it, on their record or from an earlier line", off);
    }

    [Fact]
    public void AKeywordAnEarlierLineReadsIsReplayedThoughTheAskedLineReadsNone()
    {
        // The dev SpellAbsorptionRework_B_KID.ini:75 shape: the earlier line reads '-HcBookTypeJournal', which :1 gives
        // HcJournal; SPID's :1 likewise gives HcGivenKw to the human NPCs.
        using var w = new ReachWorld(spid: "Spell = HcSpell|-HcGivenKw\r\n", kid: "Keyword = HcTag|Book|-HcBookTypeJournal\r\n");
        var kid = RecordsTools.Records(w.Svc, where: new[] { "kid reaches Keyword = HcTag|Book" }, counts_only: true);
        Assert.Contains("yes 1,", kid);   // HcJournal: the earlier line does not tag it
        Assert.Contains("1 already hold the keyword", kid);   // HcTome
        var spid = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell" }, counts_only: true);
        Assert.Contains("yes 4,", spid);   // the four human NPCs
    }

    [Fact]
    public void AnNpcHoldingTheFormOnItsRecordIsAlreadyHeld()
    {
        // The dev Adamant_DISTR.ini:15 shape: HcBodyguard's spell list holds HcSpellB; HcGuard is in HcFaction; the
        // human NPCs' race carries HcKwHumanoid.
        var spell = Records("spid reaches Spell = HcSpellB|*Guard", countsOnly: true);
        Assert.Contains("yes 1,", spell);
        Assert.Contains("1 already have it, on their record or from an earlier line", spell);
        var faction = Records("spid reaches Faction = HcFaction|*Guard", countsOnly: true);
        Assert.Contains("yes 1,", faction);
        Assert.Contains("1 already have it", faction);
        var keyword = Records("spid reaches Keyword = HcKwHumanoid|*Guard", countsOnly: true);
        Assert.Contains("yes 0,", keyword);
        Assert.Contains("2 already have it", keyword);
        // HcLeveledTemplated's race comes off its leveled template, so whether it holds the keyword is open.
        var open = Records("spid reaches Keyword = HcKwHumanoid|HcLeveledTemplated", countsOnly: true);
        Assert.Contains("it may already hold the form (its template chain ends at a leveled actor): 1", open);
    }

    [Fact]
    public void AScopedCallCountsTheHeldRecordsWithinItsScope()
    {
        // :4 gives HcSpell to every human NPC first; HcReachMore.esp's human NPC is held too, outside plugins=.
        using var w = new ReachWorld(more: true, spid: "Spell = HcSpell|HcKwHumanoid\r\n",
                                     kid: "Keyword = HcTag|Weapon|HcIronSword,HcFireSword\r\n");
        var spid = RecordsTools.Records(w.Svc, plugins: new RecordsTools.RecordsScope { names = new[] { ReachWorld.Plugin } },
                                        where: new[] { "spid reaches Spell = HcSpell||HcHumanRace" }, counts_only: true);
        Assert.Contains("scan: 0 matches", spid);
        Assert.Contains("Within the call's other terms 4 already have it, on their record or from an earlier line of the same form, which SPID skips, so the line does not reach them (5 over all).", spid);
        // KID's earlier-line count (HcIronSword in scope, HcFireSword out) and its own-held count (HcIronSword holds HcKidB).
        var earlier = RecordsTools.Records(w.Svc, formids: new[] { F(ReachWorld.IronSword), F(ReachWorld.WoodSword) },
                                           where: new[] { "kid reaches Keyword = HcTag|Weapon|*Sword" }, counts_only: true);
        Assert.Contains("scan: 1 match", earlier);
        Assert.Contains("Within the call's other terms 1 already hold the keyword, which KID skips, so the line does not reach them (2 over all).", earlier);
        var own = RecordsTools.Records(w.Svc, formids: new[] { F(ReachWorld.WoodSword) },
                                       where: new[] { "kid reaches Keyword = HcKidB|Weapon|*Sword" }, counts_only: true);
        Assert.Contains("Within the call's other terms 0 already hold the keyword, which KID skips, so the line does not reach them (1 over all).", own);
    }

    [Fact]
    public void AnNpcTemplatedOnALeveledListIsOpenToAStringOrNpcFilter()
    {
        // SPID's ID set holds the NPC a leveled template picks, so a filter naming another NPC may match it in game.
        using var w = new ReachWorld(pick: true);
        var form = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell||HcGuard" }, counts_only: true);
        Assert.Contains("yes 1, not decidable 2 (its template is a leveled list, and SPID matches the NPC it picks too: 2;", form);
        var str = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|HcGuard" }, counts_only: true);
        Assert.Contains("its template is a leveled list, and SPID matches the NPC it picks too: 1;", str);
        var part = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard" }, counts_only: true);
        Assert.Contains("its template is a leveled list, and SPID matches the NPC it picks too: 1;", part);
    }

    [Fact]
    public void AChainReachingALeveledListPastAnNpcTemplateIsOpen()
    {
        // The ARR DragonsWeatherNG shape: HcHop -> HcHopMid (an NPC) -> HcLvln.
        using var w = new ReachWorld(extra: mod =>
        {
            Npc N(uint id, string eid, uint template)
            {
                var n = new Npc(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE) { EditorID = eid };
                n.Race.SetTo(new FormKey(mod.ModKey, ReachWorld.BeastRace));
                n.Template.SetTo(new FormKey(mod.ModKey, template));
                mod.Npcs.Add(n);
                return n;
            }
            N(0x870, "HcHopMid", ReachWorld.Lvln);
            N(0x871, "HcHop", 0x870);
        });
        var r = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell||HcGuard" }, counts_only: true);
        Assert.Contains("yes 1, not decidable 3 (", r);
        Assert.Contains("its template chain reaches a leveled list past an NPC template, whose pick SPID may match too: 1", r);
    }

    [Fact]
    public void ALeveledPickLeavesOpenOnlyAStringThatIsAnNpcEditorId()
    {
        // HcPick lacks HcKwHumanoid; a pick adds only NPC EditorIDs, so SPID decides no for a keyword string.
        using var w = new ReachWorld(pick: true);
        var exact = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|HcKwHumanoid" }, counts_only: true);
        Assert.Contains("yes 4, not decidable 1 (", exact);
        Assert.DoesNotContain("SPID matches the NPC it picks too", exact);
        var part = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Humanoid" }, counts_only: true);
        Assert.Contains("yes 4, not decidable 1 (", part);
        Assert.DoesNotContain("SPID matches the NPC it picks too", part);
    }
}

/// <summary>The reach term's arms one test each, every one failing on a mutation of its line (review round 1).</summary>
[Trait("tier", "integration")]
public sealed class RecordReachArmTests
{
    static string F(uint id) => ReachWorld.Fk(id);
    static string Row(uint id) => "  " + F(id) + "  runtime=";
    static string Reach(ReachWorld w, string term, bool countsOnly = true)
        => RecordsTools.Records(w.Svc, where: new[] { term }, counts_only: countsOnly);

    [Fact]
    public void KidsRollIsAFixedKnownValue()
    {
        // Draws worked out apart from Roll, by a separate implementation of the same hash chain; each low bit is set.
        Assert.Equal(0.8846440842744882, KidReach.Roll("OCF_WeapTypeSword1H", 0x000139A1));
        Assert.Equal(0.7182297868870487, KidReach.Roll("OCF_WeapMelee", 0x00012EB9));
    }

    [Fact]
    public void AnUninheritedCategoryIsReadOffTheNpcItself()
    {
        // HcOwnLevel inherits Traits from HcTemplateBase (level 8) but not Stats: its own level 30 decides.
        using var w = new ReachWorld(extra: mod =>
        {
            var n = new Npc(new FormKey(mod.ModKey, 0x828), SkyrimRelease.SkyrimSE) { EditorID = "HcOwnLevel" };
            n.Race.SetTo(new FormKey(mod.ModKey, ReachWorld.HumanRace));
            n.Configuration.Level = new NpcLevel { Level = 30 };
            n.Template.SetTo(new FormKey(mod.ModKey, ReachWorld.TemplateBase));
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
            mod.Npcs.Add(n);
        });
        var r = Reach(w, "spid reaches Spell = HcSpell|||25/40", countsOnly: false);
        Assert.Contains(Row(0x828), r);
    }

    [Fact]
    public void APcLevelMultRangeInsideIsYesAndOutsideIsNo()
    {
        using var w = new ReachWorld();
        Assert.Contains(Row(ReachWorld.Mult), Reach(w, "spid reaches Spell = HcSpell|||1/60", countsOnly: false));   // CalcMin 1, CalcMax 50
        Assert.Contains("yes 0, not decidable 0,", Reach(w, "spid reaches Spell = HcSpell|||60/70"));
    }

    [Fact]
    public void AFeederIsReplayedWhenAPartialStringOrAGroupReadsItsKeyword()
    {
        // :1 gives HcGivenKw to the human NPCs; the asked line reads it by part of its name.
        using var w = new ReachWorld(spid: "Keyword = HcMadeA|HcKwHumanoid\r\nExclusiveGroup = HcMadePair|HcMadeA,HcMadeB\r\n");
        Assert.Contains("yes 4,", Reach(w, "spid reaches Spell = HcSpell|*GivenK"));
        // A line reading only forms still sees HcMadeA, its group sibling, which :4 gives the human NPCs.
        Assert.Contains("yes 0, not decidable 1", Reach(w, "spid reaches Keyword = HcMadeB||HcHumanRace"));
    }

    [Fact]
    public void AKidFeederIsReplayedForAKeywordRecordAPartialStringOrAFormList()
    {
        using var w = new ReachWorld(kid: "Keyword = HcKidA|Book|*journal\r\n", extra: mod =>
        {
            var list = new FormList(new FormKey(mod.ModKey, 0x862), SkyrimRelease.SkyrimSE) { EditorID = "HcKwList" };
            list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(new FormKey(mod.ModKey, ReachWorld.KidA)));
            mod.FormLists.Add(list);
        });
        Assert.Contains(Row(ReachWorld.Journal), Reach(w, "kid reaches Keyword = HcQ1|Book|HcKidA", countsOnly: false));
        Assert.Contains(Row(ReachWorld.Journal), Reach(w, "kid reaches Keyword = HcQ2|Book|*kida", countsOnly: false));
        Assert.Contains(Row(ReachWorld.Journal), Reach(w, "kid reaches Keyword = HcQ3|Book|HcKwList", countsOnly: false));
    }

    [Fact]
    public void AMagicEffectLineFeedsASpellsCostliestEffect()
    {
        // HcReachSpell's one effect is tagged; HcTwoSpell has a tagged and an untagged one, so its costliest decides.
        using var w = new ReachWorld(kid: "Keyword = HcMgefTag|Magic Effect|HcMgef\r\n", extra: mod =>
        {
            mod.MagicEffects.Add(new MagicEffect(new FormKey(mod.ModKey, 0x860), SkyrimRelease.SkyrimSE) { EditorID = "HcMgef2" });
            var s = new Spell(new FormKey(mod.ModKey, 0x861), SkyrimRelease.SkyrimSE) { EditorID = "HcTwoSpell" };
            s.Effects.Add(new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(new FormKey(mod.ModKey, ReachWorld.Mgef)), Data = new EffectData() });
            s.Effects.Add(new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(new FormKey(mod.ModKey, 0x860)), Data = new EffectData() });
            mod.Spells.Add(s);
        });
        var r = Reach(w, "kid reaches Keyword = HcS|Spell|HcMgefTag");
        Assert.Contains("yes 1, not decidable 1 (its costliest effect decides, which the engine computes: 1", r);
    }

    [Fact]
    public void AReachTermOnAnOffOrderSourceIsRefused()
    {
        using var w = new ReachWorld(off: true);
        var r = RecordsTools.Records(w.Svc, source: ApplyGuardWorld.Je("\"" + ReachWorld.Off + "\""), where: new[] { "spid reaches Spell = HcSpell" });
        Assert.Contains(RecordReads.ReachOffOrderRefusal, r);
    }

    [Fact]
    public void KidsChanceReadsTheRuntimeFormIdOffTheLoadOrder()
    {
        // HcReach.esp loads second, so its records run 01xxxxxx in game: HcDice draws under 50 for 01000830 only.
        using var w = new ReachWorld(first: true);
        var r = Reach(w, "kid reaches Keyword = HcDice|Weapon|NONE|NONE|50", countsOnly: false);
        Assert.Contains(Row(ReachWorld.IronSword), r);
        Assert.DoesNotContain(Row(ReachWorld.FireSword), r);
    }

    [Fact]
    public void AFeederChainIsReplayedUntilStable()
    {
        // :4 reads the keyword :5 gives, so a second pass is needed, and :5 is kept only because :4 reads it.
        using var w = new ReachWorld(spid: "Keyword = HcChainB|HcChainA\r\nKeyword = HcChainA|HcKwHumanoid\r\n",
                                     kid: "Keyword = HcKChainB|Book|HcKChainA\r\nKeyword = HcKChainA|Book|*journal\r\n");
        Assert.Contains("yes 4,", Reach(w, "spid reaches Spell = HcSpell|HcChainB"));
        Assert.Contains(Row(ReachWorld.Journal), Reach(w, "kid reaches Keyword = HcKQ|Book|HcKChainB", countsOnly: false));
    }

    [Fact]
    public void AKeywordSiblingTheNpcHoldsKeepsTheLineOff()
    {
        // The human NPCs hold HcKwHumanoid through their race, HcSibOther's group sibling.
        using var w = new ReachWorld(spid: "ExclusiveGroup = HcKwPair|HcKwHumanoid,HcSibOther\r\n");
        Assert.Contains("yes 2, not decidable 1", Reach(w, "spid reaches Keyword = HcSibOther|*Hc"));
    }

    [Fact]
    public void AGroupSiblingKeepsOffTheNpcThatHoldsIt()
    {
        using var w = new ReachWorld();
        var r = Reach(w, "spid reaches Spell = HcSpellA|*Guard", countsOnly: false);
        Assert.Contains(Row(ReachWorld.Guard), r);
        Assert.DoesNotContain(Row(ReachWorld.Bodyguard), r);   // holds HcSpellB
    }

    [Fact]
    public void TwoReachTermsAreAnded()
    {
        using var w = new ReachWorld();
        var r = RecordsTools.Records(w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard", "spid reaches Spell = HcSpell|-HcBodyguard" });
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(Row(ReachWorld.Guard), r);
    }

    [Fact]
    public void AGroupALinkedOrAnUnreadableLineIsRefused()
    {
        using var w = new ReachWorld();
        Assert.Contains("is an ExclusiveGroup line", Reach(w, "spid reaches ExclusiveGroup = HcG|HcSpellA,HcSpellB"));
        Assert.Contains("is a Linked line", Reach(w, "spid reaches LinkedSpell = HcSpell|HcSpellA"));
        Assert.Contains("is no SPID line", Reach(w, "spid reaches Spell = HcSpell\nSpell = HcSpellA"));   // two lines in one term
    }

    [Fact]
    public void OnlyAWholeOperatorMakesAReachTerm()
    {
        // 'exactly' is as long as 'reaches', so only the operator test keeps that term a field term.
        var (others, terms) = ReachTerm.Split(new[] { "spid reachesX Spell = A", "kid", "kid contains x", "spid exactly Spell = A", "SPID  REACHES  Spell = A" });
        Assert.Equal(new[] { "spid reachesX Spell = A", "kid", "kid contains x", "spid exactly Spell = A" }, others);
        Assert.Equal("Spell = A", Assert.Single(terms).Line);
    }

    [Fact]
    public void ReachFieldsMissingAReaderFailsTheLoad()
    {
        var readers = string.Join(",", Enum.GetNames<ReachFields.NpcReader>().Where(n => n != "Outfit").Select(n => $"\"{n}\": \"Traits\""));
        var json = "{\"npcTemplate\":{\"readers\":{" + readers + "}},\"kidForms\":{\"fields\":[]},\"kidStrings\":{\"archetypes\":[],\"actorValues\":[]}}";
        var ex = Assert.Throws<InvalidOperationException>(() => ReachFields.Parse(json));
        Assert.Contains("names no category for 'Outfit'", ex.Message);
    }
}
