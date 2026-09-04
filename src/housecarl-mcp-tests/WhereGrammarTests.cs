using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The where-grammar terms, evaluated in memory over synthesized bodies against brute-force oracles.
/// No load order and no service call, which is what the "unit" tier names — but the bodies still come from the
/// shared collection fixture, so <c>--filter tier=unit</c> pays the world build.</summary>
[Collection("records")]
[Trait("tier", "unit")]
public sealed class WhereGrammarTests
{
    readonly RecordsWorld _w;
    public WhereGrammarTests(RecordsFixture f) => _w = f.W;

    HashSet<FormKey> Run(string clause, IEnumerable<IMajorRecordGetter> bodies,
                         Func<FormKey, string?>? winnerOf = null, Func<FormKey, IMajorRecordGetter?>? fetch = null)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        if (set!.NeedsResolution) set.BindResolution(winnerOf ?? (_ => null), fetch);
        return bodies.Where(b => set.Matches(b)).Select(b => b.FormKey).ToHashSet();
    }

    HashSet<FormKey> Weapons() => _w.WeaponBodies.Select(b => b.FormKey).ToHashSet();
    HashSet<FormKey> WeaponsExcept(params FormKey[] drop) => Weapons().Except(drop).ToHashSet();

    // ---- startswith and the editorid pseudo-path ------------------------------------------------

    [Fact]
    public void Startswith_OnEditoridSelectsExactlyTheThreeNamedWeapons_TheNoEditoridRecordDropsOut() =>
        Assert.Equal(_w.Weapons.ToHashSet(), Run("editorid startswith HcRecW", _w.WeaponBodies));

    [Fact]
    public void Startswith_OnARealLeafTokenRoutesThroughCompare_TenMatchesOneAndTwentyThirtyFiveDoNot() =>
        Assert.Equal(new[] { _w.Weapons[0] }.ToHashSet(), Run("BasicStats.Damage startswith 1", _w.WeaponBodies));

    [Fact]
    public void EditoridTerm_ContainsIsCaseInsensitiveAndSelectsTheOneMatch() =>
        Assert.Equal(new[] { _w.Weapons[1] }.ToHashSet(), Run("editorid contains recw1", _w.WeaponBodies));

    [Fact]
    public void EditoridTerm_MissingSelectsExactlyTheNoEditoridRecord() =>
        Assert.Equal(new[] { _w.NoEidWeapon }.ToHashSet(), Run("editorid missing", _w.WeaponBodies));

    [Fact]
    public void EditoridTerm_EqualsIsAnExactCaseInsensitiveMatch() =>
        Assert.Equal(new[] { _w.Weapons[2] }.ToHashSet(), Run("editorid = HcRecW2", _w.WeaponBodies));

    // ---- generalized membership ------------------------------------------------------------------

    [Fact]
    public void Membership_ANumericLeafInAListKeepsExactlyTheListedValues() =>
        Assert.Equal(new[] { _w.Weapons[0], _w.Weapons[2] }.ToHashSet(),
                     Run("BasicStats.Damage in [10, 30]", _w.WeaponBodies));

    [Fact]
    public void Membership_NotInIsTheComplementOverValueBearingRecords() =>
        Assert.Equal(new[] { _w.Weapons[1], _w.NoEidWeapon }.ToHashSet(),
                     Run("BasicStats.Damage not in [10, 30]", _w.WeaponBodies));

    [Fact]
    public void Membership_AFormLinkLeafAgainstAFormKeyListUsesIdentityCanonicalEquality() =>
        Assert.Equal(new[] { _w.SpellA, _w.SpellC }.ToHashSet(),
                     Run($"Effects[0].BaseEffect in [{RecordsWorld.Fid(_w.MgefA)}]", _w.SpellBodies));

    // ---- the winner provenance term ---------------------------------------------------------------

    string? WinnerOf(FormKey fk) => fk == _w.Weapons[0] ? _w.OverrideName : _w.MasterName;

    [Fact]
    public void WinnerTerm_SelectsExactlyTheOverriddenRecord() =>
        Assert.Equal(new[] { _w.Weapons[0] }.ToHashSet(),
                     Run($"winner = {_w.OverrideName}", _w.WeaponBodies, WinnerOf));

    [Fact]
    public void WinnerTerm_NotEqualIsTheComplement() =>
        Assert.Equal(WeaponsExcept(_w.Weapons[0]),
                     Run($"winner != {_w.OverrideName}", _w.WeaponBodies, WinnerOf));

    [Fact]
    public void WinnerTerm_NeedsResolutionIsTrueSoTheCallSiteKnowsToBind()
    {
        var (set, _) = FieldPredicateSet.Parse(new[] { $"winner = {_w.OverrideName}" });
        Assert.True(set!.NeedsResolution);
    }

    [Fact]
    public void WinnerTerm_EvaluatingUnboundIsATypedFatalError_NeverASilentNonMatch()
    {
        var (set, _) = FieldPredicateSet.Parse(new[] { $"winner = {_w.OverrideName}" });
        set!.Matches(_w.WeaponBodies[0]);   // deliberately UNBOUND
        Assert.Contains("winner", set.FatalError ?? "");
    }

    // ---- the -> link step -------------------------------------------------------------------------

    IMajorRecordGetter? Fetch(FormKey fk) => _w.MgefByKey.GetValueOrDefault(fk);

    [Fact]
    public void LinkStep_SelectsTheSpellWhoseEffectTargetMatches() =>
        Assert.Equal(new[] { _w.SpellA, _w.SpellC }.ToHashSet(),
                     Run("Effects->editorid startswith HcRec", _w.SpellBodies, null, Fetch));

    [Fact]
    public void LinkStep_ArrowFormidInListTestsTheTargetsIdentity() =>
        Assert.Equal(new[] { _w.SpellB }.ToHashSet(),
                     Run($"Effects->formid in [{RecordsWorld.Fid(_w.MgefB)}]", _w.SpellBodies, null, Fetch));

    [Fact]
    public void LinkStep_AWrongLeftPathParses_ItIsAPerRecordClassificationNotAParseError()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "NoSuchField->editorid contains x" });
        Assert.Null(err);
    }

    [Fact]
    public void LinkStep_AWrongLeftPathFailsLoudInTheAccountingNamedWithTheArrow_NeverASilentZeroMatches()
    {
        var (set, _) = FieldPredicateSet.Parse(new[] { "NoSuchField->editorid contains x" });
        set!.BindResolution(_ => null, Fetch);
        foreach (var b in _w.SpellBodies) set.Matches(b);
        Assert.Contains("NoSuchField->editorid", set.AccountingNote() ?? "");
    }

    // ---- polarity over the no-EditorID record -----------------------------------------------------

    [Fact]
    public void EditoridNotEqual_KeepsTheNoEditoridRecord_NotEqualIsUnambiguouslyTrueThere() =>
        Assert.Equal(WeaponsExcept(_w.Weapons[0]), Run("editorid != HcRecW0", _w.WeaponBodies));

    [Fact]
    public void EditoridMembership_InListSelectsExactlyTheListedEditorids() =>
        Assert.Equal(new[] { _w.Weapons[0], _w.Weapons[2] }.ToHashSet(),
                     Run("editorid in [HcRecW0, HcRecW2]", _w.WeaponBodies));

    [Fact]
    public void EditoridMembership_NotInKeepsTheRestIncludingTheNoEditoridRecord() =>
        Assert.Equal(WeaponsExcept(_w.Weapons[0]), Run("editorid not in [HcRecW0]", _w.WeaponBodies));
}

/// <summary>The parse refusals, named before any scan. One row per refusal: the clause and the word the
/// caller needs to see in the sentence.</summary>
[Trait("tier", "unit")]
public sealed class WhereParseRefusalTests
{
    [Theory]
    // the clause                          the teaching the refusal must name
    [InlineData("EditorID blorp x", "startswith")]           // the operator list names startswith
    [InlineData("Perks-> startswith x", "link step")]        // a malformed arrow is a named link-step refusal
    [InlineData("A->B->C = 1", "ONE")]                       // one link step only — the walk construct owns chains
    [InlineData("winner > 5", "winner")]                     // a non-equality op on the provenance term
    [InlineData("winner exists", "provenance")]              // a presence test on winner
    [InlineData("formid exists", "always exists")]           // identity can never filter
    [InlineData("editorid > 5", "text term")]                // a numeric op on a text term
    [InlineData("formid = 000801:X.esp", "membership")]      // a value op on identity points at the membership ops
    [InlineData("Effects->winner = X.esp", "link step")]     // winner behind an arrow names the CANDIDATE's resolution
    public void AParseRefusalNamesTheRuleItBroke(string clause, string teaching) =>
        Assert.Contains(teaching, FieldPredicateSet.Parse(new[] { clause }).Error ?? "");
}
