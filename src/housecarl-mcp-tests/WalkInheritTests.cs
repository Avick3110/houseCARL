using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>walk.inherit resolving NPC template inheritance: crossing a Template link where the category's flag is
/// set, masking the NPC's own fields for it, in both directions, and unset leaving the walk as it was.</summary>
[Trait("tier", "integration")]
public sealed class WalkInheritTests : IClassFixture<WalkInheritWorld>
{
    readonly WalkInheritWorld W;
    public WalkInheritTests(WalkInheritWorld w) => W = w;

    static string Fid(Mutagen.Bethesda.Plugins.FormKey fk) => WalkInheritWorld.Fid(fk);

    static RecordsTools.RecordsWalkExclusion Stop(string type) => new() { match = type, severity = "stop" };

    // "Which NPCs carry this cuirass": through lists and outfits, stop at Npc and Container.
    string Reverse(string[]? inherit, string? format = null) =>
        RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Cuirass) }, format: format,
                             walk: new RecordsTools.RecordsWalk
                             {
                                 direction = "reverse", depth = 8, through = new[] { "LeveledItem", "Outfit" },
                                 exclusions = new[] { Stop("Npc"), Stop("Container") }, inherit = inherit,
                             });

    static void Served(string r)
        => Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);

    [Fact]
    public void AReverseWalkReachesAnNpcTemplatedWithUseInventoryOnTheCarrier()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.Contains("HcIwCarrier", r);
        Assert.Contains("HcIwHeir", r);
    }

    [Fact]
    public void AReverseWalkReachesAnNpcTemplatedThroughALeveledNpcListAndCrossesTheList()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.Contains("HcIwLvlHeir", r);
        // The list is crossed, not reached: it is counted as left out and is not a row.
        Assert.DoesNotContain("HcIwLvln", r);
        Assert.Contains("left out, not in walk.through: 1 LeveledNpc", r);
    }

    [Fact]
    public void AnNpcReachedOnlyThroughAnOutfitItsUseInventoryFlagMasksIsLeftOutAndCounted()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.DoesNotContain("HcIwMasked", r);
        Assert.Contains("masked by template (Inventory): 1 Npc", r);
    }

    [Fact]
    public void WithTheFlagClearTheTemplateIsNotCrossedAndTheOwnOutfitIsFollowed()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.Contains("HcIwClearOwn", r);
        Assert.DoesNotContain("HcIwClearHeir", r);
    }

    [Fact]
    public void UnsetTheWalkFollowsRawLinksAndSaysNothingOfTemplates()
    {
        var r = Reverse(null);
        Served(r);
        Assert.Contains("HcIwMasked", r);
        Assert.Contains("HcIwClearOwn", r);
        Assert.DoesNotContain("HcIwHeir", r);
        Assert.DoesNotContain("HcIwLvlHeir", r);
        Assert.DoesNotContain("masked by template", r);
    }

    [Fact]
    public void AForwardWalkFromAnNpcWithTheFlagSetCrossesItsTemplateAndSkipsItsOwnOutfit()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Heir) },
                                     walk: new RecordsTools.RecordsWalk { depth = 8, inherit = new[] { "Inventory" } });
        Served(r);
        Assert.Contains("HcIwCarrier", r);
        Assert.Contains("HcIwCuirass", r);
        Assert.DoesNotContain("HcIwOtherOutfit", r);
        Assert.Contains("masked by template (Inventory): 1 Npc", r);
    }

    [Fact]
    public void AForwardWalkFromAnNpcWithTheFlagClearDoesNotCrossItsTemplate()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.ClearHeir) },
                                     walk: new RecordsTools.RecordsWalk { depth = 8, inherit = new[] { "Inventory" } });
        Served(r);
        Assert.Contains("HcIwClearHeir", r);
        Assert.DoesNotContain("HcIwCarrier", r);
    }

    [Fact]
    public void AForwardWalkCrossesALeveledNpcListTemplateOutsideWalkThrough()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.LvlHeir) },
                                     walk: new RecordsTools.RecordsWalk
                                     {
                                         depth = 8, inherit = new[] { "Inventory" },
                                         through = new[] { "Npc", "Outfit", "LeveledItem" },
                                     });
        Served(r);
        Assert.Contains("HcIwCarrier", r);
        Assert.Contains("HcIwList", r);
        Assert.DoesNotContain("HcIwLvln", r);
    }

    [Theory]
    [InlineData("Inventry", "is not an NPC template category")]
    [InlineData("Traits", "has no field map")]
    public void AnUnknownOrUnmappedCategoryRefusesNamingTheSupportedOnes(string category, string phrase)
    {
        var r = Reverse(new[] { category });
        Assert.StartsWith("error:", r);
        Assert.Contains(phrase, r);
        Assert.Contains("Inventory", r);
    }

    [Fact]
    public void AJsonWalkCarriesTheMaskedCountInItsEnvelope()
    {
        var r = Reverse(new[] { "Inventory" }, format: "json");
        Served(r);
        Assert.Contains("\"walk_masked\"", r);
        Assert.Contains("masked by template (Inventory): 1 Npc", r);
    }
}
