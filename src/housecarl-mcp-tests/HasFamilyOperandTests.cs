using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The has-family operand spellings (brackets, ',' and the decode's '|', slotNN) and its refusal on a list path, over in-memory armors.</summary>
[Trait("tier", "unit")]
public sealed class HasFamilyOperandTests
{
    readonly SkyrimMod _mod = new(new ModKey("HasWorld", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly List<IMajorRecordGetter> _armors = new();
    readonly FormKey _kwA, _kwB, _headBody, _hands, _feet;
    readonly IMajorRecordGetter _npc;

    public HasFamilyOperandTests()
    {
        var kwa = _mod.Keywords.AddNew(); kwa.EditorID = "HKwA"; _kwA = kwa.FormKey;
        var kwb = _mod.Keywords.AddNew(); kwb.EditorID = "HKwB"; _kwB = kwb.FormKey;
        _headBody = AddArmor("HArmorHeadBody", BipedObjectFlag.Head | BipedObjectFlag.Body, _kwA);
        _hands = AddArmor("HArmorHands", BipedObjectFlag.Hands, _kwA, _kwB);
        _feet = AddArmor("HArmorFeet", BipedObjectFlag.Feet, _kwA);
        var fac = _mod.Factions.AddNew(); fac.EditorID = "HFaction";
        var npc = _mod.Npcs.AddNew(); npc.EditorID = "HNpc";
        npc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(fac.FormKey), Rank = 1 });
        _npc = npc;
    }

    FormKey AddArmor(string eid, BipedObjectFlag slots, params FormKey[] keywords)
    {
        var a = _mod.Armors.AddNew();
        a.EditorID = eid;
        a.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots };
        a.Value = (uint)slots;   // Head|Body -> 5, Hands -> 8, Feet -> 128: a plain integer leaf to bit-test
        a.Armature.Add(new FormLink<IArmorAddonGetter>(_mod.ArmorAddons.AddNew().FormKey));
        var kws = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        foreach (var k in keywords) kws.Add(new FormLink<IKeywordGetter>(k));
        a.Keywords = kws;
        _armors.Add(a);
        return a.FormKey;
    }

    (HashSet<FormKey> Hits, string? Fatal) Run(string clause) => RunOn(_armors, clause);

    static (HashSet<FormKey> Hits, string? Fatal) RunOn(IEnumerable<IMajorRecordGetter> records, string clause)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        var hits = records.Where(b => set!.Matches(b)).Select(b => b.FormKey).ToHashSet();
        return (hits, set!.FatalError);
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    [Theory]
    [InlineData("BodyTemplate.FirstPersonFlags has_any [Body, Hands]")]
    [InlineData("BodyTemplate.FirstPersonFlags has_any Body, Hands")]
    [InlineData("BodyTemplate.FirstPersonFlags has_any Body | Hands")]
    public void HasAny_BracketsCommasAndPipesSpellTheSameOperand(string clause)
    {
        var (hits, fatal) = Run(clause);
        Assert.Null(fatal);
        Assert.Equal(new[] { _headBody, _hands }.ToHashSet(), hits);
    }

    [Fact]
    public void Has_PipeOperandRequiresEveryBit() =>
        Assert.Equal(new[] { _headBody }.ToHashSet(), Run("BodyTemplate.FirstPersonFlags has Head | Body").Hits);

    [Fact]
    public void SlotToken_FromTheDecode_IsItsBit() =>
        Assert.Equal(Run("BodyTemplate.FirstPersonFlags has_any Body").Hits, Run("BodyTemplate.FirstPersonFlags has_any slot32").Hits);

    [Fact]
    public void SlotToken_OutOfRange_RefusesNamingTheSlotRange() =>
        Assert.Contains("slot30 to slot61", Run("BodyTemplate.FirstPersonFlags has_any slot99").Fatal ?? "");

    [Fact]
    public void BadFlagName_StillRefuses() =>
        Assert.Contains("not a bit value or a valid BipedObjectFlag flag name", Run("BodyTemplate.FirstPersonFlags has_any [Body, Nope]").Fatal ?? "");

    [Theory]
    [InlineData("[]")]
    [InlineData("|")]
    [InlineData(",")]
    [InlineData("0")]
    public void AnOperandWithNoBits_RefusesNamingTheOperandWritten(string operand) =>
        Assert.Contains($"'has_any {operand}' tests no bits", Run($"BodyTemplate.FirstPersonFlags has_any {operand}").Fatal ?? "");

    [Fact]
    public void HasNone_OnAKeywordList_RefusesWithTheNoneRewrite() =>
        Assert.Contains($"'Keywords[*none] in [{Fid(_kwB)}]'", Run($"Keywords has_none [{Fid(_kwB)}]").Fatal ?? "");

    [Fact]
    public void TheNoneRewrite_IsAWorkingPredicate() =>
        Assert.Equal(new[] { _headBody, _feet }.ToHashSet(), Run($"Keywords[*none] in [{Fid(_kwB)}]").Hits);

    const string Tail = "([*any] / [*all] / [*none] fold the same way), or use references= for list→FormID membership.";

    static string Head(string clause, string path, string op) =>
        $"predicate '{clause}': '{path}' is a list here, not a scalar leaf, and '{op}' tests a flags leaf's bits. ";

    [Fact]
    public void HasNone_OnAKeywordList_GivesTheWholeNoneSentence()
    {
        var clause = $"Keywords has_none [{Fid(_kwB)}]";
        Assert.Equal(Head(clause, "Keywords", "has_none") + $"For list members write 'Keywords[*none] in [{Fid(_kwB)}]' " + Tail, Run(clause).Fatal);
    }

    [Fact]
    public void HasAny_OnAKeywordList_GivesTheWholeAnyInSentence()
    {
        var clause = $"Keywords has_any {Fid(_kwA)} | {Fid(_kwB)}";
        Assert.Equal(Head(clause, "Keywords", "has_any") + $"For list members write 'Keywords[*any] in [{Fid(_kwA)}, {Fid(_kwB)}]' " + Tail, Run(clause).Fatal);
    }

    [Fact]
    public void Has_OnAKeywordList_GivesOneAnyEqualsPerMember()
    {
        var clause = $"Keywords has {Fid(_kwA)}, {Fid(_kwB)}";
        Assert.Equal(Head(clause, "Keywords", "has") + "For list members write one 'Keywords[*any] = <form>' per member " + Tail, Run(clause).Fatal);
    }

    [Theory]
    [InlineData("Armature has_any 1", "Armature", "1")]
    [InlineData("Keywords has_any ArmorHeavy", "Keywords", "ArmorHeavy")]
    public void HasAny_OnAFormListWithANonFormOperand_GivesTheLinkStepAdvice(string clause, string path, string operand)
    {
        Assert.Equal(Head(clause, path, "has_any") +
                     $"Step through the links instead (e.g. '{path}-><field> has_any {operand}', or '{path}->editorid = {operand}' " +
                     "to match a linked record by EditorID), or use references= for list→FormID membership.",
                     Run(clause).Fatal);
    }

    [Fact]
    public void HasAny_OnADict_GivesTheEntryAdvice()
    {
        var race = _mod.Races.AddNew();
        race.BipedObjectNames[BipedObject.Head] = "Head";
        const string clause = "BipedObjectNames has_any 1";
        Assert.Equal($"predicate '{clause}': 'BipedObjectNames' is a dict here, not a scalar leaf, and 'has_any' tests a flags leaf's bits. " +
                     "Filter on one entry instead (e.g. 'BipedObjectNames[<key>] has_any 1').", RunOn(new[] { race }, clause).Fatal);
    }

    [Theory]
    [InlineData("BodyTemplate.FirstPersonFlags = Head | Body")]
    [InlineData("BodyTemplate.FirstPersonFlags = [Head, Body]")]
    public void Equals_TakesTheSameOperandSpellingsAsHas(string clause)
    {
        var (hits, fatal) = Run(clause);
        Assert.Null(fatal);
        Assert.Equal(new[] { _headBody }.ToHashSet(), hits);
    }

    [Fact]
    public void Equals_TakesTheSlotToken() =>
        Assert.Equal(new[] { _hands }.ToHashSet(), Run("BodyTemplate.FirstPersonFlags = slot33").Hits);

    [Fact]
    public void ASlotTokenAndAPipeFitTheEnumLiteralCheck()
    {
        Assert.True(FieldPredicateSet.EnumLiteralFits("Body | Hands", typeof(BipedObjectFlag)));
        Assert.True(FieldPredicateSet.EnumLiteralFits("slot44", typeof(BipedObjectFlag)));
    }

    [Fact]
    public void ASignedNumberIsNoFlagName()
    {
        Assert.False(ReadEngine.TryEnumBitsFromName(typeof(FileAttributes), "-1", out _));
        Assert.True(ReadEngine.TryEnumBitsFromName(typeof(FileAttributes), "ReadOnly", out var b));
        Assert.Equal((ulong)FileAttributes.ReadOnly, b);
    }

    [Fact]
    public void ASignedOperandRefusesInsteadOfMatchingEveryBit() =>
        Assert.Contains("value '-1' is not a bit value or a valid BipedObjectFlag flag name", Run("BodyTemplate.FirstPersonFlags has_any -1").Fatal ?? "");

    [Theory]
    [InlineData("Factions has_any 1")]
    [InlineData("Factions has_any 000801:HasWorld.esm")]
    public void HasAny_OnAStructList_GivesTheSubPathAdvice(string clause)
    {
        var operand = clause["Factions has_any ".Length..];
        Assert.Equal(Head(clause, "Factions", "has_any") +
                     $"Filter on a scalar sub-path instead (e.g. 'Factions[*any].<field> has_any {operand}' or 'Factions[0]'), or use references= for list→FormID membership.",
                     RunOn(new[] { _npc }, clause).Fatal);
    }

    [Fact]
    public void SubPathAdvice_IsAWorkingPredicate() =>
        Assert.Equal(new[] { _npc.FormKey }.ToHashSet(), RunOn(new[] { _npc }, "Factions[*any].Rank = 1").Hits);

    [Theory]
    [InlineData("Value has 1 | 4", new[] { 0 })]
    [InlineData("Value has [1, 4]", new[] { 0 })]
    [InlineData("Value has_any 1 | 8", new[] { 0, 1 })]
    public void IntegerLeaf_SplitsTheOperandLikeTheFlagsLeaf(string clause, int[] expected)
    {
        var all = new[] { _headBody, _hands, _feet };
        var (hits, fatal) = Run(clause);
        Assert.Null(fatal);
        Assert.Equal(expected.Select(i => all[i]).ToHashSet(), hits);
    }

    [Theory]
    [InlineData("slot 32", "slot32")]
    [InlineData("slots 30 31", "slot30, slot31")]
    public void SlotToken_InTheDisplaysSpacedSpelling_RefusesNamingTheOperandSpelling(string operand, string spelling)
    {
        var clause = $"BodyTemplate.FirstPersonFlags has_any {operand}";
        Assert.Equal($"predicate '{clause}': 'has_any' value '{operand}' is the read's display spelling — write {spelling}.", Run(clause).Fatal);
    }
}

/// <summary>A typed scan refuses a has-family op on a list from the schema, so a scope where no record carries the
/// list still refuses rather than answering 0 matches; the link-step advice it gives is a working predicate.</summary>
[Trait("tier", "integration")]
public sealed class HasFamilyPlanTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-has-plan-");

    public HasFamilyPlanTests()
    {
        var key = new Mutagen.Bethesda.Plugins.ModKey("HcHasPlan", Mutagen.Bethesda.Plugins.ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Armors.AddNew().EditorID = "HcNoKeywords";
        var kw = m.Keywords.AddNew(); kw.EditorID = "HcHeavy";
        var a = m.Weapons.AddNew(); a.EditorID = "HcHeavyWeapon";
        a.Keywords = new Noggog.ExtendedList<Mutagen.Bethesda.Plugins.IFormLinkGetter<IKeywordGetter>> { new Mutagen.Bethesda.Plugins.FormLink<IKeywordGetter>(kw.FormKey) };
        m.BeginWrite.ToPath(Path.Combine(_mo2.DataDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile("HcHasPlan.esm\r\n", "*HcHasPlan.esm\r\n", "");
    }

    public void Dispose() => _mo2.Delete();

    string Where(string type, string clause)
    {
        using var svc = _mo2.Open();
        return HousecarlMcp.RecordsTools.Records(svc, types: new[] { type }, where: new[] { clause }, counts_only: true);
    }

    [Fact]
    public void AListThatNoScannedRecordCarriesStillRefuses()
    {
        var r = Where("ARMO", "Keywords has_any ArmorHeavy");
        Assert.Contains("'Keywords' is a list here", r);
        Assert.DoesNotContain("0 matches", r);
    }

    [Fact]
    public void TheLinkStepAdviceIsAWorkingPredicate() =>
        Assert.Contains("1 match", Where("WEAP", "Keywords->editorid = HcHeavy"));
}
