using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// What a comparison batch reads: a delta row whose subject holds nothing walks no reference plugin, and an absent
/// plugin is explained from the profile once a call, not once a row, on the tree, the delta and the post-state pole.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBatchReadTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    public ComparisonBatchReadTests(OwnedChildFixture f) => _w = f.W;

    static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>The top plugin holds no version of CellG, so every row refuses on the subject: the batch walks the
    /// subject plugin and not the winner's.</summary>
    [Fact]
    public void ADeltaWhoseSubjectHoldsNothingDoesNotWalkTheReference()
    {
        Assert.DoesNotContain(_w.TopName, _w.Svc.CaptureView().TouchingPlugins(_w.CellG)!);
        var project = new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } };
        long before = _w.Svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(_w.CellG) }, source: Json("\"" + _w.TopName + "\""),
                                     versus: Json("\"winner\""), project: project);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(1, _w.Svc.Counters.CollectPasses - before);
    }

    [Theory]
    [InlineData("tree", null)]
    [InlineData("delta", null)]
    [InlineData("delta", "{\"overlay\": \"skypatcher\", \"state\": \"post\"}")]
    public void AbsentIdsExplainTheirPluginOnce(string form, string? source)
    {
        var ids = Enumerable.Range(0x800, 50).Select(i => $"{i:X6}:HcAbsentComparison.esp").ToArray();
        var project = new RecordsTools.RecordsProject { form = form, fields = new[] { "EditorID" } };
        System.Text.Json.JsonElement? versus = form == "delta" ? Json("\"winner\"") : null;
        System.Text.Json.JsonElement? src = source is null ? null : Json(source);
        long before = _w.Svc.Counters.AbsenceExplains;
        var r = RecordsTools.Records(_w.Svc, formids: ids, source: src, versus: versus, project: project, counts_only: true);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(1, _w.Svc.Counters.AbsenceExplains - before);
    }
}
