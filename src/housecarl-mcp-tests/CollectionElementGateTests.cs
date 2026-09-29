using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>Pre-flight on a collection verb's element value, key and index: a malformed or missing link element, a
/// missing or malformed dict key or list index (leaf and mid-path), and a malformed plain element value are refused
/// before apply, the legal shapes still pass, and each refusal holds end to end with no file written. Migrated from the
/// nested-create-guard probe (FLELEM, KEYIDX, KEYSHAPE, GAP1 and GAP2 arms).</summary>
[Trait("tier", "integration")]
public sealed class CollectionElementGateTests : IDisposable
{
    const string AnyLink = "012345:Skyrim.esm";

    NestedCreateRig? _rig;
    NestedCreateRig W => _rig ??= new NestedCreateRig();

    static string? Check(WriteRequest req) => TestCorpus.Rulebook.Validate(req);

    static WriteRequest Req(string type, string path, string verb, string? value = null, string? key = null)
        => WritePathRig.Req(type, path, verb, value, key);

    static WriteRequest ReqValues(string type, string path, string verb, params string[] values)
        => new() { RecordType = type, Path = path.Split('.'), Verb = verb, Values = values };

    static WriteRequest ReqEntries(string type, string path, string verb, string key, string value)
        => new() { RecordType = type, Path = path.Split('.'), Verb = verb, Entries = new() { [key] = value } };

    static void Refuses(WriteRequest req, params string[] words)
    {
        var reject = Check(req);
        Assert.NotNull(reject);
        foreach (var w in words) Assert.Contains(w, reject, StringComparison.OrdinalIgnoreCase);
    }

    static void Accepts(WriteRequest req) => Assert.Null(Check(req));

    /// <summary>A topic plus one line whose only edit is <paramref name="edit"/>, refused with no file written.</summary>
    string RefusedOnALine(string tag, WriteRequest edit)
        => W.Refused($"HcNcRej{tag}.esp", Spec("DialogTopic", $"HcNc{tag}Topic"), Under($"HcNc{tag}Topic", "DialogResponses", $"HcNc{tag}L1", edit));

    // FLELEM-REJ-GATE: a malformed element past a valid one in a ReplaceAll is refused, naming the element.
    [Fact]
    public void AMalformedLinkElementInAReplaceAllIsRefusedByName()
        => Refuses(ReqValues("DialogResponses", "LinkTo", "ReplaceAll", AnyLink, "notaformkey"), "Illegal FormLink element", "notaformkey");

    // FLELEM-REJ-ADD: a malformed element in an Add's value is refused.
    [Fact]
    public void AMalformedLinkElementInAnAddIsRefused()
        => Refuses(Req("DialogResponses", "LinkTo", "Add", "notaformkey"), "Illegal FormLink element");

    // FLELEM-NULLCLEAR-OK: a null-clear synonym '00000000' is a legal element.
    [Fact]
    public void ANullClearLinkElementIsAccepted()
        => Accepts(ReqValues("DialogResponses", "LinkTo", "ReplaceAll", AnyLink, "00000000"));

    // FLELEM-OK-E2E: a valid element round-trips through create onto LinkTo on disk.
    [Fact]
    public void AValidLinkElementIsWrittenToTheList()
    {
        var (o, path) = W.Create("HcNcFlElemOk.esp",
            Spec("DialogTopic", "HcNcFoTopic"),
            Under("HcNcFoTopic", "DialogResponses", "HcNcFoL1", ReqValues("DialogResponses", "LinkTo", "ReplaceAll", W.Topic.ToString())));
        Assert.True(o.Success, o.Error);
        Assert.Contains(W.Topic, Info(W.Open(path), o.Created[1].FormKey)!.LinkTo.Select(l => l.FormKey));
    }

    // FLELEM-REJ-E2E: a malformed element refuses end to end with no file, by the pre-flight message.
    [Fact]
    public void AMalformedLinkElementIsRefusedBeforeTheFileIsWritten()
        => Assert.Contains("Illegal FormLink element", RefusedOnALine("FlElem", ReqValues("DialogResponses", "LinkTo", "ReplaceAll", "notaformkey")), StringComparison.OrdinalIgnoreCase);

    // FLELEM-REJ-NULLADD: a null value on a link-list Add is refused 'requires an element value'.
    [Fact]
    public void AnAddWithNoValueOnALinkListIsRefused()
        => Refuses(Req("DialogResponses", "LinkTo", "Add"), "requires an element value");

    // FLELEM-REJ-NULLADD-PLAIN: the same for a plain List<String> (Race.MovementTypeNames).
    [Fact]
    public void AnAddWithNoValueOnAPlainListIsRefused()
        => Refuses(Req("Race", "MovementTypeNames", "Add"), "requires an element value");

    // FLELEM-REJ-NULLSETIDX: a compose with no value on a plain-list SetAtIndex still refuses.
    [Fact]
    public void AComposeDoesNotStandInForTheValueOfAPlainListSetAtIndex()
        => Refuses(new WriteRequest
        {
            RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "SetAtIndex", Key = "0",
            Struct = new StructSpec { Type = "Keyword" },
        }, "requires an element value");

    // FLELEM-REJ-NULLADD-E2E: a null-value LinkTo Add refuses end to end with no file.
    [Fact]
    public void AnAddWithNoValueIsRefusedBeforeTheFileIsWritten()
        => Assert.Contains("requires an element value", RefusedOnALine("FlElemNull", Req("DialogResponses", "LinkTo", "Add")), StringComparison.OrdinalIgnoreCase);

    // KEYIDX-REJ-DICTADD: a dict Add with no key refuses 'requires a key'.
    [Fact]
    public void ADictAddWithNoKeyIsRefused()
        => Refuses(Req("Class", "SkillWeights", "Add", "5"), "requires a key");

    // KEYIDX-REJ-DICTREMOVE: a dict Remove with no key refuses.
    [Fact]
    public void ADictRemoveWithNoKeyIsRefused()
        => Refuses(Req("Class", "SkillWeights", "Remove"), "requires a key");

    // KEYIDX-REJ-SETIDX: a list SetAtIndex with no index refuses 'requires an index'.
    [Fact]
    public void AListSetAtIndexWithNoIndexIsRefused()
        => Refuses(Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Walk"), "requires an index");

    // KEYIDX-OK-LISTREMOVE: a keyless list Remove with a value (remove by value) is accepted.
    [Fact]
    public void AListRemoveByValueNeedsNoIndex()
        => Accepts(Req("Race", "MovementTypeNames", "Remove", "MT_Walk"));

    // KEYIDX-REJ-SETIDX-E2E: a keyless LinkTo SetAtIndex refuses end to end with no file.
    [Fact]
    public void ASetAtIndexWithNoIndexIsRefusedBeforeTheFileIsWritten()
        => Assert.Contains("requires an index", RefusedOnALine("KeyIdxNoIdx", Req("DialogResponses", "LinkTo", "SetAtIndex", AnyLink)), StringComparison.OrdinalIgnoreCase);

    // KEYSHAPE-REJ-DICTADD: a dict Add with a non-coercible enum key refuses 'dict key' + 'not a legal Skill'.
    [Fact]
    public void ADictAddWithAnIllegalEnumKeyIsRefused()
        => Refuses(Req("Class", "SkillWeights", "Add", "5", "notaskill"), "dict key", "not a legal Skill");

    // KEYSHAPE-REJ-DICTREMOVE: a dict Remove with a non-coercible key refuses.
    [Fact]
    public void ADictRemoveWithAnIllegalEnumKeyIsRefused()
        => Refuses(Req("Class", "SkillWeights", "Remove", key: "notaskill"), "not a legal Skill");

    // KEYSHAPE-REJ-MERGEKEY: a Merge with a non-coercible Entries key refuses.
    [Fact]
    public void AMergeWithAnIllegalEntryKeyIsRefused()
        => Refuses(ReqEntries("Class", "SkillWeights", "Merge", "notaskill", "5"), "not a legal Skill");

    // KEYSHAPE-REJ-SBYTE: Package.Data (sbyte keys) Remove with a non-numeric key refuses 'does not coerce to sbyte'.
    [Fact]
    public void ANonNumericKeyOnAnSbyteKeyedDictIsRefused()
        => Refuses(Req("Package", "Data", "Remove", key: "notanumber"), "does not coerce to sbyte");

    // KEYSHAPE-REJ-SETIDX: a non-integer SetAtIndex index refuses 'Illegal list index' + 'non-negative integer'.
    [Fact]
    public void ANonIntegerListIndexIsRefused()
        => Refuses(Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Walk", "abc"), "Illegal list index", "non-negative integer");

    // KEYSHAPE-REJ-NEGIDX: a negative SetAtIndex index refuses.
    [Fact]
    public void ANegativeListIndexIsRefused()
        => Refuses(Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Walk", "-1"), "non-negative integer");

    // KEYSHAPE-REJ-LISTREMOVE-IDX: a non-integer index on a list Remove refuses.
    [Fact]
    public void ANonIntegerIndexOnAListRemoveIsRefused()
        => Refuses(Req("Race", "MovementTypeNames", "Remove", key: "abc"), "Illegal list index");

    // KEYSHAPE-OK-DICTADD: a valid enum key + value is accepted.
    [Fact]
    public void ADictAddWithAValidKeyIsAccepted()
        => Accepts(Req("Class", "SkillWeights", "Add", "5", "OneHanded"));

    // KEYSHAPE-OK-SETIDX: a valid index is accepted.
    [Fact]
    public void AListSetAtIndexWithAValidIndexIsAccepted()
        => Accepts(Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Walk", "0"));

    // KEYSHAPE-OK-NUMENUM-SET: a numeric enum key '3' on a dict Set is accepted, as apply accepts it.
    [Fact]
    public void ANumericEnumKeyOnADictSetIsAccepted()
        => Accepts(Req("Class", "SkillWeights", "Set", "5", "3"));

    // KEYSHAPE-REJ-E2E: a non-integer LinkTo SetAtIndex index refuses end to end with no file ('Illegal list index').
    [Fact]
    public void ANonIntegerIndexIsRefusedBeforeTheFileIsWritten()
        => Assert.Contains("Illegal list index", RefusedOnALine("KeyShapeIdx", Req("DialogResponses", "LinkTo", "SetAtIndex", AnyLink, "abc")), StringComparison.OrdinalIgnoreCase);

    // GAP1-REJ-MIDKEY-SBYTE: a malformed sbyte key in a mid-path hop 'Data[notasbyte].Name' refuses.
    [Fact]
    public void AMalformedMidPathDictKeyIsRefused()
        => Refuses(Req("Package", "Data[notasbyte].Name", "Set", "houseCARL"), "does not coerce to sbyte");

    // GAP1-OK-MIDKEY: a valid sbyte mid-path key 'Data[0].Name' is accepted.
    [Fact]
    public void AValidMidPathDictKeyIsAccepted()
        => Accepts(Req("Package", "Data[0].Name", "Set", "houseCARL"));

    // GAP1-REJ-E2E: a malformed mid-path key refuses end to end with no file.
    [Fact]
    public void AMalformedMidPathDictKeyIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejGap1E2E.esp", Spec("Package", "HcNcG1Pack", Req("Package", "Data[notasbyte].Name", "Set", "houseCARL")));
        Assert.Contains("does not coerce to sbyte", err, StringComparison.OrdinalIgnoreCase);
    }

    // GAP1-REJ-MIDLISTIDX-NEG: a negative mid-path list index 'Conditions[-1].CompareOperator' refuses.
    [Fact]
    public void ANegativeMidPathListIndexIsRefused()
        => Refuses(Req("Faction", "Conditions[-1].CompareOperator", "Set", "EqualTo"), "non-negative integer");

    // GAP1-OK-MIDLISTIDX: a valid mid-path list index 'Conditions[0]' is accepted.
    [Fact]
    public void AValidMidPathListIndexIsAccepted()
        => Accepts(Req("Faction", "Conditions[0].CompareOperator", "Set", "EqualTo"));

    // GAP1-REJ-NEGIDX-E2E: after a compose materializes Conditions, a [-1] hop refuses cleanly, no wrapper, no file.
    [Fact]
    public void ANegativeMidPathIndexIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejGap1NegIdx.esp", Spec("Faction", "HcNcG1NegIdx",
            new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add", Struct = new StructSpec { Type = "ConditionFloat" } },
            Req("Faction", "Conditions[-1].CompareOperator", "Set", "EqualTo")));
        Assert.Contains("non-negative integer", err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("real inconsistency", err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pre-flight ACCEPTED", err, StringComparison.OrdinalIgnoreCase);
    }

    // GAP2-REJ-DICTADD: a malformed dict Add value refuses 'does not coerce to Byte'.
    [Fact]
    public void AMalformedDictAddValueIsRefused()
        => Refuses(Req("Class", "SkillWeights", "Add", "notabyte", "OneHanded"), "does not coerce to Byte");

    // GAP2-REJ-LISTADD: a malformed List<Single> Add value refuses 'does not coerce to Single'.
    [Fact]
    public void AMalformedListAddValueIsRefused()
        => Refuses(Req("MusicTrack", "CuePoints", "Add", "notafloat"), "does not coerce to Single");

    // GAP2-REJ-LISTREPLACEALL: a bad ReplaceAll value past a good one is refused, named.
    [Fact]
    public void AMalformedReplaceAllValueIsRefusedByName()
        => Refuses(ReqValues("MusicTrack", "CuePoints", "ReplaceAll", "1.5", "notafloat"), "does not coerce to Single", "notafloat");

    // GAP2-REJ-LISTREMOVE: a malformed Remove-by-value refuses.
    [Fact]
    public void AMalformedRemoveByValueIsRefused()
        => Refuses(Req("MusicTrack", "CuePoints", "Remove", "notafloat"), "does not coerce to Single");

    // GAP2-REJ-DICTMERGE: a malformed Merge entries value refuses 'does not coerce to Byte'.
    [Fact]
    public void AMalformedMergeValueIsRefused()
        => Refuses(ReqEntries("Class", "SkillWeights", "Merge", "OneHanded", "notabyte"), "does not coerce to Byte");

    // GAP2-OK-VALID: valid list and dict element values are accepted.
    [Fact]
    public void ValidPlainElementValuesAreAccepted()
    {
        Accepts(Req("MusicTrack", "CuePoints", "Add", "1.5"));
        Accepts(Req("Class", "SkillWeights", "Add", "5", "OneHanded"));
    }

    // GAP2-OK-REMOVE-BYINDEX: a by-index Remove with a stray value is not refused (apply ignores the value).
    [Fact]
    public void ARemoveByIndexIgnoresAStrayValue()
        => Accepts(Req("MusicTrack", "CuePoints", "Remove", "notafloat", "0"));

    // GAP2-FORMLINK-ROUTE: a link list keeps the link check ('Illegal FormLink element', not 'does not coerce').
    [Fact]
    public void ALinkListElementTakesTheLinkCheckNotThePlainOne()
    {
        Accepts(Req("Weapon", "Keywords", "Add", AnyLink));
        var bad = Check(Req("Weapon", "Keywords", "Add", "notaformkey"));
        Assert.NotNull(bad);
        Assert.Contains("Illegal FormLink element", bad, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not coerce", bad, StringComparison.OrdinalIgnoreCase);
    }

    // GAP2-OK-OFFCARD-SLOT: a stray Entries on a list ReplaceAll (apply ignores it) is not refused.
    [Fact]
    public void AStrayEntriesSlotOnAListReplaceAllIsNotRefused()
        => Accepts(new WriteRequest
        {
            RecordType = "MusicTrack", Path = new[] { "CuePoints" }, Verb = "ReplaceAll",
            Values = new[] { "1.5" }, Entries = new() { ["x"] = "notafloat" },
        });

    // GAP2-REJ-E2E: a malformed list value on a MusicTrack create refuses end to end with no file.
    [Fact]
    public void AMalformedListValueIsRefusedBeforeTheFileIsWritten()
    {
        var err = W.Refused("HcNcRejGap2.esp", Spec("MusicTrack", "HcNcG2Mtrk", Req("MusicTrack", "CuePoints", "Add", "notafloat")));
        Assert.Contains("does not coerce to Single", err, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _rig?.Dispose();
}
