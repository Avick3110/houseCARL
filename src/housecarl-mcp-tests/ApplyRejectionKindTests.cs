using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>Refusals only apply can see (an index past the end, an absent entry, a remove that removes nothing) come
/// back as the expected kind and read cleanly, never under the pre-flight inconsistency wrapper; a present-but-null
/// element is its own malformed-data kind; and the in-range and present cases still apply. Migrated from the
/// nested-create-guard probe (EXPECTED, MALFORMED and REMOVE arms).</summary>
[Trait("tier", "integration")]
public sealed class ApplyRejectionKindTests : IDisposable
{
    static readonly ModKey Mod = ModKey.FromFileName(MasterName);

    NestedCreateRig? _rig;
    NestedCreateRig W => _rig ??= new NestedCreateRig();

    static WriteRequest Req(string type, string path, string verb, string? value = null, string? key = null)
        => WritePathRig.Req(type, path, verb, value, key);

    static WriteRequest PackageBool(string verb, string value) => new()
    {
        RecordType = "Package", Path = new[] { "Data" }, Verb = verb, Key = "0",
        Struct = new StructSpec { Type = "PackageDataBool", Fields = new() { ["Data"] = value } },
    };

    /// <summary>A one-record create refused cleanly: the word is there, the wrapper is not, no file.</summary>
    void RefusedCleanly(string tag, string word, WritePatchBuilder.CreateSpec spec)
    {
        var err = W.Refused($"HcNcRej{tag}.esp", spec);
        Assert.Contains(word, err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("real inconsistency", err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pre-flight ACCEPTED", err, StringComparison.OrdinalIgnoreCase);
    }

    // EXPECTED-REJ-SETIDX-OOB: MovementTypeNames SetAtIndex[5] refuses 'out of range' cleanly, no file.
    [Fact]
    public void ASetAtIndexPastTheEndIsRefusedCleanly()
        => RefusedCleanly("ExpSetIdxOob", "out of range",
            Spec("Race", "HcNcExpSetIdx", Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Walk", "5")));

    // EXPECTED-REJ-REMOVEIDX-OOB: Add one then Remove[5] refuses 'out of range' cleanly, no file.
    [Fact]
    public void ARemoveByIndexPastTheEndIsRefusedCleanly()
        => RefusedCleanly("ExpRmIdxOob", "out of range",
            Spec("Race", "HcNcExpRmIdx", Req("Race", "MovementTypeNames", "Add", "MT_Walk"), Req("Race", "MovementTypeNames", "Remove", key: "5")));

    // EXPECTED-OK-SETIDX-INRANGE: Add then SetAtIndex[0] applies; the value lands.
    [Fact]
    public void AnInRangeSetAtIndexApplies()
    {
        var race = new Race(new FormKey(Mod, 0x901u), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "Add", "MT_Walk"));
        WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "SetAtIndex", "MT_Run", "0"));
        Assert.Equal(new[] { "MT_Run" }, race.MovementTypeNames);
    }

    // EXPECTED-NAV-TYPE: a mid-path step into an absent dict entry throws the expected kind.
    [Fact]
    public void AMidPathStepIntoAnAbsentDictEntryIsAnExpectedRejection()
    {
        var pkg = new Package(new FormKey(Mod, 0x902u), SkyrimRelease.SkyrimSE);
        Assert.ThrowsAny<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(pkg, Req("Package", "Data[0].Data", "Set", "true")));
    }

    // EXPECTED-NAV-TYPE: a mid-path step past the end of a list throws the expected kind.
    [Fact]
    public void AMidPathStepPastTheEndOfAListIsAnExpectedRejection()
    {
        var fac = new Faction(new FormKey(Mod, 0x903u), SkyrimRelease.SkyrimSE);
        Assert.ThrowsAny<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(fac, Req("Faction", "Conditions[5].Flags", "Set", "0")));
    }

    // EXPECTED-REJ-NAV-E2E: Package.Data[5].Name on a fresh package refuses 'No entry with key' cleanly, no file.
    [Fact]
    public void AMidPathStepIntoAnAbsentEntryIsRefusedCleanly()
        => RefusedCleanly("ExpNavKey", "No entry with key",
            Spec("Package", "HcNcExpNav", Req("Package", "Data[5].Name", "Set", "houseCARL")));

    // MALFORMED-NAV-TYPE: a present-but-null dict entry throws MalformedTargetDataException ('malformed').
    [Fact]
    public void AStepIntoANullDictEntryIsMalformedData()
    {
        var pkg = new Package(new FormKey(Mod, 0x913u), SkyrimRelease.SkyrimSE);
        pkg.Data[(sbyte)0] = null!;
        var ex = Assert.ThrowsAny<MalformedTargetDataException>(() => WriteEngine.ApplyVerb(pkg, Req("Package", "Data[0].Data", "Set", "true")));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // MALFORMED-NAV-TYPE: a present-but-null list element throws MalformedTargetDataException ('malformed').
    [Fact]
    public void AStepIntoANullListElementIsMalformedData()
    {
        var fac = new Faction(new FormKey(Mod, 0x914u), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(fac, new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add", Struct = new StructSpec { Type = "ConditionFloat" } });
        fac.Conditions![0] = null!;
        var ex = Assert.ThrowsAny<MalformedTargetDataException>(() => WriteEngine.ApplyVerb(fac, Req("Faction", "Conditions[0].CompareOperator", "Set", "EqualTo")));
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // REMOVE-REJ-DICTKEY-ABSENT: a Package.Data Remove of an absent key refuses 'nothing to remove' cleanly, no file.
    [Fact]
    public void ADictRemoveOfAnAbsentKeyIsRefusedCleanly()
        => RefusedCleanly("RmDictKey", "nothing to remove", Spec("Package", "HcNcRmDictKey", Req("Package", "Data", "Remove", key: "0")));

    // REMOVE-REJ-NULLCOLL: a Remove on a null Faction.Conditions refuses 'nothing to remove' cleanly, no file.
    [Fact]
    public void ARemoveOnAnAbsentCollectionIsRefusedCleanly()
        => RefusedCleanly("RmNullColl", "nothing to remove", Spec("Faction", "HcNcRmNull", Req("Faction", "Conditions", "Remove", key: "5")));

    // REMOVE-REJ-LISTVAL-ABSENT: a list Remove-by-value of an absent value throws the expected kind; the list is untouched.
    [Fact]
    public void AListRemoveOfAnAbsentValueIsAnExpectedRejection()
    {
        var race = new Race(new FormKey(Mod, 0x910u), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "Add", "MT_Run"));
        Assert.ThrowsAny<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "Remove", "MT_Walk")));
        Assert.Single(race.MovementTypeNames!);
    }

    // REMOVE-OK-PRESENT-DICT: a dict Remove of a present key empties the dict.
    [Fact]
    public void ADictRemoveOfAPresentKeyRemovesIt()
    {
        var pkg = new Package(new FormKey(Mod, 0x911u), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(pkg, PackageBool("Add", "true"));
        WriteEngine.ApplyVerb(pkg, Req("Package", "Data", "Remove", key: "0"));
        Assert.Empty(pkg.Data);
    }

    // REMOVE-OK-PRESENT-LIST: a list Remove-by-value of a present value empties the list.
    [Fact]
    public void AListRemoveOfAPresentValueRemovesIt()
    {
        var race = new Race(new FormKey(Mod, 0x912u), SkyrimRelease.SkyrimSE);
        WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "Add", "MT_Run"));
        WriteEngine.ApplyVerb(race, Req("Race", "MovementTypeNames", "Remove", "MT_Run"));
        Assert.Empty(race.MovementTypeNames!);
    }

    public void Dispose() => _rig?.Dispose();
}
