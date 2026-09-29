using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's voice and script reuse: a voiced line with no .fuz, a bound fragment with no
/// .pex, and the fragment tally. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateAssetTests
{
    // VOICE-WIRED: a voiced line with no .fuz surfaces as one silent VoiceLine.
    [Fact]
    public void AVoicedLineWithNoFuzIsSilent()
        => Assert.False(Assert.Single(DialogueValidateWorld.Topic("voiced").VoiceLines).FuzPresent);

    // SCRIPT-WIRED: a bound result script with no .pex is ScriptNotCompiled.
    [Fact]
    public void ABoundFragmentWithNoPexIsNotCompiled()
        => Assert.Equal(ScriptBindingStatus.ScriptNotCompiled, Assert.Single(DialogueValidateWorld.Topic("scripted").ScriptFindings).Status);

    // FRAGMENT-PRESENCE: a line carrying a result-script fragment is HasFragment and FragmentInfoCount 1.
    [Fact]
    public void ALineWithAFragmentIsTallied()
    {
        var t = DialogueValidateWorld.Topic("scripted");
        Assert.Equal(1, t.FragmentInfoCount);
        Assert.True(Assert.Single(t.ScriptFindings).HasFragment);
    }

    // FRAGMENT-FREE: a plain voiced line is counted with FragmentInfoCount 0, not omitted.
    [Fact]
    public void APlainVoicedLineIsCountedFragmentFree()
    {
        var t = DialogueValidateWorld.Topic("voiced");
        Assert.Equal(0, t.FragmentInfoCount);
        Assert.Equal(1, t.InfoCount);
        Assert.Single(t.VoiceLines);
        Assert.Empty(t.ScriptFindings);
    }
}
