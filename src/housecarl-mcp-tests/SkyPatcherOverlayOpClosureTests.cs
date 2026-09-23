using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>Migrated from the skypatcher-overlay-guard probe's Wave-2 op-closure, bracket-label and NPC skin-donor
/// arms: dict-keyed race stats, cell colour channels, biped-slot bits, Book Teaches, recipe entry counts, an inert
/// '[Name]' label, and skin=&lt;donor NPC&gt;.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherOverlayOpClosureTests
{
    static SkyrimMod NewMod() => new(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
    static SkyPatcherOverlay.OrderedLine L(int n, string text) => Line("c.ini", n, text);

    static (Race Race, SkyPatcherOverlay.SkyPatcherOverlayResult Result) StatRace()
    {
        var race = NewMod().Races.AddNew();
        race.EditorID = "HcStatRace";
        race.Starting[BasicStat.Health] = 50;
        race.Regen[BasicStat.Magicka] = 3;
        var r = Apply(race, race.FormKey, race.EditorID, "race", "Race", new StubResolver(),
            L(1, "startingHealth=100"),
            L(2, "startingHealthMult=1.5"),
            L(3, "regenMagickaMult=2"),
            L(4, "startingStaminaMult=2"));
        return (race, r);
    }

    [Fact] // race dict stats: set 100 then x1.5 = 150 (ordered, stateful, running-value)
    public void ARaceDictStatMultRunsOnTheRunningValue() => Assert.Equal(150f, StatRace().Race.Starting[BasicStat.Health], 3);

    [Fact] // race regen dict mult (3 x 2 = 6)
    public void ARaceRegenDictMultMultiplies() => Assert.Equal(6f, StatRace().Race.Regen[BasicStat.Magicka], 3);

    [Fact] // dict mult on an ABSENT key skips LOUD, never invents a base value
    public void ADictMultOnAnAbsentKeySkipsLoud()
    {
        var (race, r) = StatRace();
        Assert.False(race.Starting.ContainsKey(BasicStat.Stamina));
        Assert.Contains(r.Warnings, w => w.Contains("startingStaminaMult") && w.Contains("no current value"));
    }

    static Cell ColourCell()
    {
        var cell = new Cell(new FormKey(new ModKey("HcSpOv", ModType.Plugin), 0xCE2), SkyrimRelease.SkyrimSE);
        cell.EditorID = "HcColorCell";
        cell.Lighting = new CellLighting { AmbientColor = System.Drawing.Color.FromArgb(255, 10, 20, 30) };
        Apply(cell, cell.FormKey, cell.EditorID, "cell", "Cell", new StubResolver(),
            L(1, "filterByCells=HcSpOv.esp|CE2:ambientRed=64"),
            L(2, "filterByCells=HcSpOv.esp|CE2:ambientBlue=128"),
            L(3, "filterByCells=HcSpOv.esp|CE2:directionalAmbientXMinGreen=99"));
        return cell;
    }

    [Fact] // colour channels spliced independently (R=64, G kept 20, B=128, A kept 255)
    public void ColourChannelsAreSplicedIndependently()
    {
        var c = ColourCell().Lighting!.AmbientColor;
        Assert.Equal((64, 20, 128, 255), ((int)c.R, (int)c.G, (int)c.B, (int)c.A));
    }

    [Fact] // nested directional-ambient channel landed (XMinus.G=99)
    public void ANestedDirectionalAmbientChannelLands()
        => Assert.Equal((byte)99, ColourCell().Lighting!.AmbientColors?.DirectionalXMinus.G);

    [Fact] // bipedSlots ops: index 11 cleared, 12 set (the documented example)
    public void BipedSlotOpsClearAndSetByIndex()
    {
        var ar = NewMod().Armors.AddNew();
        ar.EditorID = "HcSlotArmor";
        ar.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.LongHair };
        Apply(ar, ar.FormKey, ar.EditorID, "armor", "Armor", new StubResolver(), L(1, "bipedSlotsToRemove=11:bipedSlotsToAdd=12"));
        Assert.False(ar.BodyTemplate!.FirstPersonFlags.HasFlag(BipedObjectFlag.LongHair));
        Assert.True(ar.BodyTemplate.FirstPersonFlags.HasFlag(BipedObjectFlag.Circlet));
    }

    [Fact] // teachSpell composed the BookSpell arm (visible in the applied note); teachSkill overwrote with the BookSkill arm (marksman->Archery)
    public void TeachesComposesTheSpellArmThenTheSkillArmOverwrites()
    {
        var book = NewMod().Books.AddNew();
        book.EditorID = "HcTome";
        var r = Apply(book, book.FormKey, book.EditorID, "book", "Book", new StubResolver(),
            L(1, "teachSpell=HcSpl.esp|F10"),
            L(2, "teachSkill=marksman"));
        Assert.Contains(r.Applied, a => a.Op == "teachSpell" && a.Note == "Teaches → BookSpell");
        Assert.Equal(Skill.Archery, Assert.IsType<BookSkill>(book.Teaches).Skill);
    }

    [Fact] // setEntryCount: the matching ingredient set to 2, the other untouched; null form hits EVERY entry (all counts 0)
    public void ChangeCobjsCountSetsOneEntryThenNullSetsEvery()
    {
        var ingotA = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x930);
        var ingotB = new FormKey(new ModKey("HcItm", ModType.Plugin), 0x931);
        var c = NewMod().ConstructibleObjects.AddNew();
        c.EditorID = "HcCountRecipe";
        c.Items = new()
        {
            new ContainerEntry { Item = new ContainerItem { Item = ingotA.ToLink<IItemGetter>(), Count = 5 } },
            new ContainerEntry { Item = new ContainerItem { Item = ingotB.ToLink<IItemGetter>(), Count = 5 } },
        };
        Apply(c, c.FormKey, c.EditorID, "constructibleObject", "ConstructibleObject", new StubResolver(), L(1, "changeCobjsCount=HcItm.esp|930~2"));
        Assert.Equal(new[] { (ingotA, 2), (ingotB, 5) }, c.Items!.Select(e => (e.Item.Item.FormKey, e.Item.Count)));

        Apply(c, c.FormKey, c.EditorID, "constructibleObject", "ConstructibleObject", new StubResolver(), L(1, "changeCobjsCount=null~0"));
        Assert.All(c.Items!, e => Assert.Equal(0, e.Item.Count));
    }

    [Fact] // the '[Name]' label produced NO warning and NO unresolved-skip; the patch line below the label still applied (damage=42)
    public void ABracketLabelIsInertAndTheLineBelowApplies()
    {
        var w = NewMod().Weapons.AddNew();
        w.EditorID = "HcLabelSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1, Value = 1 };
        var r = Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(),
            Line("lbl.ini", 1, "[Vernaccus]"),
            Line("lbl.ini", 2, $"filterByWeapons=HcSpOv.esp|{w.FormKey.ID:X}:attackDamage=42"));
        Assert.DoesNotContain(r.Warnings, x => x.Contains("Vernaccus"));
        Assert.Equal(0, r.LinesSkippedUnresolvedFilter);
        Assert.Equal(42, w.BasicStats!.Damage);
    }

    static readonly FormKey ArmorFk = new(new ModKey("HcArm", ModType.Plugin), 0xF01);
    static readonly FormKey ExistingSkin = new(new ModKey("HcArm", ModType.Plugin), 0xE00);

    static StubResolver SkinResolver()
    {
        var resolver = new StubResolver();
        resolver.EditorIds["HcWornArmor"] = ArmorFk; resolver.EditorIdTypes["HcWornArmor"] = "Armor";
        resolver.EditorIds["012_HLIORemi"] = new FormKey(new ModKey("LRemiel Re", ModType.Plugin), 0x800);
        resolver.EditorIdTypes["012_HLIORemi"] = "Npc";
        return resolver;
    }

    static (Npc Npc, SkyPatcherOverlay.SkyPatcherOverlayResult Result) Skin(string ops, FormKey? worn = null)
    {
        var npc = NewMod().Npcs.AddNew();
        npc.EditorID = "HLIORemi";
        if (worn is { } fk) npc.WornArmor.SetTo(fk);
        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", SkinResolver(),
            Line("skin.ini", 1, $"filterByNpcs=HcSpOv.esp|{npc.FormKey.ID:X}:{ops}"));
        return (npc, r);
    }

    [Fact] // skin=<donor NPC> does NOT throw a malformed-FormKey
    public void SkinNamingADonorNpcDoesNotThrowAMalformedFormKey()
        => Assert.DoesNotContain(Skin("copyVisualStyle=012_HLIORemi:skin=012_HLIORemi", ExistingSkin).Result.Warnings,
            w => w.Contains("Malformed FormKey"));

    [Fact] // skin=<donor NPC> classifies as an unmodeled donor-copy, naming donor + copyVisualStyle
    public void SkinNamingADonorNpcIsADonorCopyWarning()
        => Assert.Contains(Skin("copyVisualStyle=012_HLIORemi:skin=012_HLIORemi", ExistingSkin).Result.Warnings,
            w => w.Contains("skin=012_HLIORemi") && w.Contains("donor") && w.Contains("copyVisualStyle"));

    [Fact] // skin=<donor NPC> left the target's WornArmor UNCHANGED (not applied)
    public void SkinNamingADonorNpcLeavesWornArmor()
        => Assert.Equal(ExistingSkin, Skin("copyVisualStyle=012_HLIORemi:skin=012_HLIORemi", ExistingSkin).Npc.WornArmor.FormKey);

    [Fact] // copyVisualStyle is still surfaced as a HARD directive (tiered honesty intact)
    public void CopyVisualStyleIsADirective()
        => Assert.Contains(Skin("copyVisualStyle=012_HLIORemi:skin=012_HLIORemi", ExistingSkin).Result.Directives,
            d => d.Op == "copyVisualStyle");

    [Fact] // skin=<Armor> still sets WornArmor directly (CLEAN path unbroken)
    public void SkinNamingAnArmorSetsWornArmor() => Assert.Equal(ArmorFk, Skin("skin=HcWornArmor").Npc.WornArmor.FormKey);

    [Fact] // skin=null still clears WornArmor
    public void SkinNullClearsWornArmor() => Assert.True(Skin("skin=null", ArmorFk).Npc.WornArmor.IsNull);

    [Fact] // a missing skin donor is loud + names the token (never a malformed-FormKey throw)
    public void AMissingSkinDonorWarnsByName()
    {
        var r = Skin("skin=NoSuchThing").Result;
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Malformed FormKey"));
        Assert.Contains(r.Warnings, w => w.Contains("NoSuchThing") && w.Contains("does not resolve"));
    }
}
