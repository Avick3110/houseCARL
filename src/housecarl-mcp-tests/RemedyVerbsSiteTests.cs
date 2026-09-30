using HousecarlCore;
using HousecarlGenerator;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Each message that prints the verbs a collection takes carries that shape's verbs and none of the other
/// cardinality's: the bracket remedy (gate and engine twin), Set-on-list, the element remedy, the unknown-verb list,
/// the composes= refusals, the modeled-elements message and the array refusal. Migrated from the remedy-verbs-guard
/// probe's sites arm; its SITE-ELEMENT-CONFLICT-LIST-KEYED and SITE-BRACKET-ENGINE-BADKEY-WITHHELD arms, and the
/// record-axis half of SITE-ELEMENT-CONFLICT-OWNEDRECORD, are already made by <see cref="ElementRefusalRemedyTests"/>.
/// </summary>
[Trait("tier", "unit")]
public sealed class RemedyVerbsSiteTests
{
    /// <summary>The published verb vocabulary, spelled out here rather than read from <see cref="WriteVerbs.All"/>, so a
    /// message is checked against a second route and not the collection that built it.</summary>
    static readonly string[] PublishedVocabulary =
        { "Set", "Add", "Remove", "ReplaceAll", "SetAtIndex", "InsertAtIndex", "Merge", "CopyFrom" };

    static readonly string[] ListOnly = { "SetAtIndex", "InsertAtIndex" };
    static readonly string[] DictOnly = { "Merge" };

    static readonly CollectionShape ListCoerced = new(CollectionKind.List, ElementPlacement.Coerced);
    static readonly CollectionShape ListComposed = new(CollectionKind.List, ElementPlacement.Composed);
    static readonly CollectionShape DictCoerced = new(CollectionKind.Dict, ElementPlacement.Coerced);
    static readonly CollectionShape DictComposed = new(CollectionKind.Dict, ElementPlacement.Composed);

    static IEnumerable<VerbUse> Keyed(CollectionShape s) => WriteVerbs.On(s).Where(u => u.NeedsKey);
    static IEnumerable<VerbUse> Placing(CollectionShape s) => WriteVerbs.On(s).Where(u => u.Places);
    static IEnumerable<VerbUse> PlacingOne(CollectionShape s) => WriteVerbs.On(s)
        .Where(u => u.Places && u.Input is not (VerbInput.Values or VerbInput.Entries or VerbInput.Composes));

    static readonly ModKey Owner = new("HcRemedyTests", ModType.Master);
    static FormKey Fk() => new(Owner, 0x800);

    static string? Gate(WriteRequest r) => TestCorpus.Rulebook.Validate(r);

    static WriteRequest Req(string rec, string path, string verb, string? value = null, string? key = null) =>
        new() { RecordType = rec, Path = new[] { path }, Verb = verb, Value = value, Key = key };

    static string Thrown(Action act)
    {
        var ex = Record.Exception(act);
        Assert.NotNull(ex);
        return (ex!.InnerException ?? ex).Message;
    }

    /// <summary>The message carries every verb in <paramref name="expected"/> and none of the other cardinality's
    /// exclusive verbs (an absolute set: list-only index verbs for a dict, Merge for a list).</summary>
    static void Shaped(string? msg, CollectionShape shape, IEnumerable<VerbUse> expected)
    {
        Assert.NotNull(msg);
        foreach (var v in expected.Select(u => u.Verb).Distinct()) Assert.Contains(v, msg);
        foreach (var v in shape.Kind == CollectionKind.List ? DictOnly : ListOnly) Assert.DoesNotContain(v, msg);
    }

    // SITE-BRACKET-GATE-LIST — a bracketed LIST leaf is answered with the index verbs and no dict verb
    [Fact]
    public void TheGateAnswersABracketedListLeafWithTheIndexVerbs()
        => Shaped(Gate(Req("Race", "MovementTypeNames[0]", "Set", "x")), ListCoerced, Keyed(ListCoerced));

    // SITE-BRACKET-GATE-DICT — a bracketed DICT leaf is answered with the keyed verbs and NO SetAtIndex/InsertAtIndex
    [Fact]
    public void TheGateAnswersABracketedDictLeafWithTheKeyedVerbs()
        => Shaped(Gate(Req("Class", "SkillWeights[Alteration]", "Set", "1")), DictCoerced, Keyed(DictCoerced));

    // SITE-BRACKET-ORDER — the bracketed-leaf remedy names SetAtIndex (overwrite in place) BEFORE InsertAtIndex (insert and shift)
    [Fact]
    public void TheBracketRemedyNamesSetAtIndexBeforeInsertAtIndex()
    {
        var msg = Gate(Req("Race", "MovementTypeNames[0]", "Set", "x"))!;
        var set = msg.IndexOf("SetAtIndex", StringComparison.Ordinal);
        Assert.True(set >= 0);
        Assert.True(set < msg.IndexOf("InsertAtIndex", StringComparison.Ordinal));
    }

    // SITE-BRACKET-ENGINE-LIST — the engine twin answers a bracketed LIST leaf with the index verbs
    [Fact]
    public void TheEngineTwinAnswersABracketedListLeafWithTheIndexVerbs()
        => Shaped(Thrown(() => WriteEngine.ApplyVerb(new Race(Fk(), SkyrimRelease.SkyrimSE), Req("Race", "MovementTypeNames[0]", "Set", "x"))),
            ListCoerced, Keyed(ListCoerced));

    // SITE-BRACKET-ENGINE-DICT — the engine twin answers a bracketed DICT leaf with the keyed verbs and no index verb
    [Fact]
    public void TheEngineTwinAnswersABracketedDictLeafWithTheKeyedVerbs()
        => Shaped(Thrown(() => WriteEngine.ApplyVerb(new Class(Fk(), SkyrimRelease.SkyrimSE), Req("Class", "SkillWeights[Alteration]", "Set", "1"))),
            DictCoerced, Keyed(DictCoerced));

    // SITE-SET-ON-LIST-COERCED — the remedy names every placing verb a plain-value list takes
    [Fact]
    public void SetOnAPlainListNamesEveryPlacingVerb()
        => Shaped(Gate(Req("Race", "MovementTypeNames", "Set", "x")), ListCoerced, Placing(ListCoerced));

    // SITE-SET-ON-LIST-COMPOSED — a modeled list gets the compose-carrying placing verbs
    [Fact]
    public void SetOnAModeledListNamesItsPlacingVerbs()
        => Shaped(Gate(Req("Faction", "Conditions", "Set", "1")), ListComposed, Placing(ListComposed));

    // SITE-SET-ON-LIST-OWNEDRECORD — a list of owned child records has no placing verb, so the remedy names the record axis instead of trailing off
    [Fact]
    public void SetOnAListOfOwnedRecordsNamesTheRecordAxis()
    {
        var msg = Gate(Req("Cell", "Persistent", "Set", "1"))!;
        Assert.True(ToolNameMatch.ReferencedAtBoundary(msg, "housecarl_create"), msg);
        foreach (var v in ListOnly) Assert.DoesNotContain(v, msg);
    }

    // SITE-ELEMENT-CONFLICT-LIST — the element remedy on a modeled LIST names the keyed placing verbs
    [Fact]
    public void TheElementRemedyOnAModeledListNamesTheKeyedPlacingVerbs()
        => Shaped(Gate(new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions[0]", "ComparisonValue" }, Verb = "Set", Value = "1" }),
            ListComposed, PlacingOne(ListComposed).Where(u => u.NeedsKey));

    // SITE-ELEMENT-CONFLICT-OWNEDRECORD — ...and the owned-record element remedy names no list index verb
    // (the record-axis words are asserted by ElementRefusalRemedyTests.AnOwnedRecordElementNamesNoContainerCall)
    [Fact]
    public void TheElementRemedyOnOwnedRecordsNamesNoIndexVerb()
    {
        var msg = Gate(new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent[0]", "MajorFlags" }, Verb = "Set", Value = "1" });
        Assert.Contains("owned child RECORDS", msg);
        foreach (var v in ListOnly) Assert.DoesNotContain(v, msg);
    }

    // SITE-BRACKET-ENGINE-BADKEY — an unusable key still gets the shape's keyed verbs
    [Fact]
    public void TheEngineTwinGivesAnUnusableKeyTheShapesKeyedVerbs()
        => Shaped(Thrown(() => WriteEngine.ApplyVerb(new Package(Fk(), SkyrimRelease.SkyrimSE), Req("Package", "Data[notasbyte]", "Set"))),
            DictComposed, Keyed(DictComposed));

    // SITE-UNKNOWN-VERB — the legal list is the whole published vocabulary, and WriteVerbs.All still IS that vocabulary
    [Fact]
    public void AnUnknownVerbIsAnsweredWithTheWholeVocabulary()
    {
        var msg = Gate(Req("Race", "MovementTypeNames", "Nope", "x", "0"));
        foreach (var v in PublishedVocabulary) Assert.Contains(v, msg);
        Assert.Equal(PublishedVocabulary.OrderBy(v => v, StringComparer.Ordinal), WriteVerbs.All.OrderBy(v => v, StringComparer.Ordinal));
    }

    static WriteRequest ConditionsComposes() => new()
    {
        RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Set",
        Structs = new[] { new StructSpec { Type = "ConditionFloat" } },
    };

    // SITE-COMPOSES-LIST — the singular-path parenthetical on a modeled LIST names the SINGULAR placing verbs
    [Fact]
    public void ComposesOnAModeledListNamesTheSingularPlacingVerbs()
        => Shaped(Gate(ConditionsComposes()), ListComposed, PlacingOne(ListComposed));

    // SITE-COMPOSES-ONE-AT-A-TIME — a remedy labelled "one element at a time" does not then name the BATCH verb the head sentence just recommended
    [Fact]
    public void TheOneAtATimeRemedyNamesNoBatchVerb()
    {
        var msg = Gate(ConditionsComposes());
        Assert.Contains("One element at a time", msg);
        Assert.DoesNotContain("composes=)", msg);
    }

    // SITE-COMPOSES-DICT / SITE-COMPOSES-SUBSTRUCT — composes= on a shape it does not serve says so, and offers no verb that would refuse on the next call
    [Theory]
    [InlineData("Package", "Data", "PackageDataBool", "dict")]
    [InlineData("Armor", "BodyTemplate", "BodyTemplate", "substruct")]
    public void ComposesOnAShapeItDoesNotServeSaysSo(string rec, string field, string type, string shapeWord)
    {
        var msg = Gate(new WriteRequest { RecordType = rec, Path = new[] { field }, Verb = "Set", Structs = new[] { new StructSpec { Type = type } } });
        Assert.Contains($"is a {shapeWord}", msg);
        Assert.DoesNotContain("use it with Add", msg);
        foreach (var v in ListOnly) Assert.DoesNotContain(v, msg);
    }

    static WriteRequest KeywordsComposes() => new()
    {
        RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "SetAtIndex", Key = "0",
        Structs = new[] { new StructSpec { Type = "Keyword" } },
    };

    // SITE-COMPOSES-COERCIBLE — a coercible-element list is told its elements are not modeled structs, and gets the slot every verb of its shape wants
    [Fact]
    public void ComposesOnACoercibleListGetsItsShapesPlacingVerbs()
        => Shaped(Gate(KeywordsComposes()), ListCoerced, Placing(ListCoerced));

    // SITE-COMPOSES-COERCIBLE-NOT-VERB-FIRST — and is never handed the Add/ReplaceAll head sentence, which is about a shape composes= does serve
    [Fact]
    public void ComposesOnACoercibleListIsNotHandedTheComposeHead()
    {
        var msg = Gate(KeywordsComposes());
        Assert.Contains("not modeled structs", msg);
        Assert.DoesNotContain("use it with Add", msg);
    }

    // SITE-COMPOSES-OWNEDRECORD — composes= on a list of owned child records gets the record axis, and no half of the sentence calls those elements coercible or formlink
    [Theory]
    [InlineData("Cell", "Persistent")]
    [InlineData("DialogTopic", "Responses")]
    public void ComposesOnAListOfOwnedRecordsGetsTheRecordAxisInOneVoice(string rec, string field)
    {
        var msg = Gate(new WriteRequest { RecordType = rec, Path = new[] { field }, Verb = "Add", Structs = new[] { new StructSpec { Type = "PlacedObject" } } })!;
        Assert.Contains("owned child records", msg);
        Assert.True(ToolNameMatch.ReferencedAtBoundary(msg, "housecarl_create"), msg);
        Assert.DoesNotContain("coercible", msg);
        Assert.DoesNotContain("formlink", msg);
        Assert.DoesNotContain("not modeled structs", msg);
        foreach (var v in ListOnly) Assert.DoesNotContain(v, msg);
    }

    // SITE-COMPOSES-OWNEDRECORD-ONE-SENTENCE — the composes= door and the collection-verb door give one sentence, not two phrasings of the shape
    [Fact]
    public void TheComposesDoorAndTheVerbDoorGiveOneSentence()
    {
        var viaComposes = Gate(new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Add", Structs = new[] { new StructSpec { Type = "PlacedObject" } } });
        var viaVerb = Gate(new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Add", Struct = new StructSpec { Type = "PlacedObject" } });
        Assert.NotNull(viaComposes);
        Assert.Equal(viaComposes, viaVerb);
    }

    // SITE-MODELED-LIST — a values= ReplaceAll on a modeled LIST is answered with the list's own placing verbs
    [Fact]
    public void ValuesOnAModeledListNamesTheListsPlacingVerbs()
        => Shaped(Gate(new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "ReplaceAll", Values = new[] { "1" } }),
            ListComposed, Placing(ListComposed));

    // SITE-MODELED-DICT — the medium: a Package.Data Merge no longer offers InsertAtIndex, which that caller cannot use
    [Fact]
    public void AMergeOnAModeledDictNamesTheDictsPlacingVerbs()
        => Shaped(Gate(new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Merge", Entries = new Dictionary<string, string>() }),
            DictComposed, Placing(DictComposed));

    static string ArrayRefusal() => Thrown(() => WriteEngine.ApplyVerb(new Weather(Fk(), SkyrimRelease.SkyrimSE),
        Req("Weather", "CloudTextures", "Add", "x")));

    // SITE-ARRAY-REFUSAL — the unsupported set is the shape's collection verbs, so a verb cannot fall out of it and read as supported
    [Fact]
    public void TheArrayRefusalListsTheShapesCollectionVerbs()
        => Shaped(ArrayRefusal(), ListCoerced, WriteVerbs.On(ListCoerced).Where(u => u.Places || u.NeedsKey));

    // SITE-ARRAY-REFUSAL-NOT-COPYFROM — CopyFrom is left out by the purpose filter, not by hand: it is neither a placing nor a keyed verb
    [Fact]
    public void TheArrayRefusalLeavesOutCopyFrom()
        => Assert.DoesNotContain("CopyFrom", ArrayRefusal());
}
