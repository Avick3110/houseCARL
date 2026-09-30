using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.ClosureCopySourceGraph;

namespace HousecarlMcpTests;

/// <summary>Internalize copies a walk's reached set into a patch under fresh keys off the patch's own counter,
/// remaps the links among the copies, and leaves a record already in the patch alone. Migrated from
/// <c>closure-copy-guard</c> (INTERNALIZE, REMAP SCOPING, PROVENANCE).</summary>
[Trait("tier", "unit")]
public sealed class ClosureCopyInternalizeTests
{
    // the walk reaches the source subtree
    [Fact]
    public void TheWalkReachesTheHeadPartAndItsTextureSet()
    {
        var g = new ClosureCopySourceGraph();
        Assert.True(g.Walk.Success);
        Assert.Equal(new[] { Hp, Txst }.OrderBy(k => k.ID), g.Walk.Reached.Select(n => n.Key).OrderBy(k => k.ID));
    }

    // internalize succeeds; every copy lands under the patch's OWN new keys
    [Fact]
    public void EveryCopyLandsUnderThePatchsOwnKeys()
    {
        var g = new ClosureCopySourceGraph();
        Assert.True(g.Copy.Success, g.Copy.Refusal?.Detail);
        Assert.Equal(2, g.Copy.Copied.Count);
        Assert.All(g.Copy.Copied, c => Assert.Equal(PatchKey, c.NewKey.ModKey));
    }

    // allocated from the patch's own counter, each under a distinct key: an EXTENDED patch keeps counting
    [Fact]
    public void AnExtendedPatchKeepsCountingFromItsOwnCounter()
    {
        var g = new ClosureCopySourceGraph();
        Assert.Equal(new uint[] { PatchCounter, PatchCounter + 1 }, g.Copy.Copied.Select(c => c.NewKey.ID).OrderBy(i => i));
        Assert.Equal(PatchCounter + 2, g.Patch.ModHeader.Stats.NextFormID);
    }

    // EditorIDs are preserved by the whole-record duplicate
    [Fact]
    public void TheCopyKeepsItsEditorId()
    {
        var g = new ClosureCopySourceGraph();
        Assert.Equal("SrcHair", g.HeadPartAt(g.Copy.Map[Hp]).EditorID);
    }

    // an internal reference among the copies is REMAPPED to the new key
    [Fact]
    public void ALinkBetweenTwoCopiesPointsAtTheNewKey()
    {
        var g = new ClosureCopySourceGraph();
        Assert.Equal(g.Copy.Map[Txst], g.HeadPartAt(g.Copy.Map[Hp]).TextureSet.FormKey);
    }

    // REMAP SCOPING: the patch's PRE-EXISTING deliberate reference survives UNTOUCHED (a whole-mod remap would rewrite it)
    [Fact]
    public void ARecordAlreadyInThePatchKeepsItsReferenceToTheSource()
    {
        var g = new ClosureCopySourceGraph();
        Assert.Equal(Txst, g.HeadPartAt(Prior).TextureSet.FormKey);
    }

    // PROVENANCE: the walk's per-node arm attribution survives into the copy report
    // (ATTRIBUTION after attach is the same data: the attach never receives the copy report)
    [Fact]
    public void EachCopyNamesTheArmThatProducedItsBody()
    {
        var src = SourceMod().EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (IMajorRecordGetter)r);
        var chain = new SourceChain(new[]
        {
            FileArm("Source.esp", fk => fk == Hp ? src[Hp] : null),
            FileArm("Other.esp", fk => fk == Txst ? src[Txst] : null),
        });
        var patch = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
        var copy = ClosureCopy.Internalize(patch, Run(chain).Reached);

        var tex = copy.Copied.Single(c => c.OldKey == Txst);
        Assert.Equal((1, "Other.esp"), (tex.ArmIndex, tex.ArmSpelling));
        var hair = copy.Copied.Single(c => c.OldKey == Hp);
        Assert.Equal((0, "Source.esp"), (hair.ArmIndex, hair.ArmSpelling));
    }

    // ...as does what pulled each record in
    [Fact]
    public void TheSeededCopyNamesTheSeedThatPulledIt()
    {
        var g = new ClosureCopySourceGraph();
        Assert.Equal("Npc.HeadParts", g.Copy.Copied.Single(c => c.OldKey == Hp).PulledBy);
    }
}
