using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Composing a type that has only constructors with parameters (Stryker row T17): pre-flight refuses constructor
/// arguments that do not coerce, and when the fields cannot build the type it names the smallest constructor's
/// parameters as the fields to add.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComposeFromConstructorTests
{
    readonly CorpusRulebook _rulebook;

    public ComposeFromConstructorTests() => _rulebook = TestCorpus.Rulebook();

    string? SetArchetype(StructSpec arm) => _rulebook.Validate(new WriteRequest
    {
        RecordType = "MagicEffect", Path = new[] { "Archetype" }, Verb = "Set", Struct = arm,
    });

    [Fact]
    public void CtorArgsThatDoNotCoerceAreRefusedNamingTheArgument()
        => Assert.Contains("ctor arg #0", SetArchetype(new StructSpec { Type = "MagicEffectArchetype", CtorArgs = new[] { "NotAType" } }));

    [Fact]
    public void AComposeMissingTheConstructorParameterNamesItInFields()
        => Assert.Contains("'Type' in fields=",
            SetArchetype(new StructSpec { Type = "MagicEffectArchetype", Fields = new() { ["ActorValue"] = "Health" } }));

    /// <summary>A type with several constructors: the refusal names the smallest one's parameter (<c>IsDefault</c>),
    /// not a larger one's.</summary>
    [Fact]
    public void TheNotInstantiableRefusalNamesTheSmallestConstructorsFields()
        => Assert.Contains("'IsDefault' in fields=", WriteEngine.TryRecognizeInstantiable("VoiceContainer", null));

    /// <summary>A constructor parameter no property is named after is named by the parameter itself.</summary>
    [Fact]
    public void AConstructorParameterWithNoMatchingPropertyIsNamedByTheParameter()
        => Assert.Contains("'allCellContexts' in fields=", WriteEngine.TryRecognizeInstantiable("WorldspaceCellLocationCache", null));
}

/// <summary>
/// A compose given nothing (Stryker row T18), past pre-flight: whichever empty form it takes, the engine refuses it
/// and names a settable field, rather than writing an element that serializes to nothing.
/// </summary>
[Trait("tier", "unit")]
public sealed class EmptyComposeRefusalTests
{
    public static TheoryData<string> EmptyForms() => new() { "nothing", "ctor_args=[]", "fields={}", "sets=[]" };

    static StructSpec Spec(string form) => form switch
    {
        "ctor_args=[]" => new StructSpec { Type = "Rank", CtorArgs = Array.Empty<string>() },
        "fields={}" => new StructSpec { Type = "Rank", Fields = new() },
        "sets=[]" => new StructSpec { Type = "Rank", Sets = new() },
        _ => new StructSpec { Type = "Rank" },
    };

    [Theory, MemberData(nameof(EmptyForms))]
    public void AnEmptyComposeIsRefusedNamingASettableField(string form)
    {
        var mod = new SkyrimMod(new ModKey("HcEmptyMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var faction = mod.Factions.AddNew("HcEmptyFaction");

        var ex = Assert.Throws<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(faction, new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add", Struct = Spec(form),
        }));
        Assert.Contains("Number", ex.Message);
    }
}

/// <summary>
/// List verbs (Stryker rows T20, T21): Remove on an absent list is refused as absent; a composes= Add appends every
/// element and says so only when one duplicates, telling a list carry from a repeat within the op.
/// </summary>
[Trait("tier", "unit")]
public sealed class ListVerbTests
{
    static Faction FactionWithRank(params uint[] numbers)
    {
        var mod = new SkyrimMod(new ModKey("HcListMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var f = mod.Factions.AddNew("HcListFaction");
        foreach (var n in numbers) f.Ranks.Add(new Rank { Number = n });
        return f;
    }

    static string? AddRanks(Faction f, params uint[] numbers) => WriteEngine.ApplyVerb(f, new WriteRequest
    {
        RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add",
        Structs = numbers.Select(n => new StructSpec { Type = "Rank", Fields = new() { ["Number"] = n.ToString() } }).ToList(),
    });

    [Fact]
    public void RemoveOnAnAbsentListIsRefusedAsAbsent()
    {
        var mod = new SkyrimMod(new ModKey("HcListMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew("HcListWeap");
        Assert.Null(w.Keywords);

        var ex = Assert.Throws<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(w, new WriteRequest
        {
            RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "Remove", Value = "000800:HcListMod.esp",
        }));
        Assert.Contains("absent", ex.Message);
    }

    [Fact]
    public void ComposingTwoDistinctElementsAppendsBothWithNoNote()
    {
        var f = FactionWithRank(1);
        var note = AddRanks(f, 2, 3);
        Assert.Null(note);
        Assert.Equal(new uint?[] { 1, 2, 3 }, f.Ranks.Select(r => r.Number));
    }

    [Fact]
    public void ComposingAnElementTheListAlreadyCarriesNotesItAsAlreadyInAndNotARepeat()
    {
        var note = AddRanks(FactionWithRank(1), 1, 3);
        Assert.Contains("already in", note);
        Assert.DoesNotContain("repeat", note);
    }

    [Fact]
    public void ComposingTheSameElementTwiceNotesARepeat()
        => Assert.Contains("repeat", AddRanks(FactionWithRank(1), 5, 5));
}
