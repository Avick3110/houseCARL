using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The in-place dry run on both copies of the consent bypass (the former <c>dry-run-guard</c> probe, arm F for
/// ApplyEdits and arm K for ForwardRecordsInPlace): no prompt, no consent recorded, the pending consent noted, and
/// nothing written — the target, its mod folder's marker and .seq, and the consent store all unchanged.
/// </summary>
[Trait("tier", "integration")]
public sealed class DryRunGuardInPlaceTests : IDisposable
{
    readonly DryRunGuardWorld _w = new();

    // F: dry run + acknowledge=true succeeds without the prompt.
    [Fact]
    public void AnInPlaceDryRunWithAcknowledgeSucceedsWithoutThePrompt()
    {
        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 61) }, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true, acknowledge: true, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        Assert.True(o.InPlace);
        Assert.False(o.NeedsAcknowledge);
    }

    // F: dry run without acknowledge succeeds AND notes the pending consent; the target file is byte-identical
    // (strengthened: nothing in the instance or the consent store changed).
    [Fact]
    public void AnInPlaceDryRunWithoutAcknowledgeNotesThePendingConsentAndWritesNothing()
    {
        var before = _w.Snapshot();

        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 62) }, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.False(o.NeedsAcknowledge);
        Assert.Contains("PENDING", o.Note);
        Assert.Equal(before, _w.Snapshot());
    }

    // F: a REAL in-place write afterwards still shows the first-touch prompt (no dry run recorded consent).
    [Fact]
    public void ARealInPlaceWriteAfterAnAcknowledgedDryRunStillPrompts()
    {
        var dry = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 61) }, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true, acknowledge: true, dryRun: true);
        Assert.True(dry.Success, dry.Error);

        var real = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 63) }, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true);

        Assert.True(real.NeedsAcknowledge);
    }

    // K: dry forward succeeds without the prompt AND notes the pending consent; the target file is byte-identical
    // (strengthened: nothing in the instance or the consent store changed).
    [Fact]
    public void AnInPlaceForwardDryRunNotesThePendingConsentAndWritesNothing()
    {
        var before = _w.Snapshot();

        var o = _w.Svc.ForwardRecords(new[] { _w.Fid }, DryRunGuardWorld.MasterFile, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        Assert.True(o.InPlace);
        Assert.False(o.NeedsAcknowledge);
        Assert.Contains("PENDING", o.Note);
        Assert.Equal(before, _w.Snapshot());
    }

    // K: a REAL in-place forward afterwards still shows the first-touch prompt (no dry run recorded consent).
    [Fact]
    public void ARealInPlaceForwardAfterAnAcknowledgedDryRunStillPrompts()
    {
        var dry = _w.Svc.ForwardRecords(new[] { _w.Fid }, DryRunGuardWorld.MasterFile, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true, acknowledge: true, dryRun: true);
        Assert.True(dry.Success, dry.Error);

        var real = _w.Svc.ForwardRecords(new[] { _w.Fid }, DryRunGuardWorld.MasterFile, null, null,
            target: DryRunGuardWorld.UserFile, inPlace: true);

        Assert.True(real.NeedsAcknowledge);
    }

    public void Dispose() => _w.Dispose();
}
