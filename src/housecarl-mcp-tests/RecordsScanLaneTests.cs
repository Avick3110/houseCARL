using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// SPEC §2.2 / §6.1 — the types=/plugins= (scan) lane, form-scoping, the off-order scan arm, and the
/// PR #307 review folds. (RecordsGuardProbe arms 4 and 4b.)
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScanLaneTests : RecordsTestBase
{
    public RecordsScanLaneTests(RecordsFixture f) : base(f) { }

    // ---- 4: the scan lane ------------------------------------------------------------------------

    [Fact]
    public void TypesIsASet_TheScanStreamsTheUnionOfWeapAndArmo() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP", "ARMO" }), "HcRecW0", "HcRecA0");

    [Fact]
    public void TheWinnerProvenanceTermRidesWhereEndToEnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"winner = {W.OverrideName}" },
                                     project: Form("summary"));
        Served(r, "HcRecW0");
        Assert.DoesNotContain("HcRecW1", r);
    }

    [Fact]
    public void TheLinkStepRidesWhereEndToEnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects->editorid startswith HcRecMgef" });
        Served(r, "HcRecSpellA");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void TheEditoridTermReplacesEditoridContains_StartswithWorksInAScan() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "editorid startswith HcRecW" }),
               "HcRecW0", "HcRecW1");

    [Fact]
    public void FormScoping_DepthOutsideTheFieldsAndEverythingFormsRefusesNamingTheRule() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" },
                                     project: new RecordsTools.RecordsProject { form = "summary", depth = 3 }), "fields");

    [Fact]
    public void FormScoping_GroupByOutsideTheAggregateFormRefusesNamingTheRule() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" },
                                     project: new RecordsTools.RecordsProject { form = "summary", group_by = "winner" }), "aggregate");

    [Fact]
    public void TreeForm_RendersTheProviderStackWinnerLastWithPerNodeDeltas() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: Form("tree")),
               "winner last", W.OverrideName);

    [Fact]
    public void SourcePreviousProvider_RefusesNamingTheSubjectRelativeRule()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin("previous_provider"));
        Assert.StartsWith("error:", r);
        Assert.Contains("subject", r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormidsTimesScanComposition_TheIdentitySetIntersectsTheTypeScan()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), Fid(W.Armor) }, types: new[] { "WEAP" });
        Served(r, "HcRecW0");
        Assert.DoesNotContain("HcRecA0", r);
    }

    [Fact]
    public void FormidsAsTheOnlyBound_AWhereOverTheSetNeedsNoTypesOrPlugins()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), Fid(W.Weapons[1]) },
                                     where: new[] { "BasicStats.Damage >= 90" });
        Served(r, "HcRecW0");
        Assert.DoesNotContain("HcRecW1", r);
    }

    [Fact]
    public void IdentityOnTheScanLaneRefuses_SummaryRowsAlreadyCarryIdentity() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Form("identity")), "summary");

    [Fact]
    public void ScanPlusEverything_SelectionViaTheScanFullBodiesViaTheBatchLane() =>
        Served(RecordsTools.Records(Svc, types: new[] { "ARMO" }, project: Form("everything")),
               "HcRecA0", "match(es)");

    [Fact]
    public void ScanAggregateInJson_CarriesTheRecordsEnvelopeFormInBand() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "json",
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }),
               "\"form\": \"aggregate\"", "\"groups\"");

    [Fact]
    public void ListSummaryInJson_CarriesFormPlusTheResolvedSourceArmInTheEnvelope() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, format: "json"),
               "\"form\": \"summary\"", "\"source\": \"winner\"");

    [Fact]
    public void OffOrderScan_TypesEnumeratesTheFilesOwnRecordsAndTheArmIsStated() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName)),
               "OUT-OF-LOAD-ORDER", "HcRecW1");

    [Fact]
    public void OffOrderScan_TheFieldsFormReadsTheFilesBodies() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "WEAP" },
                                    project: Fields("BasicStats.Damage")),
               "55", "OUT-OF-LOAD-ORDER");

    [Fact]
    public void OffOrderScan_TheFullWhereGrammarRunsOverTheFilesOwnBodies() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "WEAP" },
                                    where: new[] { "BasicStats.Damage >= 50" }), "HcRecW1");

    // ---- 4b: PR #307 review folds ----------------------------------------------------------------

    [Fact]
    public void TheWinnerTermSeesADeletedRecord_ResolutionIsARealFactAboutIt() =>
        Assert.Contains(Fid(W.Weapons[2]),
                        RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"winner = {W.OverrideName}" }));

    [Fact]
    public void ABodyPredicateStillSkipsADeletedRecord_NoLiveBodyToJudge() =>
        Assert.DoesNotContain(Fid(W.Weapons[2]),
                              RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage > 0" }));

    [Fact]
    public void ScanPlusEverythingUnderAPluginsScope_DumpsTheScopedBodyNotTheWinner()
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.MasterName), types: new[] { "WEAP" },
                                     where: new[] { "editorid = HcRecW0" },
                                     project: new RecordsTools.RecordsProject { form = "everything", depth = 2 });
        Served(r, "Damage = 10");
        Assert.DoesNotContain("Damage = 99", r);
    }

    [Fact]
    public void FieldsSourceWinner_RetargetsTheDumpToTheWinnerSameAsTheFieldsForm() =>
        Served(RecordsTools.Records(Svc, plugins: Scope(W.MasterName), types: new[] { "WEAP" },
                                    where: new[] { "editorid = HcRecW0" }, fields_source: "winner",
                                    project: new RecordsTools.RecordsProject { form = "everything", depth = 2 }),
               "Damage = 99");

    [Fact]
    public void AZeroMatchScanWithANamedSourceAndEverything_RendersAnHonestEmptyResultNotATearRefusal()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "BasicStats.Damage > 9999" },
                                     source: Plugin(W.OverrideName), project: Form("everything"));
        Served(r, "0 match(es)");
        Assert.DoesNotContain("vanished", r);
    }

    [Fact]
    public void OffOrderScan_OffsetPagesInExactWindows_PastTheEndRendersTheTrueTotalWithNoRows() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), offset: 500),
               "offset=500 skipped past");

    [Fact]
    public void OffOrderScan_CountsOnlyReturnsTheCensusOverTheFilesRecords() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), counts_only: true),
               "1 match");

    RecordsTools.RecordsProject AggByWinner => new() { form = "aggregate", group_by = "winner" };

    [Fact]
    public void ListAggregateText_StatesTheResolvedOffOrderArm() =>
        Assert.Contains("OUT-OF-LOAD-ORDER",
                        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, source: Plugin(W.OldName),
                                             project: AggByWinner));

    [Fact]
    public void ListAggregateJson_CarriesTheArmAndTheEpochCoverageQualifierInTheEnvelope() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, source: Plugin(W.OldName), format: "json",
                                    project: AggByWinner),
               "OUT-OF-LOAD-ORDER", "epoch_covers_source");

    [Fact]
    public void DensePlusEverythingRefusesByName_NoFixedColumnSetNeverASilentTextFallback() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "dense", project: Form("everything")), "column");

    [Fact]
    public void DensePlusAggregateRefusesByNamePointingAtJson_NeverASilentJsonSwitch() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "WEAP" }, format: "dense", project: AggByWinner), "json");

    [Fact]
    public void FieldsSourceOnTheFormidsLaneRefusesByName_NeverAcceptedAndDropped() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, source: Plugin(W.OldName),
                                     fields_source: "winner", project: Fields("BasicStats.Damage")), "fields_source");

    [Fact]
    public void CountsOnlyPlusToFileRefusesByName_UsedToReturnTheCensusAndSilentlyWriteNothing() =>
        Refused(RecordsTools.Records(Svc, formids: AllWeaponIds, counts_only: true,
                                     to_file: W.Scratch("never.jsonl")), "counts_only");

    [Fact]
    public void IdentityPlusCountsOnlyReturnsTheCensusWithNoRows()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), "notaformid" }, counts_only: true,
                                     project: Form("identity"));
        Served(r, "count=2", "errors=1");
        Assert.DoesNotContain("HcRecW0", r);
    }

    [Fact]
    public void LimitAndOffsetWindowTheFormidsRenderWithTheNoteInBand()
    {
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds, limit: 2, offset: 1);
        Served(r, "window: rows 2–3 of 3");
        Assert.DoesNotContain(Fid(W.Weapons[0]), r);
    }

    [Fact]
    public void AnExplicitDepthOutsideItsFormsRefusesRegardlessOfValue() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                     project: new RecordsTools.RecordsProject { form = "summary", depth = 1 }), "depth");

    [Fact]
    public void OffOrderUntouched_ThePerItemRefusalNamesTheActiveTouchers() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.OldName)),
               "does not define or override", "Touched by", W.MasterName);

    [Fact]
    public void OffOrderPolePlusOutOfIndexFormid_IsAPerItemRefusalNotAnNre()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { "000123:NotInOrder.esp" }, source: Plugin(W.OldName));
        Assert.DoesNotContain("NullReferenceException", r);
        Assert.Contains("No active plugin touches it either", r);
    }

    [Fact]
    public void AnOffsetPastTheEndRendersTheHonestEmptyWindowNote()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, offset: 10);
        Served(r, "no rows", "past the end");
        Assert.DoesNotContain("rows 11", r);
    }

    [Fact]
    public void ToFilePlusLimit_NoWindowNoteOverTheCompleteArtifact()
    {
        var art = W.Scratch("results", "nowindow.jsonl");
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds, limit: 2, to_file: art, project: Form("identity"));
        Assert.DoesNotContain("window:", r);
        Assert.True(File.Exists(art));
    }

    [Fact]
    public void AggregatePlusOffsetRefusesByNameOnTheListLaneToo() =>
        Refused(RecordsTools.Records(Svc, formids: AllWeaponIds, offset: 1, project: AggByWinner), "offset");

    [Fact]
    public void APathFormSourceOnTheScanLaneResolvesToItsActivePluginAndTheScanRuns() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" },
                                    source: Je(System.Text.Json.JsonSerializer.Serialize(W.OverrideFile))),
               "active in the load order", "HcRecW0");
}
