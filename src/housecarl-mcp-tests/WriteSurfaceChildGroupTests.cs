using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>#324: a forward onto a record the destination already carries replaces its FIELDS and keeps its child group
/// (INFOs under a topic, placed refs under a cell), and the render says which. Migrated from write-surface-guard's
/// child-group arm; the in-place half is <see cref="WriteSurfaceChildGroupInPlaceTests"/>.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceChildGroupTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceChildGroupTests(WriteSurfaceWorld w) => _w = w;

    /// <summary>A patch hosting the topic with one child created into it.</summary>
    string TopicPatchWithChild(string stem)
    {
        var made = CreateTools.Create(_w.Svc, patch: stem,
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W324Line","parent":"{{_w.TopicFid}}"}]"""));
        var path = _w.ArtifactPathFrom(made);
        // probe: #324 fixture: a child creates into a patch, hosted on the parent's definer
        Assert.NotNull(path);
        return path!;
    }

    // probe: dry_run over a replace: the PREVIEW says the fields would go and the nested records would be KEPT
    // probe: …and it really was a preview: the child is still the only thing under the topic on disk
    [Fact]
    public void DryRunReplacePreviewsFieldsGoneChildrenKept()
    {
        var path = TopicPatchWithChild("W324Dry");
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.ReplacerName,
            into: Path.GetFileName(path), dry_run: true);
        Assert.Contains("the old FIELDS would be gone", r);
        Assert.Contains("1 record(s) nested under it would be KEPT", r);
        Assert.DoesNotContain("the old body would be gone", r);
        Assert.Equal(new[] { "W324Line" }, ChildLinesIn(path, _w.TopicKey));
        Assert.Equal("Master Topic", TopicNameIn(path, _w.TopicKey));
    }

    // probe: forward into= a patch that already carries the record is accepted
    // probe: …and the child SURVIVES the replace (#324: the drop no longer takes the child group)
    // probe: …and the group holds exactly the destination's child — the SOURCE's own line did not come with it
    // probe: …and the forward still DID its job: the source's fields are inlined in the host
    [Fact]
    public void ForwardIntoKeepsTheChildAndLandsTheFields()
    {
        var path = TopicPatchWithChild("W324");
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.ReplacerName, into: Path.GetFileName(path));
        Assert.StartsWith("extended ", r);
        Assert.Equal(new[] { "W324Line" }, ChildLinesIn(path, _w.TopicKey));
        Assert.Equal("Winner Topic", TopicNameIn(path, _w.TopicKey));
    }

    // probe: …and the RESPONSE says so: the replace reports the fields gone and the nested records KEPT, with a count
    [Fact]
    public void ForwardIntoResponseReportsChildrenKept()
    {
        var path = TopicPatchWithChild("W324Say");
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.ReplacerName, into: Path.GetFileName(path));
        Assert.Contains("the old FIELDS are gone", r);
        Assert.Contains("1 record(s) nested under it were KEPT", r);
        Assert.DoesNotContain("the old body is gone", r);
    }

    // probe: a child and its parent forward in ONE call to a fresh patch — the call is not refused
    // probe: …and the child lands under its parent in the patch
    // probe: …and the parent's own fields land too — the second spec is not skipped over a record the call created
    [Fact]
    public void ChildAndParentForwardInOneCall()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.WinnerLineFid, _w.TopicFid }, source: _w.ReplacerName, patch: "W324Pair");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("W2WinnerLine", ChildLinesIn(path!, _w.TopicKey));
        Assert.Equal("Winner Topic", TopicNameIn(path!, _w.TopicKey));
    }

    // probe: #324 cell fixture: a placed ref creates into a patch, hosted on the cell's definer
    // probe: forward into= a patch that already carries the CELL is accepted
    // probe: …and the placed ref SURVIVES the replace on the nested-group path too
    // probe: …and the forward still DID its job: the source's cell fields are inlined in the host
    // probe: …and the group holds exactly the destination's ref — the SOURCE's own refs did not come with it
    [Fact]
    public void ForwardIntoACellKeepsItsPlacedRef()
    {
        var made = CreateTools.Create(_w.Svc, patch: "W324Cell",
            records: Json($$"""[{"record_type":"PlacedObject","editorid":"W324Ref","parent":"{{_w.CellFid}}","collection":"Persistent"}]"""));
        var path = _w.ArtifactPathFrom(made);
        Assert.NotNull(path);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.CellFid }, source: _w.ReplacerName, into: Path.GetFileName(path));
        Assert.StartsWith("extended ", r);
        Assert.Equal(new[] { "W324Ref" }, CellRefsIn(path!, _w.CellKey));
        Assert.Equal("Winner Cell", CellNameIn(path!, _w.CellKey));
    }

    // probe: …and its dry-run twin says the same: the body would go, and there is nothing nested to keep
    // probe: …and a replace of a record with NO children says so, rather than counting a preservation that was not one
    [Fact]
    public void ReplaceOfARecordWithNoChildrenSaysSo()
    {
        // The probe's setup: the same patch already holds a topic whose child a replace kept, so the weapon's
        // count must be its own, not the file's.
        var path = TopicPatchWithChild("W324Flat");
        var file = Path.GetFileName(path);
        Assert.Contains("were KEPT", ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.ReplacerName, into: file));
        ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, into: file);

        var dry = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerName, into: file, dry_run: true);
        Assert.Contains("the old body would be gone", dry);
        Assert.Contains("carries no nested records", dry);
        Assert.DoesNotContain("would be KEPT", dry);

        var flat = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerName, into: file);
        Assert.Contains("the old body is gone", flat);
        Assert.Contains("carried no nested records", flat);
        Assert.DoesNotContain("were KEPT", flat);
    }
}

/// <summary>The in-place half of #324: the caller's OWN plugin keeps its own children. Its own world per test, because
/// each call rewrites the replacer.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceChildGroupInPlaceTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    // probe: #324 in-place fixture: the target plugin carries the record AND a child of its own
    // probe: forward in_place= onto a record the file already carries is accepted
    // probe: …and the caller's OWN child survives it — the no-backup lane does not lose records
    // probe: …and ONLY the caller's own child: the master's line did not ride in on the copy
    // probe: …and the forward still DID its job: the master's fields replaced the winner's
    [Fact]
    public void InPlaceTopicForwardKeepsTheCallersChild()
    {
        Assert.Contains("W2WinnerLine", ChildLinesIn(_w.ReplacerPath, _w.TopicKey));
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.MasterName,
            in_place: _w.ReplacerName, acknowledge: true);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Equal(new[] { "W2WinnerLine" }, ChildLinesIn(_w.ReplacerPath, _w.TopicKey));
        Assert.Equal("Master Topic", TopicNameIn(_w.ReplacerPath, _w.TopicKey));
    }

    // probe: forward in_place= onto a CELL the file already carries is accepted
    // probe: …and the caller's OWN placed ref survives it, and only it
    // probe: …and the master's cell fields replaced the winner's
    [Fact]
    public void InPlaceCellForwardKeepsTheCallersRef()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.CellFid }, source: _w.MasterName,
            in_place: _w.ReplacerName, acknowledge: true);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Equal(new[] { "W2WinnerRef" }, CellRefsIn(_w.ReplacerPath, _w.CellKey));
        Assert.Equal("Master Cell", CellNameIn(_w.ReplacerPath, _w.CellKey));
    }
}
