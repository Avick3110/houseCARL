using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>The LANE grammar on create / remove / forward: destinations are exclusive and a dropped one is refused by
/// name. Migrated from write-surface-guard's lane arm; the in-place half is <see cref="WriteSurfaceInPlaceLaneTests"/>.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceLaneTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceLaneTests(WriteSurfaceWorld w) => _w = w;

    static readonly string Recs = """[{"record_type":"Keyword","editorid":"W2LaneKw"}]""";

    // probe: create: patch= + into= is refused BY NAME (both lanes quoted), never silently ignoring one
    [Fact]
    public void CreatePatchAndIntoIsRefused()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), patch: "W2A", into: "W2B.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("patch='W2A'", r);
        Assert.Contains("into='W2B.esp'", r);
    }

    // probe: create: into= + in_place= is refused BY NAME — they are different lanes
    [Fact]
    public void CreateIntoAndInPlaceIsRefused()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), into: "W2B.esp", in_place: _w.ReplacerName);
        Assert.StartsWith("error:", r);
        Assert.Contains("into=", r);
        Assert.Contains("in_place=", r);
    }

    // probe: create: acknowledge= without in_place= is refused, not accepted-and-ignored
    [Fact]
    public void CreateAcknowledgeWithoutInPlaceIsRefused()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), patch: "W2A", acknowledge: true);
        Assert.StartsWith("error:", r);
        Assert.Contains("acknowledge=", r);
    }

    // probe: forward: patch= + in_place= is refused BY NAME
    [Fact]
    public void ForwardPatchAndInPlaceIsRefused()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName,
            patch: "W2F", in_place: _w.ReplacerName);
        Assert.StartsWith("error:", r);
        Assert.Contains("patch='W2F'", r);
        Assert.Contains("in_place=", r);
    }

    // probe: remove: naming NO lane is refused, spelling both into= and in_place=
    [Fact]
    public void RemoveWithNoLaneIsRefused()
    {
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid });
        Assert.StartsWith("error:", r);
        Assert.Contains("into=", r);
        Assert.Contains("in_place=", r);
    }

    // probe: remove: into= + in_place= is refused BY NAME
    [Fact]
    public void RemoveIntoAndInPlaceIsRefused()
    {
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, into: "W2B.esp", in_place: _w.ReplacerName);
        Assert.StartsWith("error:", r);
        Assert.Contains("Name one", r);
    }

    // probe: forward in place: a non-active target is refused post-capture, and the refusal carries its epoch
    [Fact]
    public void ForwardInPlaceNonActiveTargetIsRefusedWithEpoch()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, in_place: "NotAPlugin.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("NotAPlugin.esp", r);
        Assert.Contains("\nepoch=", r);
    }
}

/// <summary>in_place is the file's NAME with a one-time consent handshake. Each test builds its own world: consent is
/// recorded per plugin and an acknowledged write rewrites the replacer.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceInPlaceLaneTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    static readonly string Recs = """[{"record_type":"Keyword","editorid":"W2InPlaceKw"}]""";

    // probe: remove in place: the FIRST touch returns the one-time CONSENT prompt, epoch-stamped
    [Fact]
    public void RemoveInPlaceFirstTouchIsAnEpochStampedConsentPrompt()
    {
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { _w.SubjectFid }, in_place: _w.ReplacerName);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("acknowledge=true", r);
        Assert.Contains("\nepoch=", r);
    }

    // probe: create in place: the FIRST touch returns the one-time CONSENT prompt, not an error and not a write
    // probe: the consent prompt carries the §2.1.1 epoch, like every other outcome
    [Fact]
    public void CreateInPlaceFirstTouchIsAConsentPromptNotAWrite()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), in_place: _w.ReplacerName);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("acknowledge=true", r);
        Assert.Contains("\nepoch=", r);
        Assert.DoesNotContain("W2InPlaceKw", EditorIdsIn(_w.ReplacerPath));
    }

    // probe: create in place: acknowledge=true writes into the ORIGINAL file, reported as the in-place lane
    [Fact]
    public void CreateInPlaceAcknowledgedWritesTheOriginalFile()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), in_place: _w.ReplacerName, acknowledge: true);
        Assert.Contains("IN PLACE", r);
        Assert.Contains("W2InPlaceKw", EditorIdsIn(_w.ReplacerPath));
    }

    // probe: create in place: the render states the rewrite hazard — the caller's own file, and no way back
    [Fact]
    public void CreateInPlaceStatesTheRewriteHazard()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), in_place: _w.ReplacerName, acknowledge: true);
        Assert.Contains(WriteSentences.InPlaceRewritten, r);
    }

    // probe: the in-place follow-up hint teaches in_place="X.esp", never the 1.x target= + in_place=true pair
    [Fact]
    public void InPlaceFollowUpHintTeachesThe2xSpelling()
    {
        var r = CreateTools.Create(_w.Svc, records: Json(Recs), in_place: _w.ReplacerName, acknowledge: true);
        Assert.Contains($"pass in_place=\"{_w.ReplacerName}\" again", r);
        Assert.DoesNotContain("target=", r);
    }
}
