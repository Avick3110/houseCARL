using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arm 1: the ops grammar — op=, the @file spelling, and the strict element reader's named
/// refusals, down into the compose recursion.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardOpsGrammarTests : IClassFixture<ApplyGuardCorpus>, IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    // probe: "one op is a set of one: BasicStats.Damage=55 lands in a new patch"
    [Fact]
    public void OneOpIsASetOfOne()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("55")), patch: "ApOne");
        Assert.Equal((ushort)55, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Dmg);
    }

    // probe: "op= carries the verb: Add on Keywords appends to the winner's empty list"
    [Fact]
    public void OpCarriesTheVerbAddOnKeywords()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"Keywords","op":"Add","value":"{{W.KeywordFid}}"}]"""),
            patch: "ApAdd");
        Assert.Equal(1, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Kw);
    }

    // probe: "ops=\"@<path>\" reads the SAME array from disk (from_file= retired into the @file convention)"
    [Fact]
    public void OpsAtFileReadsTheArrayFromDisk()
    {
        var manifest = Path.Combine(W.Root, "ops.json");
        File.WriteAllText(manifest, W.DamageOp("77"));
        var r = ApplyTools.Apply(W.Svc, ops: Je(AtPath(manifest)), patch: "ApFile");
        Assert.Equal((ushort)77, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Dmg);
    }

    // probe: "ops=[\"@<path>\"] — the same convention in the one-element array form formids= uses"
    [Fact]
    public void OpsAtFileInAOneElementArray()
    {
        var manifest = Path.Combine(W.Root, "ops.json");
        File.WriteAllText(manifest, W.DamageOp("78"));
        var r = ApplyTools.Apply(W.Svc, ops: Je("[" + AtPath(manifest) + "]"), patch: "ApFileArr");
        Assert.Equal((ushort)78, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Dmg);
    }

    // probe: "a MIXED inline/@file ops array is refused by name, never half-honored"
    [Fact]
    public void AMixedInlineAndAtFileOpsArrayIsRefused()
    {
        var manifest = Path.Combine(W.Root, "ops.json");
        File.WriteAllText(manifest, W.DamageOp("1"));
        var r = ApplyTools.Apply(W.Svc,
            ops: Je("[" + AtPath(manifest) + $$""", {"formid":"{{W.SubjectFid}}","field_path":"Name","value":"x"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("cannot be mixed with inline elements", r);
    }

    // probe: "a 1.x 'verb' member inside an op is refused BY NAME (not silently dropped)"
    // probe: "...and the refusal carries the §5.3 correction — the alias layer cannot reach an op's members"
    [Fact]
    public void AVerbMemberInsideAnOpIsRefusedWithTheOpCorrection()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"BasicStats.Damage","verb":"Set","value":"1"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("the verb member is now op", r);
    }

    // probe: "no ops and no zip: refused naming BOTH ways to give work"
    [Fact]
    public void NoOpsAndNoZipIsRefusedNamingBoth()
    {
        var r = ApplyTools.Apply(W.Svc);
        Assert.StartsWith("error:", r);
        Assert.Contains("ops=", r);
        Assert.Contains("bundle=", r);
    }

    // probe: "a bad field path refuses the whole call with nothing written (all-or-nothing preserved)"
    [Fact]
    public void ABadFieldPathRefusesWithNothingWritten()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"NoSuchField","value":"1"}]"""), patch: "ApBad");
        Assert.StartsWith("error:", r);
        Assert.Empty(Directory.GetFiles(W.ModsDir, "ApBad*", SearchOption.AllDirectories));
    }

    string ComposeOps() =>
        $$"""[{"formid":"{{W.PotionAFid}}","field_path":"Effects","op":"ReplaceAll","composes":[""" +
        """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"11"}]},""" +
        """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"22"}]},""" +
        """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"33"}]}]}]""";

    // probe: "composes= + ReplaceAll builds a modeled list through the new strict reader (StructInput -> NestedSet recursion)"
    [Fact]
    public void ComposesReplaceAllBuildsAModeledList()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(ComposeOps()), patch: "ApCompose");
        Assert.Equal(3, CountEffects(W.PatchPathFrom(r), W.PotionAKey));
    }

    // probe: "...and the IDENTICAL composed payload via ops=\"@<path>\" (ONE reader, both lanes)"
    [Fact]
    public void ComposesViaAtFileBuildsTheSameList()
    {
        var manifest = Path.Combine(W.Root, "compose-ops.json");
        File.WriteAllText(manifest, ComposeOps());
        var r = ApplyTools.Apply(W.Svc, ops: Je(AtPath(manifest)), patch: "ApComposeFile");
        Assert.Equal(3, CountEffects(W.PatchPathFrom(r), W.PotionAKey));
    }

    // probe: "an undeclared member NESTED in compose.sets[0] is refused BY NAME (Disallow reaches the recursion)"
    [Fact]
    public void AnUndeclaredMemberNestedInComposeSetsIsRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(
            $$"""[{"formid":"{{W.PotionAFid}}","field_path":"Effects","op":"Add","compose":""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"1","nosuchmember":"x"}]}}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("nosuchmember", r);
    }

    // probe: "a stray `op` in a NESTED SET is corrected toward verb (the nested shape's own word)"
    [Fact]
    public void AStrayOpInANestedSetIsCorrectedTowardVerb()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(
            $$"""[{"formid":"{{W.PotionAFid}}","field_path":"Effects","op":"Add","compose":""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","op":"Set","value":"1"}]}}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("still spells its verb", r);
    }

    // probe: "...while the SAME stray in an ASSIGNMENT gets the assignment's own correction, not a compose= lecture"
    [Fact]
    public void AStrayOpInAnAssignmentGetsTheAssignmentCorrection()
    {
        var r = ApplyTools.Apply(W.Svc, bundle: new[] { "Name" },
            assignments: Je($$"""[{"target":"{{W.SubjectFid}}","from":"{{W.DonorWeaponFid}}","op":"CopyFrom"}]"""));
        Assert.StartsWith("error:", r);
        Assert.Contains("carries no verb", r);
        Assert.DoesNotContain("compose=", r);
    }

    // probe: "values= drives a list ReplaceAll through the new reader"
    [Fact]
    public void ValuesDrivesAListReplaceAll()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"Keywords","op":"ReplaceAll","values":["{{W.KeywordFid}}"]}]"""),
            patch: "ApValues");
        Assert.Equal(1, ReadWeapon(W.PatchPathFrom(r), W.SubjectKey).Kw);
    }
}
