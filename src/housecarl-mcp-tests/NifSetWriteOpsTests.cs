using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>Each nif_set whitelist op applied end to end through <see cref="NifService.Set"/> and read back: the value
/// changes and every other whitelisted value is kept. Migrated from the nif-set-guard probe's write arms.</summary>
[Trait("tier", "unit")]
public sealed class NifSetWriteOpsTests
{
    readonly byte[] _guard = Guard();

    static NifSetOutcome Set(byte[] bytes, params NifSetOp[] ops) => NifService.Set(bytes, ops);

    // probe: "set_flags succeeds" / "set_flags read-back is 0x800000E"
    [Fact]
    public void SetFlagsLandsAndReadsBack()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E));
        Assert.Null(o.Error);
        Assert.Equal(0x800000Eu, ShapeOf(o.WrittenBytes, "GuardShape")!.Flags);
    }

    // probe: "set_flags preserved scale / alpha / partitions"
    [Fact]
    public void SetFlagsKeepsScaleAlphaAndPartitions()
    {
        var s = ShapeOf(Set(_guard, new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E)).WrittenBytes, "GuardShape")!;
        Assert.Equal(1.25f, s.Scale);
        Assert.Equal((ushort)0x12ED, s.Alpha!.Flags);
        Assert.Equal(new[] { 30, 31 }, s.Partitions.Select(p => p.BodyPartId));
    }

    // probe: "set_flags report: 1 op, header unchanged, 1 block"
    [Fact]
    public void SetFlagsReportsOneOpOneBlockAndNoHeaderChange()
    {
        var r = Set(_guard, new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E)).Report!;
        Assert.Single(r.Ops);
        Assert.False(r.HeaderChanged);
        Assert.Single(r.ChangedBlocks);
    }

    // probe: "set_scale read-back is 2.5" / "set_scale preserved flags"
    [Fact]
    public void SetScaleLandsAndKeepsFlags()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 2.5f));
        Assert.Null(o.Error);
        var s = ShapeOf(o.WrittenBytes, "GuardShape")!;
        Assert.Equal(2.5f, s.Scale, 6);
        Assert.Equal(0x400000Eu, s.Flags);
    }

    // probe: "set_alpha read-back 0x00ED/thr200" / "set_alpha preserved flags / scale"
    [Fact]
    public void SetAlphaChangesFlagsWordAndThresholdTogether()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.SetAlpha, "GuardShape", AlphaFlags: 0x00ED, AlphaThreshold: 200));
        Assert.Null(o.Error);
        var s = ShapeOf(o.WrittenBytes, "GuardShape")!;
        Assert.Equal((ushort)0x00ED, s.Alpha!.Flags);
        Assert.Equal((byte)200, s.Alpha.Threshold);
        Assert.Equal(0x400000Eu, s.Flags);
        Assert.Equal(1.25f, s.Scale);
    }

    // probe: "set_partition read-back [0]=32,[1]=31"
    [Fact]
    public void SetPartitionChangesOnlyTheNamedPartition()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.SetPartition, "GuardShape", BodyPartId: 32, PartitionIndex: 0));
        Assert.Null(o.Error);
        Assert.Equal(new[] { 32, 31 }, ShapeOf(o.WrittenBytes, "GuardShape")!.Partitions.Select(p => p.BodyPartId));
    }

    // probe: "rename_shape (same length)" / "rename touches the header string table, ZERO blocks"
    [Fact]
    public void SameLengthRenameChangesTheHeaderAndNoBlock()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "GuardShapX"));
        Assert.Null(o.Error);
        Assert.NotNull(ShapeOf(o.WrittenBytes, "GuardShapX"));
        Assert.True(o.Report!.HeaderChanged);
        Assert.Empty(o.Report.ChangedBlocks);
    }

    // probe: "rename_shape (LONGER) does not false-abort" / "a length-changing rename preserved flags / scale / alpha / partitions"
    [Fact]
    public void LongerRenameDoesNotFalseAbortAndKeepsEveryValue()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "GuardShapeRenamedMuchLonger"));
        Assert.Null(o.Error);
        var s = ShapeOf(o.WrittenBytes, "GuardShapeRenamedMuchLonger")!;
        Assert.Equal(0x400000Eu, s.Flags);
        Assert.Equal(1.25f, s.Scale);
        Assert.Equal((ushort)0x12ED, s.Alpha!.Flags);
        Assert.Equal(2, s.Partitions.Count);
    }

    // probe: "rename_node GuardChildA -> RenamedChildA"
    [Fact]
    public void RenameNodeReplacesTheNodeName()
    {
        var o = Set(_guard, new NifSetOp(NifSetOpKind.RenameNode, "GuardChildA", NewName: "RenamedChildA"));
        Assert.Null(o.Error);
        var nodes = NifService.Inspect(o.WrittenBytes!).Inspect!.Nodes.Select(n => n.Name).ToList();
        Assert.Contains("RenamedChildA", nodes);
        Assert.DoesNotContain("GuardChildA", nodes);
    }

    // probe: "two ops in one call both land"
    [Fact]
    public void TwoOpsInOneCallBothLand()
    {
        var o = Set(_guard,
            new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E),
            new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 3f));
        Assert.Null(o.Error);
        var s = ShapeOf(o.WrittenBytes, "GuardShape")!;
        Assert.Equal(0x800000Eu, s.Flags);
        Assert.Equal(3f, s.Scale, 6);
    }

    // probe corpus smoke, on a synthetic head: "set_path on a real facegen slot 6 succeeds" / "set_path read-back shows the new slot-6 path"
    [Fact]
    public void SetPathSwapsTheSlotSixTexture()
    {
        const string swapped = @"textures\actors\character\facegendata\facetint\HOUSECARL_TEST\swapped.dds";
        var o = Set(Textured(), new NifSetOp(NifSetOpKind.SetPath, "FemaleHead", TextureSlot: 6, Path: swapped));
        Assert.Null(o.Error);
        Assert.Contains(ShapeOf(o.WrittenBytes, "FemaleHead")!.Textures, t => t.Slot == 6 && t.Path == swapped);
    }

    // probe corpus smoke, on a synthetic head: "set_path on real data preserved the shape's flags / partitions"
    [Fact]
    public void SetPathOnATextureSlotKeepsFlagsAndPartitions()
    {
        var bytes = Textured();
        var before = ShapeOf(bytes, "FemaleHead")!;
        var o = Set(bytes, new NifSetOp(NifSetOpKind.SetPath, "FemaleHead", TextureSlot: 6, Path: @"textures\x\swapped.dds"));
        var after = ShapeOf(o.WrittenBytes, "FemaleHead")!;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Partitions.Select(p => p.BodyPartId), after.Partitions.Select(p => p.BodyPartId));
    }

    // probe corpus smoke, on a synthetic head: "rename_shape on a real facegen mesh does NOT false-refuse" / "landed + preserved flags/partitions" / "touched the header string table"
    [Fact]
    public void LongerRenameOfATexturedHeadLandsAndKeepsFlagsAndPartitions()
    {
        var bytes = Textured();
        var before = ShapeOf(bytes, "FemaleHead")!;
        var o = Set(bytes, new NifSetOp(NifSetOpKind.RenameShape, "FemaleHead", NewName: "FemaleHead_HC_RENAMED_LONGER"));
        Assert.Null(o.Error);
        var after = ShapeOf(o.WrittenBytes, "FemaleHead_HC_RENAMED_LONGER")!;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Partitions.Count, after.Partitions.Count);
        Assert.True(o.Report!.HeaderChanged);
    }
}
