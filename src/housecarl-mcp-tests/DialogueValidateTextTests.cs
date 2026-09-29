using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's player-facing text lints: non-ASCII characters and &lt;Global=X&gt; tags the
/// owning quest's TextDisplayGlobals does not cover. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateTextTests
{
    static bool IsGlobalTag(DialogueIssue i) => i.Message.Contains("TextDisplayGlobals", StringComparison.Ordinal);

    // TEXT-MOJIBAKE: non-ASCII in the Prompt and a response Text warns 'non-ASCII' for each.
    [Fact]
    public void NonAsciiPlayerTextWarnsForEachString()
        => Assert.True(DialogueValidateWorld.Topic("text-mojibake").Issues.Count(i => i.Severity == DialogueIssueSeverity.Warning
            && i.Message.Contains("non-ASCII", StringComparison.OrdinalIgnoreCase)) >= 2);

    // TEXT-CLEAN: pure-ASCII player text is not flagged.
    [Fact]
    public void AsciiPlayerTextIsNotFlagged()
        => Assert.DoesNotContain(DialogueValidateWorld.Topic("text-clean").Issues, i => i.Message.Contains("non-ASCII", StringComparison.OrdinalIgnoreCase));

    // GLOBAL-TAG-OK: a <Global=X> covered by TextDisplayGlobals is not flagged.
    [Fact]
    public void ACoveredGlobalTagIsNotFlagged()
        => Assert.DoesNotContain(DialogueValidateWorld.Topic("global-tag-ok").Issues, IsGlobalTag);

    // GLOBAL-TAG-MISSING: a <Global=X> not in TextDisplayGlobals warns, naming X.
    // GLOBAL-TAG-SUBTAG: the <Global.Time=X> subtag form with an uncovered global warns too.
    [Theory]
    [InlineData("global-tag-missing")]
    [InlineData("global-tag-subtag")]
    public void AnUncoveredGlobalTagWarnsNamingTheGlobal(string seed)
        => Assert.Contains(DialogueValidateWorld.Topic(seed).Issues, i => i.Severity == DialogueIssueSeverity.Warning
            && IsGlobalTag(i) && i.Message.Contains("HcDvUnknownGlobal", StringComparison.Ordinal));
}
