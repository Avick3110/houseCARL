using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_write_seq</c>'s standing-limit sentence tells the caller to run the dialogue check,
/// which REFUSES a call with no <c>seeds=</c> — it validates only the topics and quests named and will not
/// sweep the whole order. Both directions are held here, so the sentence's <c>seeds=</c> clause is
/// load-bearing rather than decoration.</summary>
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

    /// <summary>This fixture has no DIAL/QUST/DLVW/DLBR record, so it cannot show a content-valid seed being
    /// SERVED. What it shows is that with <c>seeds=</c> present the response is about the seed's CONTENT, not
    /// about a missing parameter.</summary>
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
