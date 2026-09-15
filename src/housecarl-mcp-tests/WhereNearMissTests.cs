using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The near-miss sentence for a legal <c>editorid =</c> that matched nothing: the winner lane filters on
/// the winner's body, so a name a losing copy carries reads as a clean zero. One sentence when the order holds
/// such a record, nothing at all when it does not (#669).</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class WhereNearMissTests : RecordsTestBase
{
    public WhereNearMissTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void ARenamedRecordNamesTheLosingCopyAndTheWinnersOwnEditorId()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" });
        Assert.Contains("near miss", r);
        Assert.Contains(W.MasterName, r);                              // the plugin whose losing copy carries the name asked for
        Assert.Contains(W.OverrideName, r);                            // the winner
        Assert.Contains(RecordsWorld.RenamedArmorNewEid, r);           // the name that WOULD match
        Assert.Contains(Fid(W.RenamedArmor), r);
    }

    [Fact]
    public void AnEditoridNoPluginCarriesGetsNoSentence_ThePlainZeroRowResultStands()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" }, where: new[] { "editorid = HcRecNoSuchArmorAnywhere" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AMatchingScanIsUntouched_TheHintOnlyRunsOnZeroRows()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorNewEid}" });
        Assert.DoesNotContain("near miss", r);
        Assert.Contains(RecordsWorld.RenamedArmorNewEid, r);
    }

    [Fact]
    public void AContainsTermGetsNoSentence_TheHintIsForTheExactSpellingOnly()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid contains {RecordsWorld.RenamedArmorOldEid}" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AWinnerThatDroppedTheEditorIdGetsTheFormIdRemedy_NotAskForThatName()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.DroppedEidArmorOldEid}" });
        Assert.Contains("near miss", r);
        Assert.Contains("NO EditorID", r);
        Assert.Contains("ask by FormID", r);
        Assert.Contains(Fid(W.DroppedEidArmor), r);
        Assert.DoesNotContain("names it", r);          // there is no name to re-ask for
    }

    // ---- the gate: only where the sentence's one cause is the only cause ---------------------------

    [Fact]
    public void ASecondPredicateGetsNoSentence_TheZeroHasAnotherCandidateCause()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}", "Value > 9000" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AFormidSetGetsNoSentence_TheCallerAskedAboutNamedRecords()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Armor) },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void ConflictsOnlyGetsNoSentence_TheZeroCouldBeTheDepthFilter()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" }, conflicts_only: true,
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AReferencesFilterGetsNoSentence_TheZeroCouldBeTheLinkFilter()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" }, references: new[] { Fid(W.MgefA) },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AReferencesNoneFilterGetsNoSentence_TheZeroCouldBeTheExclusion()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" }, references: new[] { "!" + Fid(W.MgefA) },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void AnAggregateGetsNoSentence_TheGroupedAnswerCarriesNoScanNoteRow()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorOldEid}" },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" });
        Assert.DoesNotContain("near miss", r);
    }

    [Fact]
    public void APluginScopeGetsNoSentence_TheScopedLaneAlreadyReadsEachPluginsOwnBody()
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.MasterName), types: new[] { "ARMO" },
                                     where: new[] { $"editorid = {RecordsWorld.RenamedArmorNewEid}" });
        Assert.DoesNotContain("near miss", r);
    }
}
