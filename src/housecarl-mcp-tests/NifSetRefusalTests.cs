using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>What <see cref="NifService.Set"/> cannot safely do is a named refusal with nothing written. Migrated from the
/// nif-set-guard probe's refusal arms.</summary>
[Trait("tier", "unit")]
public sealed class NifSetRefusalTests
{
    readonly byte[] _guard = Guard();

    string? Refusal(params NifSetOp[] ops) => NifService.Set(_guard, ops).Error;

    // #1359: a mesh that will not open is refused with nif_inspect's own sentence, one full stop, then the tail.
    static string RefusesWithInspectsSentence(byte[] bytes, string tail)
    {
        var set = NifService.Set(bytes, new[] { new NifSetOp(NifSetOpKind.SetFlags, "X", Flags: 1) }).Error!;
        Assert.Equal(NifService.Inspect(bytes).Error!.TrimEnd('.') + ". Nothing was written.", set);
        Assert.EndsWith(tail, set);
        return set;
    }

    // probe: "empty bytes → named refusal"
    [Fact]
    public void EmptyBytesRefuseWithInspectsEmptySentence() =>
        RefusesWithInspectsSentence(Array.Empty<byte>(), "(0 bytes) — nothing to read. Nothing was written.");

    // #1359: the return-code sentence keeps "(Load returned 1)" and the NifSkope sentence.
    [Fact]
    public void NonNifGarbageRefusesWithInspectsReturnCodeSentence() =>
        Assert.Contains("(Load returned 1)", RefusesWithInspectsSentence(
            System.Text.Encoding.ASCII.GetBytes("this is plainly not a NIF file, just ASCII text padding padding padding."),
            "houseCARL will not guess at. Nothing was written."));

    // #1359: the structure-read failure ends with one full stop, never "..", which the tail pins.
    [Fact]
    public void AMeshWhoseStructureReadThrowsRefusesWithOneFullStop() =>
        RefusesWithInspectsSentence(NifInspectFixtures.BuildMeshWhoseStructureReadThrows(), "an instance of an object. Nothing was written.");

    // probe: "no ops → named refusal"
    [Fact]
    public void NoOpsRefuse() => Assert.NotNull(NifService.Set(_guard, Array.Empty<NifSetOp>()).Error);

    // #1359: no ops answers before the parse, so a mesh that will not open still gets the no-ops sentence.
    [Fact]
    public void NoOpsOnAMeshThatWillNotOpenRefusesBeforeTheParse() =>
        Assert.Equal("no write op was given.", NifService.Set(NifInspectFixtures.BuildMeshWhoseStructureReadThrows(), Array.Empty<NifSetOp>()).Error);

    // probe: "target not found → named refusal"
    [Fact]
    public void AMissingTargetRefuses() =>
        Assert.Contains("no shape or node named", Refusal(new NifSetOp(NifSetOpKind.SetFlags, "NoSuchShape", Flags: 1)));

    // probe: "set_partition on a shape with no skin → named refusal"
    [Fact]
    public void SetPartitionOnAShapeWithNoSkinRefuses() =>
        Assert.Contains("no BSDismember", Refusal(new NifSetOp(NifSetOpKind.SetPartition, "BareShape", BodyPartId: 32)));

    // probe: "set_alpha on a shape with no alpha → named refusal"
    [Fact]
    public void SetAlphaOnAShapeWithNoAlphaRefuses() =>
        Assert.Contains("no alpha property", Refusal(new NifSetOp(NifSetOpKind.SetAlpha, "BareShape", AlphaThreshold: 10)));

    // probe: "set_path on a shape with no texture set → named refusal"
    [Fact]
    public void SetPathOnAShapeWithNoTextureSetRefuses() =>
        Assert.Contains("no shader texture set", Refusal(new NifSetOp(NifSetOpKind.SetPath, "BareShape", TextureSlot: 0, Path: "x.dds")));

    // probe: "partition_index out of range → named refusal"
    [Fact]
    public void AnOutOfRangePartitionIndexRefuses() =>
        Assert.Contains("out of range", Refusal(new NifSetOp(NifSetOpKind.SetPartition, "GuardShape", BodyPartId: 32, PartitionIndex: 9)));

    // probe: "rename with no new_name → named refusal"
    [Fact]
    public void ARenameWithNoNewNameRefuses() =>
        Assert.Contains("new_name", Refusal(new NifSetOp(NifSetOpKind.RenameShape, "GuardShape")));

    // probe: "rename ONTO an existing shape name → named refusal (no manufactured duplicate; keeps gate-2 read-back sound)"
    [Fact]
    public void ARenameOntoAnExistingShapeNameRefuses() =>
        Assert.Contains("already named", Refusal(new NifSetOp(NifSetOpKind.RenameShape, "GuardShape", NewName: "BareShape")));

    // probe: "ambiguous shape name → named refusal (never a silent first-match write)"
    [Fact]
    public void AnAmbiguousShapeNameRefuses() =>
        Assert.Contains("ambiguous", NifService.Set(DupNames(), new[] { new NifSetOp(NifSetOpKind.SetFlags, "Dup", Flags: 1) }).Error);

    // not in the probe: set_flags resolves a shape OR node, so this pins the shape-only lookup's ambiguity refusal too
    [Fact]
    public void AnAmbiguousShapeNameRefusesOnAShapeOnlyOp() =>
        Assert.Contains("ambiguous", NifService.Set(DupNames(), new[] { new NifSetOp(NifSetOpKind.SetPartition, "Dup", BodyPartId: 32) }).Error);

    // probe: "non-SE stream → named refusal (no cross-game write)"
    [Fact]
    public void ANonSeStreamRefuses()
    {
        Assert.Contains("NOT a Skyrim SE", NifService.Set(NonSe(), new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 1) }).Error);
    }
}
