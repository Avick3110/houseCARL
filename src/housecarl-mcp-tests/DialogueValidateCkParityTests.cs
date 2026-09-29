using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's CK-parity checks on an INFO and on DLVW, DLBR and QUST inputs, each gap
/// against a complete control. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateCkParityTests
{
    // Keyed on the gap's head ("... is missing the X subrecord"): the ENAM gap's detail mentions DNAM.
    static bool Gap(DialogueIssue i, string sig)
        => i.Severity == DialogueIssueSeverity.Warning && i.Message.Split(" — ")[0].Contains(sig, StringComparison.Ordinal);

    static bool Warns(IEnumerable<DialogueIssue> issues, string sig) => issues.Any(i => Gap(i, sig));

    // INFO-CKPARITY-GAP: a live INFO missing CNAM and ENAM warns twice, naming each and the 'Creation Kit'.
    [Theory]
    [InlineData("CNAM")]
    [InlineData("ENAM")]
    public void ABareInfoWarnsItsMissingSubrecord(string sig)
        => Assert.Contains(DialogueValidateWorld.Topic("info-ckparity-gap").Issues, i => Gap(i, sig)
            && i.Message.Contains("Creation Kit", StringComparison.Ordinal));

    // INFO-CKPARITY-OK: a CK-parity-complete INFO is not flagged for CNAM or ENAM.
    [Fact]
    public void ACompleteInfoIsNotFlagged()
    {
        var t = DialogueValidateWorld.Topic("info-ckparity-ok");
        Assert.Equal(1, t.InfoCount);
        Assert.DoesNotContain(t.Issues, i => i.Message.Contains("CNAM", StringComparison.Ordinal) || i.Message.Contains("ENAM", StringComparison.Ordinal));
    }

    // VIEW-CKPARITY-GAP: a bare DLVW is kind "view", zero topics, and warns DNAM and ENAM.
    [Fact]
    public void ABareDialogViewWarnsDnamAndEnam()
    {
        var r = DialogueValidateWorld.Report("view-gap");
        Assert.Equal("view", r.InputKind);
        Assert.Null(r.Error);
        Assert.Empty(r.Topics);
        Assert.True(Warns(r.InputIssues, "DNAM"));
        Assert.True(Warns(r.InputIssues, "ENAM"));
    }

    // VIEW-CKPARITY-OK: a CK-parity-complete DLVW reports no input issues.
    [Fact]
    public void ACompleteDialogViewReportsNoInputIssues()
    {
        var r = DialogueValidateWorld.Report("view-ok");
        Assert.Equal("view", r.InputKind);
        Assert.Null(r.Error);
        Assert.Empty(r.InputIssues);
    }

    // BRANCH-CKPARITY-GAP: a bare DLBR is kind "branch", zero topics, and warns TNAM and DNAM.
    [Fact]
    public void ABareDialogBranchWarnsTnamAndDnam()
    {
        var r = DialogueValidateWorld.Report("branch-gap");
        Assert.Equal("branch", r.InputKind);
        Assert.Null(r.Error);
        Assert.Empty(r.Topics);
        Assert.True(Warns(r.InputIssues, "TNAM"));
        Assert.True(Warns(r.InputIssues, "DNAM"));
    }

    // BRANCH-CKPARITY-OK: a CK-parity-complete DLBR with Flags set reports no input issues.
    [Fact]
    public void ACompleteDialogBranchReportsNoInputIssues()
    {
        var r = DialogueValidateWorld.Report("branch-ok");
        Assert.Equal("branch", r.InputKind);
        Assert.Null(r.Error);
        Assert.Empty(r.InputIssues);
    }

    // QUST-CKPARITY-GAP: a quest lacking ANAM with a Flags-less objective warns each once, at quest level.
    [Fact]
    public void AQuestLackingAnamAndObjectiveFlagsWarnsEachOnce()
    {
        var r = DialogueValidateWorld.Report("quest-gap");
        Assert.Equal("quest", r.InputKind);
        Assert.Null(r.Error);
        Assert.Single(r.InputIssues, i => Gap(i, "ANAM"));
        Assert.Single(r.InputIssues, i => Gap(i, "FNAM"));
    }

    // QUST-CKPARITY-OK: a CK-parity-complete quest reports no input issues.
    [Fact]
    public void ACompleteQuestReportsNoInputIssues()
    {
        var r = DialogueValidateWorld.Report("quest-ok");
        Assert.Equal("quest", r.InputKind);
        Assert.Null(r.Error);
        Assert.Empty(r.InputIssues);
    }
}
