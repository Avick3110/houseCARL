using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>housecarl_remove is plural: many records drop in one re-serialize, all-or-nothing. Migrated from
/// write-surface-guard's remove arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceRemovePluralTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceRemovePluralTests(WriteSurfaceWorld w) => _w = w;

    /// <summary>Three keywords authored into one fresh patch; returns its path and the allocated FormIDs.</summary>
    (string path, List<string> ids) ThreeInOnePatch(string stem)
    {
        var made = CreateTools.Create(_w.Svc, patch: stem, records: Json($$"""
            [{"record_type":"Keyword","editorid":"{{stem}}A"},
             {"record_type":"Keyword","editorid":"{{stem}}B"},
             {"record_type":"Keyword","editorid":"{{stem}}C"}]
            """));
        var path = _w.ArtifactPathFrom(made);
        Assert.NotNull(path);
        return (path!, FormIdsFrom(made));
    }

    // probe: remove arm fixture: three records authored, their allocated FormIDs reported back
    [Fact]
    public void CreateReportsTheAllocatedFormIds()
    {
        var (path, ids) = ThreeInOnePatch("W2RmF");
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, EditorIdsIn(path).Count(e => e.StartsWith("W2RmF", StringComparison.Ordinal)));
    }

    // probe: MANY records drop in ONE re-serialize (the recovered engine capability): 2 gone, the third stands
    [Fact]
    public void ManyRecordsDropInOneCall()
    {
        var (path, ids) = ThreeInOnePatch("W2RmM");
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { ids[0], ids[1] }, into: Path.GetFileName(path));
        Assert.StartsWith("removed 2 records", r);
        var left = EditorIdsIn(path);
        Assert.DoesNotContain("W2RmMA", left);
        Assert.DoesNotContain("W2RmMB", left);
        Assert.Contains("W2RmMC", left);
    }

    // probe: all-or-nothing: one not-carried target refuses the whole call and NOTHING is removed
    [Fact]
    public void OneNotCarriedTargetRemovesNothing()
    {
        var (path, ids) = ThreeInOnePatch("W2RmN");
        var r = RemoveTools.Remove(_w.Svc, formids: new[] { ids[2], _w.SubjectFid }, into: Path.GetFileName(path));
        Assert.StartsWith("error:", r);
        Assert.Contains("not carried by patch", r);
        Assert.Contains("W2RmNC", EditorIdsIn(path));
    }

    // probe: formids=[] is refused by name (a set of one is the minimum, not zero)
    [Fact]
    public void EmptyFormidsIsRefused()
    {
        var (path, _) = ThreeInOnePatch("W2RmE");
        var r = RemoveTools.Remove(_w.Svc, formids: Array.Empty<string>(), into: Path.GetFileName(path));
        Assert.StartsWith("error:", r);
        Assert.Contains("formids=", r);
    }
}
