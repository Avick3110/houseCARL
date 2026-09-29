using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The closure copy's refusals and seed-shape handling, driven through <c>LoadOrderService.CopyClosure</c>:
/// the off-order link told apart by cause, the post-attach leak check, a 'stop' the clone lane can write, and an
/// unset seed CLEARING the target's value on disk.</summary>
[Trait("tier", "integration")]
public sealed class CopyServiceRefusalTests : IDisposable
{
    readonly CopyServiceWorld _w = new();
    public void Dispose() => _w.Dispose();

    // probe arm 7a: 'stop' on an OFF-ORDER record refuses up front, naming the plugin the game does not load, and
    // writes nothing (C11)
    [Fact]
    public void StopOnAnOffOrderRecordRefusesUpFrontNamingThePluginAndWritesNothing()
    {
        var o = _w.Copy(_w.WideNpc, new[] { "Src.esp", "Extra.esp" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.StopHeadParts, _w.TargetNpc, null, "StopOffPatch", null);

        Assert.False(o.Success);
        Assert.Equal(CopyRefusalKind.StopOffOrder, o.CopyRefusal?.Kind);
        Assert.Contains(o.CopyRefusal?.Detail, new[] { "Src.esp", "Extra.esp" });
        Assert.False(_w.AnyFolderNamed("StopOffPatch"));
    }

    // probe arm 7b: an ON-ORDER pruned link is CAUGHT by the post-attach leak check, and the refusal carries WHY, so
    // the render blames the exclusion rather than the caller's target
    [Fact]
    public void AnOnOrderPrunedLinkIsCaughtByTheLeakCheckMarkedAsTheExclusions()
    {
        var o = _w.Copy(_w.ShadowNpc, new[] { "Shadow.esp" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.StopHeadParts, _w.TargetNpc, null, "LeakPatch", null);

        Assert.False(o.Success);
        Assert.Equal(CopyRefusalKind.DonorLeak, o.CopyRefusal?.Kind);
        Assert.Equal(ClosureCopy.ExclusionLeakMarker, o.CopyRefusal?.Field);
    }

    // probe arm 7f: 'stop' on an off-order record WRITES in the clone lane; the response does NOT also report it as
    // kept; the strip list is where that link is reported instead
    [Fact]
    public void StopOnAnOffOrderRecordWritesInTheCloneLaneAndReportsTheLinkOnlyAsStripped()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.StopHeadParts, null, "StopCloneEid", "StopClonePatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.DoesNotContain("pruned by exclude_types", CopyTools.Render(o));
        Assert.Contains(o.Stripped, s => s.Field.StartsWith("HeadParts", StringComparison.Ordinal));
    }

    ClosureCopyOutcome Carried() => _w.Copy(_w.WideNpc, new[] { "Src.esp", "Extra.esp" }, CopyServiceWorld.HeadParts,
        CopyServiceWorld.NoExclusions, null, "CarriedOffOrderClone", "CarriedOffOrderPatch", null);

    // probe arm 7i: an off-order link CARRIED across by the copy refuses as its own kind, not as 'stop', naming the
    // plugin the game does not load and the RECORD carrying it as it exists in the patch; writes nothing (C11)
    [Fact]
    public void AnOffOrderLinkCarriedAcrossRefusesAsItsOwnKindNamingPluginAndCarrier()
    {
        var o = Carried();

        Assert.False(o.Success);
        Assert.Equal(CopyRefusalKind.CopiedOffOrderLink, o.CopyRefusal?.Kind);
        Assert.Equal("Ghost.esp", o.CopyRefusal?.Detail);
        Assert.Contains("CarriedOffOrderClone", o.CopyRefusal?.Field ?? "");
        Assert.False(_w.AnyFolderNamed("CarriedOffOrderPatch"));
    }

    // probe arm 7i: the remedy is the SEED SET, never an exclude_types argument the caller never passed
    [Fact]
    public void ACarriedOffOrderLinksRemedyIsTheSeedSetNotAnExclusion()
    {
        var text = CopyTools.Render(Carried());

        Assert.Contains("seed_paths=", text);
        Assert.DoesNotContain("'Type:refuse'", text);
    }

    // probe arm 7d (a): a struct-element seed the donor carries NONE of still refuses by shape, naming the field
    [Fact]
    public void AStructElementSeedTheDonorCarriesNoneOfRefusesByShapeNamingTheField()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, new[] { "HeadParts", "Perks" }, CopyServiceWorld.NoExclusions,
            null, "PerkClone", "PerkPatch", null);

        Assert.False(o.Success);
        Assert.Equal(WalkRefusalKind.UnsupportedSeedShape, o.WalkRefusal?.Kind);
        Assert.Contains("Perks", o.WalkRefusal?.Detail ?? "");
    }

    // probe arm 7d (b): an UNSET source link copies and CLEARS the target's rather than leaving a mixture, reported as
    // cleared, in the WRITTEN FILE, not just in the outcome object
    [Fact]
    public void AnUnsetSourceLinkClearsTheTargetsInTheWrittenFile()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, new[] { "HeadParts", "WornArmor" }, CopyServiceWorld.NoExclusions,
            _w.TargetNpc, null, "ClearLinkPatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.Attached, a => a.Field == "WornArmor" && a.Cleared);
        var wornArmorNull = CopyServiceWorld.ReadBack(o.OutPath!, back =>
            back.Npcs.FirstOrDefault(n => n.FormKey == _w.TargetNpc)?.WornArmor.IsNull);
        Assert.True(wornArmorNull);
    }

    // probe arm 7d (c): an UNSET source LIST copies, clearing the target's list, in the WRITTEN FILE — the target's
    // own keywords are gone, not merely reported gone
    [Fact]
    public void AnUnsetSourceListClearsTheTargetsInTheWrittenFile()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, new[] { "HeadParts", "Keywords" }, CopyServiceWorld.NoExclusions,
            _w.TargetNpc, null, "ClearListPatch", null);

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.Attached, a => a.Field == "Keywords" && a.Cleared);
        var keywords = CopyServiceWorld.ReadBack(o.OutPath!, back =>
        {
            var t = back.Npcs.FirstOrDefault(n => n.FormKey == _w.TargetNpc);
            Assert.NotNull(t);
            return t!.Keywords?.Count ?? 0;
        });
        Assert.Equal(0, keywords);
    }

    // probe arm 7d (d): a SUPPORTED field is never routed to housecarl_apply's zip
    [Fact]
    public void AnUnsetSupportedListIsNeverRoutedToApply()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, new[] { "HeadParts", "Keywords" }, CopyServiceWorld.NoExclusions,
            _w.TargetNpc, null, "ClearListRoute", null);

        CopyServiceWorld.Succeeded(o);
        Assert.DoesNotContain("housecarl_apply", CopyTools.Render(o));
    }
}
