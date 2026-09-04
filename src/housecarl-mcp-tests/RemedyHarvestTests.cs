using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="RemedyHarvest.HarvestLane"/> reads a response by what it is, not by the lane's name.</summary>
[Trait("tier", "unit")]
public sealed class RemedyHarvestTests
{
    /// <summary>Every lane walks a JSON render as a document, so a sentence carrying no remedy keyword is
    /// still harvested.</summary>
    [Fact]
    public void EveryLaneReadsAJsonRenderAsADocument_NotOnlyTheLaneNamedJson()
    {
        var sentence = "it has no such parameter, and the tree is a project form";
        var document = "{\"remedy\": " + JsonSerializer.Serialize(sentence) + "}";

        // The premise: the line walk cannot see this sentence, so a lane sent down it loses the sentence.
        Assert.DoesNotMatch(RemedyHarvest.RemedyLine, sentence);
        Assert.True(RemedyHarvest.IsDocument(document));

        var lanes = RemedyHarvest.Lanes.Where(l => l != RemedyHarvest.ArtifactLane).ToList();
        Assert.NotEmpty(lanes);

        foreach (var lane in lanes)
            Assert.Contains(sentence, RemedyHarvest.HarvestLane(lane, document, null));
    }

    /// <summary>The other direction: a render that is not a document is still read line by line, and the
    /// lines that carry no remedy keyword are still dropped.</summary>
    [Fact]
    public void ARenderThatIsNotADocumentIsReadLineByLine()
    {
        var kept = "narrow with project.fields=";
        var dropped = "an ordinary line of the same render";
        var text = kept + "\n" + dropped;

        Assert.False(RemedyHarvest.IsDocument(text));

        var got = RemedyHarvest.HarvestLane("text", text, null);
        Assert.Contains(kept, got);
        Assert.DoesNotContain(dropped, got);
    }
}
