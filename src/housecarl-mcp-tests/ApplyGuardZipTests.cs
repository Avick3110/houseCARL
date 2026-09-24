using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arm 3: bundle= x assignments= copies fields between records. The donor's winner (the
/// replacer) has Damage 7 and two keywords; the master's donor has 42; the subject's winner has 99 and none.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardZipTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    string Pair(string? extra = null) =>
        $$"""[{"target":"{{W.SubjectFid}}","from":"{{W.DonorWeaponFid}}"{{extra}}}]""";

    // probe: "the zip copies a bundle BETWEEN records: Damage 99 -> 7 and 0 -> 2 keywords"
    // probe: "...and leaves everything OUTSIDE the bundle untouched (Name is still the winner's)"
    // probe: "from_source is optional and defaults to the SOURCE record's winner (7, the replacer's override — not the master's 42)"
    [Fact]
    public void TheZipCopiesTheBundleFromTheSourcesWinnerAndNothingElse()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "BasicStats.Damage", "Keywords" },
            assignments: Je(Pair()), patch: "ApZip");
        var after = ReadWeapon(W.PatchPathFrom(r), W.SubjectKey);
        Assert.Equal((ushort)7, after.Dmg);
        Assert.Equal(2, after.Kw);
        Assert.Equal("Winner Sword", after.Name);
    }

    // probe: "a named from_source reads THAT plugin's version of the source record (42, the master's — not the winner's 7)"
    [Fact]
    public void ANamedFromSourceReadsThatPluginsVersion()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "BasicStats.Damage" },
            assignments: Je(Pair($",\"from_source\":\"{W.MasterName}\"")), patch: "ApZipPole");
        Assert.Equal((ushort)42, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Dmg);
    }

    // probe: "bundle= without assignments= refused BY NAME"
    [Fact]
    public void BundleWithoutAssignmentsIsRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "BasicStats.Damage" });
        Assert.StartsWith("error:", r);
        Assert.Contains("assignments=", r);
    }

    // probe: "assignments= without bundle= refused BY NAME"
    [Fact]
    public void AssignmentsWithoutBundleIsRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, assignments: Je(Pair()));
        Assert.StartsWith("error:", r);
        Assert.Contains("bundle=", r);
        Assert.Contains("the zip needs both", r);
    }

    // probe: "target == from refused as a no-op, pointing at from_source= for the version case"
    [Fact]
    public void ASelfPairIsRefusedPointingAtFromSource()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "BasicStats.Damage" },
            assignments: Je($$"""[{"target":"{{W.SubjectFid}}","from":"{{W.SubjectFid}}"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("from_source", r);
    }

    // probe: "a CROSS-TYPE pair (Armor -> Weapon) is refused by name at pre-flight, naming both types"
    [Fact]
    public void ACrossTypePairIsRefusedNamingBothTypes()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "Name" },
            assignments: Je($$"""[{"target":"{{W.SubjectFid}}","from":"{{W.ArmorFid}}"}]"""), patch: "ApCross");
        Assert.StartsWith("error:", r);
        Assert.Contains("Armor", r);
        Assert.Contains("Weapon", r);
    }

    // probe: "an assignment missing from= is refused at its own index"
    [Fact]
    public void AnAssignmentMissingFromIsRefusedAtItsIndex()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "Name" },
            assignments: Je($$"""[{"target":"{{W.SubjectFid}}"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("assignments[0]", r);
        Assert.Contains("from is required", r);
    }

    // probe: "a bad FormID in an assignment is refused NAMING THE ASSIGNMENT, not a phantom op index"
    [Fact]
    public void ABadFormIdInAnAssignmentNamesTheAssignmentNotAnOpIndex()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"Name","value":"a"},{"formid":"{{W.SubjectFid}}","field_path":"Value","value":"1"}]"""),
            bundle: new[] { "Name" },
            assignments: Je($$"""[{"target":"NOTAFORMID","from":"{{W.DonorWeaponFid}}"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("assignments[0]", r);
        Assert.DoesNotContain("ops[2]", r);
    }

    // probe: "a MIXED inline/@file bundle= is refused by name (parity with ops=/assignments=)"
    [Fact]
    public void AMixedInlineAndAtFileBundleIsRefused()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "@" + Path.Combine(W.Root, "paths.json"), "Keywords" },
            assignments: Je(Pair()));
        Assert.StartsWith("error:", r);
        Assert.Contains("cannot be mixed with inline elements", r);
    }

    // probe: "CopyFrom + value= is refused without from_source (the value is never silently discarded)"
    // probe: "CopyFrom + value= is refused with from_source (the value is never silently discarded)"
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyFromWithAValueIsRefusedWhetherOrNotThePoleIsNamed(bool namePole)
    {
        var extra = namePole ? $",\"from_source\":\"{W.MasterName}\"" : "";
        var r = ApplyTools.Apply(W.Svc, ops: Je(
            $$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from":"{{W.DonorWeaponFid}}"{{extra}},"value":"55"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("takes no value", r);
    }

    // probe: "ops= and the zip compose in ONE call"
    [Fact]
    public void OpsAndTheZipComposeInOneCall()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"Name","value":"Renamed"}]"""),
            bundle: new[] { "BasicStats.Damage" }, assignments: Je(Pair()), patch: "ApBoth");
        var both = ReadWeapon(W.PatchPathFrom(r), W.SubjectKey);
        Assert.Equal("Renamed", both.Name);
        Assert.Equal((ushort)7, both.Dmg);
    }
}
