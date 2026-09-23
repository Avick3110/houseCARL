using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>#300: a nested create under an existing load-order parent hosts the child in the parent's DEFINING plugin's
/// version, and says so. Migrated from write-surface-guard's nested-parent-host arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceNestedHostTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceNestedHostTests(WriteSurfaceWorld w) => _w = w;

    (string render, string path) HostedLine(string stem)
    {
        var made = CreateTools.Create(_w.Svc, patch: stem,
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W2HostedLine","parent":"{{_w.TopicFid}}"}]"""));
        var path = _w.ArtifactPathFrom(made);
        // probe: a nested create under an existing load-order parent succeeds
        Assert.NotNull(path);
        return (made, path!);
    }

    // probe: …and the parent's load-order WINNER is NOT a master of the patch (the child never needed it)
    [Fact]
    public void WinnerIsNotAMaster()
    {
        var (made, _) = HostedLine("W2HostM");
        var masters = MastersLineOf(made);
        Assert.DoesNotContain(_w.ReplacerName, masters, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_w.MasterName, masters, StringComparison.OrdinalIgnoreCase);
    }

    // probe: …and the hosted parent carries the DEFINER's fields, not a frozen copy of the winner's
    [Fact]
    public void HostCarriesTheDefinersFields()
    {
        var (_, path) = HostedLine("W2HostF");
        Assert.Equal("Master Topic", TopicNameIn(path, _w.TopicKey));
    }

    // probe: …and the host carries ONLY the new child — not the definer's existing lines, and not the winner's
    [Fact]
    public void HostCarriesOnlyTheNewChild()
    {
        var (_, path) = HostedLine("W2HostC");
        Assert.Equal(new[] { "W2HostedLine" }, ChildLinesIn(path, _w.TopicKey));
    }

    // probe: …and the render REPORTS which plugin's version hosted the child (the choice is invisible afterwards)
    // probe: …and it names the winning mod, how the record resolves, and the lever the caller has
    // probe: …and the inlining lever names the order-independent remedy, with no stale open-bug warning
    [Fact]
    public void RenderReportsTheHostAndTheLever()
    {
        var (made, _) = HostedLine("W2HostR");
        Assert.Contains("parent: ", made);
        Assert.Contains("DEFINING plugin", made);
        Assert.Contains(_w.MasterName, made, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_w.ReplacerName, made, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("currently WINS this record", made);
        Assert.Contains("out-ranks", made);
        Assert.Contains("inlined", made);
        Assert.Contains("forward", made);
        Assert.Contains("Either order works", made);
        Assert.DoesNotContain("deletes the child", made);
        Assert.DoesNotContain("#324", made);
    }

    // probe: the SAFE ordering, step 1: the winner's version of the parent forwards into a patch of its own
    // probe: …step 2: the child creates into THAT patch, hosting in the parent it already carries
    // probe: …and BOTH land: the winner's FIELDS are inlined AND the new child is in the group
    [Fact]
    public void ForwardFirstThenCreateInlinesTheWinnerAndKeepsTheChild()
    {
        var pre = ForwardTools.Forward(_w.Svc, formids: new[] { _w.TopicFid }, source: _w.ReplacerName, patch: "W2Inline");
        var prePath = _w.ArtifactPathFrom(pre);
        Assert.NotNull(prePath);
        var child = CreateTools.Create(_w.Svc, into: Path.GetFileName(prePath!),
            records: Json($$"""[{"record_type":"DialogResponses","editorid":"W2InlinedLine","parent":"{{_w.TopicFid}}"}]"""));
        Assert.StartsWith("extended ", child);
        Assert.Equal("Winner Topic", TopicNameIn(prePath!, _w.TopicKey));
        Assert.Contains("W2InlinedLine", ChildLinesIn(prePath!, _w.TopicKey));
    }
}
