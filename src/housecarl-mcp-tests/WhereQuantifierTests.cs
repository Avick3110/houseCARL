using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The quantified path step (<c>[*any]</c> / <c>[*all]</c> / <c>[*none]</c> / <c>[*count]</c>) and the
/// two exclusion folds of <c>has</c>, evaluated in memory over bodies this class builds — lists of more than one
/// element, which is what a fold needs to be worth anything. No load order and no service call.</summary>
[Trait("tier", "unit")]
public sealed class WhereQuantifierTests
{
    readonly SkyrimMod _mod = new(new ModKey("QuantWorld", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly FormKey _mgefPlain, _mgefReq, _kwA, _kwB;
    readonly List<IMajorRecordGetter> _spells = new();
    readonly List<IMajorRecordGetter> _armors = new();
    readonly Dictionary<FormKey, IMajorRecordGetter> _targets = new();
    readonly FormKey _spellLow, _spellMixed, _spellHigh, _spellEmpty;
    readonly FormKey _armorHeadBody, _armorHands, _armorFeet;

    public WhereQuantifierTests()
    {
        var plain = _mod.MagicEffects.AddNew(); plain.EditorID = "QMgefPlain"; _mgefPlain = plain.FormKey;
        var req = _mod.MagicEffects.AddNew(); req.EditorID = "REQ_QMgef"; _mgefReq = req.FormKey;
        _targets[_mgefPlain] = plain; _targets[_mgefReq] = req;

        _spellLow = AddSpell("QSpellLow", (5, _mgefPlain), (9, _mgefPlain));
        _spellMixed = AddSpell("QSpellMixed", (5, _mgefPlain), (90, _mgefReq));
        _spellHigh = AddSpell("QSpellHigh", (70, _mgefReq), (80, _mgefReq));
        _spellEmpty = AddSpell("QSpellEmpty");

        var kwa = _mod.Keywords.AddNew(); kwa.EditorID = "QKwA"; _kwA = kwa.FormKey;
        var kwb = _mod.Keywords.AddNew(); kwb.EditorID = "QKwB"; _kwB = kwb.FormKey;
        _armorHeadBody = AddArmor("QArmorHeadBody", BipedObjectFlag.Head | BipedObjectFlag.Body, _kwA);
        _armorHands = AddArmor("QArmorHands", BipedObjectFlag.Hands, _kwA, _kwB);
        _armorFeet = AddArmor("QArmorFeet", BipedObjectFlag.Feet);
    }

    FormKey AddSpell(string eid, params (ushort Magnitude, FormKey Base)[] effects)
    {
        var s = _mod.Spells.AddNew();
        s.EditorID = eid;
        foreach (var (mag, bse) in effects)
        {
            var e = new Effect();
            e.BaseEffect.SetTo(bse);
            e.Data = new EffectData { Magnitude = mag };
            s.Effects.Add(e);
        }
        _spells.Add(s);
        return s.FormKey;
    }

    FormKey AddArmor(string eid, BipedObjectFlag slots, params FormKey[] keywords)
    {
        var a = _mod.Armors.AddNew();
        a.EditorID = eid;
        a.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots };
        // Left NULL where there are none, so the keywordless armor exercises the absent-reads-as-empty path.
        if (keywords.Length > 0)
        {
            var kws = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
            foreach (var k in keywords) kws.Add(new FormLink<IKeywordGetter>(k));
            a.Keywords = kws;
        }
        _armors.Add(a);
        return a.FormKey;
    }

    HashSet<FormKey> Run(string clause, IEnumerable<IMajorRecordGetter> bodies) => RunWithNote(clause, bodies).Hits;

    (HashSet<FormKey> Hits, string Note) RunWithNote(string clause, IEnumerable<IMajorRecordGetter> bodies)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        if (set!.NeedsResolution) set.BindResolution(_ => null, fk => _targets.GetValueOrDefault(fk));
        var hits = bodies.Where(b => set.Matches(b)).Select(b => b.FormKey).ToHashSet();
        return (hits, set.AccountingNote() ?? "");
    }

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    // ---- the three boolean folds ------------------------------------------------------------------

    [Fact]
    public void Any_MatchesTheSpellsWithAtLeastOneBigEffect() =>
        Assert.Equal(new[] { _spellMixed, _spellHigh }.ToHashSet(),
                     Run("Effects[*any].Data.Magnitude > 50", _spells));

    [Fact]
    public void All_MatchesOnlyWhereEveryElementSatisfies_AndIsVacuouslyTrueOnTheEmptyList() =>
        Assert.Equal(new[] { _spellHigh, _spellEmpty }.ToHashSet(),
                     Run("Effects[*all].Data.Magnitude > 50", _spells));

    [Fact]
    public void None_IsTheProvedAbsence_TheEmptyListIncluded() =>
        Assert.Equal(new[] { _spellLow, _spellEmpty }.ToHashSet(),
                     Run("Effects[*none].Data.Magnitude > 50", _spells));

    [Fact]
    public void AnyOnAnEmptyListIsADefiniteNonMatch_NotANoVerdict() =>
        Assert.DoesNotContain(_spellEmpty, Run("Effects[*any].Data.Magnitude > 0", _spells));

    // ---- [*count] ---------------------------------------------------------------------------------

    [Fact]
    public void Count_ComparesTheNumberOfElements() =>
        Assert.Equal(new[] { _spellLow, _spellMixed, _spellHigh }.ToHashSet(),
                     Run("Effects[*count] > 1", _spells));

    [Fact]
    public void Count_ReadsAnAbsentOrEmptyListAsZero() =>
        Assert.Equal(new[] { _spellEmpty }.ToHashSet(), Run("Effects[*count] = 0", _spells));

    [Fact]
    public void Count_TakesTheMembershipOpsToo() =>
        Assert.Equal(new[] { _spellEmpty }.ToHashSet(), Run("Effects[*count] in [0]", _spells));

    // ---- the fold over a link step ----------------------------------------------------------------

    [Fact]
    public void NoneOverALinkStep_ProvesNoEffectPointsAtAReqBaseEffect() =>
        Assert.Equal(new[] { _spellLow, _spellEmpty }.ToHashSet(),
                     Run("Effects[*none].BaseEffect->editorid startswith REQ_", _spells));

    [Fact]
    public void AnyOverALinkStep_FindsTheSpellsThatDoPointAtOne() =>
        Assert.Equal(new[] { _spellMixed, _spellHigh }.ToHashSet(),
                     Run("Effects[*any].BaseEffect->editorid startswith REQ_", _spells));

    // ---- the fold on the LAST step: the element itself is the leaf --------------------------------

    [Fact]
    public void AFoldOnTheLastStepComparesTheElementItself_ListMembershipByValue() =>
        Assert.Equal(new[] { _armorHands }.ToHashSet(), Run($"Keywords[*any] = {Fid(_kwB)}", _armors));

    [Fact]
    public void NoneOnTheLastStepKeepsTheArmorsWithoutThatKeyword_TheKeywordlessOneIncluded() =>
        Assert.Equal(new[] { _armorHeadBody, _armorFeet }.ToHashSet(), Run($"Keywords[*none] = {Fid(_kwB)}", _armors));

    // ---- has / has_any / has_none ------------------------------------------------------------------

    [Fact]
    public void Has_StillRequiresEveryOperandBit() =>
        Assert.Empty(Run("BodyTemplate.FirstPersonFlags has Head,Hands", _armors));

    [Fact]
    public void HasAny_MatchesWhenAtLeastOneOperandBitIsSet() =>
        Assert.Equal(new[] { _armorHeadBody, _armorHands }.ToHashSet(),
                     Run("BodyTemplate.FirstPersonFlags has_any Head,Hands", _armors));

    [Fact]
    public void HasNone_MatchesWhenNoOperandBitIsSet() =>
        Assert.Equal(new[] { _armorFeet }.ToHashSet(),
                     Run("BodyTemplate.FirstPersonFlags has_none Head,Hands", _armors));

    [Fact]
    public void HasNone_ZeroMaskIsRefusedByItsOwnName_NeverAVacuousMatch()
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { "BodyTemplate.FirstPersonFlags has_none 0" });
        Assert.Null(err);
        set!.Matches(_armors[0]);
        Assert.Contains("has_none 0", set.FatalError ?? "");
    }

    [Fact]
    public void HasAny_OnANonFlagsTextTermIsAParseRefusal()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "editorid has_any 3" });
        Assert.Contains("text term", err ?? "");
    }

    // ---- the parse refusals ------------------------------------------------------------------------

    [Fact]
    public void BareStarIsRefusedInWhere_NamingTheThreeFoldTokens()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "Effects[*].Data.Magnitude > 50" });
        Assert.Contains("[*any]", err ?? "");
    }

    [Fact]
    public void AnUnknownQuantifierWordIsRefusedNamingTheTokens()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "Effects[*sum].Data.Magnitude > 50" });
        Assert.Contains("not a quantifier", err ?? "");
    }

    [Fact]
    public void NothingMayFollowACountStep()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "Effects[*count].Data.Magnitude > 5" });
        Assert.Contains("[*count]", err ?? "");
    }

    [Fact]
    public void CountRefusesAnOperatorThatIsNotANumberComparison()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "Effects[*count] contains 2" });
        Assert.Contains("[*count]", err ?? "");
    }

    [Fact]
    public void CountCannotCarryALinkStep()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "Effects[*count]->editorid = x" });
        Assert.Contains("link step", err ?? "");
    }

    [Fact]
    public void AQuantifierWithNoFieldBeforeItIsRefused()
    {
        var (_, err) = FieldPredicateSet.Parse(new[] { "[*any].Data.Magnitude > 5" });
        Assert.Contains("no field name", err ?? "");
    }

    // ---- a quantifier on something that is not a list ----------------------------------------------

    [Fact]
    public void AQuantifierOnANonListStepFailsLoudInTheAccounting_NeverASilentZeroMatches()
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { "BodyTemplate[*any].FirstPersonFlags has Head" });
        Assert.Null(err);
        foreach (var a in _armors) Assert.False(set!.Matches(a));
        var note = set!.AccountingNote() ?? "";
        Assert.Contains("not a list", note);
        Assert.Contains("BodyTemplate[*any]", note);
    }

    // ---- a quantifier on a NULL non-list step ------------------------------------------------------

    [Fact]
    public void ANullNonListStepIsStillNamedNotAList_NeverAVacuouslyTrueEmptyList()
    {
        var bare = _mod.Armors.AddNew(); bare.EditorID = "QArmorNoTemplate";   // BodyTemplate left null
        var (hits, note) = RunWithNote("BodyTemplate[*all].FirstPersonFlags has Head", new[] { (IMajorRecordGetter)bare });
        Assert.Empty(hits);
        Assert.Contains("not a list", note);
    }

    [Fact]
    public void ANullScalarWithCountIsNotAnEmptyList()
    {
        var bare = _mod.Armors.AddNew(); bare.EditorID = "QArmorNoName";       // Name left null
        var (hits, note) = RunWithNote("Name[*count] = 0", new[] { (IMajorRecordGetter)bare });
        Assert.Empty(hits);
        Assert.Contains("not a list", note);
    }

    [Fact]
    public void ADictStepIsNamedADict_NotFoldedOverItsEntries()
    {
        var cls = _mod.Classes.AddNew(); cls.EditorID = "QClass";
        cls.SkillWeights[Skill.OneHanded] = 5;
        var (hits, note) = RunWithNote("SkillWeights[*any] = 5", new[] { (IMajorRecordGetter)cls });
        Assert.Empty(hits);
        Assert.Contains("dict", note);
    }

    [Fact]
    public void AByteBlockStepIsNamedBytes_NotCountedElementByElement()
    {
        var st = _mod.Statics.AddNew(); st.EditorID = "QStatic";
        st.Model = new Model { File = "meshes\\q.nif", Data = new byte[] { 1, 2, 3 } };
        var (hits, note) = RunWithNote("Model.Data[*count] > 0", new[] { (IMajorRecordGetter)st });
        Assert.Empty(hits);
        Assert.Contains("bytes", note);
    }

    // ---- an absent collection reads as empty, however far up the absence starts --------------------

    [Fact]
    public void AnAbsentParentSubstructReadsAsAnEmptyCollection()
    {
        // No fixture armor carries a VirtualMachineAdapter, so "carries no script" must find all of them.
        Assert.Equal(_armors.Select(a => a.FormKey).ToHashSet(),
                     Run("VirtualMachineAdapter.Scripts[*count] = 0", _armors));
    }

    // ---- an element that could not be judged sinks the universal folds -----------------------------

    [Fact]
    public void OneUnjudgedElementStopsAllAndNoneClaimingAVerdict()
    {
        var s = _mod.Spells.AddNew(); s.EditorID = "QSpellHalfRead";
        var blind = new Effect(); blind.BaseEffect.SetTo(_mgefPlain);          // no Data — Magnitude cannot be judged
        var seen = new Effect(); seen.BaseEffect.SetTo(_mgefPlain); seen.Data = new EffectData { Magnitude = 5 };
        s.Effects.Add(blind); s.Effects.Add(seen);
        var one = new[] { (IMajorRecordGetter)s };
        Assert.Empty(Run("Effects[*none].Data.Magnitude > 50", one));          // was a definite "proved absence"
        Assert.Empty(Run("Effects[*all].Data.Magnitude < 50", one));
        // …while a fold the judged elements already decide stays definite.
        Assert.Single(Run("Effects[*any].Data.Magnitude < 50", one));
    }

    [Fact]
    public void AnAllUnsetListIsNoVerdict_NotAReadFault()
    {
        var s = _mod.Spells.AddNew(); s.EditorID = "QSpellNoBases";
        s.Effects.Add(new Effect { Data = new EffectData { Magnitude = 1 } }); // BaseEffect null → nothing to judge
        s.Effects.Add(new Effect { Data = new EffectData { Magnitude = 2 } });
        var (hits, note) = RunWithNote("Effects[*none].BaseEffect->editorid startswith REQ_", new[] { (IMajorRecordGetter)s });
        Assert.Empty(hits);
        Assert.DoesNotContain("read FAULT", note);
    }

    // ---- composition: a quantified step inside another ---------------------------------------------

    [Fact]
    public void TwoQuantifiedStepsComposeWithoutASecondRule()
    {
        // Every effect of the spell carries at least one condition — no fixture has conditions, so the inner
        // fold sees an empty list and [*all] is vacuously true on each element.
        Assert.Equal(new[] { _spellLow, _spellMixed, _spellHigh, _spellEmpty }.ToHashSet(),
                     Run("Effects[*all].Conditions[*all].Data.RunOnType = Subject", _spells));
    }
}
