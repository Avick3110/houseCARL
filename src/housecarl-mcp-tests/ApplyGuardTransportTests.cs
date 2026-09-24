using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>apply-guard arm 5: format=json is valid JSON, refusals and the consent prompt are documents too, both
/// renders carry the epoch, and the empty-input rules for bundle= and patch=.</summary>
[Trait("tier", "integration")]
public sealed class ApplyGuardTransportTests : IDisposable
{
    readonly ApplyGuardWorld W = new();
    public void Dispose() => W.Dispose();

    // probe: "every TEXT write render carries epoch=<hex> — the build the winners resolved from (§2.1.1)"
    [Fact]
    public void ATextWriteRenderCarriesTheEpoch()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("61")), patch: "ApEpoch");
        Assert.Contains("\nepoch=", r);
    }

    // probe: "format=json emits VALID JSON"
    // probe: "...ok=true, lane=patch, and the epoch rides in-band (never a silently degraded mode)"
    // probe: "...and the read-back's provenance is DATA, not prose (the written file, not load-order truth)"
    [Fact]
    public void AJsonWriteIsADocumentWithOkLaneAndEpoch()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("62")), patch: "ApJson", format: "json");
        using var doc = JsonDocument.Parse(r);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("patch", root.GetProperty("lane").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("epoch").ValueKind);
        Assert.True(!root.TryGetProperty("readback", out _) || root.TryGetProperty("readback_source", out _), r);
    }

    // probe: "a json REFUSAL is a document with ok:false + error, not a bare error string"
    [Fact]
    public void AJsonRefusalIsADocument()
    {
        var r = ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"NoSuchField","value":"1"}]"""),
            patch: "ApJsonBad", format: "json");
        using var doc = JsonDocument.Parse(r);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _), r);
    }

    // probe: "the in-place CONSENT prompt is its own json flag + 'confirmation' key, never an 'error'"
    // probe: "the json CONSENT prompt carries the epoch (decided after a capture ⇒ stamped)"
    [Fact]
    public void TheJsonConsentPromptIsFlaggedAndCarriesTheEpoch()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("63")), in_place: W.MasterName, format: "json");
        using var doc = JsonDocument.Parse(r);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("needs_acknowledge").GetBoolean());
        Assert.True(root.TryGetProperty("confirmation", out _), r);
        Assert.Equal(JsonValueKind.String, root.GetProperty("epoch").ValueKind);
    }

    // probe: "the TEXT consent prompt carries the epoch too"
    [Fact]
    public void TheTextConsentPromptCarriesTheEpoch()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("65")), in_place: W.MasterName);
        Assert.Contains("\nepoch=", r);
    }

    // probe: "the service-side in-place 'not an active plugin' refusal carries the epoch"
    [Fact]
    public void TheNotActivePluginRefusalCarriesTheEpoch()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("66")), in_place: "NotInTheOrder.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("\nepoch=", r);
    }

    // probe: "an unrecognized format= is refused by name, never a silent fall-through to text"
    [Fact]
    public void AnUnknownFormatIsRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("64")), format: "yaml");
        Assert.StartsWith("error:", r);
        Assert.Contains("format=", r);
    }

    // probe: "a PRE-ENGINE refusal answers in json when asked — a LANE conflict"
    [Fact]
    public void ALaneConflictRefusalAnswersInJson() =>
        AssertJsonError(ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("70")), patch: "X", into: "Y.esp", format: "json"));

    // probe: "a PRE-ENGINE refusal answers in json when asked — an undeclared op member"
    [Fact]
    public void AnUndeclaredOpMemberRefusalAnswersInJson() =>
        AssertJsonError(ApplyTools.Apply(W.Svc,
            ops: Je($$"""[{"formid":"{{W.SubjectFid}}","field_path":"Name","verb":"Set","value":"x"}]"""), format: "json"));

    // probe: "a PRE-ENGINE refusal answers in json when asked — half a zip"
    [Fact]
    public void AHalfZipRefusalAnswersInJson() =>
        AssertJsonError(ApplyTools.Apply(W.Svc, bundle: new[] { "Name" }, format: "json"));

    // probe: "a PRE-ENGINE refusal answers in json when asked — nothing to apply"
    [Fact]
    public void ANothingToApplyRefusalAnswersInJson() =>
        AssertJsonError(ApplyTools.Apply(W.Svc, format: "json"));

    static void AssertJsonError(string r)
    {
        using var doc = JsonDocument.Parse(r);
        Assert.True(doc.RootElement.TryGetProperty("error", out _), r);
    }

    // probe: "an EMPTY bundle= is refused by name, not silently dropped (parity with ops=[])"
    [Fact]
    public void AnEmptyBundleIsRefusedByName()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("71")), bundle: Array.Empty<string>(), patch: "ApEmptyBundle");
        Assert.StartsWith("error:", r);
        Assert.Contains("bundle= is an empty array", r);
    }

    // probe: "a whitespace-only patch= counts as ABSENT and the call TAKES the into= lane (its own refusal, not the exclusivity one)"
    [Fact]
    public void AWhitespacePatchIsAbsentAndTheCallTakesTheIntoLane()
    {
        var r = ApplyTools.Apply(W.Svc, ops: Je(W.DamageOp("72")), patch: "   ", into: "NoSuchPatch.esp");
        Assert.StartsWith("error:", r);
        Assert.DoesNotContain("the two lanes are exclusive", r);
        Assert.Contains("NoSuchPatch.esp", r);
    }
}
