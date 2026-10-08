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
    public void ADifferentOperatorDoesNotMatch() =>
        Assert.DoesNotContain(_equal, Run("Conditions[*any].CompareOperator = GreaterThan").Hits);

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
