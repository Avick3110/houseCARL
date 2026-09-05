using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The records surface's wording and remedy repairs: a summary row's own label, the aggregate scope
/// pre-check, the form-scoped field selector, the walk's carrier bound, and the two refusals that used to state a
/// negative they could have qualified. Every one is a sentence a caller reads, so each is driven, not derived.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsRemedyRepairTests : RecordsTestBase
{
    public RecordsRemedyRepairTests(RecordsFixture f) : base(f) { }

    // ---- the summary row's override-depth label (#442) ---------------------------------------------------

    [Fact]
    public void ASummaryRowLabelsTheOverrideDepthOverrideDepthNotDepth()
    {
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds);
        Served(r, "override_depth=");
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(?<!override_)depth="), r);
    }

    // ---- the aggregate scope pre-check speaks this tool's levers (#443) -----------------------------------

    [Fact]
    public void GroupByTypeWithoutABodyBearingScopeRefusesInThisToolsOwnSpelling()
    {
        var r = RecordsTools.Records(Svc, conflicts_only: true,
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
        Refused(r, "project.group_by='type'", "types=", "plugins=", "formids=");
        Assert.DoesNotContain("group_by=type", r);
        Assert.DoesNotContain("add type=", r);
    }

    /// <summary>The off-order lane has no such precondition — the file's own records ARE the universe there and
    /// every match has a body — so the pre-check must not fire on it and displace the refusal this call has really
    /// earned. Gating it on the in-order lane restores the sentence this call got before the pre-check existed.</summary>
    [Fact]
    public void TheOffOrderScanIsNotPreRefused_ItKeepsItsOwnRefusal()
    {
        var r = RecordsTools.Records(Svc, conflicts_only: true, source: Plugin(W.OldName),
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
        Refused(r, "conflicts_only= has no meaning on an out-of-load-order file");
        Assert.DoesNotContain("body-bearing scope", r);
    }

    /// <summary>And a real off-order aggregate by type serves: the file IS the scope.</summary>
    [Fact]
    public void AnOffOrderAggregateByTypeServes_TheFileIsTheScope() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName),
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }),
               "form=aggregate");

    /// <summary>An in-order named source= IS the scope the scan runs over, so it satisfies the check too.</summary>
    [Fact]
    public void AnInOrderNamedSourceCountsAsTheBodyBearingScope() =>
        Served(RecordsTools.Records(Svc, conflicts_only: true, source: Plugin(W.OverrideName),
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }),
               "form=aggregate");

    [Fact]
    public void GroupByTypeWithATypesScopeStillServes_ThePreCheckOnlyRefusesTheUnboundCall() =>
        Served(RecordsTools.Records(Svc, types: new[] { "WEAP" },
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }),
               "form=aggregate");

    // ---- the everything form has no field selector to narrow with (#445) ---------------------------------

    [Fact]
    public void TheEverythingFormsTruncationNoticeNamesNoFieldSelector_ThatFormRefusesOne()
    {
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds, max_chars: 220, project: Form("everything"));
        Assert.Contains("max_chars", r);
        Assert.DoesNotContain("project.fields=", r);
        Assert.Contains("project.depth=", r);
    }

    [Fact]
    public void AndItSaysTheSameThingOnTheJsonLane()
    {
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds, max_chars: 220, format: "json", project: Form("everything"));
        Assert.Contains("truncated at max_chars", r);
        Assert.DoesNotContain("project.fields=", r);
    }

    [Fact]
    public void TheOffOrderScansEverythingLaneDropsItToo_ThreeLanesOneRule()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, source: Plugin(W.OldName), max_chars: 12,
                                     project: Form("everything"));
        Assert.Contains("max_chars", r);
        Assert.DoesNotContain("project.fields=", r);
    }

    [Fact]
    public void TheFieldsFormStillNamesItsSelector_TheVocabularyIsPerFormNotPerTool()
    {
        var r = RecordsTools.Records(Svc, formids: AllWeaponIds, max_chars: 220,
                                     project: Fields("BasicStats.Damage", "EditorID", "Name"));
        Assert.Contains("project.fields=", r);
    }

    // ---- the walk's carrier bound is walk.max_nodes, not limit= (#446) ------------------------------------

    [Fact]
    public void ACappedCarrierListNamesWalkMaxNodes_LimitWindowsTheSeedsAndWouldBeANoOp()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) }, project: Form("chain"),
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", max_nodes = 1 });
        Served(r, "raise walk.max_nodes");
        Assert.DoesNotContain("raise limit=", r);
    }

    // ---- a FormID that resolves nowhere says whether its PLUGIN did (#460) --------------------------------

    [Fact]
    public void AMissingRecordInAPresentPluginSaysThePluginIsThere()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { "FFFFF0:" + W.MasterName });
        Assert.Contains($"Plugin '{W.MasterName}' IS in the load order", r);
        Assert.Contains("defines no record FFFFF0", r);
        Assert.Contains("defined_in", r);   // the call that lists what it DOES define
    }

    /// <summary>The fixture's master is a plain, unflagged Mutagen-authored master, and 0x800 is where such a
    /// master's own records start — so the compaction story is not this record's cause and must not be asserted.
    /// The light-flagged side of the same sentence is driven in RuntimeFormIdTests, whose world has an ESL.</summary>
    [Fact]
    public void AndDoesNotBlameEslCompactionOnAPluginThatIsNotEslFlagged()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { "FFFFF0:" + W.MasterName });
        Assert.DoesNotContain("ESL", r);
        Assert.DoesNotContain("0x800", r);
    }

    [Fact]
    public void AMissingRecordInAnAbsentPluginSaysThePluginIsTheThingThatIsMissing()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { "000800:NotInstalled.esp" });
        Assert.Contains("its plugin 'NotInstalled.esp' is not in the order", r);
        Assert.DoesNotContain("IS in the load order", r);
    }

    // ---- a mid-path list hop is a missing bracket, not a mistyped field (#461) ----------------------------

    [Fact]
    public void APredicateHoppingThroughAListIsToldTheExactBracketedSpelling()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.Data.Magnitude > 0" });
        Assert.Contains("'Effects'", r);
        // The leaf IS a field on the element type, so the remedy is the WHOLE fixed path — a prefix would refuse
        // again the moment the caller pasted it back.
        Assert.Contains("'Effects[0].Data.Magnitude'", r);
        Assert.DoesNotContain("[0]. …", r);
        Assert.DoesNotContain("check the field name against the record's schema", r);
    }

    /// <summary>The did-you-mean arm carries the trailing segments too: a near miss mid-path names the whole
    /// corrected spelling, not the corrected segment alone.</summary>
    [Fact]
    public void ANearMissMidPathNamesTheWholeCorrectedSpelling()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.Dat.Magnitude > 0" });
        Assert.Contains("did you mean 'Effects[0].Data.Magnitude'?", r);
    }

    /// <summary>The remedy is the same for every record the scan dead-ends on, and computing it costs a property
    /// resolve, the element type's whole field list and a nearest-name sweep — so it is computed once for the
    /// (element type, segment) pair, not once per scanned record. The segment is unique to this test so the memo
    /// is cold when it runs.</summary>
    [Fact]
    public void AScanComputesOneListHopRemedyForTheWholeScan()
    {
        var before = HousecarlCore.ReadEngine.ListHopVerdictComputations;
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.HopMemoProbe > 0" });
        Assert.Contains("is not a field on its element type", r);
        Assert.True(W.SpellBodies.Count > 1, "the scan must cross more than one record for this to say anything");
        Assert.Equal(1, HousecarlCore.ReadEngine.ListHopVerdictComputations - before);
    }

    /// <summary>The bracket is only half the diagnosis: a trailing segment that is not a field on the ELEMENT type
    /// gets no bracket assertion, because adding one would not fix it.</summary>
    [Fact]
    public void ALeafThatIsNotAFieldOnTheElementTypeIsToldThat_NotToAddABracket()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.NotOnAnEffect > 0" });
        Assert.Contains("is not a field on its element type", r);
        Assert.DoesNotContain("'Effects[0].NotOnAnEffect'", r);
    }

    /// <summary>And where a near miss exists, the fixed path it names is a real one.</summary>
    [Fact]
    public void ANearMissOnTheElementTypeIsOfferedTheRealFieldName()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.Dat > 0" });
        Assert.Contains("did you mean 'Effects[0].Data'?", r);
    }

    [Fact]
    public void APresenceOperatorGetsTheSameDiagnosis_TheOperatorDoesNotDecideWhyThePathMissed()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.Data.Magnitude exists" });
        Assert.Contains("'Effects[0].Data.Magnitude'", r);
        Assert.DoesNotContain("check the field name against the record's schema", r);
    }

    [Fact]
    public void AMixedScanWhereOnlySomeTypesCarryTheListStillGetsTheBracketAdvice()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL", "WEAP" }, where: new[] { "Effects.Data.Magnitude > 0" });
        Assert.Contains("'Effects[0].Data.Magnitude'", r);
        Assert.Contains("on the rest the path is not a field at all", r);
    }

    [Fact]
    public void AGenuinelyMistypedFieldKeepsTheSchemaAdvice_TheTwoCausesStayApart()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "NotAFieldAtAll > 0" });
        Assert.Contains("check the field name against the record's schema", r);
        Assert.DoesNotContain("BRACKETS", r);
    }
    // ---- every lane's unresolved-FormID sentence is the SAME three-cause sentence (#460) ------------------
    //
    // One ghost FormID in a plugin that IS installed, driven through each form that used to compose its own
    // "not present in the active order" — the sentence a caller reads must not depend on which form they asked for.

    string Ghost => "FFFFF0:" + W.MasterName;

    [Fact]
    public void TheTreeFormSaysWhichOfTheThreeCausesItIs() =>
        Assert.Contains("IS in the load order", RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("tree")));

    /// <summary>The forward walk's own form: form='chain' is where a per-seed outcome is listed (the reading forms
    /// consume the reached set and say only how many seeds errored), so it is where this sentence is read.</summary>
    [Fact]
    public void TheWalkSeedSaysWhichOfTheThreeCausesItIs()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), Ghost }, project: Form("chain"),
                                     walk: new RecordsTools.RecordsWalk { depth = 1 });
        Assert.Contains("IS in the load order", r);
        Assert.Contains("Nothing to walk from", r);
    }

    [Fact]
    public void TheInfoOrderFormSaysWhichOfTheThreeCausesItIs() =>
        Assert.Contains("IS in the load order", RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("info_order")));

    [Fact]
    public void TheDeltaSubjectPoleSaysWhichOfTheThreeCausesItIs() =>
        Assert.Contains("IS in the load order",
            RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("delta"), versus: Plugin(W.OverrideName)));

    [Fact]
    public void TheOverlayPrePoleSaysWhichOfTheThreeCausesItIs() =>
        Assert.Contains("IS in the load order",
            RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("delta"), versus: Overlay("pre")));

    [Fact]
    public void TheIdentityFormSaysWhichOfTheThreeCausesItIs()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("identity"));
        Assert.Contains("IS in the load order", r);
        Assert.DoesNotContain("error=not present in the active order", r);
    }

    [Fact]
    public void AndTheIdentityFormSaysItOnTheJsonLaneToo() =>
        Assert.Contains("IS in the load order",
            RecordsTools.Records(Svc, formids: new[] { Ghost }, project: Form("identity"), format: "json"));
}
