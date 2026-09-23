using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arm 4: CopyFrom on the in-place lane — from another plugin, from the target's own file
/// (source read from the mutable mod, not a released overlay), with no element aliasing, and reading pre-call state.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardInPlaceCopyTests : IClassFixture<ApplyGuardCorpus>, IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    // probe: "CopyFrom composes with in_place= (it previously died as an engine-inconsistency wrapper)"
    // probe: "...and it is NOT the old internal-fault wording"
    [Fact]
    public void InPlaceCopyFromTheMasterLands()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from_source":"{{W.MasterName}}"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("edited ", r);
        Assert.DoesNotContain("pre-flight ACCEPTED it but the apply threw", r);
        Assert.Equal((ushort)10, ReadWeapon(W.ReplacerPath, W.SubjectKey).Dmg);
    }

    // probe: "copying the in-place target's own field onto itself is refused as a no-op"
    [Fact]
    public void CopyingTheTargetsOwnFieldOntoItselfIsRefusedAsANoOp()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from_source":"{{W.ReplacerName}}"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains("no-op", r);
    }

    // probe: "a CROSS-record copy sourced from the in-place target's OWN file completes (source read from the mutable mod, not the released overlay)"
    // probe: "...and the copied value actually landed intact in the rewritten file (Damage 7)"
    [Fact]
    public void ACrossRecordCopyFromTheTargetsOwnFileLandsIntact()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from":"{{W.DonorWeaponFid}}","from_source":"{{W.ReplacerName}}"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("edited ", r);
        Assert.Equal((ushort)7, ReadWeapon(W.ReplacerPath, W.SubjectKey).Dmg);
    }

    // probe: "a same-file LIST copy followed by an edit to the target's copy completes"
    // probe: "...the TARGET took the edit (B magnitude 99)"
    // probe: "...and the SOURCE record is UNTOUCHED — no shared element aliasing (A magnitude still 5)"
    [Fact]
    public void ASameFileListCopyThenEditLeavesTheSourceUntouched()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.PotionBFid}}","field_path":"Effects","op":"CopyFrom","from":"{{W.PotionAFid}}","from_source":"{{W.ReplacerName}}"}, {"formid":"{{W.PotionBFid}}","field_path":"Effects[0].Data.Magnitude","value":"99"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("edited ", r);
        var (magA, magB) = W.PotionMagnitudes();
        Assert.Equal((ushort)99, magB);
        Assert.Equal((ushort)5, magA);
    }

    // probe: "an A<->B swap on ONE file completes"
    // probe: "...and each record took the OTHER's PRE-CALL value, not a mid-call one (subject expect 222, donor expect 111)"
    [Fact]
    public void AnInPlaceSwapReadsPreCallValues()
    {
        ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","value":"111"}, {"formid":"{{W.DonorWeaponFid}}","field_path":"BasicStats.Damage","value":"222"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        var swap = ApplyTools.Apply(W.Svc, bundle: new[] { "BasicStats.Damage" },
            assignments: Je($$"""[{"target":"{{W.SubjectFid}}","from":"{{W.DonorWeaponFid}}","from_source":"{{W.ReplacerName}}"}, {"target":"{{W.DonorWeaponFid}}","from":"{{W.SubjectFid}}","from_source":"{{W.ReplacerName}}"}]"""),
            in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("edited ", swap);
        Assert.Equal((ushort)222, ReadWeapon(W.ReplacerPath, W.SubjectKey).Dmg);
        Assert.Equal((ushort)111, ReadWeapon(W.ReplacerPath, W.DonorKey).Dmg);
    }
}
