using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's graph checks: quest and branch wiring, LinkTo targets, a set-but-dangling PNAM,
/// and deleted INFOs. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateGraphTests
{
    static bool Has(TopicValidation t, DialogueIssueSeverity sev, string word)
        => t.Issues.Any(i => i.Severity == sev && i.Message.Contains(word, StringComparison.Ordinal));

    // CLEAN: a well-formed topic (empty PNAM, valid LinkTo) reports zero issues.
    [Fact]
    public void AWellFormedTopicWithEmptyPnamAndAValidLinkToReportsNoIssues()
    {
        var t = DialogueValidateWorld.Topic("clean");
        Assert.Equal(2, t.InfoCount);
        Assert.Empty(t.Issues);
    }

    // LINKTO-DANGLE: a LinkTo to a missing topic is a Problem naming 'LinkTo'.
    [Fact]
    public void ALinkToAMissingTopicIsAProblem()
        => Assert.True(Has(DialogueValidateWorld.Topic("linkto-dangle"), DialogueIssueSeverity.Problem, "LinkTo"));

    // PNAM-DANGLE: a set PreviousDialog resolving to no INFO is a Problem naming 'PNAM'.
    [Fact]
    public void ASetPreviousLinkToNoInfoIsAProblem()
        => Assert.True(Has(DialogueValidateWorld.Topic("pnam-dangle"), DialogueIssueSeverity.Problem, "PNAM"));

    // PNAM-RESOLVES: a set previous-link to a real sibling INFO is not flagged.
    [Fact]
    public void APreviousLinkToARealSiblingIsNotFlagged()
    {
        var t = DialogueValidateWorld.Topic("pnam-resolves");
        Assert.Equal(2, t.InfoCount);
        Assert.Empty(t.Issues);
    }

    // DELETED-SKIP: a deleted INFO is not counted live, is tallied deleted, and gets no findings.
    [Fact]
    public void ADeletedInfoIsSkippedAndTallied()
    {
        var t = DialogueValidateWorld.Topic("deleted");
        Assert.Equal(1, t.InfoCount);
        Assert.Equal(1, t.DeletedInfoCount);
        Assert.Empty(t.ScriptFindings);
        Assert.Empty(t.VoiceLines);
        Assert.Empty(t.Issues);
    }

    // NO-QUEST: a topic with no Quest warns 'Quest'.
    [Fact]
    public void ATopicWithNoQuestIsFlagged()
        => Assert.Contains(DialogueValidateWorld.Topic("no-quest").Issues, i => i.Message.Contains("Quest", StringComparison.Ordinal));

    // BAD-BRANCH: a Branch pointing at no DLBR is a Problem naming 'Branch'.
    [Fact]
    public void ABranchResolvingToNoDialogBranchIsAProblem()
        => Assert.True(Has(DialogueValidateWorld.Topic("bad-branch"), DialogueIssueSeverity.Problem, "Branch"));
}
