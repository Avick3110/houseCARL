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
        Guard = 0x820, Bodyguard = 0x821, Beast = 0x822, TemplateBase = 0x823, Templated = 0x824, LeveledTemplated = 0x825, Mult = 0x826,
        IronSword = 0x830, FireSword = 0x831, WoodSword = 0x832, Journal = 0x840, Tome = 0x841, ReachSpell = 0x850,
        SpellA = 0x851, SpellB = 0x852, KidA = 0x853, KidB = 0x854;

    public const string Spid =
        "Keyword = HcGivenKw|HcKwHumanoid\r\n"                         // :1 gives HcGivenKw to every NPC whose race carries HcKwHumanoid
        + "Keyword = HcChanceKw|HcGuard|NONE|NONE|NONE|NONE|50\r\n"    // :2 gives HcChanceKw to HcGuard, at chance 50
        + "ExclusiveGroup = HcPair|HcSpellA,HcSpellB\r\n";             // :3 HcBodyguard already holds HcSpellB
    public const string Kid = "Keyword = HcBookTypeJournal|Book|*journal\r\n"   // :1 the BOOBIES shape: a keyword KID creates, by part of a name
        + "ExclusiveGroup = HcKidPair|HcKidA,HcKidB\r\n";                            // :2 HcIronSword already holds HcKidB

    public ReachWorld()
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

        Weapon W(uint id, string eid) { var w = new Weapon(K(id), SkyrimRelease.SkyrimSE) { EditorID = eid, Name = eid }; mod.Weapons.Add(w); return w; }
        W(IronSword, "HcIronSword").Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kidB.ToLink() };
        W(FireSword, "HcFireSword").ObjectEffect.SetTo(K(Ench));
        W(WoodSword, "HcWoodSword");
        mod.Books.Add(new Book(K(Journal), SkyrimRelease.SkyrimSE) { EditorID = "HcJournal", Name = "Old journal" });
        mod.Books.Add(new Book(K(Tome), SkyrimRelease.SkyrimSE) { EditorID = "HcTome", Name = "Tome" });
        var spell = new Spell(K(ReachSpell), SkyrimRelease.SkyrimSE) { EditorID = "HcReachSpell" };
        spell.Effects.Add(new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(K(Mgef)), Data = new EffectData() });
        mod.Spells.Add(spell);
        mod.BeginWrite.ToPath(Path.Combine(mods, "HcReachPlugins", Plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(iniMod, "HcReach_DISTR.ini"), Spid);
        File.WriteAllText(Path.Combine(iniMod, "HcReach_KID.ini"), Kid);
        File.WriteAllText(Path.Combine(profileDir, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + Plugin + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*" + Plugin + "\r\n");
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

    [Fact]
    public void ARaceLineReadsATemplatedNpcThroughItsChain()
    {
        // Q2 (b): HcTemplated's own race is the beast's, but it takes Traits from a human template.
        var r = Records("spid reaches Spell = HcSpell||HcHumanRace");
        Assert.Contains(F(ReachWorld.Templated), r);
        Assert.Contains(F(ReachWorld.Guard), r);
        Assert.DoesNotContain(F(ReachWorld.Beast), r);
    }

    [Fact]
    public void AChainEndingAtALeveledActorIsNotDecidable()
    {
        var r = Records("spid reaches Spell = HcSpell||HcHumanRace", countsOnly: true);
        Assert.Contains("not decidable 1 (its template chain ends at a leveled actor: 1", r);
        Assert.DoesNotContain(F(ReachWorld.LeveledTemplated) + "  ", Records("spid reaches Spell = HcSpell||HcHumanRace"));
    }

    [Fact]
    public void AKeywordAKeywordLineGivesCounts()
    {
        // Q3 (b): HcGivenKw exists on no record; SPID's :1 gives it to every NPC of a race carrying HcKwHumanoid.
        var r = Records("spid reaches Spell = HcSpell|HcGivenKw");
        Assert.Contains("scan: 4 matches", r);   // HcGuard, HcBodyguard, HcTemplateBase, and HcTemplated through its chain
        Assert.Contains(F(ReachWorld.Bodyguard), r);
        Assert.DoesNotContain(F(ReachWorld.Beast), r);
        Assert.Contains("Keyword line(s) of the layer were replayed first", r);
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
        Assert.Contains(F(ReachWorld.Guard), Records("spid reaches Spell = HcSpell|||5/10"));
        Assert.DoesNotContain(F(ReachWorld.Bodyguard), Records("spid reaches Spell = HcSpell|||5/10"));
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
        Assert.Contains(F(ReachWorld.Guard), Records("spid reaches Outfit = HcOutfit|HcGuard"));
        Assert.Contains("scan: 0 matches", Records("spid reaches Outfit = HcOutfit|HcGuard", formids: new[] { F(ReachWorld.Boots) }));
    }

    [Fact]
    public void AKidLineSeesAKeywordAnEarlierKidLineGives()
    {
        // The dev BOOBIES chain: BOOBIES_GENERAL_KID.ini:16 reads BookTypeJournal, which BOOBIES_ImmersiveIcons_KID.ini:13 gives by '*journal'.
        var r = Records("kid reaches Keyword = HcBoobies|Book|HcBookTypeJournal");
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(F(ReachWorld.Journal), r);
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
        Assert.Contains(F(ReachWorld.FireSword), r);
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
        Assert.DoesNotContain(F(ReachWorld.IronSword) + "  ", kid);
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
    }

    [Fact]
    public void AReachTermComposesWithAFieldTerm()
    {
        var r = RecordsTools.Records(_w.Svc, where: new[] { "spid reaches Spell = HcSpell|*Guard", "editorid = HcBodyguard" });
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(F(ReachWorld.Bodyguard), r);
    }
}
