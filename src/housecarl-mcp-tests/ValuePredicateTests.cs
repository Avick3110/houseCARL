using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The where= value predicates over in-memory records built with known field values: each matched set is
/// compared to a brute-force set computed from those literals, never from the read walk. Moved from the
/// value-predicate-guard probe; a test named for an operator pins that operator's row of the S1 select axis.</summary>
[Trait("tier", "unit")]
public sealed class ValuePredicateTests
{
    sealed record Mgef(IMagicEffectGetter Rec, string Eid, ActorValue MagicSkill, float BaseCost, ActorValue ArchActorValue, FormKey Projectile);
    sealed record Weap(IWeaponGetter Rec, ushort Damage);
    sealed record Armo(IArmorGetter Rec, ulong Bits);

    const uint Slot52 = 0x400000;   // bit 22, an unnamed modder slot, renders "4194304"
    const uint Slot53 = 0x800000;   // bit 23, an unnamed modder slot, renders "8388608"

    readonly SkyrimMod _mod = new(new ModKey("hcvalguard", ModType.Plugin), SkyrimRelease.SkyrimSE);
    readonly FormKey _projX = FormKey.Factory("0ABCDE:hcvalguard.esp");
    readonly FormKey _projY = FormKey.Factory("0FEDCB:hcvalguard.esp");
    readonly List<Mgef> _mgefs;
    readonly List<Weap> _weaps;
    readonly List<Armo> _armos;

    public ValuePredicateTests()
    {
        _mgefs = new()
        {
            MakeMgef("hcFireDamage",    ActorValue.Destruction, 0.5f, ActorValue.Infamy,      _projX),
            MakeMgef("hcConjureFlame",  ActorValue.Conjuration, 1.0f, ActorValue.Conjuration, _projY),
            MakeMgef("hcFrostDamage",   ActorValue.Destruction, 2.0f, ActorValue.Infamy,      _projX),
            MakeMgef("hcRestoreHealth", ActorValue.Restoration, 0.5f, ActorValue.Destruction, _projY),
        };
        _weaps = new() { MakeWeap(10), MakeWeap(50), MakeWeap(100) };
        // Named vanilla slots render as names, unnamed modder slots and a body+modder combo render as one number.
        _armos = new()
        {
            MakeArmo(BipedObjectFlag.Body),
            MakeArmo(BipedObjectFlag.Forearms),
            MakeArmo((BipedObjectFlag)Slot53),
            MakeArmo((BipedObjectFlag)Slot52),
            MakeArmo(BipedObjectFlag.Body | (BipedObjectFlag)Slot53),
        };
    }

    IEnumerable<IMajorRecordGetter> MgefBodies => _mgefs.Select(m => (IMajorRecordGetter)m.Rec);
    IEnumerable<IMajorRecordGetter> WeapBodies => _weaps.Select(w => (IMajorRecordGetter)w.Rec);
    IEnumerable<IMajorRecordGetter> ArmoBodies => _armos.Select(a => (IMajorRecordGetter)a.Rec);

    HashSet<FormKey> Mgefs(Func<Mgef, bool> pred) => _mgefs.Where(pred).Select(m => m.Rec.FormKey).ToHashSet();
    HashSet<FormKey> Weaps(Func<Weap, bool> pred) => _weaps.Where(pred).Select(w => w.Rec.FormKey).ToHashSet();
    HashSet<FormKey> Armos(Func<Armo, bool> pred) => _armos.Where(pred).Select(a => a.Rec.FormKey).ToHashSet();

    static HashSet<FormKey> Run(IEnumerable<IMajorRecordGetter> cohort, params string[] where) => RunWithSet(cohort, where).Matched;

    static (HashSet<FormKey> Matched, FieldPredicateSet Set) RunWithSet(IEnumerable<IMajorRecordGetter> cohort, params string[] where)
    {
        var (set, err) = FieldPredicateSet.Parse(where);
        Assert.Null(err);
        return (cohort.Where(b => set!.Matches(b)).Select(b => b.FormKey).ToHashSet(), set!);
    }

    static string? ParseError(params string[] where) => FieldPredicateSet.Parse(where).Error;

    // ---- scalar ops: = != > >= < <= contains ------------------------------------------------------

    // probe: "MagicSkill = Destruction"
    [Fact]
    public void Eq_OnATopLevelEnumMatchesByName() =>
        Assert.Equal(Mgefs(m => m.MagicSkill == ActorValue.Destruction), Run(MgefBodies, "MagicSkill = Destruction"));

    // probe: "MagicSkill = destruction  (case-insensitive)"
    [Fact]
    public void Eq_OnAnEnumNameIsCaseInsensitive() =>
        Assert.Equal(Mgefs(m => m.MagicSkill == ActorValue.Destruction), Run(MgefBodies, "MagicSkill = destruction"));

    // probe: "MagicSkill != Destruction"
    [Fact]
    public void Ne_IsTheComplementOfEq() =>
        Assert.Equal(Mgefs(m => m.MagicSkill != ActorValue.Destruction), Run(MgefBodies, "MagicSkill != Destruction"));

    // probe: "Archetype.ActorValue = Infamy"
    [Fact]
    public void Eq_OnANestedEnumPathWalksTheSubstruct() =>
        Assert.Equal(Mgefs(m => m.ArchActorValue == ActorValue.Infamy), Run(MgefBodies, "Archetype.ActorValue = Infamy"));

    // probe: "BasicStats.Damage >= 50"
    [Fact]
    public void Ge_OnANestedNumericLeaf() =>
        Assert.Equal(Weaps(w => w.Damage >= 50), Run(WeapBodies, "BasicStats.Damage >= 50"));

    // probe: "BasicStats.Damage < 50"
    [Fact]
    public void Lt_OnANestedNumericLeaf() =>
        Assert.Equal(Weaps(w => w.Damage < 50), Run(WeapBodies, "BasicStats.Damage < 50"));

    // probe: "BasicStats.Damage = 50"
    [Fact]
    public void Eq_OnANestedNumericLeaf() =>
        Assert.Equal(Weaps(w => w.Damage == 50), Run(WeapBodies, "BasicStats.Damage = 50"));

    // probe: "BasicStats.Damage > 50"
    [Fact]
    public void Gt_OnANestedNumericLeaf() =>
        Assert.Equal(Weaps(w => w.Damage > 50), Run(WeapBodies, "BasicStats.Damage > 50"));

    // probe: "BasicStats.Damage <= 50"
    [Fact]
    public void Le_OnANestedNumericLeaf() =>
        Assert.Equal(Weaps(w => w.Damage <= 50), Run(WeapBodies, "BasicStats.Damage <= 50"));

    // probe: "BaseCost = 0.50  (matches stored 0.5)"
    [Fact]
    public void Eq_OnAFloatComparesNumerically_0Point50MatchesAStored0Point5() =>
        Assert.Equal(Mgefs(m => Math.Abs(m.BaseCost - 0.5f) < 1e-6), Run(MgefBodies, "BaseCost = 0.50"));

    // probe: "Projectile = <FormKey>"
    [Fact]
    public void Eq_OnAFormLinkComparesAsAFormKey() =>
        Assert.Equal(Mgefs(m => m.Projectile == _projX), Run(MgefBodies, $"Projectile = {_projX}"));

    // probe: "[MagicSkill = Destruction] AND [BaseCost >= 1.0]"
    [Fact]
    public void AndList_EveryPredicateMustHold() =>
        Assert.Equal(Mgefs(m => m.MagicSkill == ActorValue.Destruction && m.BaseCost >= 1.0f),
                     Run(MgefBodies, "MagicSkill = Destruction", "BaseCost >= 1.0"));

    // probe: "EditorID contains Frost"
    [Fact]
    public void Contains_IsASubstringTestOnTheEditorId() =>
        Assert.Equal(Mgefs(m => m.Eid.Contains("Frost", StringComparison.OrdinalIgnoreCase)), Run(MgefBodies, "EditorID contains Frost"));

    // probe: "EditorID contains frost  (case-insensitive)"
    [Fact]
    public void Contains_IsCaseInsensitive() =>
        Assert.Equal(Mgefs(m => m.Eid.Contains("Frost", StringComparison.OrdinalIgnoreCase)), Run(MgefBodies, "EditorID contains frost"));

    // probe: "EditorID contains hc  (all)"
    [Fact]
    public void Contains_ACommonPrefixMatchesEveryRecord() =>
        Assert.Equal(Mgefs(_ => true), Run(MgefBodies, "EditorID contains hc"));

    // ---- flags: has, and the flags-aware = and >= ------------------------------------------------

    // probe: "FirstPersonFlags has 8388608  (slot 53, incl. combos)"
    [Fact]
    public void Has_ADecimalBitMatchesEveryRecordCarryingIt_CombosIncluded() =>
        Assert.Equal(Armos(a => (a.Bits & Slot53) == Slot53), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags has 8388608"));

    // probe: "FirstPersonFlags has 0x800000  (hex form of slot 53)"
    [Fact]
    public void Has_AHexBitIsTheSameTest() =>
        Assert.Equal(Armos(a => (a.Bits & Slot53) == Slot53), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags has 0x800000"));

    // probe: "FirstPersonFlags has Body  (named slot, incl. combos)"
    [Fact]
    public void Has_AFlagNameMatchesCombosToo() =>
        Assert.Equal(Armos(a => (a.Bits & 4UL) == 4UL), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags has Body"));

    // probe: "FirstPersonFlags = 8388608  (exact — slot-53-ONLY, excludes the combo)"
    [Fact]
    public void Eq_OnFlagsIsExact_TheComboIsExcluded() =>
        Assert.Equal(Armos(a => a.Bits == Slot53), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags = 8388608"));

    // probe: "FirstPersonFlags = 16  (matches the name-rendered Forearms)"
    [Fact]
    public void Eq_OnFlagsTakesANumberForANameRenderedValue() =>
        Assert.Equal(Armos(a => a.Bits == 16UL), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags = 16"));

    // probe: "FirstPersonFlags = Forearms  (name still works)"
    [Fact]
    public void Eq_OnFlagsStillTakesTheName() =>
        Assert.Equal(Armos(a => a.Bits == 16UL), Run(ArmoBodies, "BodyTemplate.FirstPersonFlags = Forearms"));

    // probe: "FirstPersonFlags >= 65536  (modder-range slots; no longer a type error)" and "no FatalError"
    [Fact]
    public void Ge_OnFlagsComparesTheBitsNumerically_NotATypeError()
    {
        var (matched, set) = RunWithSet(ArmoBodies, "BodyTemplate.FirstPersonFlags >= 65536");
        Assert.Null(set.FatalError);
        Assert.Equal(Armos(a => a.Bits >= 65536UL), matched);
    }

    // probe: "BasicStats.Damage has 16  (integer bit-test)"
    [Fact]
    public void Has_OnAPlainIntegerLeafBitTestsItsValue() =>
        Assert.Equal(Weaps(w => (w.Damage & 16) == 16), Run(WeapBodies, "BasicStats.Damage has 16"));

    // probe: "has on non-flags enum: FatalError set" and "0 matches"
    [Fact]
    public void Has_OnANonFlagsEnumIsATypedFatalError_NotASilentNonMatch()
    {
        var (matched, set) = RunWithSet(MgefBodies, "MagicSkill has 4");
        Assert.Contains("flags/bitmask", set.FatalError);
        Assert.Empty(matched);
    }

    // ---- presence: exists / missing --------------------------------------------------------------

    // probe: "Archetype exists  (substruct present on all)"
    [Fact]
    public void Exists_OnASubstructPresentOnEveryRecordMatchesAll() =>
        Assert.Equal(Mgefs(_ => true), Run(MgefBodies, "Archetype exists"));

    // probe: "Archetype missing  (complement — none)"
    [Fact]
    public void Missing_OnASubstructPresentOnEveryRecordMatchesNone() =>
        Assert.Empty(Run(MgefBodies, "Archetype missing"));

    // probe: "MagicSkill exists  (scalar present on all)"
    [Fact]
    public void Exists_OnAScalarPresentOnEveryRecordMatchesAll() =>
        Assert.Equal(Mgefs(_ => true), Run(MgefBodies, "MagicSkill exists"));

    (IArmorGetter Full, IArmorGetter Empty, IArmorGetter Null) KeywordCohort()
    {
        var mod = new SkyrimMod(new ModKey("hcpresence", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var full = mod.Armors.AddNew();  full.Keywords = new() { new FormLink<IKeywordGetter>(_projX) };
        var empty = mod.Armors.AddNew(); empty.Keywords = new();
        var none = mod.Armors.AddNew();
        return (full, empty, none);
    }

    // probe: "Keywords exists  (non-empty ONLY — empty & null excluded)" and "no false-alarm note"
    [Fact]
    public void Exists_OnAListMatchesOnlyTheNonEmptyList_AndATrueResultGivesNoNote()
    {
        var (full, empty, none) = KeywordCohort();
        var (matched, set) = RunWithSet(new IMajorRecordGetter[] { full, empty, none }, "Keywords exists");
        Assert.Equal(new HashSet<FormKey> { full.FormKey }, matched);
        Assert.Null(set.AccountingNote());
    }

    // probe: "Keywords missing  (empty & null — complement)"
    [Fact]
    public void Missing_OnAListMatchesTheEmptyAndTheNullList()
    {
        var (full, empty, none) = KeywordCohort();
        Assert.Equal(new HashSet<FormKey> { empty.FormKey, none.FormKey },
                     Run(new IMajorRecordGetter[] { full, empty, none }, "Keywords missing"));
    }

    // probe: "mistyped exists: 0 matches" and "LOUD 'not a field' note"
    [Fact]
    public void Exists_OnAMistypedPathFailsLoudAsNotAField_NotASilentZero()
    {
        var (matched, set) = RunWithSet(MgefBodies, "Archetyp exists");
        Assert.Empty(matched);
        Assert.Contains("NOT A FIELD", set.AccountingNote());
    }

    // probe: "parse: `exists <value>` refused" and "parse: `missing <value>` refused"
    [Theory]
    [InlineData("Archetype exists foo")]
    [InlineData("Archetype missing foo")]
    public void ExistsAndMissing_WithAValueAreRefused_PresenceTakesNoValue(string clause) =>
        Assert.Contains("takes no value", ParseError(clause));

    // ---- identity membership: in / not in --------------------------------------------------------

    HashSet<FormKey> Claimed => new() { _mgefs[0].Rec.FormKey, _mgefs[2].Rec.FormKey };

    // probe: "formid in [inline]  (keeps exactly the listed)"
    [Fact]
    public void In_AnInlineFormIdListKeepsExactlyTheListed() =>
        Assert.Equal(Claimed, Run(MgefBodies, $"formid in [{_mgefs[0].Rec.FormKey}, {_mgefs[2].Rec.FormKey}]"));

    // probe: "formid not in [inline]  (the complement — the #226 subtraction)"
    [Fact]
    public void NotIn_AnInlineFormIdListIsTheComplement() =>
        Assert.Equal(Mgefs(m => !Claimed.Contains(m.Rec.FormKey)),
                     Run(MgefBodies, $"formid not in [{_mgefs[0].Rec.FormKey}, {_mgefs[2].Rec.FormKey}]"));

    // probe: "formid in [\"…\", \"…\"]  (JSON-array paste form)"
    [Fact]
    public void In_AJsonArrayPasteParsesAsIs() =>
        Assert.Equal(Claimed, Run(MgefBodies, $"formid in [\"{_mgefs[0].Rec.FormKey}\", \"{_mgefs[2].Rec.FormKey}\"]"));

    // probe: "formid in [ \"…\", \"…\" ]  (SPACED JSON-array style)"
    [Fact]
    public void In_ASpacedJsonArrayPasteParsesAsIs() =>
        Assert.Equal(Claimed, Run(MgefBodies, $"formid in [ \"{_mgefs[0].Rec.FormKey}\", \"{_mgefs[2].Rec.FormKey}\" ]"));

    // probe: "[formid not in …] AND [MagicSkill = Destruction]"
    [Fact]
    public void NotIn_AndsWithAValuePredicate() =>
        Assert.Equal(Mgefs(m => m.Rec.FormKey != _mgefs[0].Rec.FormKey && m.MagicSkill == ActorValue.Destruction),
                     Run(MgefBodies, $"formid not in [{_mgefs[0].Rec.FormKey}]", "MagicSkill = Destruction"));

    // probe: "membership scan: no spurious accounting note"
    [Fact]
    public void NotIn_AMembershipScanGivesNoAccountingNote() =>
        Assert.Null(RunWithSet(MgefBodies, $"formid not in [{_mgefs[0].Rec.FormKey}]").Set.AccountingNote());

    // probe: "formid in — plugin filename WITH SPACES stays one token"
    [Fact]
    public void In_APluginFilenameWithSpacesStaysOneToken()
    {
        var spaced = new SkyrimMod(new ModKey("hc spaced guard", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var s1 = spaced.MagicEffects.AddNew();
        var s2 = spaced.MagicEffects.AddNew();
        Assert.Equal(new HashSet<FormKey> { s1.FormKey }, Run(new IMajorRecordGetter[] { s1, s2 }, $"formid in [{s1.FormKey}]"));
    }

    // probe: "formid not in @file  (newline-separated list file)" and "formid not in @'<quoted path>'"
    [Theory]
    [InlineData("@{0}")]
    [InlineData("@'{0}'")]
    public void NotIn_AnAtFileListMatchesLikeTheInlineForm_BareOrSingleQuoted(string operandShape)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-value-predicate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var listFile = Path.Combine(dir, "formids.txt");
            File.WriteAllText(listFile, $"{_mgefs[0].Rec.FormKey}\r\n{_mgefs[2].Rec.FormKey}\n");
            Assert.Equal(Mgefs(m => !Claimed.Contains(m.Rec.FormKey)),
                         Run(MgefBodies, "formid not in " + string.Format(operandShape, listFile)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // probe: "parse: `in` on a non-formid LEAF path parses (W2 generalized membership)"
    [Fact]
    public void In_OnANonFormIdLeafPathParses() =>
        Assert.Null(ParseError("MagicSkill in [Destruction, Restoration]"));

    // probe: "parse: `not` without `in` refused"
    [Fact]
    public void Not_WithoutInIsRefused_NamingTheComplement() =>
        Assert.Contains("membership complement", ParseError("formid not [123456:X.esp]"));

    // probe: "parse: empty list refused (`formid in []`)"
    [Fact]
    public void In_AnEmptyFormIdListIsRefused() =>
        Assert.Contains("is empty", ParseError("formid in []"));

    // probe: "parse: non-FormID list entry refused"
    [Fact]
    public void In_ANonFormIdEntryIsRefused() =>
        Assert.Contains("is not a FormID", ParseError("formid in [not-a-formid]"));

    // probe: "parse: comma-in-filename shear refused WITH the comma-cause hint"
    [Fact]
    public void In_ACommaInAPluginFilenameIsRefusedNamingTheComma() =>
        Assert.Contains("contains a comma", ParseError("formid in [123456:Foo, Bar.esp]"));

    // probe: "parse: missing @file refused (named, not thrown)"
    [Fact]
    public void In_AnAtFileThatDoesNotExistIsRefusedNamed_NotThrown()
    {
        var absent = Path.Combine(Path.GetTempPath(), "hc-value-predicate-" + Guid.NewGuid().ToString("N"), "formids.txt");
        Assert.Contains("could not read", ParseError($"formid in @{absent}"));
    }

    // probe: "parse: RELATIVE @file path refused (server CWD is not the caller's)"
    [Fact]
    public void In_ARelativeAtFilePathIsRefused() =>
        Assert.Contains("not an absolute path", ParseError("formid in @relative-list.txt"));

    // ---- accounting: a wrong path is never a silent zero -----------------------------------------

    // probe: "wrong path: 0 matches" and "LOUD note (not silent zero)"
    [Fact]
    public void Eq_OnAWrongPathFailsLoud_NoReadableValue_NotASilentZero()
    {
        var (matched, set) = RunWithSet(MgefBodies, "Archetyp.ActorValue = Infamy");
        Assert.Empty(matched);
        var note = set.AccountingNote();
        Assert.Contains("no readable value", note);
        Assert.Contains("NOT A FIELD", note);
    }

    // probe: "valid-but-unset: 0 matches", "LOUD note", "says VALID/unset, NOT 'mistyped'"
    [Fact]
    public void Eq_OnAValidButUnsetFieldIsLoudAndSaysValid_NotMistyped()
    {
        var (matched, set) = RunWithSet(MgefBodies, "Name = Whatever");
        Assert.Empty(matched);
        var note = set.AccountingNote();
        Assert.Contains("no readable value", note);
        Assert.Contains("VALID", note);
        Assert.DoesNotContain("mistyped", note);
    }

    // probe: "container path: 0 matches" and "surfaced (note mentions container/list)"
    [Fact]
    public void Eq_OnASubstructPathIsSurfacedAsAContainer_NotMatched()
    {
        var (matched, set) = RunWithSet(MgefBodies, "Archetype = whatever");
        Assert.Empty(matched);
        Assert.Contains("container/list", set.AccountingNote());
    }

    // probe: "list path: 0 matches" and "surfaced (note mentions container/list)"
    [Fact]
    public void Eq_OnAPopulatedListPathIsSurfacedAsAContainer_NotMatched()
    {
        var armo = _mod.Armors.AddNew();
        armo.Keywords = new() { new FormLink<IKeywordGetter>(_projX) };
        var (matched, set) = RunWithSet(new IMajorRecordGetter[] { armo }, "Keywords = 0FFFFF:hcvalguard.esp");
        Assert.Empty(matched);
        Assert.Contains("container/list", set.AccountingNote());
    }

    // probe: "numeric op on enum: FatalError set" and "0 matches"
    [Fact]
    public void Gt_OnAnEnumIsATypedFatalError_NotASilentSkip()
    {
        var (matched, set) = RunWithSet(MgefBodies, "MagicSkill > 5");
        Assert.Contains("needs a numeric field", set.FatalError);
        Assert.Empty(matched);
    }

    // probe: "soft note: matched only the weapons (3)", "SOFT (>half no-value), not the loud note", "names the cause"
    [Fact]
    public void Ge_OnAPathWrongForMoreThanHalfAMixedScanGetsTheSoftNote_NotTheLoudOne()
    {
        var (matched, set) = RunWithSet(MgefBodies.Concat(WeapBodies), "BasicStats.Damage >= 0");
        Assert.Equal(Weaps(_ => true), matched);
        var note = set.AccountingNote();
        Assert.Contains("had no value on", note);
        Assert.DoesNotContain("yielded no readable value", note);
        Assert.Contains("not a field on the record read", note);
        Assert.DoesNotContain("read fault", note);
    }

    // probe: "healthy scan: no spurious accounting note"
    [Fact]
    public void Eq_AHealthyScanGivesNoAccountingNote() =>
        Assert.Null(RunWithSet(MgefBodies, "MagicSkill = Destruction").Set.AccountingNote());

    // ---- parse -----------------------------------------------------------------------------------

    // probe: "parse: `has` accepted", "`exists` accepted", "`missing` accepted", "valid predicate accepted"
    [Theory]
    [InlineData("BodyTemplate.FirstPersonFlags has 16")]
    [InlineData("VirtualMachineAdapter exists")]
    [InlineData("VirtualMachineAdapter missing")]
    [InlineData("MagicSkill = Destruction")]
    public void AWellFormedPredicateParsesClean(string clause) => Assert.Null(ParseError(clause));

    // probe: the section-11 parse errors, each refusing the whole call before any scan
    [Theory]
    [InlineData("BasicStats.Damage >= abc", "needs a numeric value")]   // numeric op needs numeric operand
    [InlineData("MagicSkill", "no operator")]                           // missing operator
    [InlineData("= Infamy", "no field path")]                           // missing path
    [InlineData("MagicSkill =", "no value after")]                      // missing value
    [InlineData("MagicSkill ~ x", "unrecognized operator")]             // unknown operator
    public void AMalformedPredicateIsRefusedAtParseNamingTheRule(string clause, string teaching) =>
        Assert.Contains(teaching, ParseError(clause));

    // probe: "parse: empty where= refused"
    [Fact]
    public void AnEmptyWhereIsRefused() => Assert.Contains("was empty", ParseError());

    // ---- builders --------------------------------------------------------------------------------

    Mgef MakeMgef(string eid, ActorValue skill, float baseCost, ActorValue archAv, FormKey projectile)
    {
        var m = _mod.MagicEffects.AddNew();
        m.EditorID = eid;
        m.MagicSkill = skill;
        m.BaseCost = baseCost;
        m.Archetype = new MagicEffectLightArchetype { ActorValue = archAv };
        m.Projectile.SetTo(projectile);
        return new Mgef(m, eid, skill, baseCost, archAv, projectile);
    }

    Weap MakeWeap(ushort damage)
    {
        var w = _mod.Weapons.AddNew();
        w.BasicStats = new WeaponBasicStats { Damage = damage };
        return new Weap(w, damage);
    }

    Armo MakeArmo(BipedObjectFlag flags)
    {
        var a = _mod.Armors.AddNew();
        a.BodyTemplate = new BodyTemplate { FirstPersonFlags = flags };
        return new Armo(a, (ulong)flags);
    }
}
