using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>The nif_set service lane over a real LoadOrderService on a synthetic MO2 instance: the new-folder default,
/// the in-place opt-in and its persisted consent, and a refused in-place write that must record no consent (#378).
/// Each test builds its own instance and store. Migrated from the nif-set-guard probe's service arms.</summary>
[Trait("tier", "integration")]
public sealed class NifSetServiceLaneTests
{
    const string MeshRel = NifSetInstance.MeshRel;
    static readonly byte[] Mesh = Guard();

    static NifSetOp[] Flags() => new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) };

    // probe: "new-folder lane writes a verified mesh into a fresh houseCARL folder" / "the edited mesh lands at the SAME rel path in the new folder" / "the placed mesh reads the new flags"
    [Fact]
    public void TheNewFolderLaneWritesTheEditedMeshAtTheSameRelPath()
    {
        using var w = new NifSetInstance(Mesh);
        var res = w.Svc.NifSet(MeshRel, Flags(), null, "FaceFix", null, inPlace: false, acknowledge: false);
        Assert.Null(res.Error);
        var placed = Path.Combine(res.OutputModFolder!, MeshRel);
        Assert.Equal(0x800000Eu, ShapeOf(File.ReadAllBytes(placed), "GuardShape")!.Flags);
    }

    // probe: "the ORIGINAL loose mesh is untouched (non-destructive default)"
    [Fact]
    public void TheNewFolderLaneLeavesTheOriginalUntouched()
    {
        using var w = new NifSetInstance(Mesh);
        w.Svc.NifSet(MeshRel, Flags(), null, "FaceFix", null, inPlace: false, acknowledge: false);
        Assert.Equal(Mesh, File.ReadAllBytes(w.LoosePath));
    }

    // probe: "reports the current winner to sort above"
    [Fact]
    public void TheNewFolderLaneReportsTheCurrentWinner()
    {
        using var w = new NifSetInstance(Mesh);
        Assert.Contains("FaceMod", w.Svc.NifSet(MeshRel, Flags(), null, "FaceFix", null, inPlace: false, acknowledge: false).CurrentWinner);
    }

    // probe: "in-place FIRST call without acknowledge → consent prompt, nothing written" / "the loose file is untouched by the un-acknowledged in-place call"
    [Fact]
    public void AFirstInPlaceCallPromptsAndWritesNothing()
    {
        using var w = new NifSetInstance(Mesh);
        var r = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: false);
        Assert.True(r.NeedsAcknowledge);
        Assert.Null(r.Report);
        Assert.Equal(Mesh, File.ReadAllBytes(w.LoosePath));
    }

    // probe: "the mesh prompt states WHEN it stops — a landed write, not \"once\""
    [Fact]
    public void TheMeshPromptSaysItStopsWhenAWriteToThisMeshLands()
    {
        using var w = new NifSetInstance(Mesh);
        var prompt = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: false).AckPrompt;
        Assert.Contains("shown until an in-place write to this mesh LANDS", prompt);
        Assert.Contains("a call that is refused records nothing", prompt);
    }

    // probe: "the mesh prompt's file claim is direction-neutral (true whether or not a prior call already mutated it)"
    [Fact]
    public void TheMeshPromptFileClaimIsDirectionNeutral()
    {
        using var w = new NifSetInstance(Mesh);
        var prompt = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: false).AckPrompt;
        Assert.Contains("not a copy", prompt);
        Assert.Contains("cannot restore what it overwrites", prompt);
    }

    // probe: "in-place WITH acknowledge overwrites the loose file where it sits" / "the in-place file now reads the new flags"
    [Fact]
    public void AnAcknowledgedInPlaceCallOverwritesTheLooseFile()
    {
        using var w = new NifSetInstance(Mesh);
        var r = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: true);
        Assert.Null(r.Error);
        Assert.True(r.InPlace);
        Assert.Equal(w.LoosePath, r.InPlacePath);
        Assert.Equal(0x800000Eu, ShapeOf(File.ReadAllBytes(w.LoosePath), "GuardShape")!.Flags);
    }

    // probe: "a LATER in-place edit of the same file needs NO re-acknowledge (consent persisted)"
    [Fact]
    public void ALaterInPlaceEditNeedsNoReAcknowledge()
    {
        using var w = new NifSetInstance(Mesh);
        w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: true);
        var r = w.Svc.NifSet(MeshRel, new[] { new NifSetOp(NifSetOpKind.SetScale, "GuardShape", Scale: 2f) }, null, null, null, inPlace: true, acknowledge: false);
        Assert.Null(r.Error);
        Assert.False(r.NeedsAcknowledge);
        Assert.True(r.InPlace);
    }

    // probe: "in_place + into= → named refusal (mutually exclusive)"
    [Fact]
    public void InPlaceWithIntoRefuses()
    {
        using var w = new NifSetInstance(Mesh);
        Assert.Contains("mutually exclusive", w.Svc.NifSet(MeshRel, Flags(), null, null, "SomeFolder", inPlace: true, acknowledge: false).Error);
    }

    // probe: "an absent mesh path → ABSENT refusal"
    [Fact]
    public void AnAbsentMeshPathRefusesAsAbsent()
    {
        using var w = new NifSetInstance(Mesh);
        Assert.Contains("ABSENT", w.Svc.NifSet(@"meshes\nope\absent.nif", Flags(), null, null, null, false, false).Error);
    }

    /// <summary>Runs the #378 arm: with the loose mesh held open for reading, one call without acknowledge and one with.</summary>
    static (bool GateFirst, NifSetResult Blocked, bool Spent, bool Untouched) HeldTarget(NifSetInstance w)
    {
        using var hold = new FileStream(w.LoosePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var gateFirst = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: false).NeedsAcknowledge;
        var blocked = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: true);
        var spent = new UserConfigStore(w.StorePath).IsInPlaceAcknowledged(w.LoosePath);
        return (gateFirst, blocked, spent, File.ReadAllBytes(w.LoosePath).SequenceEqual(Mesh));
    }

    // probe: "the consent gate is reached BEFORE the overwrite refuses"
    [Fact]
    public void TheConsentGateIsReachedBeforeAHeldTargetRefuses()
    {
        using var w = new NifSetInstance(Mesh);
        Assert.True(HeldTarget(w).GateFirst);
    }

    // probe: "a held target makes the in-place overwrite refuse with the mesh byte-intact"
    [Fact]
    public void AHeldTargetRefusesTheOverwriteWithTheMeshIntact()
    {
        using var w = new NifSetInstance(Mesh);
        var (_, blocked, _, untouched) = HeldTarget(w);
        Assert.NotNull(blocked.Error);
        Assert.False(blocked.InPlace);
        Assert.True(untouched);
    }

    // probe: "the refused in-place edit records NO consent"
    [Fact]
    public void ARefusedInPlaceEditRecordsNoConsent()
    {
        using var w = new NifSetInstance(Mesh);
        Assert.False(HeldTarget(w).Spent);
    }

    // probe: "…so the NEXT call still meets the first-touch prompt rather than overwriting unprompted"
    [Fact]
    public void TheCallAfterARefusedInPlaceEditStillPrompts()
    {
        using var w = new NifSetInstance(Mesh);
        HeldTarget(w);
        var after = w.Svc.NifSet(MeshRel, Flags(), null, null, null, inPlace: true, acknowledge: false);
        Assert.True(after.NeedsAcknowledge);
        Assert.Null(after.Report);
    }
}
