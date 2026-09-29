using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ApplyGuardWorld;

namespace HousecarlMcpTests;

/// <summary>
/// What a comparison batch reads: a delta walks no pole plugin for a row that pole holds nothing of, and an absent
/// plugin is explained from the profile once a call, not once a row, on every list lane that names it.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComparisonBatchReadTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    public ComparisonBatchReadTests(OwnedChildFixture f) => _w = f.W;

    const string Post = "{\"overlay\": \"skypatcher\", \"state\": \"post\"}";
    const string Pre = "{\"overlay\": \"skypatcher\", \"state\": \"pre\"}";

    static RecordsTools.RecordsProject Delta => new() { form = "delta", fields = new[] { "EditorID" } };

    static string[] AbsentIds => Enumerable.Range(0x800, 50).Select(i => $"{i:X6}:HcAbsentComparison.esp").ToArray();

    long Walks(string[] ids, string source, string versus, Func<string, bool>? expect = null)
    {
        long before = _w.Svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_w.Svc, formids: ids, source: Je(source), versus: Je(versus), project: Delta);
        Assert.False(r.StartsWith("error:"), r);
        if (expect is not null) Assert.True(expect(r), r);
        return _w.Svc.Counters.CollectPasses - before;
    }

    /// <summary>The top plugin holds no version of CellG, so neither the subject plugin nor the winner's is walked.</summary>
    [Fact]
    public void ADeltaWhoseSubjectHoldsNothingDoesNotWalkTheReference()
    {
        Assert.DoesNotContain(_w.TopName, _w.Svc.CaptureView().TouchingPlugins(_w.CellG)!);
        Assert.Equal(0, Walks(new[] { OwnedChildWorld.Fid(_w.CellG) }, "\"" + _w.TopName + "\"", "\"winner\""));
    }

    /// <summary>A post-state subject whose replay cannot be set up holds nothing, so the winners are not walked.</summary>
    [Fact]
    public void APostStateSubjectWhoseReplayIsUnavailableDoesNotWalkTheReference()
    {
        var ids = new[] { OwnedChildWorld.Fid(_w.Weapon), OwnedChildWorld.Fid(_w.CellG) };
        _w.Svc.ReadArea.AfterReadPinForGuard = () => throw new IOException("the asset build is down for this test");
        try
        {
            Assert.Equal(0, Walks(ids, Post, "\"winner\"", r => r.Contains("could not be discovered")));
        }
        finally { _w.Svc.ReadArea.AfterReadPinForGuard = null; }
    }

    /// <summary>A post-state subject that holds its records declares them to the reference, which walks each winner's
    /// plugin once for the chunk.</summary>
    [Fact]
    public void APostStateSubjectThatHoldsItsRecordsGathersTheReference()
    {
        var view = _w.Svc.CaptureView();
        int winners = new[] { _w.Weapon, _w.CellG }.Select(k => view.ResolveWinner(k)!.Value.WinnerPlugin).Distinct().Count();
        Assert.Equal(winners, Walks(new[] { OwnedChildWorld.Fid(_w.Weapon), OwnedChildWorld.Fid(_w.CellG) }, Post, "\"winner\"",
                                    r => r.Contains(" 0 error(s)")));
    }

    /// <summary>An off-order subject that holds its record declares it to the reference, which walks the winner's plugin.</summary>
    [Fact]
    public void AnOffOrderSubjectThatHoldsItsRecordGathersTheReference()
    {
        var dir = Path.Combine(_w.Root, "instance", "mods", "OffTopMod");
        Directory.CreateDirectory(dir);
        File.Copy(_w.PluginPaths[2], Path.Combine(dir, "HcOcTopOff.esp"), overwrite: true);
        Assert.Equal(1, Walks(new[] { OwnedChildWorld.Fid(_w.Weapon) }, "\"HcOcTopOff.esp\"", "\"winner\"", r => r.Contains(" 0 error(s)")));
    }

    /// <summary>A winner subject that holds CellG against a named versus= that does not walks only the winner's plugin.</summary>
    [Fact]
    public void ANamedReferenceThatHoldsNothingIsNotWalked() =>
        Assert.Equal(1, Walks(new[] { OwnedChildWorld.Fid(_w.CellG) }, "\"winner\"", "\"" + _w.TopName + "\""));

    [Theory]
    [InlineData("tree", null)]
    [InlineData("delta", null)]
    [InlineData("delta", Post)]
    [InlineData("delta", Pre)]
    [InlineData("fields", null)]
    [InlineData("fields", Post)]
    [InlineData("info_order", null)]
    public void AbsentIdsExplainTheirPluginOnce(string form, string? source)
    {
        var project = new RecordsTools.RecordsProject { form = form, fields = form == "info_order" ? null : new[] { "EditorID" } };
        System.Text.Json.JsonElement? versus = form == "delta" ? Je("\"winner\"") : null;
        System.Text.Json.JsonElement? src = source is null ? null : Je(source);
        long before = _w.Svc.Counters.AbsenceExplains;
        var r = RecordsTools.Records(_w.Svc, formids: AbsentIds, source: src, versus: versus, project: project);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("HcAbsentComparison.esp", r);
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
        var project = new RecordsTools.RecordsProject { form = "delta", fields = new[] { "EditorID" } };
        long before = _w.Svc.Counters.CollectPasses;
        var r = RecordsTools.Records(_w.Svc, formids: _w.PlainContestedIds.ToArray(), source: Je("\"" + _w.OffOrderName + "\""),
                                     versus: Je("\"" + _w.MasterName + "\""), project: project);
        Assert.False(r.StartsWith("error:"), r);
        Assert.Contains("subject:", r);
        Assert.Equal(0, _w.Svc.Counters.CollectPasses - before);
    }
}
