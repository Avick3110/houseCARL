using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>walk.inherit resolving NPC template inheritance on the reverse walk: crossing a Template link from an NPC
/// the category carried where the heir's flag is set, masking the heir's own fields for it, and unset leaving the
/// walk as it was.</summary>
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
        // The list is crossed, not reached: it has its own count and is not a row or left out.
        Assert.DoesNotContain("HcIwLvln", r);
        Assert.Contains("crossed for template inheritance (Inventory): 1 LeveledNpc", r);
        Assert.DoesNotContain("LeveledNpc", r.Split('\n').Single(l => l.StartsWith("left out, not in walk.through:")));
    }

    [Fact]
    public void AnNpcReachedOnlyThroughItsDeathItemPassesNothingToUseInventoryNpcsTemplatedOnIt()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.Contains("HcIwDeathCarrier", r);
        Assert.DoesNotContain("HcIwDeathHeir", r);
    }

    [Fact]
    public void ALaterPluginsOverrideSettingTheFlagIsWhatTheWalkJudges()
    {
        var r = Reverse(new[] { "Inventory" });
        Served(r);
        Assert.Contains("HcIwPatchedHeir", r);
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
    public void AForwardWalkRefusesNamingTheChainForm()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Heir) },
                                     walk: new RecordsTools.RecordsWalk { depth = 8, inherit = new[] { "Inventory" } });
        Assert.StartsWith("error: walk.inherit is reverse-only", r);
        Assert.Contains("walk.follow=\"Template\"", r);
    }

    [Fact]
    public void TwoCategoriesRefuseAskingForOneCallPerCategory()
    {
        var r = Reverse(new[] { "Inventory", "Factions" });
        Assert.StartsWith("error:", r);
        Assert.Contains("one call per category", r);
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
        Assert.Contains("\"walk_crossed\"", r);
        Assert.Contains("masked by template (Inventory): 1 Npc", r);
    }
}
