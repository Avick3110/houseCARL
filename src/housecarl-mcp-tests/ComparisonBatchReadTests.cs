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

    static RecordsTools.RecordsProject Delta => new() { form = "delta", fields = new[] { "EditorID" } };

    long Walks(string[] ids, string source, string versus)
    {
        long before = _w.Svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_w.Svc, formids: ids, source: Json(source), versus: Json(versus), project: Delta);
        Assert.False(r.StartsWith("error:"), r);
        return _w.Svc.Counters.CollectPasses - before;
    }

    static string[] AbsentIds => Enumerable.Range(0x800, 50).Select(i => $"{i:X6}:HcAbsentComparison.esp").ToArray();

    /// <summary>The top plugin holds no version of CellG, so the row refuses on the subject from the index: neither
    /// the subject plugin nor the winner's is walked.</summary>
    [Fact]
    public void ADeltaWhoseSubjectHoldsNothingDoesNotWalkTheReference()
    {
        Assert.DoesNotContain(_w.TopName, _w.Svc.CaptureView().TouchingPlugins(_w.CellG)!);
        Assert.Equal(0, Walks(new[] { OwnedChildWorld.Fid(_w.CellG) }, "\"" + _w.TopName + "\"", "\"winner\""));
    }

    /// <summary>A post-state subject holds nothing when the record has no winner, so a named reference is not walked
    /// for those rows.</summary>
    [Fact]
    public void APostStateSubjectThatHoldsNothingDoesNotWalkTheReference() =>
        Assert.Equal(0, Walks(AbsentIds, "{\"overlay\": \"skypatcher\", \"state\": \"post\"}", "\"" + _w.TopName + "\""));

    /// <summary>The mirror: a winner subject that holds CellG against a named versus= that does not walks only the
    /// winner's plugin, not the versus plugin to its end.</summary>
    [Fact]
    public void ANamedReferenceThatHoldsNothingIsNotWalked() =>
        Assert.Equal(1, Walks(new[] { OwnedChildWorld.Fid(_w.CellG) }, "\"winner\"", "\"" + _w.TopName + "\""));

    [Theory]
    [InlineData("tree", null)]
    [InlineData("delta", null)]
    [InlineData("delta", "{\"overlay\": \"skypatcher\", \"state\": \"post\"}")]
    [InlineData("delta", "{\"overlay\": \"skypatcher\", \"state\": \"pre\"}")]
    public void AbsentIdsExplainTheirPluginOnce(string form, string? source)
    {
        var ids = AbsentIds;
        var project = new RecordsTools.RecordsProject { form = form, fields = new[] { "EditorID" } };
        System.Text.Json.JsonElement? versus = form == "delta" ? Json("\"winner\"") : null;
        System.Text.Json.JsonElement? src = source is null ? null : Json(source);
        long before = _w.Svc.Counters.AbsenceExplains;
        var r = RecordsTools.Records(_w.Svc, formids: ids, source: src, versus: versus, project: project, counts_only: true);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Equal(1, _w.Svc.Counters.AbsenceExplains - before);
    }
}

/// <summary>An off-order subject that lacks the records refuses on its own file, so the in-order reference is not walked.</summary>
[Trait("tier", "integration")]
public sealed class ComparisonBatchOffOrderReadTests : IClassFixture<RenderCostFixture>
{
    readonly RenderCostWorld _w;
    public ComparisonBatchOffOrderReadTests(RenderCostFixture f) => _w = f.W;

    [Fact]
    public void AnOffOrderSubjectThatHoldsNothingDoesNotWalkTheReference()
    {
        static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();
        var ids = _w.PlainContestedIds.ToArray();
        var project = new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } };
        long before = _w.Svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_w.Svc, formids: ids, source: Json("\"" + _w.OffOrderName + "\""),
                                     versus: Json("\"" + _w.MasterName + "\""), project: project);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("subject:", r);
        Assert.Equal(0, _w.Svc.Counters.CollectPasses - before);
    }
}
