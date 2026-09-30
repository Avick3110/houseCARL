using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The forward dry run (the former <c>dry-run-guard</c> probe, arm H): a would-copy report, nothing on disk, the
/// would-be phrasing in the render, and a refusal identical to the real call's.
/// </summary>
[Trait("tier", "integration")]
public sealed class DryRunGuardForwardTests : IDisposable
{
    readonly DryRunGuardWorld _w = new();

    // H: dry forward reports the would-be copy (source = the master, not already the winner); no mod folder appeared
    // (strengthened: nothing in the instance or the consent store changed).
    [Fact]
    public void AForwardDryRunReportsTheWouldBeCopyAndWritesNothing()
    {
        var before = _w.Snapshot();

        var o = _w.Svc.ForwardRecords(new[] { _w.Fid }, DryRunGuardWorld.MasterFile, "DryH", null, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        var f = Assert.Single(o.Forwarded);
        Assert.Equal(DryRunGuardWorld.MasterFile, f.FromPlugin, ignoreCase: true);
        Assert.False(f.WasAlreadyWinner);
        Assert.Equal(before, _w.Snapshot());
    }

    // H: forward render uses the would-be phrasing (+ the expected-masters preview, not a real header).
    [Fact]
    public void AForwardDryRunRendersTheWouldBePhrasing()
    {
        var text = WriteTools.RenderForward(
            _w.Svc.ForwardRecords(new[] { _w.Fid }, DryRunGuardWorld.MasterFile, "DryH", null, dryRun: true));

        Assert.StartsWith(WriteSentences.DryRunHeader, text);
        Assert.Contains("would be copied from", text);
        Assert.Contains("\nexpected masters: ", text);
        Assert.DoesNotContain("\nmasters: ", text);
    }

    // H: a source that doesn't define the record refuses identically.
    [Fact]
    public void AForwardFromASourceWithoutTheRecordRefusesIdenticallyDryAndReal()
    {
        var before = _w.Snapshot();
        var dry = _w.Svc.ForwardRecords(new[] { _w.Fid2 }, DryRunGuardWorld.UserFile, "DryH2", null, dryRun: true);
        Assert.Equal(before, _w.Snapshot());
        var real = _w.Svc.ForwardRecords(new[] { _w.Fid2 }, DryRunGuardWorld.UserFile, "DryH2", null);

        Assert.False(dry.Success);
        Assert.Contains(DryRunGuardWorld.UserFile, dry.Error);
        Assert.Equal(real.Error, dry.Error);
    }

    public void Dispose() => _w.Dispose();
}
