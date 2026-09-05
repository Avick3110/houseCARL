using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The unbounded reverse question — <c>references=</c> with no <c>types=</c>/<c>plugins=</c> scope —
/// answered off the reverse-reference index, driven through the tool.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsReverseIndexTests : RecordsTestBase
{
    public RecordsReverseIndexTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void AnUnboundedReferencesFindsEveryReferencerWithNoScope()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) });
        Served(r, "HcRecSpellA", "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void AnUnboundedReferencesOverTwoTargetsUnionsThem()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA), Fid(W.MgefB) });
        Served(r, "HcRecSpellA", "HcRecSpellB", "HcRecSpellC");
    }

    [Fact]
    public void AnUnboundedReferencesOnATargetNothingLinksMatchesNothing()
    {
        var r = RecordsTools.Records(Svc, references: new[] { Fid(W.SpellB) });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.DoesNotContain("HcRecSpell", r);
    }

    [Fact]
    public void AnUnboundedNegatedReferencesKeepsTheRecordsThatDoNotLinkTheTarget()
    {
        var r = RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) });
        Served(r, "HcRecSpellB");
        Assert.DoesNotContain("HcRecSpellA", r);
        Assert.DoesNotContain("HcRecSpellC", r);
    }

    [Fact]
    public void AnUnboundedWhereIsStillRefusedNamingTheBound() =>
        Refused(RecordsTools.Records(Svc, where: new[] { "EditorID = HcRecSpellA" }), "where=");

    [Fact]
    public void AnUnboundedReferencesWithAWhereIsStillRefused() =>
        Refused(RecordsTools.Records(Svc, references: new[] { Fid(W.MgefA) },
                                     where: new[] { "EditorID = HcRecSpellA" }), "types=");
}

/// <summary>The index's own lifecycle: what a build costs, that a second call pays nothing, and that a plugin
/// whose bytes changed rebuilds only its own slice. Each test owns its world, because the accounting these assert
/// on is "what THIS call did" and a shared world would have been indexed by whichever test ran first.</summary>
[Trait("tier", "integration")]
public sealed class ReverseIndexLifecycleTests
{
    [Fact]
    public void TheFirstUnboundedCallReportsTheBuildAndItsPerPluginFreshnessKey()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("reverse-reference index", r);
        Assert.Contains("key=", r);
        Assert.Contains("partition", r);
    }

    [Fact]
    public void ANegatedUnboundedReferencesDeclaresThatItsUniverseIsTheWholeOrder()
    {
        using var w = new RecordsWorld();
        var r = RecordsTools.Records(w.Svc, references: new[] { "!" + RecordsWorld.Fid(w.MgefA) });
        Assert.Contains("whole order", r);
    }

    [Fact]
    public void TheIndexIsBuiltOnceAndASecondCallRebuildsNothing()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.True(first.Rebuilt > 0);
        Assert.True(first.Partitions > 1, "the world must hold more than one plugin for the partition claim to mean anything");
        var second = w.Svc.CaptureView().EnsureReverseIndex();
        Assert.Equal(0, second.Rebuilt);
        Assert.Equal(first.Partitions, second.Partitions);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void AChangedPluginRebuildsOnlyItsOwnPartition()
    {
        using var w = new RecordsWorld();
        var first = w.Svc.CaptureView().EnsureReverseIndex();
        File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddHours(1));
        var after = w.Svc.CaptureView().EnsureReverseIndex();   // the capture rebuilds the SNAPSHOT; the index must not follow it wholesale
        Assert.Equal(1, after.Rebuilt);
        Assert.Equal(first.Partitions, after.Partitions);
        Assert.NotEqual(first.Key, after.Key);
    }

    [Fact]
    public void TheIndexSurvivesASnapshotSwapAndStillAnswers()
    {
        using var w = new RecordsWorld();
        var before = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Served(before, "HcRecSpellA");
        File.SetLastWriteTimeUtc(w.OverrideFile, DateTime.UtcNow.AddHours(1));
        var after = RecordsTools.Records(w.Svc, references: new[] { RecordsWorld.Fid(w.MgefA) });
        Served(after, "HcRecSpellA", "HcRecSpellC");
    }

    static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), response);
        foreach (var s in mustName) Assert.Contains(s, response);
    }
}
