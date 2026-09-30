using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.ClosureCopySourceGraph;

namespace HousecarlMcpTests;

/// <summary>Attach sets the walked seed fields on a target with the internalized keys substituted, touches nothing
/// else in the patch, and refuses a target inside the source universe. Migrated from <c>closure-copy-guard</c> (ATTACH).</summary>
[Trait("tier", "unit")]
public sealed class ClosureCopyAttachTests
{
    static readonly FormKey KeptArmor = new(Keep, 0x902);

    /// <summary>The donor NPC: its head part at the DONOR key, and a worn armor outside the bound universe.</summary>
    static Npc Donor()
    {
        var npc = new Npc(SrcNpc, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
        npc.HeadParts.Add(Hp);
        npc.WornArmor.SetTo(KeptArmor);
        return npc;
    }

    /// <summary>A target in the patch carrying a pre-existing head part, attached from the donor.</summary>
    static (ClosureCopySourceGraph G, Npc Target, StripResult Result) Attach()
    {
        var g = new ClosureCopySourceGraph();
        var target = new Npc(new FormKey(PatchKey, 0x950), SkyrimRelease.SkyrimSE) { EditorID = "AttachTarget" };
        target.HeadParts.Add(new FormKey(Keep, 0x9FF));
        g.Patch.Npcs.Add(target);
        var r = ClosureCopy.AttachSeedFields(target, Donor(), new[] { "HeadParts", "WornArmor" }, g.Copy.Map, IsBound);
        return (g, target, r);
    }

    // attach succeeds
    [Fact]
    public void AttachingTwoSeedFieldsSucceeds()
    {
        var (_, _, r) = Attach();
        Assert.True(r.Success, r.Refusal?.Detail);
    }

    // ATTACH: a list seed is set to the INTERNALIZED key, not the donor's
    [Fact]
    public void AListSeedIsSetToTheInternalizedKey()
    {
        var (g, target, _) = Attach();
        Assert.Equal(g.Copy.Map[Hp], Assert.Single(target.HeadParts).FormKey);
    }

    // ...a single-link seed outside the copied set keeps its own key (nothing to substitute)
    [Fact]
    public void ASingleLinkSeedOutsideTheCopyIsSetToItsOwnKey()
    {
        var (_, target, r) = Attach();
        Assert.Equal(KeptArmor, target.WornArmor.FormKey);
        Assert.Contains(r.Stripped, s => s.Field == "WornArmor" && s.Removed == KeptArmor.ToString());
    }

    // ...and the attached target carries NO link into the source universe
    [Fact]
    public void TheAttachedTargetHasNoLeak()
    {
        var (_, target, _) = Attach();
        Assert.Null(ClosureCopy.FindBoundLeak(target, IsBound));
    }

    // ATTACH SCOPING (end-to-end): after internalize AND attach, the patch's pre-existing deliberate reference is STILL untouched
    [Fact]
    public void AfterAttachTheRecordAlreadyInThePatchStillPointsAtTheSource()
    {
        var (g, _, _) = Attach();
        Assert.Equal(Txst, g.HeadPartAt(Prior).TextureSet.FormKey);
    }

    // a target INSIDE the source universe REFUSES: no standalone-izing a record onto itself
    [Fact]
    public void ATargetInsideTheSourceRefuses()
    {
        var g = new ClosureCopySourceGraph();
        var self = new Npc(new FormKey(Src, 0x950), SkyrimRelease.SkyrimSE) { EditorID = "SelfTarget" };
        var r = ClosureCopy.AttachSeedFields(self, Donor(), new[] { "HeadParts" }, g.Copy.Map, IsBound);
        Assert.False(r.Success);
        Assert.Equal(CopyRefusalKind.DonorLeak, r.Refusal?.Kind);
    }

    // ...naming the offending target; ...and the refusal changed nothing on it
    [Fact]
    public void TheSelfTargetRefusalNamesTheTargetAndLeavesItEmpty()
    {
        var g = new ClosureCopySourceGraph();
        var self = new Npc(new FormKey(Src, 0x950), SkyrimRelease.SkyrimSE) { EditorID = "SelfTarget" };
        var r = ClosureCopy.AttachSeedFields(self, Donor(), new[] { "HeadParts" }, g.Copy.Map, IsBound);
        Assert.Equal(self.FormKey, r.Refusal?.Key);
        Assert.Empty(self.HeadParts);
    }
}
