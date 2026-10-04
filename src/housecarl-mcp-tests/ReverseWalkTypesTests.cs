using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>walk.through and walk.exclusions shaping a walk: which types it expands through and where it stops.</summary>
[Trait("tier", "integration")]
public sealed class ReverseWalkTypesTests : IClassFixture<ReverseWalkTypesWorld>
{
    readonly ReverseWalkTypesWorld W;
    public ReverseWalkTypesTests(ReverseWalkTypesWorld w) => W = w;

    static string Fid(Mutagen.Bethesda.Plugins.FormKey fk) => ReverseWalkTypesWorld.Fid(fk);

    static RecordsTools.RecordsWalkExclusion Stop(string type) => new() { match = type, severity = "stop" };

    string Reverse(string[]? through, params RecordsTools.RecordsWalkExclusion[] exclusions) =>
        RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Cuirass) },
                             walk: new RecordsTools.RecordsWalk
                             {
                                 direction = "reverse", depth = 6, through = through,
                                 exclusions = exclusions.Length > 0 ? exclusions : null,
                             });

    static void Served(string r, params string[] names)
    {
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        foreach (var n in names) Assert.Contains(n, r);
    }

    [Fact]
    public void AReverseWalkThroughListsAndOutfitsReachesTheNpcAndLeavesTheRecipeOut()
    {
        var r = Reverse(new[] { "LeveledItem", "Outfit", "LeveledNpc" }, Stop("Npc"), Stop("Container"));
        Served(r, "HcRwtList", "HcRwtOutfit", "HcRwtGuard", "HcRwtChest", "left out, not in walk.through: 1 ConstructibleObject");
        Assert.DoesNotContain("HcRwtRecipe", r);
        Assert.DoesNotContain("HcRwtGuardList", r);
    }

    [Fact]
    public void AJsonReverseWalkCarriesTheLeftOutAndBoundaryCountsInItsEnvelope()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Cuirass) }, format: "json",
                                     walk: new RecordsTools.RecordsWalk
                                     {
                                         direction = "reverse", depth = 6,
                                         through = new[] { "LeveledItem", "Outfit", "LeveledNpc" },
                                         exclusions = new[] { Stop("Npc"), Stop("Container") },
                                     });
        Served(r, "\"walk_left_out\"", "1 ConstructibleObject", "\"walk_boundaries\"", "2 reached record(s) matched a stop exclusion");
    }

    [Fact]
    public void AForwardWalkNeverCountsASeedAnotherSeedReachesAsLeftOut()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Guard), Fid(W.Outfit) },
                                     walk: new RecordsTools.RecordsWalk { depth = 6, through = new[] { "LeveledItem" } });
        Served(r, "HcRwtGuard", "HcRwtOutfit", "HcRwtList");
        var leftOut = r.Split('\n').Where(l => l.Contains("left out, not in walk.through:")).ToList();
        Assert.NotEmpty(leftOut);
        Assert.All(leftOut, l => Assert.EndsWith("left out, not in walk.through: 1 Armor", l.TrimEnd('\r')));
    }

    [Fact]
    public void AStopOnAReverseWalkWithoutThroughKeepsTheBoundaryAndDoesNotExpandPastIt()
    {
        var r = Reverse(null, Stop("Npc"));
        Served(r, "HcRwtGuard", "HcRwtRecipe");
        Assert.DoesNotContain("HcRwtGuardList", r);
        Assert.DoesNotContain("left out", r);
    }

    [Fact]
    public void ARefuseOnAReverseWalkFailsTheCall()
    {
        var r = Reverse(null, new RecordsTools.RecordsWalkExclusion { match = "NPC_", severity = "refuse" });
        Assert.StartsWith("error:", r);
        Assert.Contains("refuse", r);
        Assert.Contains("Npc", r);
    }

    [Fact]
    public void ThroughOnAForwardWalkExpandsOnlyThoseTypesAndCountsTheRest()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Guard) },
                                     walk: new RecordsTools.RecordsWalk { depth = 6, through = new[] { "OTFT", "LVLI" } });
        Served(r, "HcRwtOutfit", "HcRwtList", "left out, not in walk.through: 1 Armor");
        Assert.DoesNotContain("HcRwtCuirass", r);
    }

    [Fact]
    public void AReverseWalkWithNothingSetReachesEveryReferrerAsBefore()
    {
        var r = Reverse(null);
        Served(r, "HcRwtRecipe", "HcRwtList", "HcRwtOutfit", "HcRwtGuard", "HcRwtGuardList", "HcRwtChest");
        Assert.DoesNotContain("left out", r);
        Assert.DoesNotContain("boundaries", r);
    }

    [Theory]
    [InlineData("through")]
    [InlineData("exclusions")]
    public void ThroughOrExclusionsOnTheMgefCarrierWalkRefuses(string field)
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Cuirass) },
                                     walk: new RecordsTools.RecordsWalk
                                     {
                                         direction = "reverse", follow = "Effects[].BaseEffect",
                                         through = field == "through" ? new[] { "Outfit" } : null,
                                         exclusions = field == "exclusions" ? new[] { Stop("Npc") } : null,
                                     });
        Assert.StartsWith("error:", r);
        Assert.Contains("walk.exclusions/through shape a walk that expands", r);
        Assert.Contains("types=", r);
    }

    [Fact]
    public void SeedPathsOnAReverseWalkRefusesAndKeepsFollow()
    {
        var r = RecordsTools.Records(W.Svc, formids: new[] { Fid(W.Cuirass) },
                                     walk: new RecordsTools.RecordsWalk
                                     {
                                         direction = "reverse", seed_paths = new[] { "Keywords" },
                                     });
        Assert.StartsWith("error:", r);
        Assert.Contains("walk.seed_paths shapes a FORWARD expansion", r);
        Assert.Contains("walk.follow stays", r);
    }

    [Theory]
    [InlineData("through")]
    [InlineData("exclusions")]
    public void AnUnknownWalkTypeRefuses(string field)
    {
        var r = field == "through" ? Reverse(new[] { "Outfit", "Bogus" }) : Reverse(null, Stop("Bogus"));
        Assert.StartsWith("error:", r);
        Assert.Contains($"walk.{field} 'Bogus'", r);
    }
}
