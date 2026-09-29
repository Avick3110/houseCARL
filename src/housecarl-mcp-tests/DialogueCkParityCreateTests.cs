using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.DialogueCkParityWorld;

namespace HousecarlMcpTests;

/// <summary>
/// The create path's CK-parity fills across DIAL/INFO/DLVW/DLBR/QUST, read back off the written patch, and the BNAM
/// lint, moved from the retired <c>dialogue-ckparity-guard</c> probe. A fill is reported as an op; a value the
/// author passed wins and produces no fill op. Each <c>*-WINS</c> arm is one test with the arm name in it.
/// The DLBR no-Flags refusal (DLBR-FLAGS-REFUSED) is <see cref="DialogBranchFlagsRefusalTests"/>'s.
/// </summary>
[Trait("tier", "integration")]
public sealed class DialogueCkParityCreateTests
{
    /// <summary>True when the create reported a CK-parity fill op naming <paramref name="field"/>.</summary>
    static bool FillReported(WritePatchBuilder.CreatedRecord rec, string field) =>
        rec.Ops.Any(op => op.Label.Contains(field, StringComparison.OrdinalIgnoreCase)
                          && op.Label.Contains("auto-set", StringComparison.Ordinal));

    static WritePatchBuilder.CreatedRecord Created(WritePatchBuilder.CreateOutcome o, string editorId)
    {
        Assert.True(o.Success, "refused: " + o.Error);
        return Assert.Single(o.Created, c => c.EditorId == editorId);
    }

    // INFO-AUTOFILL: a bare INFO -> FavorLevel=None + Flags (ENAM) present on disk, both reported.
    [Fact]
    public void InfoAutofill_BareInfoGetsFavorLevelNoneAndFlags_BothReported()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.CreateTopicWithInfo("HcCkpAf", Set("Prompt", "Hello there."));
        var info = Created(o, "HcCkpAfInfo");

        var (favor, hasFlags, _) = ReadInfo(o.OutputPath, info.FormKey);
        Assert.Equal(FavorLevel.None, favor);
        Assert.True(hasFlags);
        Assert.True(FillReported(info, "FavorLevel"));
        Assert.True(FillReported(info, "ENAM"));
    }

    // INFO-FAVOR-WINS: an explicit FavorLevel=Large is kept, not overridden to None; Flags still fills.
    [Fact]
    public void InfoFavorWins_ExplicitFavorLevelIsKept()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.CreateTopicWithInfo("HcCkpFv", Set("FavorLevel", "Large"));
        var info = Created(o, "HcCkpFvInfo");

        var (favor, hasFlags, _) = ReadInfo(o.OutputPath, info.FormKey);
        Assert.Equal(FavorLevel.Large, favor);
        Assert.True(hasFlags);
        Assert.False(FillReported(info, "FavorLevel"));
    }

    // INFO-FLAGS-WINS: an explicit Flags.ResetHours=3 is not reset by the fill; FavorLevel still fills.
    [Fact]
    public void InfoFlagsWins_ExplicitResetHoursIsKept()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.CreateTopicWithInfo("HcCkpFl", Set("Flags.ResetHours", "3"));
        var info = Created(o, "HcCkpFlInfo");

        var (favor, hasFlags, reset) = ReadInfo(o.OutputPath, info.FormKey);
        Assert.True(hasFlags);
        Assert.Equal(3f, reset, 3);
        Assert.Equal(FavorLevel.None, favor);
        Assert.False(FillReported(info, "ENAM"));
    }

    // DLVW-AUTOFILL: a bare DialogView -> DNAM=00, ENAM=00000000 on disk, both reported.
    [Fact]
    public void DlvwAutofill_BareViewGetsDnamAndEnam_BothReported()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogView", "HcCkpView");
        var view = Created(o, "HcCkpView");

        var (dnam, enam) = ReadView(o.OutputPath, view.FormKey);
        Assert.Equal("00", dnam);
        Assert.Equal("00000000", enam);
        Assert.True(FillReported(view, "DNAM"));
        Assert.True(FillReported(view, "ENAM"));
    }

    // DLVW-DNAM-WINS: an explicit DNAM=FF is kept, not overridden to 00; ENAM still fills.
    [Fact]
    public void DlvwDnamWins_ExplicitDnamIsKept()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogView", "HcCkpViewDnam", Set("DNAM", "FF"));
        var view = Created(o, "HcCkpViewDnam");

        var (dnam, enam) = ReadView(o.OutputPath, view.FormKey);
        Assert.Equal("FF", dnam);
        Assert.Equal("00000000", enam);
        Assert.False(FillReported(view, "DNAM"));
    }

    // DLBR-TNAM-AUTOFILL: a DialogBranch with only Flags -> Category=Player (TNAM) on disk, reported.
    [Fact]
    public void DlbrTnamAutofill_BranchWithOnlyFlagsGetsCategoryPlayer_Reported()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogBranch", "HcCkpBr", Set("Flags", "TopLevel"));
        var br = Created(o, "HcCkpBr");

        Assert.Equal(DialogBranch.CategoryType.Player, ReadBranchCategory(o.OutputPath, br.FormKey));
        Assert.True(FillReported(br, "Category (TNAM"));
    }

    // DLBR-TNAM-WINS: an explicit Category=Command is kept, not overridden to Player.
    [Fact]
    public void DlbrTnamWins_ExplicitCategoryCommandIsKept()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogBranch", "HcCkpBrCmd", Set("Category", "Command"), Set("Flags", "TopLevel"));
        var br = Created(o, "HcCkpBrCmd");

        Assert.Equal(DialogBranch.CategoryType.Command, ReadBranchCategory(o.OutputPath, br.FormKey));
        Assert.False(FillReported(br, "Category (TNAM"));
    }

    // DIAL-PNAM-AUTOFILL: a Custom topic with no Priority -> Priority=50 (PNAM) on disk, reported.
    [Fact]
    public void DialPnamAutofill_TopicWithNoPriorityGets50_Reported()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogTopic", "HcCkpPnam", Set("Subtype", "Custom"));
        var t = Created(o, "HcCkpPnam");

        Assert.Equal(50f, ReadTopicPriority(o.OutputPath, t.FormKey));
        Assert.True(FillReported(t, "Priority (PNAM"));
    }

    // DIAL-PNAM-WINS: an explicit Priority=10 is kept, not overridden to 50.
    [Fact]
    public void DialPnamWins_ExplicitPriority10IsKept()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogTopic", "HcCkpPnam10", Set("Subtype", "Custom"), Set("Priority", "10"));
        var t = Created(o, "HcCkpPnam10");

        Assert.Equal(10f, ReadTopicPriority(o.OutputPath, t.FormKey));
        Assert.False(FillReported(t, "Priority (PNAM"));
    }

    // DIAL-PNAM-WINS: an explicit Priority=0 stays 0 (author-set 0 is not unset): no fill, no op.
    [Fact]
    public void DialPnamWins_ExplicitPriorityZeroStaysZero()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("DialogTopic", "HcCkpPnam0", Set("Subtype", "Custom"), Set("Priority", "0"));
        var t = Created(o, "HcCkpPnam0");

        Assert.Equal(0f, ReadTopicPriority(o.OutputPath, t.FormKey));
        Assert.False(FillReported(t, "Priority (PNAM"));
    }

    // QUST-ANAM-EMPTY: an alias-less Quest -> NextAliasID=0 (ANAM) on disk, reported.
    [Fact]
    public void QustAnamEmpty_AliasLessQuestGetsNextAliasIdZero_Reported()
    {
        using var w = new DialogueCkParityWorld();
        var o = w.Create("Quest", "HcCkpQ");
        var q = Created(o, "HcCkpQ");

        Assert.Equal(0u, ReadQuestNextAliasId(o.OutputPath, q.FormKey));
        Assert.True(FillReported(q, "NextAliasID (ANAM"));
    }

    // BNAM-LINT: a Custom topic with no Branch -> a Warning naming Branch (BNAM) and the Dialogue Views editor.
    [Fact]
    public void BnamLint_BranchLessCustomTopicWarns()
    {
        using var w = new DialogueCkParityWorld();
        var r = w.Svc.ValidateDialogue(w.NoBranchTopic);

        var topic = Assert.Single(r.Topics);
        Assert.Contains(topic.Issues, i => i.Severity == DialogueIssueSeverity.Warning
            && i.Message.Contains("Branch (BNAM)", StringComparison.Ordinal)
            && i.Message.Contains("Dialogue Views", StringComparison.Ordinal));
    }

    // BNAM-LINT: a Custom topic WITH a branch raises no Branch (BNAM) issue.
    [Fact]
    public void BnamLint_BranchedCustomTopicDoesNotWarn()
    {
        using var w = new DialogueCkParityWorld();
        var r = w.Svc.ValidateDialogue(w.BranchedTopic);

        var topic = Assert.Single(r.Topics);
        Assert.DoesNotContain(topic.Issues, i => i.Message.Contains("Branch (BNAM)", StringComparison.Ordinal));
    }
}
