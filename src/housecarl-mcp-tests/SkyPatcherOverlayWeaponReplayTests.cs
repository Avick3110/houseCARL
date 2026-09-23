using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>One weapon, 30 lines across a.ini, m.ini and z.ini, replayed once in file order. The damage chain
/// (a.ini set 40, z.ini x2.5, z.ini +11 = 111) is the value only an ordered, stateful, running-value replay gives:
/// last-write-wins gives 40, ops off the original value give 25 or 51.</summary>
public sealed class SkyPatcherOverlayWeaponReplay
{
    public static readonly FormKey K1 = new(new ModKey("HcKw", ModType.Plugin), 0x801);
    public static readonly FormKey K2 = new(new ModKey("HcKw", ModType.Plugin), 0x802);
    public static readonly FormKey KEid = new(new ModKey("HcKw", ModType.Plugin), 0x803);

    public Weapon Weapon { get; }
    public SkyPatcherOverlay.SkyPatcherOverlayResult Result { get; }

    public SkyPatcherOverlayWeaponReplay()
    {
        var mod = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var weap = mod.Weapons.AddNew();   // 000800:HcSpOv.esp
        weap.EditorID = "HcTestSword";
        weap.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 5, Value = 100 };
        weap.Critical = new CriticalData { Damage = 3 };
        weap.Keywords = new() { K1.ToLink<IKeywordGetter>() };
        weap.EquipSound.SetTo(new FormKey(new ModKey("HcSnd", ModType.Plugin), 0x900));

        var resolver = new StubResolver();
        resolver.Plugins.Add("HcSpOv.esp");
        resolver.EditorIds["HcNamedKeyword"] = KEid;

        const string me = "HcSpOv.esp|800";
        Weapon = weap;
        Result = Apply(weap, weap.FormKey, weap.EditorID, "weapon", "Weapon", resolver,
            Line("a.ini", 1, $"filterByWeapons={me}:attackDamage=40:weight=9:keywordsToAdd=HcKw.esp|802:skillType=onehanded"),
            Line("m.ini", 1, "filterByWeapons=Other.esp|123:attackDamage=1"),
            Line("m.ini", 2, $"filterByWeapons={me}:weight=2"),
            Line("z.ini", 1, $"filterByWeapons={me}:attackDamageMult=2.5"),
            Line("z.ini", 2, "filterByWeapons=HcTestSword:attackDamageToAdd=11"),
            Line("z.ini", 3, $"filterByWeapons={me}:critDamageSetToBase=true"),
            Line("z.ini", 4, $"filterByWeapons={me}:mirrorWeapon=Skyrim.esm|139B9"),
            Line("z.ini", 5, $"filterByWeapons={me}:restrictToSkills=twohanded:speed=9"),
            Line("z.ini", 25, $"filterByWeapons={me}:restrictToSkills=onehanded:speed=6"),
            Line("z.ini", 26, $"filterByWeapons={me}:filterByHasAmmoFromWeaponList=1:stagger=7"),
            Line("z.ini", 27, "filterByModNames=HcSpOv.esp:rangeMin=11"),
            Line("z.ini", 28, "filterByModNamesExcluded=HcSpOv.esp:rangeMin=99"),
            Line("z.ini", 6, $"filterByWeapons={me}:notAnOp=1"),
            Line("z.ini", 7, $"filterByWeapons={me}:fullName=~Reforged Blade~"),
            Line("z.ini", 8, $"filterByWeapons={me}:animationType=bow:weaponHitType=no:soundLevel=silent"),
            Line("z.ini", 9, $"filterByWeapons={me}:equipSound=null"),
            Line("z.ini", 10, $"filterByWeapons={me}:minX=-7"),
            Line("z.ini", 11, $"filterByWeapons={me}:keywordsToRemove=HcKw.esp|777"),
            Line("z.ini", 12, $"filterByWeapons={me}:keywordsToRemove=HcKw.esp|801"),
            Line("z.ini", 13, "filterByKeywords=HcKw.esp|802:stagger=1.5"),
            Line("z.ini", 14, "filterByEditorIdContains=TestSw:reach=1.25"),
            Line("z.ini", 15, "rangeMax=99"),
            Line("z.ini", 16, $"filterByWeaponsExcluded={me}:value=1"),
            Line("z.ini", 17, $"hasPlugins=Missing.esp:filterByWeapons={me}:value=7"),
            Line("z.ini", 18, $"hasPlugins=HcSpOv.esp:filterByWeapons={me}:value=777"),
            Line("z.ini", 19, $"filterByWeapons={me}:keywordsToAdd=HcNamedKeyword"),
            Line("z.ini", 20, "bogusFilter=1:stagger=9"),
            Line("z.ini", 21, "filterByWeapons=HcSpOv.esp|FE000800:enchantAmount=77"),
            Line("z.ini", 22, $"filterByWeapons={me}:reach="),
            Line("z.ini", 23, $"filterByWeapons={me}:model=NotARealDonor"),
            Line("z.ini", 24, $"filterByWeapons={me}:critPercentMult=3"));
    }
}

/// <summary>Migrated from the skypatcher-overlay-guard probe's weapon arm.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherOverlayWeaponReplayTests(SkyPatcherOverlayWeaponReplay replay) : IClassFixture<SkyPatcherOverlayWeaponReplay>
{
    Weapon W => replay.Weapon;
    SkyPatcherOverlay.SkyPatcherOverlayResult R => replay.Result;

    [Fact] // weapon field map present
    public void TheWeaponFieldMapIsPresent() => Assert.NotNull(FieldMap.For("weapon", "Weapon"));

    [Fact] // damage replays 40 x2.5 +11 = 111 (ordered, stateful, running-value)
    public void DamageReplaysSetThenMultThenAddInFileOrder() => Assert.Equal(111, W.BasicStats!.Damage);

    [Fact] // weight: later-sorted set wins (9 then 2 => 2)
    public void TheLaterSortedWeightSetWins() => Assert.Equal(2f, W.BasicStats!.Weight, 3);

    [Fact] // foreign-record line did NOT apply
    public void ALineNamingAnotherRecordDoesNotApply()
        => Assert.DoesNotContain(R.Applied, a => a.File == "m.ini" && a.LineNumber == 1);

    [Fact] // EditorID primary filter matched (attackDamageToAdd applied)
    public void AnEditorIdPrimaryFilterMatches() => Assert.Contains(R.Applied, a => a.Op == "attackDamageToAdd");

    [Fact] // Excluded connective skips the record (value never 1)
    public void AnExcludedFilterSkipsTheRecord()
    {
        Assert.Equal(777u, W.BasicStats!.Value);
        Assert.DoesNotContain(R.Applied, a => a.File == "z.ini" && a.LineNumber == 16);
    }

    [Fact] // hasPlugins gates the line (7 skipped, 777 applied)
    public void HasPluginsGatesTheLine() => Assert.Single(R.Applied, a => a.Op == "value");

    [Fact] // no-filter line applies to the type (rangeMax=99)
    public void ALineWithNoFilterAppliesToTheType() => Assert.Equal(99f, W.Data!.RangeMax, 3);

    [Fact] // keyword filter sees the RUNNING copy (stagger applied after keywordsToAdd)
    public void AKeywordFilterSeesAKeywordAddedEarlierInTheReplay() => Assert.Equal(1.5f, W.Data!.Stagger, 3);

    [Fact] // editorIdContains filter matched (reach=1.25)
    public void AnEditorIdContainsFilterMatches() => Assert.Equal(1.25f, W.Data!.Reach, 3);

    [Fact] // HARD op => a directive, not a value (mirrorWeapon)
    public void AHardOpComesBackAsADirective()
        => Assert.Contains(R.Directives, d => d.Op == "mirrorWeapon" && d.Reason.Contains("copy-from-form"));

    [Fact] // restrictToSkills EVALUATES: twohanded no-match, onehanded applies (speed=6)
    public void RestrictToSkillsEvaluatesAgainstTheRecord()
    {
        Assert.Equal(6f, W.Data!.Speed, 3);
        Assert.Equal(new[] { 25 }, R.Applied.Where(a => a.Op == "speed").Select(a => a.LineNumber));
        Assert.DoesNotContain(R.Warnings, w => w.Contains("restrictToSkills") && w.Contains("UNRESOLVED"));
    }

    [Fact] // explicitly-unmapped filter => line skipped LOUD (filterByHasAmmoFromWeaponList), stagger untouched
    public void AnExplicitlyUnmappedFilterSkipsTheLineLoud()
    {
        Assert.True(R.LinesSkippedUnresolvedFilter >= 1);
        Assert.Contains(R.Warnings, w => w.Contains("filterByHasAmmoFromWeaponList") && w.Contains("no static evaluation"));
        Assert.Equal(1.5f, W.Data!.Stagger, 3);
        Assert.DoesNotContain(R.Applied, a => a.File == "z.ini" && a.LineNumber == 26);
    }

    [Fact] // filterByModNames matches the defining master; Excluded skips (rangeMin=11)
    public void FilterByModNamesMatchesTheDefiningMasterAndItsExcludedFormSkips() => Assert.Equal(11f, W.Data!.RangeMin, 3);

    [Fact] // an unknown key poisons the WHOLE line, loud (notAnOp)
    public void AnUnknownKeyPoisonsTheWholeLineLoud()
        => Assert.Contains(R.Warnings, w => w.Contains("notAnOp") && w.Contains("UNRESOLVED"));

    [Fact] // unknown ONLY-filter line did NOT become apply-all (stagger untouched by z:20)
    public void AnUnknownOnlyFilterDoesNotBecomeApplyAll()
    {
        Assert.Equal(1.5f, W.Data!.Stagger, 3);
        Assert.Contains(R.Warnings, w => w.Contains("bogusFilter") && w.Contains("UNRESOLVED"));
    }

    [Fact] // full load-indexed ESL FormID (FE000800) matches the record
    public void AFullLoadIndexedEslFormIdMatchesTheRecord() => Assert.Equal((ushort)77, W.EnchantmentAmount);

    /// <summary>FE000800 keeps the same low 24 bits either way; FE001800 is light slot 1, local id 800, and only
    /// the 12-bit reading lands on the record.</summary>
    [Fact] // full load-indexed ESL FormID matches the record (a non-zero light slot)
    public void AFullEslFormIdWithANonZeroSlotKeepsOnlyTheLocalId()
    {
        var w = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE).Weapons.AddNew();
        w.EditorID = "HcSlotSword";
        Apply(w, w.FormKey, w.EditorID, "weapon", "Weapon", new StubResolver(),
            Line("esl.ini", 1, $"filterByWeapons=HcSpOv.esp|FE001{w.FormKey.ID:X3}:enchantAmount=77"));
        Assert.Equal((ushort)77, w.EnchantmentAmount);
    }

    [Fact] // empty set value warns loud, does not silently no-op
    public void AnEmptySetValueWarns()
    {
        Assert.Equal(1.25f, W.Data!.Reach, 3);
        Assert.Contains(R.Warnings, w => w.Contains("'reach='") && w.Contains("no value"));
    }

    [Fact] // unresolvable dot-less model donor warns, never written verbatim as a path
    public void AnUnresolvableModelDonorWarnsAndIsNotWrittenAsAPath()
    {
        Assert.NotEqual("NotARealDonor", W.Model?.File.GivenPath ?? "");
        Assert.Contains(R.Warnings, w => w.Contains("NotARealDonor") && w.Contains("not resolvable"));
    }

    [Fact] // critPercentMult is a SET of the CRDT multiplier field (0 -> 3, not 0x3)
    public void CritPercentMultSetsTheMultiplierField() => Assert.Equal(3f, W.Critical!.PercentMult, 3);

    [Fact] // rename strips the ~...~ wrapper
    public void ARenameStripsTheTildeWrapper() => Assert.Equal("Reforged Blade", W.Name?.String);

    [Fact] // enum coerces ignore-case (animationType=bow)
    public void AnEnumValueCoercesIgnoringCase() => Assert.Equal(WeaponAnimationType.Bow, W.Data!.AnimationType);

    [Fact] // valueMap translates the documented token (weaponHitType=no)
    public void AValueMapTranslatesTheDocumentedToken() => Assert.Equal("NoDismemberOrExplode", W.Data!.OnHit.ToString());

    [Fact] // enum without valueMap (soundLevel=silent)
    public void AnEnumWithoutAValueMapCoerces() => Assert.Equal(SoundLevel.Silent, W.DetectionSoundLevel);

    [Fact] // null clears the form field (equipSound)
    public void NullClearsAFormField() => Assert.True(W.EquipSound.IsNull);

    [Fact] // vec component set (minX=-7, others untouched)
    public void AVectorComponentSetLeavesTheOthers()
    {
        Assert.Equal(-7, W.ObjectBounds.First.X);
        Assert.Equal(0, W.ObjectBounds.First.Y);
    }

    [Fact] // critDamageSetToBase self-copies the RUNNING damage (111)
    public void CritDamageSetToBaseCopiesTheRunningDamage() => Assert.Equal(111, W.Critical!.Damage);

    [Fact] // keywordsToAdd accumulated + keywordsToRemove removed + EditorID keyword resolved
    public void KeywordsAreAddedRemovedAndResolvedByEditorId()
    {
        var keys = W.Keywords!.Select(k => k.FormKey).ToList();
        Assert.DoesNotContain(SkyPatcherOverlayWeaponReplay.K1, keys);
        Assert.Contains(SkyPatcherOverlayWeaponReplay.K2, keys);
        Assert.Contains(SkyPatcherOverlayWeaponReplay.KEid, keys);
    }

    [Fact] // absent-keyword remove is a VISIBLE no-op (named note)
    public void RemovingAnAbsentKeywordIsAVisibleNoOp()
        => Assert.Contains(R.Applied, a => a.Op == "keywordsToRemove" && a.Note is { } n && n.Contains("not present"));

    [Fact] // before/after tokens carried on the stateful ops
    public void AStatefulOpCarriesItsBeforeAndAfter()
        => Assert.Contains(R.Applied, a => a.Op == "attackDamageMult" && a.Before == "40" && a.After == "100");
}
