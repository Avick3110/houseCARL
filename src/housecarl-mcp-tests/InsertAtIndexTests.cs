using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What pre-flight says about an InsertAtIndex with no live list in front of it: the index's presence and
/// shape, cardinality, the element-value and compose requirements, the record-element redirect, and the verb list.
/// Migrated from the insert-at-index-guard probe (GATE arms).</summary>
[Trait("tier", "integration")]
public sealed class InsertAtIndexGateTests
{
    static CorpusRulebook Rules => TestCorpus.Rulebook;

    // Race.MovementTypeNames is a list of strings: a coercible-element list.
    static WriteRequest Names(string? key, string? value = null) => new()
    { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "InsertAtIndex", Key = key, Value = value };

    // Faction.Conditions is a list of Condition: a modeled-element list, the shape of a dialogue INFO's Conditions.
    static WriteRequest Cond(string? value, StructSpec? spec) => new()
    { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex", Key = "0", Value = value, Struct = spec };

    // GATE-REJ-NOIDX: an InsertAtIndex with NO index refuses at pre-flight, naming the index
    [Fact]
    public void AnInsertWithNoIndexRefusesNamingTheIndex()
        => Assert.Contains("requires an index", Rules.Validate(Names(null, "MT_Walk")));

    // GATE-REJ-BADIDX: a non-integer index refuses
    [Fact]
    public void ANonIntegerIndexRefuses()
        => Assert.Contains("Illegal list index", Rules.Validate(Names("abc", "MT_Walk")));

    // GATE-REJ-NEGIDX: a NEGATIVE index refuses (int.Parse would accept '-1'; the indexer would not)
    [Fact]
    public void ANegativeIndexRefuses()
        => Assert.Contains("Illegal list index", Rules.Validate(Names("-1", "MT_Walk")));

    // GATE-OK-IDX: a valid index + value is ACCEPTED (the gate does not over-reject the new verb)
    [Fact]
    public void AValidIndexAndValueIsAccepted()
        => Assert.Null(Rules.Validate(Names("0", "MT_Walk")));

    // GATE-OK-FARIDX (control): an index far past any live list is STILL accepted; in-range is apply's, not the gate's
    [Fact]
    public void AnIndexFarPastAnyListIsStillAcceptedAtTheGate()
        => Assert.Null(Rules.Validate(Names("9999", "MT_Walk")));

    // GATE-REJ-NONLIST: InsertAtIndex on a dict refuses by cardinality
    [Fact]
    public void AnInsertOnADictRefusesByCardinality()
        => Assert.Contains("InsertAtIndex is only valid on list", Rules.Validate(new WriteRequest
        { RecordType = "Npc", Path = new[] { "PlayerSkills", "SkillValues" }, Verb = "InsertAtIndex", Key = "0", Value = "50" }));

    // GATE-REJ-NOVALUE: a coercible-element insert with NO value refuses (else a null element throws at serialize)
    [Fact]
    public void ACoercibleElementInsertWithNoValueRefuses()
        => Assert.Contains("requires an element value", Rules.Validate(Names("0")));

    // GATE-REJ-BADFORMLINK (control): a malformed formlink element value refuses; step-4a is verb-agnostic
    [Fact]
    public void AMalformedFormLinkElementRefusesNamingTheValueAndTheField()
    {
        var m = Rules.Validate(new WriteRequest
        { RecordType = "Armor", Path = new[] { "Keywords" }, Verb = "InsertAtIndex", Key = "0", Value = "notaformkey" });
        Assert.Contains("notaformkey", m);
        Assert.Contains("Keywords", m);
    }

    // GATE-REJ-NOSTRUCT: a PLAIN VALUE insert into a modeled-element list refuses, naming the compose spec (as Add does)
    [Fact]
    public void APlainValueIntoAModeledElementListRefusesNamingTheCompose()
        => Assert.Contains("compose spec", Rules.Validate(Cond("1", null)), StringComparison.OrdinalIgnoreCase);

    // GATE-OK-COMPOSE: an insert CARRYING an arm compose is accepted, through the same gate Add's compose passes
    [Fact]
    public void AnInsertCarryingAnArmComposeIsAccepted()
        => Assert.Null(Rules.Validate(Cond(null, new StructSpec { Type = "ConditionFloat" })));

    // GATE-REJ-BADARMTYPE: a compose naming a type that is not an arm of the element refuses, so the spec IS validated
    [Fact]
    public void AComposeNamingANonArmTypeRefuses()
        => Assert.Contains("NotAConditionArm", Rules.Validate(Cond(null, new StructSpec { Type = "NotAConditionArm" })));

    // GATE-REJ-BADARMFIELD: a malformed field inside the compose refuses too (contents validated recursively)
    [Fact]
    public void AMalformedFieldInsideTheComposeRefuses()
        => Assert.Contains("notafloat", Rules.Validate(Cond(null, new StructSpec
        { Type = "ConditionFloat", Fields = new Dictionary<string, string> { ["ComparisonValue"] = "notafloat" } })));

    // GATE-REJ-RECORDELEM: an insert into a list of owned child RECORDS redirects to the record axis, not accepted-then-thrown
    [Fact]
    public void AnInsertIntoOwnedChildRecordsRedirectsToHousecarlCreate()
    {
        var m = Rules.Validate(new WriteRequest
        {
            RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "InsertAtIndex", Key = "0",
            Struct = new StructSpec { Type = "PlacedObject" },
        });
        Assert.Contains("owned child records", m);
        // The WHOLE identifier: housecarl_create is a prefix of housecarl_create_record.
        Assert.Matches(new Regex("(?<![A-Za-z0-9_])housecarl_create(?![A-Za-z0-9_])"), m);
    }

    // GATE-REJ-COMPOSES: composes= stays Add/ReplaceAll only, and the refusal names InsertAtIndex's singular path
    [Fact]
    public void ComposesOnAnInsertRefusesNamingTheSingularPath()
        => Assert.Contains("InsertAtIndex (compose= + key=)", Rules.Validate(new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex", Key = "0",
            Structs = new[] { new StructSpec { Type = "ConditionFloat" } },
        }));

    // GATE-LEGALVERBS: the unknown-verb message advertises InsertAtIndex, so the published vocabulary is the real one
    [Fact]
    public void TheUnknownVerbRefusalListsInsertAtIndex()
        => Assert.Contains("InsertAtIndex", Rules.Validate(new WriteRequest
        { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Nope", Key = "0", Value = "MT_Walk" }));
}

/// <summary>Where an inserted element lands and what moves, on in-memory records: the new element at the index asked
/// for, every row after it the SAME object shifted by one, the append-inclusive bound, and the sibling verbs'
/// out-of-range messages. Migrated from the insert-at-index-guard probe (APPLY arms).</summary>
[Trait("tier", "integration")]
public sealed class InsertAtIndexApplyTests
{
    static readonly ModKey Mod = new("HcInsertApplyTests", ModType.Master);
    static int _next = 0x800;
    static FormKey NextFk() => new(Mod, (uint)Interlocked.Increment(ref _next));

    static StructSpec Row(float value) => new()
    {
        Type = "ConditionFloat",
        Fields = new() { ["ComparisonValue"] = value.ToString(CultureInfo.InvariantCulture) },
        Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
            Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
    };

    /// <summary>A Faction carrying <paramref name="values"/> as ConditionFloat rows, in order.</summary>
    static Faction Conditions(params float[] values)
    {
        var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
        foreach (var v in values) Add(fac, v);
        return fac;
    }

    static void Add(Faction fac, float value) => WriteEngine.ApplyVerb(fac, new WriteRequest
    { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Add", Struct = Row(value) });

    static void Insert(Faction fac, int idx, float value) => WriteEngine.ApplyVerb(fac, new WriteRequest
    {
        RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "InsertAtIndex",
        Key = idx.ToString(CultureInfo.InvariantCulture), Struct = Row(value),
    });

    static float[] Values(IEnumerable<Condition> conds) =>
        conds.Select(c => c is IConditionFloatGetter f ? f.ComparisonValue : float.NaN).ToArray();

    /// <summary>The refusal message, asserted to be the EXPECTED kind: a reflection-wrapped throw would also leave the
    /// list untouched, but reaches the caller with no field, no index and no bound.</summary>
    static string Refused(Action act) => Assert.IsType<ExpectedApplyRejectionException>(Record.Exception(act)).Message;

    // APPLY-AT-0: inserting at 0 puts the new row first and shifts all three originals right by one, same objects, same order
    [Fact]
    public void InsertingAtZeroShiftsEveryOriginalRowRightByOne()
    {
        var fac = Conditions(1f, 2f, 3f);
        var before = fac.Conditions!.ToArray();
        Insert(fac, 0, 9f);
        Assert.Equal(new[] { 9f, 1f, 2f, 3f }, Values(fac.Conditions!));
        Assert.Same(before[0], fac.Conditions![1]);
        Assert.Same(before[1], fac.Conditions![2]);
        Assert.Same(before[2], fac.Conditions![3]);
    }

    // APPLY-MID: inserting mid-list leaves the rows BEFORE it untouched and shifts only the rows after it
    [Fact]
    public void InsertingMidListShiftsOnlyTheRowsAfterIt()
    {
        var fac = Conditions(1f, 2f, 3f);
        var before = fac.Conditions!.ToArray();
        Insert(fac, 1, 9f);
        Assert.Equal(new[] { 1f, 9f, 2f, 3f }, Values(fac.Conditions!));
        Assert.Same(before[0], fac.Conditions![0]);
        Assert.Same(before[1], fac.Conditions![2]);
        Assert.Same(before[2], fac.Conditions![3]);
    }

    // APPLY-AT-COUNT-IS-ADD: inserting AT the list's length yields the identical list an Add yields (why the bound includes count)
    [Fact]
    public void InsertingAtTheLengthYieldsWhatAnAddYields()
    {
        var viaAdd = Conditions(1f, 2f, 3f);
        Add(viaAdd, 9f);
        var viaInsert = Conditions(1f, 2f, 3f);
        Insert(viaInsert, viaInsert.Conditions!.Count, 9f);
        // Pinned to the literal list too, so a broken Add cannot make the two agree by both being wrong.
        Assert.Equal(new[] { 1f, 2f, 3f, 9f }, Values(viaInsert.Conditions!));
        Assert.Equal(Values(viaAdd.Conditions!), Values(viaInsert.Conditions!));
    }

    // APPLY-REJ-OOB: one past the append slot refuses, stating the APPEND-INCLUSIVE bound (0..count), and changes nothing
    [Fact]
    public void OnePastTheAppendSlotRefusesStatingTheAppendInclusiveBound()
    {
        var fac = Conditions(1f, 2f, 3f);
        var msg = Refused(() => Insert(fac, 4, 9f));
        Assert.Contains("0..3", msg);
        Assert.DoesNotContain("0..2", msg);
        Assert.Equal(3, fac.Conditions!.Count);
    }

    // APPLY-REJ-NEGIDX: a negative index refuses at APPLY as the EXPECTED kind, naming the index, for a call that never met the gate
    [Fact]
    public void ANegativeIndexRefusesAtApplyAsTheExpectedKind()
    {
        var fac = Conditions(1f, 2f);
        Assert.Contains("Index -1 out of range", Refused(() => Insert(fac, -1, 9f)));
        Assert.Equal(2, fac.Conditions!.Count);
    }

    // APPLY-SIBLING-SETATINDEX-MSG: an out-of-range SetAtIndex still offers Add and still states its own bound (0..count-1), not insert's
    [Fact]
    public void AnOutOfRangeSetAtIndexStillOffersAddAndItsOwnBound()
    {
        var fac = Conditions(1f, 2f, 3f);
        var msg = Refused(() => WriteEngine.ApplyVerb(fac, new WriteRequest
        {
            RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "SetAtIndex", Key = "9",
            Struct = new StructSpec { Type = "ConditionFloat" },
        }));
        Assert.Contains("or Add to append", msg);
        Assert.Contains("0..2", msg);
        Assert.DoesNotContain("0..3", msg);
    }

    // APPLY-SIBLING-REMOVE-MSG: an out-of-range Remove-by-index on a present, empty list says 'nothing to remove' and never offers Add
    [Fact]
    public void AnOutOfRangeRemoveOnAPresentEmptyListSaysNothingToRemove()
    {
        // Present and empty, not absent: an absent list stops at its own "absent" refusal before the range message.
        var fac = Conditions(1f);
        WriteEngine.ApplyVerb(fac, new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Remove", Key = "0" });
        Assert.NotNull(fac.Conditions);
        Assert.Empty(fac.Conditions!);
        var msg = Refused(() => WriteEngine.ApplyVerb(fac, new WriteRequest
        { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Remove", Key = "0" }));
        Assert.Contains("nothing to remove", msg);
        Assert.DoesNotContain("Add an element first", msg);
    }

    // APPLY-COERCIBLE-SHIFT: a plain-value list shifts the same way (A,B + X@1 -> A,X,B)
    [Fact]
    public void APlainValueListShiftsTheSameWay()
    {
        var race = new Race(NextFk(), SkyrimRelease.SkyrimSE);
        foreach (var v in new[] { "A", "B" })
            WriteEngine.ApplyVerb(race, new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Add", Value = v });
        WriteEngine.ApplyVerb(race, new WriteRequest
        { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "InsertAtIndex", Key = "1", Value = "X" });
        Assert.Equal(new[] { "A", "X", "B" }, race.MovementTypeNames!);
    }

    // APPLY-ABSENT-AT-0 (materialize control): an empty/absent list takes an insert at 0
    [Fact]
    public void AnEmptyListTakesAnInsertAtZero()
    {
        var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
        // Absent, not merely empty, so the insert goes through the materialize.
        Assert.Null(fac.Conditions);
        Insert(fac, 0, 7f);
        Assert.Equal(new[] { 7f }, Values(fac.Conditions!));
    }

    // APPLY-ABSENT-REJ-1: index 1 into an empty list refuses, and says the list is empty rather than quoting a bound
    [Fact]
    public void IndexOneIntoAnEmptyListRefusesNamingInsertAtZero()
    {
        var fac = new Faction(NextFk(), SkyrimRelease.SkyrimSE);
        // "the list is empty" is in the shared SetAtIndex/Remove sentence too; the remedy is Insert's own.
        Assert.Contains("insert at index 0", Refused(() => Insert(fac, 1, 7f)));
        Assert.Equal(0, fac.Conditions?.Count ?? 0);
    }
}

/// <summary>The position claim through a real write and a binary-overlay re-read: a row inserted into the middle of
/// an OR-run comes back off disk where it was inserted, with the Or flags still on the rows that carried them.
/// Migrated from the insert-at-index-guard probe (SERIALIZE arm).</summary>
[Trait("tier", "integration")]
public sealed class InsertAtIndexSerializeTests : IDisposable
{
    static readonly ModKey Mod = new("HcInsertSerializeTests", ModType.Master);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-insert-serialize-" + Guid.NewGuid().ToString("N"));

    public InsertAtIndexSerializeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    static WriteRequest Row(string verb, float value, bool or, string? key = null) => new()
    {
        RecordType = "Faction", Path = new[] { "Conditions" }, Verb = verb, Key = key,
        Struct = new StructSpec
        {
            Type = "ConditionFloat",
            Fields = new() { ["ComparisonValue"] = value.ToString(CultureInfo.InvariantCulture), ["Flags"] = or ? "OR" : "0" },
            Sets = new() { new WriteRequest { RecordType = "ConditionFloat", Path = new[] { "Data" }, Verb = "Set",
                Struct = new StructSpec { Type = "GetRandomPercentConditionData" } } },
        },
    };

    // SERIALIZE-ORDER: the file holds 1,9,2,3: the inserted row sits where it was inserted and the tail kept its order
    // SERIALIZE-ORRUN: the Or flags came back on the rows that carried them, so the inserted arm is INSIDE the OR-run
    [Fact]
    public void ARowInsertedIntoAnOrRunComesBackOffDiskInsideIt()
    {
        var mod = new SkyrimMod(Mod, SkyrimRelease.SkyrimSE);
        var fac = mod.Factions.AddNew();
        fac.EditorID = "HcInsertSerializeFaction";
        // An OR-run of two, then a plain AND row.
        WriteEngine.ApplyVerb(fac, Row("Add", 1f, or: true));
        WriteEngine.ApplyVerb(fac, Row("Add", 2f, or: false));
        WriteEngine.ApplyVerb(fac, Row("Add", 3f, or: false));
        // A third arm into the run, at index 1 and carrying the Or flag: the run reads 1|9|2.
        WriteEngine.ApplyVerb(fac, Row("InsertAtIndex", 9f, or: true, key: "1"));

        var path = Path.Combine(_dir, Mod.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var conds = back.Factions.Single(f => f.EditorID == "HcInsertSerializeFaction").Conditions!.ToArray();
        Assert.Equal(new[] { 1f, 9f, 2f, 3f }, conds.Select(c => c is IConditionFloatGetter f ? f.ComparisonValue : float.NaN));
        Assert.Equal(new[] { true, true, false, false }, conds.Select(c => c.Flags.HasFlag(Condition.Flag.OR)));
    }
}

/// <summary>The dev harness's two value-requiring verb lists both carry InsertAtIndex, so a valueless insert is refused
/// by name before any patch is built. Migrated from the insert-at-index-guard probe (CLI arm).</summary>
[Trait("tier", "integration")]
[Collection(SerialCollection.Name)]   // captures Console.Error, which is process-wide
public sealed class InsertAtIndexCliTests : IDisposable
{
    static readonly ModKey Mod = new("HcInsertCliTests", ModType.Master);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-insert-cli-" + Guid.NewGuid().ToString("N"));
    readonly string _source;

    public InsertAtIndexCliTests()
    {
        Directory.CreateDirectory(_dir);
        // RunPatch requires a real --source file before it parses the edits; a header-only mod is enough.
        _source = Path.Combine(_dir, Mod.FileName.String);
        new SkyrimMod(Mod, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(_source).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    static (int Code, string Err) Run(params string[] args)
    {
        var prior = Console.Error;
        var sw = new StringWriter();
        try { Console.SetError(sw); return (WriteEngine.RunPatch(args), sw.ToString()); }
        finally { Console.SetError(prior); }
    }

    // CLI-OP-NEEDSVALUE: a repeatable --op InsertAtIndex with no value is refused by name, before any patch is built
    [Fact]
    public void AnOpInsertWithNoValueIsRefusedByName()
    {
        var (code, err) = Run("--source", _source, "--type", "Armor", "--editorid", "Whatever", "--op", "InsertAtIndex|Keywords|0");
        Assert.Equal(1, code);
        Assert.Contains("verb 'InsertAtIndex' needs a value", err);
    }

    // CLI-VERB-NEEDSVALUE: the single-edit --verb form refuses it too, so the two lists cannot drift apart
    [Fact]
    public void ASingleEditInsertWithNoValueIsRefusedByName()
    {
        var (code, err) = Run("--source", _source, "--type", "Armor", "--editorid", "Whatever",
            "--path", "Keywords", "--verb", "InsertAtIndex", "--key", "0");
        Assert.Equal(1, code);
        Assert.Contains("--value is required for verb 'InsertAtIndex'", err);
    }
}
