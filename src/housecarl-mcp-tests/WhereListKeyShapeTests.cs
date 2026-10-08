using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1106: a list step keyed by a word ('Conditions[any]') is a wrong path, refused with the starred
/// quantifier it meant, never a Mutagen read fault; the starred step reads the operator.</summary>
[Trait("tier", "unit")]
public sealed class WhereListKeyShapeTests
{
    readonly SkyrimMod _mod = new(new ModKey("KeyShapeWorld", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly List<IMajorRecordGetter> _effects = new();
    readonly FormKey _equal, _notEqual;

    public WhereListKeyShapeTests()
    {
        _equal = Add("HcCondEqual", CompareOperator.EqualTo);
        _notEqual = Add("HcCondNotEqual", CompareOperator.NotEqualTo);
        Add("HcCondNone");
    }

    FormKey Add(string eid, params CompareOperator[] ops)
    {
        var e = _mod.MagicEffects.AddNew();
        e.EditorID = eid;
        foreach (var op in ops)
            e.Conditions.Add(new ConditionFloat { CompareOperator = op, ComparisonValue = 1, Data = new GetLevelConditionData() });
        _effects.Add(e);
        return e.FormKey;
    }

    (HashSet<FormKey> Hits, FieldPredicateSet Set) Run(string clause)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        var hits = _effects.Where(b => set!.Matches(b)).Select(b => b.FormKey).ToHashSet();
        return (hits, set!);
    }

    [Fact]
    public void TheStarredStepMatchesTheOperator() =>
        Assert.Equal(new[] { _equal }.ToHashSet(), Run("Conditions[*any].CompareOperator = EqualTo").Hits);

    [Fact]
    public void ADifferentOperatorDoesNotMatch()
    {
        var (hits, set) = Run("Conditions[*any].CompareOperator = GreaterThan");
        Assert.Empty(hits);
        Assert.Null(set.FatalError);
        Assert.Null(set.AccountingNote());
        Assert.Equal(new[] { _notEqual }.ToHashSet(), Run("Conditions[*any].CompareOperator = NotEqualTo").Hits);
    }

    [Fact]
    public void AWordKeyRefusesAndNamesTheStarredStep()
    {
        var (hits, set) = Run("Conditions[any].CompareOperator = EqualTo");
        Assert.Empty(hits);
        Assert.Contains("'Conditions[*any]'", set.FatalError);
        Assert.DoesNotContain("read FAULT", set.AccountingNote() ?? "");
    }

    [Fact]
    public void APlainReadOfAWordKeyIsAWrongPathNotAFault()
    {
        var f = ReadEngine.ReadFields((IMajorRecordGetter)_mod.MagicEffects.First(), new[] { "Conditions[any].CompareOperator" }).Fields.Single();
        Assert.True(ReadEngine.IsNoSuchFieldNote(f.Note), f.Note);
    }

    [Fact]
    public void AWordKeyThatIsNoQuantifierGetsNoStarHint()
    {
        var (_, set) = Run("Conditions[abc].CompareOperator = EqualTo");
        Assert.Contains("got 'abc'.", set.FatalError);
        Assert.DoesNotContain("A quantifier takes a star", set.FatalError);
    }

    [Fact]
    public void AWordKeyOnANullListStillRefuses()
    {
        Assert.All(_mod.MagicEffects, e => Assert.Null(e.Keywords));
        var (hits, set) = Run("Keywords[any] exists");
        Assert.Empty(hits);
        Assert.Contains("'Keywords[*any]'", set.FatalError);
        var f = ReadEngine.ReadFields((IMajorRecordGetter)_mod.MagicEffects.First(), new[] { "Keywords[abc]" }).Fields.Single();
        Assert.True(ReadEngine.IsNoSuchFieldNote(f.Note), f.Note);
    }

    [Theory]
    [InlineData("Conditions[any].CompareOperator")]
    [InlineData("Conditionz")]
    public void ADiffNeverCallsAPathWithNoFieldOnBothSidesIdentical(string path)
    {
        RecordFields Read(FormKey fk) => ReadEngine.ReadFields(_effects.Single(e => e.FormKey == fk), new[] { path });
        var d = FieldsDiff.Compare(Read(_equal), Read(_equal));
        Assert.False(d.Complete);
        Assert.Equal(1, d.NoVerdictCount);
        Assert.StartsWith($"{path}: NO FIELD on both sides — not compared (no field", Assert.Single(d.Deltas));
    }
}

/// <summary>The genuine fault keeps its bucket next to the key-shape refusal: a cut-short DATA still reads as a read fault.</summary>
[Trait("tier", "integration")]
public sealed class WhereListKeyShapeFaultTests(TruncatedSubFieldFixture t) : IClassFixture<TruncatedSubFieldFixture>
{
    [Fact]
    public void AFieldMutagenCannotParseIsStillAReadFault()
    {
        var r = RecordsTools.Records(t.W.Svc, source: JsonDocument.Parse("\"" + t.W.TruncName + "\"").RootElement.Clone(),
            types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage >= 0" }, counts_only: true);
        Assert.Contains("a read FAULT", r);
    }
}

/// <summary>The fold of Aaron's review: a bad key vetoes no other type, the star hint is where='s alone, every
/// key-shape throw is a wrong path, and a diff names a path with no field apart from a read fault.</summary>
[Trait("tier", "unit")]
public sealed class WhereListKeyShapeFoldTests
{
    readonly SkyrimMod _mod = new(new ModKey("KeyShapeFold", ModType.Master), SkyrimRelease.SkyrimSE);

    IMagicEffectGetter Effect(CompareOperator op)
    {
        var e = _mod.MagicEffects.AddNew();
        e.Conditions.Add(new ConditionFloat { CompareOperator = op, ComparisonValue = 1, Data = new GetLevelConditionData() });
        return e;
    }

    static FieldPredicateSet Scan(string clause, IEnumerable<IMajorRecordGetter> records)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        foreach (var r in records) set!.Matches(r);
        return set!;
    }

    [Fact]
    public void ABadKeyOnOneTypeDoesNotRefuseTheScanOfAnother()
    {
        var armor = _mod.Armors.AddNew();
        var set = Scan("Conditions[any].CompareOperator = EqualTo", new IMajorRecordGetter[] { Effect(CompareOperator.EqualTo), armor });
        Assert.Null(set.FatalError);
        Assert.Contains("List 'Conditions' must be indexed by a non-negative integer; got 'any'.", set.AccountingNote() ?? "");
    }

    [Fact]
    public void TheStarHintIsTheWhereRefusalsAlone()
    {
        var e = Effect(CompareOperator.EqualTo);
        var note = ReadEngine.ReadFields(e, new[] { "Conditions[any].CompareOperator" }).Fields.Single().Note;
        Assert.True(ReadEngine.IsNoSuchFieldNote(note), note);
        Assert.DoesNotContain("A quantifier takes a star", note);
        Assert.Contains("A quantifier takes a star: 'Conditions[*any]'.", Scan("Conditions[any].CompareOperator = EqualTo", new[] { e }).FatalError);
    }

    [Fact]
    public void TheDisplayReadCallsABadKeyAWrongPath()
    {
        var note = WriteEngine.ReadLeafDisplay(Effect(CompareOperator.EqualTo), new[] { "Conditions[any]", "CompareOperator" }, null);
        Assert.True(ReadEngine.IsNoSuchFieldNote(note), note);
    }

    [Fact]
    public void AGenderedIndexOutOfRangeRefusesInsteadOfFaulting()
    {
        var armor = _mod.Armors.AddNew();
        armor.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel(), new ArmorModel());
        var set = Scan("WorldModel[2] exists", new[] { armor });
        Assert.Contains("indexed by [0] (male) or [1] (female); got '2'", set.FatalError);
        Assert.DoesNotContain("read FAULT", set.AccountingNote() ?? "");
    }

    [Fact]
    public void ADiffNamesAPathWithNoFieldApartFromAReadFault()
    {
        var e = Effect(CompareOperator.EqualTo);
        var read = ReadEngine.ReadFields(e, new[] { "Conditionz" });
        var d = FieldsDiff.Compare(read, read);
        Assert.Equal(1, d.NoFieldCount);
        Assert.Contains("names no field", d.Why);
        Assert.DoesNotContain("could not be read", d.Why);

        var row = new RecordReads.DeltaRow(FormIdToken.Of(e.FormKey),
            new RecordReads.DiffPole("B.esp", "active", true, "MagicEffect", null),
            new RecordReads.DiffPole("A.esm", "active", true, "MagicEffect", null), d, null, null, null);
        var text = RecordsTools.RenderRecordsDelta(new[] { row }, 1, 0, 0, 1, 0, "records  form=delta", null, 40_000, null, out _, noField: 1);
        Assert.Contains("1 with a path that names no field", text);
        Assert.Contains("1 path with no field", text);
        Assert.DoesNotContain("could not be read", text);
        Assert.DoesNotContain("Narrow with", text);
    }
}

/// <summary>A typed scan refuses a bad list key from the schema, before any data: no record need reach the step.</summary>
[Trait("tier", "integration")]
public sealed class WhereListKeyShapePlanTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-key-shape-");

    public WhereListKeyShapePlanTests()
    {
        var key = new ModKey("HcKeyShapeWorld", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Npcs.AddNew().EditorID = "HcNoVmad";
        var e = m.MagicEffects.AddNew();
        e.EditorID = "HcNoConditions";
        m.BeginWrite.ToPath(Path.Combine(_mo2.DataDir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile("HcKeyShapeWorld.esm\r\n", "*HcKeyShapeWorld.esm\r\n", "");
    }

    public void Dispose() => _mo2.Delete();

    string Where(string type, string clause)
    {
        using var svc = _mo2.Open();
        return RecordsTools.Records(svc, types: new[] { type }, where: new[] { clause }, counts_only: true);
    }

    [Fact]
    public void AWordKeyBehindAnAbsentSubstructStillRefuses()
    {
        var r = Where("NPC_", "VirtualMachineAdapter.Scripts[any].ScriptName = Foo");
        Assert.Contains("List 'Scripts' must be indexed by a non-negative integer; got 'any'.", r);
        Assert.Contains("'Scripts[*any]'", r);
        Assert.DoesNotContain("0 matches", r);
    }

    [Fact]
    public void AWordKeyOnAnEmptyListRefusesFromTheSchema()
    {
        var r = Where("MGEF", "Conditions[abc].CompareOperator = EqualTo");
        Assert.Contains("List 'Conditions' must be indexed by a non-negative integer; got 'abc'.", r);
        Assert.DoesNotContain("A quantifier takes a star", r);
    }
}
