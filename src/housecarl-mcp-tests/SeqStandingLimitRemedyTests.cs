using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_write_seq</c>'s standing-limit sentence names a call, and the cut changed which call: it used
/// to say "use housecarl_validate_dialogue", and the repair pointed it at <c>housecarl_check
/// findings=["dialogue"]</c>.
///
/// <para>Driven, that repair was incomplete. The dialogue family REFUSES a call with no <c>seeds=</c> — it
/// validates the topics and quests the caller names and deliberately will not sweep the whole order — so a
/// caller following the sentence as written landed on a second refusal. The sentence now names
/// <c>seeds=</c>, and both directions are held here: with seeds the call is accepted, without them it is
/// refused, which is what makes the clause load-bearing rather than decoration.</para>
///
/// <para>The seed here is not a quest, so the response carries a per-seed error — that is the family's normal
/// output and is not a refusal of the CALL. What this arm asserts is the parameter journey the sentence
/// promises: the call is accepted, and the tool does not send the caller back for a missing parameter.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class SeqStandingLimitRemedyTests : IClassFixture<CheckWorldFixture>
{
    readonly CheckWorld W;
    public SeqStandingLimitRemedyTests(CheckWorldFixture f) => W = f.W;

    /// <summary>The sentence's own text is the subject, read off the shipped constant rather than retyped.</summary>
    [Fact]
    public void TheSentenceNamesSeeds()
    {
        Assert.Contains("seeds=", WriteSentences.Twins.SeqStandingLimit);
        Assert.Contains(ToolNames.Check, WriteSentences.Twins.SeqStandingLimit);
    }

    /// <summary>What this arm can honestly claim, and what it cannot. This fixture has no DIAL/QUST/DLVW/DLBR
    /// record, so it cannot show a content-valid seed being SERVED. What it does show is the exact defect the
    /// repair was for: with <c>seeds=</c> present the tool no longer sends the caller back for the missing
    /// parameter — the response is about the seed's CONTENT, which is a caller-supplied fact, not about the
    /// shape of the call the sentence told them to make.</summary>
    [Fact]
    public void TheCallTheSentenceNames_WithSeeds_IsNotSentBackForAMissingParameter()
    {
        var r = CheckTools.CheckTool(W.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { "000800:" + W.BaseMasterName });

        Assert.DoesNotContain("needs seeds=", r);
        Assert.Contains("seed(s) named", r);        // it reached the per-seed stage
    }

    [Fact]
    public void TheSameCallWithoutSeeds_IsRefused_SoTheClauseIsLoadBearing()
    {
        var r = CheckTools.CheckTool(W.Svc, findings: new[] { "dialogue" });

        Assert.StartsWith("error:", r);
        Assert.Contains("seeds=", r);
    }
}
