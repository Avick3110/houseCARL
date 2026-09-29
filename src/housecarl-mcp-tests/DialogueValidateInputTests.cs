using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue validator's input handling: a quest fans out to the topics it owns, and a FormID that is
/// absent or of the wrong type is a named error. From the retired <c>dialogue-validate-guard</c> probe.</summary>
[Trait("tier", "integration")]
public class DialogueValidateInputTests
{
    // QUEST-FANOUT: validating a quest fans out to exactly the two topics it owns, kind "quest".
    [Fact]
    public void AQuestFansOutToTheTopicsItOwns()
    {
        var r = DialogueValidateWorld.Report("fanout");
        Assert.Equal("quest", r.InputKind);
        Assert.Null(r.Error);
        Assert.Null(r.CheckError);
        Assert.Equal(2, r.Topics.Count);
    }

    // REJ-NOTFOUND: a FormID not in the order is 'not in the active load order', naming DIAL/QUST/DLVW/DLBR.
    // REJ-WRONGTYPE: a Weapon FormID is 'not a dialogue topic', naming DIAL/QUST/DLVW/DLBR.
    [Theory]
    [InlineData("not-found", "not in the active load order")]
    [InlineData("weapon", "not a dialogue topic")]
    public void ANonDialogueInputIsANamedErrorNamingEveryAcceptedKind(string seed, string word)
    {
        var r = DialogueValidateWorld.Report(seed);
        Assert.Equal("error", r.InputKind);
        Assert.Empty(r.Topics);
        Assert.Contains(word, r.Error, StringComparison.OrdinalIgnoreCase);
        foreach (var kind in new[] { "DIAL", "QUST", "DLVW", "DLBR" })
            Assert.Contains(kind, r.Error, StringComparison.Ordinal);
    }
}
