using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The closure copy wired end to end through <c>LoadOrderService.CopyClosure</c>, source DISABLED: the attach
/// lane, the clone lane, the in-patch target on an extended patch, the into= refusal, the path-named active source,
/// the asset harvest, and the folder a refusal leaves behind.</summary>
[Trait("tier", "integration")]
public sealed class CopyServiceLaneTests : IDisposable
{
    readonly CopyServiceWorld _w = new();
    public void Dispose() => _w.Dispose();

    ClosureCopyOutcome Attach(string patch) => _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts,
        CopyServiceWorld.NoExclusions, _w.TargetNpc, null, patch, null);

    ClosureCopyOutcome Clone(string eid, string patch) => _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts,
        CopyServiceWorld.NoExclusions, null, eid, patch, null);

    // probe arm 0: copy's not-found refusal still offers the fresh-write route it does have
    [Fact]
    public void IntoAPatchThatDoesNotExistRefusesOfferingToCreateItFresh()
    {
        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts, CopyServiceWorld.NoExclusions,
            _w.TargetNpc, null, null, "NoSuchCopyPatch");

        Assert.False(o.Success);
        Assert.Contains("create it fresh", o.EngineError ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // probe arm 1: attach onto an ACTIVE target with a DISABLED source succeeds; the outcome names the attach lane
    // and the target; the source subtree is internalized (2 records) under the patch's own keys
    [Fact]
    public void AttachOntoAnActiveTargetWithADisabledSourceInternalizesTheSubtreeUnderThePatchsKeys()
    {
        var o = Attach("AttachPatch");

        CopyServiceWorld.Succeeded(o);
        Assert.Equal("attach", o.Mode);
        Assert.Equal(_w.TargetNpc, o.NewKey);
        Assert.Equal(2, o.Copied.Count);
        Assert.All(o.Copied, c => Assert.Equal("AttachPatch.esp", c.NewKey.ModKey.FileName.String, ignoreCase: true));
    }

    // probe arm 1: ATTRIBUTION survives the whole wired lane — every copy names the arm that produced it
    [Fact]
    public void EveryInternalizedRecordNamesTheArmThatProducedIt()
    {
        var o = Attach("AttachAttrib");

        CopyServiceWorld.Succeeded(o);
        Assert.All(o.Copied, c => Assert.Equal("Src.esp", c.ArmSpelling));
    }

    // probe arm 1: the DISABLED source is NOT a master of the written patch; the seed field is reported as attached
    [Fact]
    public void TheDisabledSourceIsNotAMasterAndTheSeedFieldIsReportedAttached()
    {
        var o = Attach("AttachMasters");

        CopyServiceWorld.Succeeded(o);
        Assert.False(o.SourceAmongMasters);
        Assert.DoesNotContain(o.Masters, m => m.Equals("Src.esp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(o.Attached, a => a.Field == "HeadParts");
    }

    // probe arm 1: the written patch overrides the target NPC, and its head part points at the INTERNALIZED copy
    [Fact]
    public void TheWrittenAttachPatchPointsTheTargetsHeadPartAtTheInternalizedCopy()
    {
        var o = Attach("AttachOnDisk");

        CopyServiceWorld.Succeeded(o);
        var headPartMods = CopyServiceWorld.ReadBack(o.OutPath!, back =>
        {
            var npc = back.Npcs.FirstOrDefault(n => n.FormKey == _w.TargetNpc);
            Assert.NotNull(npc);
            return (Patch: back.ModKey, HeadParts: npc!.HeadParts.Select(h => h.FormKey.ModKey).ToList());
        });
        Assert.Equal(new[] { headPartMods.Patch }, headPartMods.HeadParts);
    }

    // probe arm 2: clone succeeds, minted under the patch's own key; its link into the source universe is STRIPPED
    // and named; the source is not a master of the clone patch
    [Fact]
    public void AClonesUnderThePatchsKeyWithItsSourceFactionStrippedAndNamed()
    {
        var o = Clone("MyClone", "ClonePatch");

        CopyServiceWorld.Succeeded(o);
        Assert.Equal("clone", o.Mode);
        Assert.Equal("ClonePatch.esp", o.NewKey.ModKey.FileName.String, ignoreCase: true);
        Assert.Contains(o.Stripped, s => s.Field.StartsWith("Factions", StringComparison.Ordinal));
        Assert.False(o.SourceAmongMasters);
    }

    // probe arm 2: the written clone carries the new EditorID, its head part is the internalized copy, and the
    // source faction is gone from the file
    [Fact]
    public void TheWrittenCloneCarriesTheNewEditorIdTheInternalizedHeadPartAndNoFaction()
    {
        var o = Clone("MyClone", "CloneOnDisk");

        CopyServiceWorld.Succeeded(o);
        var (eid, headParts, factions, patch) = CopyServiceWorld.ReadBack(o.OutPath!, back =>
        {
            var cl = back.Npcs.FirstOrDefault(n => n.FormKey == o.NewKey);
            Assert.NotNull(cl);
            return (cl!.EditorID, cl.HeadParts.Select(h => h.FormKey.ModKey).ToList(), cl.Factions.Count, back.ModKey);
        });
        Assert.Equal("MyClone", eid);
        Assert.Equal(new[] { patch }, headParts);
        Assert.Equal(0, factions);
    }

    // probe arm 3: E4 — an IN-PATCH target resolves off the OPENED patch mod, naming that record as the target, and
    // it EXTENDED the existing patch rather than minting a new one
    [Fact]
    public void AnInPatchTargetResolvesOffTheExtendedPatchRatherThanTheLoadOrder()
    {
        var clone = Clone("MyClone", "E4Patch");
        CopyServiceWorld.Succeeded(clone);

        var o = _w.Copy(_w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts, CopyServiceWorld.NoExclusions,
            clone.NewKey, null, null, Path.GetFileName(clone.OutPath!));

        CopyServiceWorld.Succeeded(o);
        Assert.Equal("attach", o.Mode);
        Assert.Equal(clone.NewKey, o.NewKey);
        Assert.True(o.Extended);
    }

    // probe arm 6: a full PATH naming an ACTIVE plugin resolves as the active arm, and is not described as off-order
    [Fact]
    public void AFullPathNamingAnActivePluginIsConsultedAsThatActivePlugin()
    {
        var o = _w.Copy(_w.TargetNpc, new[] { _w.FollowerPath }, new[] { "Keywords" }, CopyServiceWorld.NoExclusions,
            null, "ByPathClone", "ByPathPatch", null);

        var arm = Assert.Single(o.SourcesConsulted);
        Assert.Equal(SourceArmKind.ActiveOrder, arm.Kind);
        Assert.Equal("Follower.esp", arm.Spelling);
    }

    // probe arm 7e: I1 — the copy REPORTS the asset paths its copied records reference, harvested from the in-patch
    // duplicates, naming the actual path
    [Fact]
    public void TheCopyReportsTheAssetPathsItsCopiedRecordsReference()
    {
        var o = Clone("AssetClone", "AssetPatch");

        CopyServiceWorld.Succeeded(o);
        Assert.Contains(o.AssetPaths, a => a.Contains("srchair.nif", StringComparison.OrdinalIgnoreCase));
    }

    // probe arm 8: a refusal that happens AFTER the output folder is resolved removes the folder it created this
    // call — no orphan left behind
    [Fact]
    public void ARefusalAfterTheOutputFolderIsResolvedLeavesNoFolderBehind()
    {
        var before = Directory.GetDirectories(_w.ModsDir).Length;

        var o = _w.Copy(_w.WideNpc, new[] { "Src.esp", "Extra.esp" }, CopyServiceWorld.HeadParts,
            CopyServiceWorld.StopHeadParts, _w.TargetNpc, null, "OrphanCheck", null);

        Assert.False(o.Success);
        Assert.Equal(before, Directory.GetDirectories(_w.ModsDir).Length);
        Assert.False(_w.AnyFolderNamed("OrphanCheck"));
    }
}
