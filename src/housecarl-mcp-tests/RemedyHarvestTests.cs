using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The harvest's lane discriminant, pinned. <see cref="RemedyHarvest.HarvestLane"/> decides how a response is
/// read; getting that wrong narrows a wrong-lever grid's subject set, which goes green either way.
/// </summary>
[Trait("tier", "unit")]
public sealed class RemedyHarvestTests
{
    /// <summary>
    /// A JSON render is walked as a document whatever its lane is called. <c>dense</c> is the live case — a
    /// text-sounding name on a <c>JsonWire</c> render — and a discriminant keyed on the name "json" dropped
    /// every dense sentence that carried no <see cref="RemedyHarvest.RemedyLine"/> keyword.
    /// </summary>
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
