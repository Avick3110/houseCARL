using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arm 2: the three destinations (patch=, into=, in_place=) are exclusive, a conflict or a stray
/// acknowledge= is refused by name, in_place= takes the file's name with its consent prompt, into= extends, and a dry
/// run writes nothing.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardLaneTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    // probe: "patch= + into= refused BY NAME (1.x silently IGNORED patch_name under into=)"
    [Fact]
    public void PatchAndIntoTogetherAreRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("1")), patch: "X", into: "Y.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("patch=", r);
        Assert.Contains("the two lanes are exclusive", r);
    }

    // probe: "patch= + in_place= refused BY NAME"
    [Fact]
    public void PatchAndInPlaceTogetherAreRefused()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("1")), patch: "X", in_place: W.ReplacerName);
        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
    }

    // probe: "into= + in_place= refused BY NAME (different lanes, not a fallback)"
    [Fact]
    public void IntoAndInPlaceTogetherAreRefused()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("1")), into: "Y.esp", in_place: W.ReplacerName);
        Assert.StartsWith("error:", r);
        Assert.Contains("Name one", r);
    }

    // probe: "acknowledge= without in_place= refused BY NAME, not ignored"
    [Fact]
    public void AcknowledgeWithoutInPlaceIsRefused()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("1")), acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains("meaningless without in_place", r);
    }

    // probe: "in_place=\"X.esp\" enters the lane and the FIRST touch returns the consent prompt (a confirmation, not an error)"
    [Fact]
    public void TheFirstInPlaceTouchReturnsTheConsentPrompt()
    {
        var before = File.GetLastWriteTimeUtc(W.ReplacerPath);
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("21")), in_place: W.ReplacerName);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("acknowledge", r, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.GetLastWriteTimeUtc(W.ReplacerPath));
    }

    // probe: "in_place + acknowledge writes the target's OWN file in place"
    [Fact]
    public void InPlaceWithAcknowledgeWritesTheTargetsOwnFile()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("21")), in_place: W.ReplacerName, acknowledge: true);
        Assert.StartsWith("edited ", r);
        Assert.Contains(W.ReplacerName, r);
        Assert.Equal((ushort)21, ReadWeapon(W.ReplacerPath, W.SubjectKey).Dmg);
    }

    // probe: "into= EXTENDS the existing patch rather than writing a fresh one"
    [Fact]
    public void IntoExtendsTheExistingPatch()
    {
        var fresh = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("31")), patch: "ApExtend");
        Assert.StartsWith("wrote ", fresh);
        var file = fresh[6..].Split(' ')[0];
        var extended = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("32")), into: file);
        Assert.StartsWith("extended ", extended);
    }

    // probe (lines only): the into= "no such patch" refusal ran with patches from earlier arms in the order, so it
    // listed them; a mistyped into= offers the patch it was near
    [Fact]
    public void AnIntoThatMissesOffersTheExistingPatch()
    {
        var fresh = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("33")), patch: "ApNearby");
        Assert.StartsWith("wrote ", fresh);
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("34")), into: "ApNearbx.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("try into=\"houseCARL - ApNearby\"", r);
    }

    // probe: "dry_run reports what WOULD change and writes nothing"
    [Fact]
    public void DryRunReportsAndWritesNothing()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("41")), patch: "ApDry", dry_run: true);
        Assert.StartsWith(WriteSentences.DryRunHeader, r);
        Assert.Empty(Directory.GetFiles(W.ModsDir, "ApDry*", SearchOption.AllDirectories));
    }
}
