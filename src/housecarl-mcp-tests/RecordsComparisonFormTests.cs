using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// SPEC §3 / §4.3 / §6.1 — the comparison and traversal forms (delta, tree, chain, info_order), the
/// source/versus poles, and the compositions between them, plus the PR #309 review folds.
/// (RecordsGuardProbe arm 6.)
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsComparisonFormTests : RecordsTestBase
{
    public RecordsComparisonFormTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Delta => new() { form = "delta" };
    static RecordsTools.RecordsProject Chain => new() { form = "chain" };
    static Mutagen.Bethesda.Plugins.FormKey Fk(Mutagen.Bethesda.Plugins.FormKey k) => k;

    string DeltaVsPrevious(Mutagen.Bethesda.Plugins.FormKey rec, string subject) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(rec) }, source: Plugin(subject),
                             versus: Plugin("previous_provider"), project: Delta);

    // ---- the four §4.3 pins over the 3-deep stack on W0 (master < mid < override) -----------------

    [Fact]
    public void DeltaP1_SubjectWins_TheReferenceIsThePluginImmediatelyBelow() =>
        Served(DeltaVsPrevious(W.Weapons[0], W.OverrideName), "previous provider", W.MidName, "Damage");

    [Fact]
    public void DeltaP2_AMidStackSubjectMeasuresBelowItselfAndWhatOutranksItIsStatedAsNeutralFact()
    {
        var r = DeltaVsPrevious(W.Weapons[0], W.MidName);
        Served(r, W.MasterName, "stack above the subject", W.OverrideName);
        Assert.DoesNotContain("consider", r);
        Assert.DoesNotContain("should", r);
    }

    [Fact]
    public void DeltaP3_TheDefiningSubjectRefusesLoud_NeverAnEmptyDiffThatReadsAsNoChanges() =>
        Assert.Contains("no previous provider", DeltaVsPrevious(W.Weapons[0], W.MasterName));

    [Fact]
    public void DeltaP3_TheRefusalSaysTheSubjectDefinesTheRecord() =>
        Assert.Contains("DEFINES", DeltaVsPrevious(W.Weapons[0], W.MasterName));

    [Fact]
    public void DeltaP4_ASubjectThatDoesNotTouchTheRecordRefusesNamingTheActualTouchers() =>
        Served(DeltaVsPrevious(W.Weapons[1], W.OverrideName),
               "does not define or override", "Touched by", W.MasterName);

    [Fact]
    public void TheDeltaFormRequiresVersus_ADeltaHasTwoPoles() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: Delta), "versus");

    [Fact]
    public void VersusOutsideTheComparisonFormsRefusesNamingThem() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, versus: Plugin("winner")), "delta");

    [Fact]
    public void TheInvertedCompositionSourcePreviousProviderRefusesNamingTheSubjectRule() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin("previous_provider"),
                                     versus: Plugin("winner"), project: Delta), "SUBJECT");

    [Fact]
    public void DeltaVsAnOffOrderReference_TheOnePoleRuleServesTheFilesVersionWithCoverageDeclared() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, versus: Plugin(W.OldName), project: Delta),
               "OUTSIDE the epoch fingerprint", "55");

    [Fact]
    public void BothPolesResolvingToOneProviderIsSaid_NeverSilent() =>
        Assert.Contains("SAME provider",
                        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.OverrideName),
                                             versus: Plugin(W.OverrideName), project: Delta));

    // ---- tree -------------------------------------------------------------------------------------

    [Fact]
    public void TreeInJson_TheCommittedRender_TheOneXTextOnlyRefusalDiedByConstruction() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, format: "json", project: Form("tree")),
               "\"nodes\"", "\"touchers\"", "\"is_winner\"");

    [Fact]
    public void TreePlusToFile_TreesSpill_TheRowFormExistsNow()
    {
        var art = W.Scratch("results", "tree.jsonl");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, to_file: art, project: Form("tree"));
        Assert.True(File.Exists(art));
        Assert.Contains(art, r);
    }

    [Fact]
    public void DeltaOnTheScanLane_TheScanSelectsAndTheComparisonRidesTheSelection() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"winner = {W.OverrideName}" },
                                    source: Plugin(W.OverrideName), versus: Plugin("previous_provider"), project: Delta),
               "selected by the scan", W.MidName);

    // ---- chain / walk ------------------------------------------------------------------------------

    [Fact]
    public void ChainForwardWithFollowEffects_TheSpellsEffectLinkIsReachedWithProvenance() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
                                    walk: new RecordsTools.RecordsWalk { follow = "Effects" }, project: Chain),
               "HcRecMgefFire");

    [Fact]
    public void ChainForwardClosure_EveryLinkExpandsViaTheGenericEnumeration() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
                                    walk: new RecordsTools.RecordsWalk(), project: Chain),
               "HcRecMgefFire");

    [Fact]
    public void WalkWithoutChain_TheReachedSetSeedsIncludedFeedsTheSummaryForm() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, walk: new RecordsTools.RecordsWalk()),
               "HcRecSpellA", "HcRecMgefFire", "selection =");

    [Fact]
    public void TemplateFlagsInterpreter_InheritedCategoriesNameTheirProviderAndClearFlagsReadLocalActive() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.NpcChild) },
                                    walk: new RecordsTools.RecordsWalk { follow = "Template" }, project: Chain),
               "Traits: INHERITED from", "HcRecNpcParent", "Stats: local data ACTIVE");

    [Fact]
    public void ReverseMgefLane_TheCarriersOfThisEffectWithTheMatchingEntrysPayload()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse" }, project: Chain);
        Served(r, "HcRecSpellA");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void ReverseWithANonMgefSeedFailsLoudNamingTheType_NeverASilentZeroCarriers()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse" }, project: Chain);
        Assert.Contains("error", r);
        Assert.True(r.Contains("MagicEffect") || r.Contains("MGEF"), "the refusal names the required type");
    }

    [Fact]
    public void ReverseDepthGreaterThanOneRefusesNamingTheReverseReferenceIndexAsTheFutureCapability() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 3 }, project: Chain),
                "reverse-reference index");

    [Fact]
    public void InfoOrderOnANonDial_IsATypedPerItemRefusalTeachingTheQuestFanOutComposition() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: Form("info_order")),
               "DIAL", "Quest =");

    // ---- the SkyPatcher overlay pole ----------------------------------------------------------------

    [Fact]
    public void OverlayPostSource_TheReplayedBodyIsReadAndIniContentIsDeclaredOutsideTheFingerprint() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Overlay("post"),
                                    project: Fields("BasicStats.Damage")),
               "99", "OUTSIDE the epoch fingerprint");

    [Fact]
    public void PreVsPostOverlayDelta_NoIniLayerHereSoTheTwoStatesAgree_AnHonestIdentical() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Overlay("pre"),
                                    versus: Overlay("post"), project: Delta), "identical");

    // ---- scope-vs-pole streams ----------------------------------------------------------------------

    [Fact]
    public void ScopeVsPole_TheScopesMatchesReadFromThePoleAndUntouchedOnesAreCountedAndNamed() =>
        Served(RecordsTools.Records(Svc, types: null, plugins: Scope(W.MasterName), source: Plugin(W.OverrideName),
                                    project: Fields("BasicStats.Damage")), "99", "not_touched");

    [Fact]
    public void ScopeVsPolePlusSummaryRefusesByName_ThePoleChangesNothingThere() =>
        Refused(RecordsTools.Records(Svc, plugins: Scope(W.MasterName), source: Plugin(W.OverrideName)), "identity facts");

    // ---- PR #309 review folds -------------------------------------------------------------------------

    [Fact]
    public void FoldF1_ConflictsOnlyPlusFormidsEvaluatesWhere_TheContestedButNonMatchingKeyDropsOut()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), Fid(W.Weapons[1]) }, conflicts_only: true,
                                     where: new[] { "BasicStats.Damage >= 90" });
        Served(r, "HcRecW0");
        Assert.DoesNotContain("HcRecW1", r);
    }

    [Fact]
    public void FoldF1_AContestedKeyFailingThePredicateIsExcludedNotReturnedAsAMatch()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, conflicts_only: true,
                                     where: new[] { "BasicStats.Damage >= 500" });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal));
        Assert.DoesNotContain("HcRecW0", r);
    }

    [Fact]
    public void FoldF2_ScanPlusWalkPlusAggregateCountsTheReachedSetWithTheWalkDeclared() =>
        Served(RecordsTools.Records(Svc, types: new[] { "SPEL" }, walk: new RecordsTools.RecordsWalk(),
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }),
               "MagicEffect", "selection =");

    [Fact]
    public void FoldF3_ReverseWalkPlusToFileWritesTheCarrierArtifact()
    {
        var art = W.Scratch("results", "carriers.jsonl");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse" }, to_file: art,
                                     project: Chain);
        Assert.True(File.Exists(art));
        Assert.Contains(art, r);
    }

    [Fact]
    public void FoldF5_AnOverlayPoleOnAScanComparisonRefusesNamingTheBoundedFormidsLane() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Overlay("post"), versus: Plugin("winner"),
                                     project: Delta), "formids=");

    [Fact]
    public void FoldF8_JsonDeltaCensusCountsTheCompleteListWithRowsWindowed() =>
        Served(RecordsTools.Records(Svc, formids: AllWeaponIds, versus: Plugin("winner"), format: "json", limit: 1,
                                    project: Delta), "\"count\": 3", "\"rendered\": 1");

    [Fact]
    public void FoldF9_OffOrderWhereSourceWithAnUnknownSpellingRefusesByName() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName),
                                     where: new[] { "BasicStats.Damage >= 1" }, where_source: "winnner"), "winnner");

    [Fact]
    public void FoldF10_OffOrderFormidsExpandingToEmptyRefuses() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { "" }, types: new[] { "WEAP" }, source: Plugin(W.OldName)), "empty");

    [Fact]
    public void ReReview_AnOverlayVersusOnTheOffOrderScanRefusesToo() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), versus: Overlay("post"),
                                     project: Delta), "formids=");

    [Fact]
    public void ReReview_LimitWindowsTheSeedsOnly_BothCarriersOfTheOneSeedRender() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse" }, limit: 1, project: Chain),
               "HcRecSpellA", "HcRecSpellC");

    // ---- PR #309 round-3 folds -------------------------------------------------------------------------

    [Fact]
    public void Fold3F1_AScanLaneDeltasJsonEnvelopeCarriesExactlyOneSourceProperty() =>
        Assert.Equal(1, CountOf(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"winner = {W.OverrideName}" },
                                                     source: Plugin(W.OverrideName), versus: Plugin("previous_provider"),
                                                     format: "json", project: Delta), "\"source\":"));

    [Fact]
    public void Fold3F1_AScanSeededWalksReEnteredSummaryStatesOneSourceArm()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, walk: new RecordsTools.RecordsWalk());
        int ep = r.IndexOf("epoch=", StringComparison.Ordinal);
        Assert.True(ep > 0, "the summary stamps an epoch");
        Assert.Equal(1, CountOf(r.Substring(0, ep), "source="));
    }

    [Fact]
    public void Fold3F3_ResolveNamesUnderTheOverlayPostPoleAnnotatesLinkTargets() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, source: Overlay("post"),
                                    project: new RecordsTools.RecordsProject
                                    { form = "fields", fields = new[] { "Effects" }, depth = 4, resolve_names = true }),
               "HcRecMgefFire");

    [Fact]
    public void Fold3F4_FieldsSourcePlusScopeVsPoleRefusesByName_TwoDisplayPolesOnOneCall() =>
        Refused(RecordsTools.Records(Svc, plugins: Scope(W.MasterName), source: Plugin(W.OverrideName),
                                     fields_source: "winner", project: Fields("BasicStats.Damage")), "TWO display poles");

    [Fact]
    public void Fold3F4_FieldsSourcePlusWalkRefusesByName() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, fields_source: "winner",
                                     walk: new RecordsTools.RecordsWalk()), "fields_source");

    [Fact]
    public void Fold3F4_FieldsSourcePlusChainRefusesByName_NoFieldValuesToRetarget() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, fields_source: "winner",
                                     walk: new RecordsTools.RecordsWalk(), project: Chain), "fields_source");

    [Fact]
    public void Fold3F7_DensePlusWalkRefusesUpFront() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "dense", walk: new RecordsTools.RecordsWalk()),
                "dense");

    // ---- PR #309 round-4 folds --------------------------------------------------------------------------

    [Fact]
    public void Fold4R31_AnOffOrderTreesEnvelopeKeepsTheSelectionArmInSourceAndTheReferenceInVersus()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), format: "json",
                                     project: Form("tree"));
        Served(r, "OUT-OF-LOAD-ORDER", "\"versus\": \"winner\"");
        Assert.Equal(1, CountOf(r, "\"source\":"));
    }

    [Fact]
    public void Fold4R31_SourceOnTheListLaneTreeRefusesByName_ATreeHasNoSubject() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.OverrideName),
                                     project: Form("tree")), "no subject");

    [Fact]
    public void Fold4R33_FieldsSourceScopedTheDocumentedNoOpDefaultStaysAcceptedUnderAWalk() =>
        Assert.False(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, fields_source: "scoped",
                                          walk: new RecordsTools.RecordsWalk())
                                 .StartsWith("error:", StringComparison.Ordinal));

    [Fact]
    public void Fold4R33_AnUnknownFieldsSourceUnderAWalkGetsTheNotAKnownSourceRefusalByValue() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, fields_source: "winnner",
                                     walk: new RecordsTools.RecordsWalk()), "winnner");
}
