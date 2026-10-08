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

    public HasFamilyOperandTests()
    {
        var kwa = _mod.Keywords.AddNew(); kwa.EditorID = "HKwA"; _kwA = kwa.FormKey;
        var kwb = _mod.Keywords.AddNew(); kwb.EditorID = "HKwB"; _kwB = kwb.FormKey;
        _headBody = AddArmor("HArmorHeadBody", BipedObjectFlag.Head | BipedObjectFlag.Body, _kwA);
        _hands = AddArmor("HArmorHands", BipedObjectFlag.Hands, _kwA, _kwB);
        _feet = AddArmor("HArmorFeet", BipedObjectFlag.Feet, _kwA);
    }

    FormKey AddArmor(string eid, BipedObjectFlag slots, params FormKey[] keywords)
    {
        var a = _mod.Armors.AddNew();
        a.EditorID = eid;
        a.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots };
        var kws = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        foreach (var k in keywords) kws.Add(new FormLink<IKeywordGetter>(k));
        a.Keywords = kws;
        _armors.Add(a);
        return a.FormKey;
    }

    (HashSet<FormKey> Hits, string? Fatal) Run(string clause)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        var hits = _armors.Where(b => set!.Matches(b)).Select(b => b.FormKey).ToHashSet();
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

    [Fact]
    public void HasNone_OnAKeywordList_RefusesWithTheNoneRewrite() =>
        Assert.Contains($"'Keywords[*none] in [{Fid(_kwB)}]'", Run($"Keywords has_none [{Fid(_kwB)}]").Fatal ?? "");

    [Fact]
    public void TheNoneRewrite_IsAWorkingPredicate() =>
        Assert.Equal(new[] { _headBody, _feet }.ToHashSet(), Run($"Keywords[*none] in [{Fid(_kwB)}]").Hits);
}
