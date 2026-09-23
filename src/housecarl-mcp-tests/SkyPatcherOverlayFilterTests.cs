using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>Migrated from the skypatcher-overlay-guard probe's Wave-2 filter arm: one record per evaluation kind,
/// including the skip filters and the player rule, where a wrong model patches exactly what the author excluded.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherOverlayFilterTests
{
    static SkyrimMod NewMod() => new(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
    static SkyPatcherOverlay.OrderedLine L(int n, string text) => Line("f.ini", n, text);

    static Weapon Bow()
    {
        var w = NewMod().Weapons.AddNew();
        w.EditorID = "HcBow";
        w.Data = new WeaponData { Skill = Skill.Archery, AnimationType = WeaponAnimationType.Bow, Flags = WeaponData.Flag.BoundWeapon };
        w.BasicStats = new WeaponBasicStats { Damage = 5, Weight = 1, Value = 1 };
        Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(),
            L(1, "filterBySkills=marksman:attackDamage=21"),
            L(2, "filterByAnimationTypeOr=crossbow:attackDamage=1"),
            L(3, "restrictToFlags=boundweapon:weight=3"));
        return w;
    }

    [Fact] // enumEquals + valueMap: marksman matched the Archery-skill bow (damage=21); enum no-match leaves the record alone (crossbow line)
    public void AnEnumFilterMatchesThroughItsValueMapAndANonMatchLeavesTheRecord() => Assert.Equal(21, Bow().BasicStats!.Damage);

    [Fact] // flagAnyOf: boundweapon flag matched (weight=3)
    public void AFlagAnyOfFilterMatchesIgnoringCase() => Assert.Equal(3f, Bow().BasicStats!.Weight, 3);

    static IEnumerable<int> AppliedLines(SkyPatcherOverlay.SkyPatcherOverlayResult r, string op)
        => r.Applied.Where(a => a.Op == op).Select(a => a.LineNumber);

    sealed record NpcRun(Npc Npc, SkyPatcherOverlay.SkyPatcherOverlayResult Result);

    static NpcRun FilteredNpc()
    {
        var raceFk = new FormKey(new ModKey("HcRace", ModType.Plugin), 0xC01);
        var n = NewMod().Npcs.AddNew();
        n.EditorID = "HcFilterNpc";
        n.Configuration.Flags |= NpcConfiguration.Flag.Female | NpcConfiguration.Flag.Essential;
        n.Configuration.Level = new PcLevelMult { LevelMult = 1.15f };
        n.Race.SetTo(raceFk);
        n.Class.SetTo(new FormKey(new ModKey("HcCls", ModType.Plugin), 0xD01));
        n.Factions.Add(new RankPlacement { Faction = new FormKey(new ModKey("HcFac", ModType.Plugin), 0xA02).ToLink<IFactionGetter>(), Rank = 0 });
        var resolver = new StubResolver();
        resolver.WinnerLeafs[raceFk] = "Actors/Character/Character Assets/skeleton.nif";
        var r = Apply(n, n.FormKey, n.EditorID, "npc", "Npc", resolver,
            L(1, "filterByGender=female:weight=44"),
            L(2, "filterByGender=male:weight=1"),
            L(3, "filterByEssential=true:height=1.1"),
            L(4, "filterByProtected=true:height=9"),
            L(5, "filterByPCLevelMult=true:fullName=~Scaled~"),
            L(6, "filterByFactionsOr=HcFac.esp|A02,HcFac.esp|FFF:shortName=~Fac~"),
            L(7, "filterByRaces=HcRace.esp|C01:weight=55"),
            L(8, "filterByClassExclude=HcCls.esp|D01:height=7"),
            L(9, "restrictToMaleModelContains=skeleton:deathItem=HcItm.esp|900"),
            L(10, "restrictToSkill=onehanded~20~60:calcLevelMax=99"));
        return new(n, r);
    }

    [Fact] // gender=female matched, male didn't; race formEquals matched last (weight 44->55)
    public void GenderAndRaceFiltersMatchInOrder()
    {
        var run = FilteredNpc();
        Assert.Equal(55f, run.Npc.Weight, 3);
        Assert.Equal(new[] { 1, 7 }, AppliedLines(run.Result, "weight"));
    }

    [Fact] // flagBool: essential matched, protected didn't; class-Exclude skipped (height=1.1)
    public void AFlagBoolFilterMatchesAndAClassExcludeSkips() => Assert.Equal(1.1f, FilteredNpc().Npc.Height, 3);

    [Fact] // pcLevelMult arm detected (fullName applied)
    public void APcLevelMultFilterMatches() => Assert.Equal("Scaled", FilteredNpc().Npc.Name?.String);

    [Fact] // formInList keyPath (factions, Or) matched
    public void AFactionOrFilterMatches() => Assert.Equal("Fac", FilteredNpc().Npc.ShortName?.String);

    [Fact] // donorSubstring: NPC skeleton read off its RACE's winner (deathItem set)
    public void AModelFilterReadsTheSkeletonOffTheRace()
        => Assert.Equal(new FormKey(new ModKey("HcItm", ModType.Plugin), 0x900), FilteredNpc().Npc.DeathItem?.FormKey);

    [Fact] // restrictToSkill is explicitly unmapped => loud skip, calcLevelMax untouched
    public void RestrictToSkillSkipsLoud()
    {
        var run = FilteredNpc();
        Assert.NotEqual((short)99, run.Npc.Configuration.CalcMaxLevel);
        Assert.Contains(run.Result.Warnings, w => w.Contains("restrictToSkill") && w.Contains("no static evaluation"));
    }

    static NpcRun Player()
    {
        var resolver = new StubResolver();
        resolver.Plugins.Add("HcGate.esp");
        var player = new Npc(new FormKey(new ModKey("Skyrim", ModType.Master), 0x7), SkyrimRelease.SkyrimSE);
        var r = Apply(player, player.FormKey, "Player", "npc", "Npc", resolver,
            L(1, "weight=9"),
            L(2, "filterByGender=male:weight=3"),
            L(3, "filterByNpcs=Skyrim.esm|7:filterByEssential=false:weight=5"),
            L(4, "filterByNpcs=Skyrim.esm|7:weight=7"),
            L(5, "hasPlugins=HcGate.esp:filterByNpcs=Skyrim.esm|7:height=2"));
        return new(player, r);
    }

    [Fact] // player rule: only the lone bare primary applied (weight=7, not 9/3/5)
    public void OnlyALoneBarePrimaryReachesThePlayer()
    {
        var run = Player();
        Assert.Equal(7f, run.Npc.Weight, 3);
        Assert.Equal(new[] { 4 }, AppliedLines(run.Result, "weight"));
    }

    [Fact] // hasPlugins on an npc line is not in the reference: unknown-key LOUD skip, player untouched
    public void HasPluginsOnAnNpcLineIsAnUnknownKey()
    {
        var run = Player();
        Assert.Equal(0f, run.Npc.Height, 3);
        Assert.Contains(run.Result.Warnings, w => w.Contains("hasPlugins") && w.Contains("not in the SkyPatcher reference"));
    }

    [Fact] // EnumEquals: an unknown enum token is a LOUD unresolved skip; filterByModNames defining-master vs winning-override DISAGREEMENT is unresolved loud; agreement answers
    public void AnUnknownEnumTokenAndAModNameDisagreementSkipLoud()
    {
        var w = NewMod().Weapons.AddNew();
        w.EditorID = "HcEnumWarnBow";
        w.Data = new WeaponData { Skill = Skill.Archery };
        w.BasicStats = new WeaponBasicStats { Damage = 5 };
        var resolver = new StubResolver();
        resolver.Winners[w.FormKey] = "WinOverride.esp";
        var r = Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", resolver,
            L(1, "filterBySkills=notaskill:attackDamage=50"),
            L(2, "filterByModNames=WinOverride.esp:attackDamage=60"),
            L(3, "filterByModNames=Unrelated.esp:attackDamage=70"));
        Assert.Equal(5, w.BasicStats!.Damage);
        Assert.Contains(r.Warnings, x => x.Contains("notaskill") && x.Contains("UNRESOLVED"));
        Assert.Contains(r.Warnings, x => x.Contains("disagree on membership"));
        Assert.True(r.LinesSkippedUnresolvedFilter >= 2);
    }

    [Fact] // gender on a TRAITS-templated NPC is unresolved loud (own Female bit not authoritative)
    public void GenderOnATraitsTemplatedNpcIsUnresolved()
    {
        var npc = NewMod().Npcs.AddNew();
        npc.EditorID = "HcTemplatedNpc";
        npc.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(), L(1, "filterByGender=female:weight=44"));
        Assert.NotEqual(44f, npc.Weight, 3);
        Assert.Contains(r.Warnings, x => x.Contains("templates its TRAITS"));
    }

    static (Ammunition Ammo, SkyPatcherOverlay.SkyPatcherOverlayResult Result) Arrow()
    {
        var a = NewMod().Ammunitions.AddNew();
        a.EditorID = "HcArrow";
        a.Weight = 0.1f;
        a.Flags = Ammunition.Flag.NonBolt;
        var r = Apply(a, a.FormKey, a.EditorID, "ammo", "Ammunition", new StubResolver(),
            L(1, "filterByWeightLessThan=0.5:value=20"),
            L(2, "filterByWeightLessThan=0.05:value=1"),
            L(3, "restrictToBolts=true:weight=9"),
            L(4, "restrictToBolts=false:weight=0.75"));
        return (a, r);
    }

    [Fact] // numericLess (weightLessThan 0.5 yes, 0.05 no): value=20
    public void AWeightLessThanFilterCompares() => Assert.Equal(20u, Arrow().Ammo.Value);

    [Fact] // restrictToBolts inverts the NonBolt flag (arrow: true no, false yes): weight=0.75
    public void RestrictToBoltsInvertsTheNonBoltFlag()
    {
        var (ammo, r) = Arrow();
        Assert.Equal(0.75f, ammo.Weight, 3);
        Assert.Equal(new[] { 4 }, AppliedLines(r, "weight"));
    }

    static (Armor Armor, SkyPatcherOverlay.SkyPatcherOverlayResult Result) Cuirass()
    {
        var aaFk = new FormKey(new ModKey("HcAA", ModType.Plugin), 0xE01);
        var ar = NewMod().Armors.AddNew();
        ar.EditorID = "HcCuirass";
        ar.BodyTemplate = new BodyTemplate { ArmorType = ArmorType.HeavyArmor, FirstPersonFlags = BipedObjectFlag.Body };
        ar.Armature.Add(aaFk.ToLink<IArmorAddonGetter>());
        var resolver = new StubResolver();
        resolver.Eids[aaFk] = "HcTestAAHeavyBody";
        var r = Apply(ar, ar.FormKey, ar.EditorID, "armor", "Armor", resolver,
            L(1, "filterByBipedSlots=2:weight=4"),
            L(2, "filterByBipedSlotsOr=0,9:weight=9"),
            L(3, "filterByArmorTypes=heavy:damageResist=30"),
            L(4, "filterByArmorAddons=TestAA:weight=6"));
        return (ar, r);
    }

    [Fact] // bipedSlots: index 2 (Body) matched, 0/9 didn't; addon EID substring matched (weight 4->6)
    public void BipedSlotAndArmorAddonFiltersMatch()
    {
        var (armor, r) = Cuirass();
        Assert.Equal(6f, armor.Weight, 3);
        Assert.Equal(new[] { 1, 4 }, AppliedLines(r, "weight"));
    }

    [Fact] // filterByArmorTypes heavy->HeavyArmor (damageResist=30)
    public void AnArmorTypeFilterMatchesThroughItsValueMap() => Assert.Equal(30f, Cuirass().Armor.ArmorRating, 3);

    static (MagicEffect Mgef, SkyPatcherOverlay.SkyPatcherOverlayResult Result) Mgef()
    {
        var g = NewMod().MagicEffects.AddNew();
        g.EditorID = "HcMgef";
        g.Flags |= MagicEffect.Flag.Detrimental;
        g.HitShader.SetTo(new FormKey(new ModKey("HcShd", ModType.Plugin), 0xE01));
        var resolver = new StubResolver();
        resolver.Winners[g.FormKey] = "WinPatch.esp";
        var r = Apply(g, g.FormKey, g.EditorID, "magicEffect", "MagicEffect", resolver,
            L(1, "modNamesLastOverriddenExcluded=WinPatch.esp:baseCost=9"),
            L(2, "modNamesLastOverriddenExcluded=Other.esp:baseCost=12"),
            L(3, "effectShadersExcluded=HcShd.esp|E01:spellmakingArea=3"),
            L(4, "effectShadersExcluded=HcShd.esp|E02:spellmakingArea=7"),
            L(5, "restrictToDetrimentalEffects=true:spellmakingCastingTime=2"));
        return (g, r);
    }

    [Fact] // modNamesLastOverriddenExcluded YIELDS to the winning plugin (baseCost=12, never 9)
    public void LastOverriddenExcludedYieldsToTheWinningPlugin()
    {
        var (mgef, r) = Mgef();
        Assert.Equal(12f, mgef.BaseCost, 3);
        Assert.Equal(new[] { 2 }, AppliedLines(r, "baseCost"));
    }

    [Fact] // effectShadersExcluded reads the attached shader (area=7, never 3)
    public void EffectShadersExcludedReadsTheAttachedShader()
    {
        var (mgef, r) = Mgef();
        Assert.Equal(7u, mgef.SpellmakingArea);
        Assert.Equal(new[] { 4 }, AppliedLines(r, "spellmakingArea"));
    }

    [Fact] // restrictToDetrimentalEffects flagBool (castingTime=2)
    public void RestrictToDetrimentalMatchesTheFlag() => Assert.Equal(2f, Mgef().Mgef.SpellmakingCastingTime, 3);

    [Fact] // cobj: ingredient + workbench + donor-keyword filters all matched (3 count sets, final 3)
    public void RecipeFiltersReadIngredientsWorkbenchAndTheCreatedObjectsKeywords()
    {
        var createdFk = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x910);
        var c = NewMod().ConstructibleObjects.AddNew();
        c.EditorID = "HcRecipe";
        c.CreatedObject.SetTo(createdFk);
        c.WorkbenchKeyword.SetTo(new FormKey(new ModKey("HcKw", ModType.Plugin), 0xF01));
        c.Items = new() { new ContainerEntry { Item = new ContainerItem { Item = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x920).ToLink<IItemGetter>(), Count = 2 } } };
        var resolver = new StubResolver();
        resolver.Keywords[createdFk] = new[] { new FormKey(new ModKey("HcKw", ModType.Plugin), 0x820) };
        var r = Apply(c, c.FormKey, c.EditorID, "constructibleObject", "ConstructibleObject", resolver,
            L(1, "filterByIngredients=HcItm.esp|920:count=7"),
            L(2, "filterByWorkBenchKeywords=HcKw.esp|F01:count=5"),
            L(3, "filterByKeywords=HcKw.esp|820:count=3"),
            L(4, "filterByKeywordsExcluded=HcKw.esp|820:count=1"));
        Assert.Equal(3, r.Applied.Count(x => x.Op == "count"));
        Assert.Equal((ushort)3, c.CreatedObjectCount);
    }

    [Fact] // cell skip filters: Lux-template line yielded, ELFX one applied, origin-substring skipped (fogNear=200)
    public void CellSkipFiltersYieldOnTemplateOriginAndRecordOrigin()
    {
        var cell = new Cell(new FormKey(new ModKey("HcSpOv", ModType.Plugin), 0xCE1), SkyrimRelease.SkyrimSE);
        cell.EditorID = "HcCell";
        cell.LightingTemplate.SetTo(new FormKey(new ModKey("Lux", ModType.Plugin), 0xC01));
        var r = Apply(cell, cell.FormKey, cell.EditorID, "cell", "Cell", new StubResolver(),
            L(1, "skipRecordByLightingTemplateFromMod=Lux.esp:fogNear=100"),
            L(2, "skipRecordByLightingTemplateFromMod=ELFX.esp:fogNear=200"),
            L(3, "skipRecordByModNameContains=HcSp:fogNear=300"));
        Assert.Equal(200f, cell.Lighting?.FogNear ?? 0, 3);
        Assert.Equal(new[] { 2 }, AppliedLines(r, "fogNear"));
    }

    [Fact] // race substringLeaf: male skeleton path matched, absent female didn't (carryweight=150)
    public void ARaceModelFilterReadsTheGenderedSkeleton()
    {
        var race = NewMod().Races.AddNew();
        race.EditorID = "HcRace";
        race.SkeletalModel = new GenderedItem<SimpleModel?>(new SimpleModel { File = "Actors/Character/Character Assets/skeleton.nif" }, null);
        Apply(race, race.FormKey, race.EditorID, "race", "Race", new StubResolver(),
            L(1, "filterByMaleModelContains=skeleton:baseCarryweight=150"),
            L(2, "filterByFemaleModelContains=skeleton:baseCarryweight=9"));
        Assert.Equal(150f, race.BaseCarryWeight, 3);
    }
}
