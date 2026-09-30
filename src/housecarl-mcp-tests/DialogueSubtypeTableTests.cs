using HousecarlCore;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The DialogTopic subtype-to-SNAM-marker table: its shape, its known anchors, and every Mutagen subtype at its
/// own row. Migrated from the dialogue-subtype-marker-guard probe's table arms.</summary>
[Trait("tier", "unit")]
public sealed class DialogueSubtypeTableTests
{
    // TABLE-SHAPE 103 contiguous 4-char distinct markers
    [Fact]
    public void TheTableHas103ContiguousDistinctFourCharMarkers()
    {
        Assert.Equal(103, DialogueSubtype.Count);
        var tags = Enumerable.Range(0, DialogueSubtype.Count).Select(i => DialogueSubtype.MarkerFor(i)).ToList();
        Assert.All(tags, t => Assert.Equal(4, t?.Length));
        Assert.Equal(tags.Count, tags.Distinct().Count());
    }

    // TABLE-SHAPE ...rangeSafe: out of range answers null
    [Fact]
    public void AnIndexOutsideTheTableHasNoMarker()
    {
        Assert.Null(DialogueSubtype.MarkerFor(-1));
        Assert.Null(DialogueSubtype.MarkerFor(DialogueSubtype.Count));
    }

    // TABLE-ANCHOR common markers correct
    [Theory]
    [InlineData(DialogTopic.SubtypeEnum.Custom, "CUST")]
    [InlineData(DialogTopic.SubtypeEnum.Hello, "HELO")]
    [InlineData(DialogTopic.SubtypeEnum.Goodbye, "GBYE")]
    [InlineData(DialogTopic.SubtypeEnum.Idle, "IDLE")]
    [InlineData(DialogTopic.SubtypeEnum.ForceGreet, "PFGT")]
    [InlineData(DialogTopic.SubtypeEnum.Rumors, "RUMO")]
    [InlineData(DialogTopic.SubtypeEnum.Scene, "SCEN")]
    public void AnAnchorSubtypeMapsToItsMarker(DialogTopic.SubtypeEnum subtype, string marker)
        => Assert.Equal(marker, DialogueSubtype.MarkerFor(subtype));

    // TABLE-NAMES all Mutagen SubtypeEnum values map to their own row
    [Fact]
    public void EveryMutagenSubtypeSitsAtItsOwnRowWithAMarker()
    {
        var drift = Enum.GetValues<DialogTopic.SubtypeEnum>()
            .Where(v => DialogueSubtype.MarkerFor(v) is null || DialogueSubtype.NameAt((int)v) != v.ToString())
            .Select(v => $"{v}(={(int)v}) name='{DialogueSubtype.NameAt((int)v)}'")
            .ToList();
        Assert.Empty(drift);
    }
}
