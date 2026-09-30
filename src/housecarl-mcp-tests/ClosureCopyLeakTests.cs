using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.ClosureCopySourceGraph;

namespace HousecarlMcpTests;

/// <summary>The leak check finds a link into the source universe that survived, and only that: a pre-existing
/// dangling link elsewhere is not this copy's leak. Migrated from <c>closure-copy-guard</c> (LEAK SCOPING).</summary>
[Trait("tier", "unit")]
public sealed class ClosureCopyLeakTests
{
    // LEAK (true positive): a surviving bound link is found and named
    [Fact]
    public void ASurvivingSourceLinkIsFoundAndNamed()
    {
        var leaky = new Npc(new FormKey(PatchKey, 0x901), SkyrimRelease.SkyrimSE) { EditorID = "Leaky" };
        leaky.DefaultOutfit.SetTo(OutfitKey);
        Assert.Equal(OutfitKey, ClosureCopy.FindBoundLeak(leaky, IsBound));
    }

    // LEAK (false positive): a PRE-EXISTING dangling link is NOT a leak; the check is scoped to bound keys
    [Fact]
    public void ADanglingLinkOutsideTheSourceIsNotALeak()
    {
        var dirty = new Npc(new FormKey(PatchKey, 0x902), SkyrimRelease.SkyrimSE) { EditorID = "Dirty" };
        dirty.DefaultOutfit.SetTo(new FormKey(new ModKey("GoneAway", ModType.Plugin), 0xABC));
        Assert.Null(ClosureCopy.FindBoundLeak(dirty, IsBound));
    }

    // ...and a properly stripped record is clean
    [Fact]
    public void AStrippedRecordHasNoLeak()
    {
        var clone = new Npc(new FormKey(PatchKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Clone" };
        clone.DefaultOutfit.SetTo(OutfitKey);
        clone.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(FactionKey), Rank = 0 });
        clone.Class.SetTo(new FormKey(Keep, 0x901));
        Assert.True(ClosureCopy.StripBoundLinks(clone, IsBound).Success);
        Assert.Null(ClosureCopy.FindBoundLeak(clone, IsBound));
    }
}
