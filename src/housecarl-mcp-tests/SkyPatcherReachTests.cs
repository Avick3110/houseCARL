using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>SkyPatcher 8.0.1's reach rule on one record: finders add up, narrowers only remove, the direct list is
/// patched past them, the player rule, and each per-patcher difference the catalog carries.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherReachTests
{
    static readonly ModKey Mine = new("HcSpOv", ModType.Plugin);
    static readonly ModKey SkyrimEsm = new("Skyrim", ModType.Master);
    static readonly FormKey Kw = new(new ModKey("HcKw", ModType.Plugin), 0x801);
    static SkyrimMod NewMod() => new(Mine, SkyrimRelease.SkyrimSE);
    static SkyPatcherOverlay.OrderedLine L(int n, string text) => Line("f.ini", n, text);
    static string Sp(FormKey fk) => $"{fk.ModKey.FileName}|{fk.ID:X}";

    static IEnumerable<int> AppliedLines(SkyPatcherOverlay.SkyPatcherOverlayResult r, string op)
        => r.Applied.Where(a => a.Op == op).Select(a => a.LineNumber);

    static Weapon NewWeapon(FormKey fk, params FormKey[] keywords)
    {
        var w = new Weapon(fk, SkyrimRelease.SkyrimSE)
        {
            EditorID = "HcW" + fk.ID.ToString("X"),
            Data = new WeaponData { Skill = Skill.OneHanded },
            BasicStats = new WeaponBasicStats { Weight = 1, Value = 1 },
        };
        if (keywords.Length > 0)
            w.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>>(keywords.Select(k => k.ToLink<IKeywordGetter>()));
        return w;
    }

    // R4 D5's line: the keyword finder and the direct list add up, so the iron sword AND the bow get weight 6
    [Fact]
    public void FindersAndTheDirectListAddUp()
    {
        const string line = "filterByKeywords=Skyrim.esm|1E715:filterByWeapons=Skyrim.esm|12EB7:weight=6";
        var sword = NewWeapon(new FormKey(SkyrimEsm, 0x12EB7));
        var bow = NewWeapon(new FormKey(SkyrimEsm, 0x13985), new FormKey(SkyrimEsm, 0x1E715));
        var dagger = NewWeapon(new FormKey(SkyrimEsm, 0x1397E));
        foreach (var w in new[] { sword, bow, dagger })
            Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(), L(1, line));
        Assert.Equal(6f, sword.BasicStats!.Weight, 3);
        Assert.Equal(6f, bow.BasicStats!.Weight, 3);
        Assert.Equal(1f, dagger.BasicStats!.Weight, 3);
    }

    // Excluded keys, restrictions and filterByModNames never remove a listed record; hasPlugins stops the whole line
    [Fact]
    public void OnlyAGateStopsTheDirectList()
    {
        var w = NewWeapon(new FormKey(Mine, 0x900), Kw);
        var me = Sp(w.FormKey);
        var r = Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(),
            L(1, $"filterByWeapons={me}:filterByKeywordsExcluded=HcKw.esp|801:weight=1"),
            L(2, $"filterByWeapons={me}:restrictToSkills=twohanded:weight=2"),
            L(3, $"filterByWeapons={me}:filterByModNames=Other.esp:weight=3"),
            L(4, $"hasPlugins=Missing.esp:filterByWeapons={me}:weight=4"));
        Assert.Equal(new[] { 1, 2, 3 }, AppliedLines(r, "weight"));
    }

    // filterByModNames is case-exact
    [Fact]
    public void ModNamesAreCaseExact()
    {
        var w = NewWeapon(new FormKey(Mine, 0x901));
        var r = Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(),
            L(1, "filterByModNames=hcspov.esp:value=5"),
            L(2, "filterByModNames=HcSpOv.esp:value=6"));
        Assert.Equal(new[] { 2 }, AppliedLines(r, "value"));
    }

    // a finder token that resolves to nothing drops out, so the present one still finds; a finder whose tokens all miss finds nothing
    [Fact]
    public void AMissingFinderTokenDropsOut()
    {
        var w = NewWeapon(new FormKey(Mine, 0x902), Kw);
        var resolver = new StubResolver();
        resolver.EditorIds["HcKwA"] = Kw;
        var r = Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", resolver,
            L(1, "filterByKeywords=HcNoSuchKw,HcKwA:weight=8"),
            L(2, "filterByKeywords=HcNoSuchKw:weight=9"));
        Assert.Equal(new[] { 1 }, AppliedLines(r, "weight"));
        Assert.Contains(r.Warnings, x => x.Contains("HcNoSuchKw"));
    }

    // Leveled List: a filterless line patches nothing; noFilterLL finds every item list
    [Fact]
    public void AFilterlessLeveledListLinePatchesNothing()
    {
        var ll = NewMod().LeveledItems.AddNew();
        ll.EditorID = "HcLL";
        var r = Apply(ll, ll.FormKey, ll.EditorID, "leveledList", "LeveledItem", new StubResolver(),
            L(1, "chanceGlobal=HcSpOv.esp|A10"),
            L(2, "noFilterLL=true:chanceGlobal=HcSpOv.esp|A11"));
        Assert.Equal(new[] { 2 }, AppliedLines(r, "chanceGlobal"));
    }

    // Cell: a filterless line patches every cell, and filterByModNames is not read there, so its line applies as if it were absent
    [Fact]
    public void CellPatchesEveryCellAndIgnoresModNames()
    {
        var cell = new Cell(new FormKey(Mine, 0xCE2), SkyrimRelease.SkyrimSE) { EditorID = "HcCell2" };
        var r = Apply(cell, cell.FormKey, cell.EditorID, "cell", "Cell", new StubResolver(),
            L(1, "fogNear=5"),
            L(2, "filterByModNames=Other.esp:fogNear=7"));
        Assert.Equal(new[] { 1, 2 }, AppliedLines(r, "fogNear"));
        Assert.Contains(r.Warnings, x => x.Contains("not read by the Cell patcher"));
    }

    // FormList: filterByModNames also limits the direct list
    [Fact]
    public void FormListModNamesLimitTheDirectList()
    {
        var fl = NewMod().FormLists.AddNew();
        fl.EditorID = "HcFl";
        var me = Sp(fl.FormKey);
        var r = Apply(fl, fl.FormKey, fl.EditorID, "formList", "FormList", new StubResolver(),
            L(1, $"filterByFormLists={me}:filterByModNames=Other.esp:formsToAdd=HcSpOv.esp|A00"),
            L(2, $"filterByFormLists={me}:filterByModNames=HcSpOv.esp:formsToAdd=HcSpOv.esp|A01"));
        Assert.Equal(new[] { 2 }, AppliedLines(r, "formsToAdd"));
    }

    // Race Hook: an Excluded key removes a race of filterByRaces; the Race patcher's direct list ignores it
    [Fact]
    public void RaceHookExcludedKeysRemoveFromTheDirectList()
    {
        var race = NewMod().Races.AddNew();
        race.EditorID = "HcHookRace";
        race.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { Kw.ToLink<IKeywordGetter>() };
        var me = Sp(race.FormKey);
        var hook = Apply(race, race.FormKey, race.EditorID, "raceHook", "Race", new StubResolver(),
            L(1, $"filterByRaces={me}:filterByKeywordsExcluded=HcKw.esp|801:attackDataToAdd=key=a"),
            L(2, $"filterByRaces={me}:attackDataToAdd=key=b"));
        Assert.Equal(new[] { 2 }, hook.Directives.Select(d => d.LineNumber));
        var plain = Apply(race, race.FormKey, race.EditorID, "race", "Race", new StubResolver(),
            L(1, $"filterByRaces={me}:filterByKeywordsExcluded=HcKw.esp|801:baseCarryweight=150"));
        Assert.Equal(new[] { 1 }, AppliedLines(plain, "baseCarryweight"));
    }

    // NPC keyword filters also see the race's keywords; Spell keyword filters also see the effects' keywords
    [Fact]
    public void KeywordFiltersAlsoReadTheRaceAndTheEffects()
    {
        var raceFk = new FormKey(new ModKey("HcRace", ModType.Plugin), 0xC02);
        var npc = NewMod().Npcs.AddNew();
        npc.EditorID = "HcRaceKwNpc";
        npc.Race.SetTo(raceFk);
        var resolver = new StubResolver();
        resolver.Keywords[raceFk] = new[] { Kw };
        var rn = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", resolver, L(1, "filterByKeywords=HcKw.esp|801:weight=5"));
        Assert.Equal(new[] { 1 }, AppliedLines(rn, "weight"));

        var effectFk = new FormKey(new ModKey("HcMgef", ModType.Plugin), 0xE03);
        var spell = NewMod().Spells.AddNew();
        spell.EditorID = "HcEffectKwSpell";
        spell.Effects.Add(new Effect { BaseEffect = effectFk.ToNullableLink<IMagicEffectGetter>() });
        resolver.Keywords[effectFk] = new[] { Kw };
        var rs = Apply(spell, spell.FormKey, spell.EditorID, "spell", "Spell", resolver, L(1, "filterByKeywords=HcKw.esp|801:baseCost=7"));
        Assert.Equal(new[] { 1 }, AppliedLines(rs, "baseCost"));
    }

    // the player: class and combat style filters can find him, a race filter never does; a keyword Excluded never removes him, a class Exclude does
    [Fact]
    public void ClassAndCombatStyleFindThePlayerAndKeywordExcludedNeverRemovesHim()
    {
        var raceFk = new FormKey(new ModKey("HcRace", ModType.Plugin), 0xC01);
        var player = new Npc(new FormKey(SkyrimEsm, 0x7), SkyrimRelease.SkyrimSE);
        player.Race.SetTo(raceFk);
        player.Class.SetTo(new FormKey(new ModKey("HcCls", ModType.Plugin), 0xD01));
        player.CombatStyle.SetTo(new FormKey(new ModKey("HcCs", ModType.Plugin), 0xE01));
        player.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { Kw.ToLink<IKeywordGetter>() };
        var resolver = new StubResolver();
        resolver.Keywords[raceFk] = Array.Empty<FormKey>();
        var r = Apply(player, player.FormKey, "Player", "npc", "Npc", resolver,
            L(1, "filterByClass=HcCls.esp|D01:weight=11"),
            L(2, "filterByCombatStyle=HcCs.esp|E01:height=1.2"),
            L(3, "filterByRaces=HcRace.esp|C01:weight=99"),
            L(4, "filterByClass=HcCls.esp|D01:filterByKeywordsExcluded=HcKw.esp|801:weight=12"),
            L(5, "filterByClass=HcCls.esp|D01:filterByClassExclude=HcCls.esp|D01:weight=13"));
        Assert.Equal(new[] { 1, 4 }, AppliedLines(r, "weight"));
        Assert.Equal(new[] { 2 }, AppliedLines(r, "height"));
    }

    // the dev lines that skipped as unknown keys under 6.4.1 (SkyTactics, Spell Absorption Rework) now classify and evaluate
    [Fact]
    public void TheKeysAddedSince641Classify()
    {
        var npc = NewMod().Npcs.AddNew();
        npc.EditorID = "HcTacticsNpc";
        npc.CombatStyle.SetTo(new FormKey(SkyrimEsm, 0x3BE1B));
        npc.Factions.Add(new RankPlacement { Faction = new FormKey(SkyrimEsm, 0x86EEE).ToLink<IFactionGetter>(), Rank = 0 });
        var rn = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            L(4, "filterByCombatStyle=Skyrim.esm|0003BE1B, Skyrim.esm|00068848:setRandomCombatStyle=SkyTactics - Dynamic Combat Styles.esp|807"),
            L(14, "filterByClass=Skyrim.esm|00023C0C:setRandomCombatStyle=SkyTactics - Dynamic Combat Styles.esp|815"),
            L(25, "filterByFactionsOr=Skyrim.esm|0002BF9A, Skyrim.esm|0001C9FC:restrictToCombatStyle=Skyrim.esm|0003BE1B:setRandomCombatStyle=SkyTactics - Dynamic Combat Styles.esp|80B"),
            L(28, "filterByFactions=Skyrim.esm|00086EEE:restrictToCombatStyle=Skyrim.esm|0003BE1B, Skyrim.esm|00068848:setRandomCombatStyle=SkyTactics - Dynamic Combat Styles.esp|80C"));
        Assert.DoesNotContain(rn.Warnings, x => x.Contains("not in the SkyPatcher reference"));
        Assert.Equal(new[] { 4, 28 }, rn.Directives.Select(d => d.LineNumber));

        var scroll = NewMod().Scrolls.AddNew();
        scroll.EditorID = "HcAbsorbScroll";
        var rs = Apply(scroll, scroll.FormKey, scroll.EditorID, "scroll", "Scroll", new StubResolver(),
            L(1, "filterByKeywords=SpellAbsorptionRework.esp|809:filterByFlagsExcluded=noabsorb:mgefsToAdd=SpellAbsorptionRework.esp|80A~0~1~0"));
        Assert.DoesNotContain(rs.Warnings, x => x.Contains("not in the SkyPatcher reference"));
    }
}
