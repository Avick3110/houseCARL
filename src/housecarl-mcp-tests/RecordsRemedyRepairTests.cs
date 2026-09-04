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
        Assert.Contains("ESL-flagged edition", r);
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
    public void APredicateHoppingThroughAListIsToldToUseBracketsNotToCheckTheSchema()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects.Data.Magnitude > 0" });
        Assert.Contains("BRACKETS", r);
        Assert.Contains("'Effects'", r);
        Assert.DoesNotContain("check the field name against the record's schema", r);
    }

    [Fact]
    public void AGenuinelyMistypedFieldKeepsTheSchemaAdvice_TheTwoCausesStayApart()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "NotAFieldAtAll > 0" });
        Assert.Contains("check the field name against the record's schema", r);
        Assert.DoesNotContain("BRACKETS", r);
    }
}
