using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The <c>*parent</c> containment step in a predicate, evaluated in memory over a hand-bound child→parent
/// map: the grammar's own refusals, the hop, the chain, and what it does on a record nothing contains. The map the
/// real index builds is covered against real plugins in <see cref="RecordsContainmentTests"/>.</summary>
[Trait("tier", "unit")]
public sealed class WhereContainmentTests
{
    readonly SkyrimMod _mod = new(new ModKey("ParentWorld", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly Dictionary<FormKey, FormKey> _parents = new();
    readonly Dictionary<FormKey, IMajorRecordGetter> _bodies = new();
    readonly IMajorRecordGetter _info, _otherInfo, _placed, _topLevelWeapon;

    public WhereContainmentTests()
    {
        var wrld = new Worldspace(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwTamriel" };
        var cell = new Cell(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwWhiterun" };
        var topic = new DialogTopic(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwGreetings" };
        var otherTopic = new DialogTopic(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwFarewells" };
        var info = new DialogResponses(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwLine0" };
        var otherInfo = new DialogResponses(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwLine1" };
        var placed = new PlacedObject(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PwRef0" };
        var weapon = _mod.Weapons.AddNew(); weapon.EditorID = "PwSword";

        foreach (var r in new IMajorRecordGetter[] { wrld, cell, topic, otherTopic, info, otherInfo, placed, weapon })
            _bodies[r.FormKey] = r;
        _parents[info.FormKey] = topic.FormKey;
        _parents[otherInfo.FormKey] = otherTopic.FormKey;
        _parents[placed.FormKey] = cell.FormKey;
        _parents[cell.FormKey] = wrld.FormKey;

        _info = info; _otherInfo = otherInfo; _placed = placed; _topLevelWeapon = weapon;
    }

    /// <summary>Match one body, returning the verdict and the scan's own rollup sentence for the predicate.</summary>
    (bool Matched, string? Note) Run(string clause, IMajorRecordGetter body)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        set!.BindResolution(fk => _bodies.ContainsKey(fk) ? "ParentWorld.esm" : null,
                            fk => _bodies.GetValueOrDefault(fk),
                            fk => _parents.TryGetValue(fk, out var p) ? p : null);
        var matched = set.Matches(body);
        return (matched, set.AccountingNote());
    }

    static string? Refusal(string clause) => FieldPredicateSet.Parse(new[] { clause }).Error;

    // ---- the hop --------------------------------------------------------------------------------

    [Fact]
    public void ParentReadsTheOwningTopicOfAnInfo() =>
        Assert.True(Run("*parent.EditorID = PwGreetings", _info).Matched);

    [Fact]
    public void ParentDoesNotMatchAnInfoUnderADifferentTopic() =>
        Assert.False(Run("*parent.EditorID = PwGreetings", _otherInfo).Matched);

    [Fact]
    public void TheStepChains_APlacedReferenceReachesItsWorldspaceThroughItsCell() =>
        Assert.True(Run("*parent.*parent.EditorID = PwTamriel", _placed).Matched);

    [Fact]
    public void OneHopShortOfTheWorldspaceReadsTheCell() =>
        Assert.True(Run("*parent.EditorID = PwWhiterun", _placed).Matched);

    /// <summary>The hop is a step, so the identity terms below it read the PARENT's identity, not the child's.</summary>
    [Fact]
    public void TheIdentityTermsBelowTheHopReadTheParent()
    {
        var topic = _parents[_info.FormKey];
        Assert.True(Run($"*parent.formid in [{topic.ID:X6}:{topic.ModKey.FileName}]", _info).Matched);
        Assert.False(Run($"*parent.formid in [{_info.FormKey.ID:X6}:{_info.FormKey.ModKey.FileName}]", _info).Matched);
    }

    // ---- a record nothing contains ---------------------------------------------------------------

    [Fact]
    public void ARecordNothingContainsIsNoVerdict_NotASilentNonMatch()
    {
        var (matched, note) = Run("*parent.EditorID = PwGreetings", _topLevelWeapon);
        Assert.False(matched);
        Assert.Contains("no record CONTAINS", note);
    }

    /// <summary>The reason is the child-bearing property set, derived from Mutagen — never a hand list, and never
    /// the "did you mistype the path" advice a plain no-such-field miss would get.</summary>
    [Fact]
    public void TheNoParentSentenceNamesTheChildBearingProperties()
    {
        var note = Run("*parent.EditorID = PwGreetings", _topLevelWeapon).Note;
        Assert.Contains("DialogTopic.Responses", note);
        Assert.Contains("Cell.Temporary", note);
        Assert.DoesNotContain("check the schema", note ?? "");
    }

    // ---- the refusals ----------------------------------------------------------------------------

    [Fact]
    public void ParentAfterAFieldStepRefuses_ItLeadsAPath() =>
        Assert.Contains("can only lead a path", Refusal("Responses.*parent.EditorID = X"));

    [Fact]
    public void ParentWithNothingAfterItRefuses_ItIsARecordNotAValue() =>
        Assert.Contains("not a value", Refusal("*parent = X"));

    [Fact]
    public void ParentWithAQuantifierRefuses_ThereIsOnlyEverOneContainingRecord() =>
        Assert.Contains("not a list", Refusal("*parent[*any].EditorID = X"));

    [Fact]
    public void AnUnknownStarTokenNamesTheTwoThatExist()
    {
        var r = Refusal("*owner.EditorID = X");
        Assert.Contains("*parent", r);
        Assert.Contains("[*any]", r);
    }

    // ---- composition with the other steps ---------------------------------------------------------

    [Fact]
    public void TheHopComposesWithALinkStepOnItsOwnLeftSide() =>
        // The left side hops to the cell, then reads a link on IT — nothing on the placed reference itself.
        Assert.Null(Refusal("*parent.Location->editorid = X"));

    [Fact]
    public void ParentOnTheLinkSideWithNoFieldAfterItRefuses_AContainingRecordIsNotALink() =>
        Assert.Contains("not a link-bearing field", Refusal("*parent->editorid = X"));

    /// <summary>A presence test BELOW the hop is a real filter, not an identity that always exists: an exterior
    /// CELL usually has no EditorID at all, so "which placed references sit in an unnamed cell" must parse and
    /// answer rather than being refused as unfilterable.</summary>
    [Fact]
    public void APresenceTestOnTheParentsEditorIdIsAFilter_NotARefusal()
    {
        Assert.Null(Refusal("*parent.EditorID missing"));
        Assert.True(Run("*parent.EditorID exists", _info).Matched);
        Assert.False(Run("*parent.EditorID missing", _info).Matched);
    }

    // ---- which type a quantified step is judged against -------------------------------------------

    /// <summary>A quantified step BELOW a hop is rooted at the CONTAINING record's type, so the scan's schema check
    /// must not judge it against the scanned type — the same rule the right side of a '-&gt;' already follows.
    /// Getting this wrong refuses a valid call, or silences a real refusal, wherever a child and its container both
    /// carry a field of that name with different cardinality.</summary>
    [Fact]
    public void AQuantifiedStepBelowAHopIsNotRootedAtTheScannedType()
    {
        static FieldPredicateSet Set(string clause)
        {
            var (set, err) = FieldPredicateSet.Parse(new[] { clause });
            Assert.Null(err);
            return set!;
        }
        Assert.True(Assert.Single(Set("Responses[*any].Text = hi").QuantifiedSteps).OnScannedType);
        Assert.False(Assert.Single(Set("*parent.Responses[*any].Text = hi").QuantifiedSteps).OnScannedType);
        Assert.False(Assert.Single(Set("*parent.Keywords[*any]->editorid = X").QuantifiedSteps).OnScannedType);
    }
}
