using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The patch lane's dry run (the former <c>dry-run-guard</c> probe, arms A B C D E G I J): the real pipeline halted
/// before the serialize, so it writes nothing, refuses as the real call does, and predicts what the real write
/// produces. "Writes nothing" is a snapshot of the whole instance and the consent store, before and after.
/// </summary>
[Trait("tier", "integration")]
public sealed class DryRunGuardPatchLaneTests : IDisposable
{
    readonly DryRunGuardWorld _w = new();

    // A: dry run succeeds with DryRun flagged; bytes=0; per-op After carries the would-be value; expected-master preview.
    [Fact]
    public void AFreshPatchDryRunReportsTheWouldBeEditAndMasters()
    {
        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 77) }, "DryA", null, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        Assert.False(o.InPlace);
        Assert.Equal(0, o.Bytes);
        Assert.Contains("77", Assert.Single(o.Ops).After);
        Assert.Equal(new[] { DryRunGuardWorld.MasterFile }, o.Masters);
    }

    // A: no mod folder appeared (strengthened: nothing in the instance or the consent store changed).
    [Fact]
    public void AFreshPatchDryRunWritesNothing()
    {
        var before = _w.Snapshot();
        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 77) }, "DryA", null, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    // A: render leads with DRY RUN / nothing-written and never reads like a write.
    [Fact]
    public void ADryRunRenderLeadsWithDryRunAndNeverSaysWrote()
    {
        var text = WriteTools.Render(_w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 77) }, "DryA", null, dryRun: true));

        Assert.StartsWith(WriteSentences.DryRunHeader, text);
        Assert.Contains("NOTHING was written", text);
        Assert.DoesNotContain("wrote ", text);
    }

    // B: a malformed op refuses with the EXACT string the real call gives; neither attempt left a folder behind
    // (strengthened: the probe looked for a folder named "DryB", which the real folder name never is).
    [Fact]
    public void AMalformedOpRefusesIdenticallyDryAndRealAndLeavesNothing()
    {
        var bad = new[] { new BulkOp { Formid = _w.Fid, FieldPath = "BasicStats.Nope", Verb = "Set", Value = "1" } };
        var before = _w.Snapshot();

        var dry = _w.Svc.ApplyEdits(bad, "DryB", null, dryRun: true);
        var real = _w.Svc.ApplyEdits(bad, "DryB", null);

        Assert.False(dry.Success);
        Assert.False(real.Success);
        Assert.Contains("Nope", dry.Error);
        Assert.Equal(real.Error, dry.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    // C: prediction parity — would-be path, After value and expected masters equal the subsequent real write's.
    [Fact]
    public void ADryRunPredictsThePathValueAndMastersOfTheRealWrite()
    {
        var op = new[] { DryRunGuardWorld.DamageOp(_w.Fid, 88) };
        var before = _w.Snapshot();
        var dry = _w.Svc.ApplyEdits(op, "DryC", null, dryRun: true);
        Assert.Equal(before, _w.Snapshot());
        var real = _w.Svc.ApplyEdits(op, "DryC", null);

        Assert.True(dry.Success, dry.Error);
        Assert.True(real.Success, real.Error);
        Assert.True(File.Exists(real.OutputPath));
        Assert.Equal(real.OutputPath, dry.OutputPath);
        Assert.Equal(real.Ops[0].After, dry.Ops[0].After);
        Assert.Equal(real.Masters, dry.Masters, StringComparer.OrdinalIgnoreCase);
    }

    // D: into= dry run succeeds with Extended flagged; the extended patch's on-disk bytes are unchanged (strengthened:
    // the whole instance is unchanged).
    [Fact]
    public void AnIntoDryRunLeavesTheExtendedPatchUntouched()
    {
        var seed = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 50) }, "DryD", null);
        Assert.True(seed.Success, seed.Error);
        var before = _w.Snapshot();

        var o = _w.Svc.ApplyEdits(new[] { new BulkOp { Formid = _w.Fid, FieldPath = "BasicStats.Weight", Verb = "Set", Value = "9" } },
            null, "DryD", dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        Assert.True(o.Extended);
        Assert.Equal(seed.OutputPath, o.OutputPath);
        Assert.Equal(before, _w.Snapshot());
    }

    // E: unresolvable FormLink — the dry run refuses naming the missing plugin (NOT active).
    [Fact]
    public void ALinkIntoAnAbsentPluginIsRefusedByTheDryRunNamingIt()
    {
        var ghost = new[] { new BulkOp { Formid = _w.Fid, FieldPath = "Keywords", Verb = "Add", Value = "000ABC:Ghost.esp" } };
        var before = _w.Snapshot();

        var dry = _w.Svc.ApplyEdits(ghost, "DryE", null, dryRun: true);

        Assert.False(dry.Success);
        Assert.Contains("Ghost.esp", dry.Error);
        Assert.Contains("NOT active", dry.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    // E: the real write fails AT the serialize boundary (MissingModException) — the condition the dry refusal
    // pre-empts; neither attempt left a folder behind (strengthened: the whole instance, not a folder name never used).
    [Fact]
    public void TheSameLinkFailsTheRealWriteOnlyAtTheSerialize()
    {
        var ghost = new[] { new BulkOp { Formid = _w.Fid, FieldPath = "Keywords", Verb = "Add", Value = "000ABC:Ghost.esp" } };
        var before = _w.Snapshot();

        var real = _w.Svc.ApplyEdits(ghost, "DryE", null);

        Assert.False(real.Success);
        Assert.Contains("MissingModException", real.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    // G: in_place⇔target and in_place⊥into= refusals fire as on the real path.
    [Fact]
    public void TheLaneContractRefusesIdenticallyUnderDryRun()
    {
        var op = new[] { DryRunGuardWorld.DamageOp(_w.Fid, 1) };
        var before = _w.Snapshot();

        var noTarget = _w.Svc.ApplyEdits(op, null, null, inPlace: true, dryRun: true);
        var withInto = _w.Svc.ApplyEdits(op, null, "DryD", target: DryRunGuardWorld.UserFile, inPlace: true, dryRun: true);

        Assert.Equal(before, _w.Snapshot());

        Assert.False(noTarget.Success);
        Assert.Contains("requires target=", noTarget.Error);
        Assert.Equal(_w.Svc.ApplyEdits(op, null, null, inPlace: true).Error, noTarget.Error);
        Assert.False(withInto.Success);
        Assert.Contains("mutually exclusive", withInto.Error);
        Assert.Equal(_w.Svc.ApplyEdits(op, null, "DryD", target: DryRunGuardWorld.UserFile, inPlace: true).Error, withInto.Error);
    }

    // I: compose missing a required arm — the dry run refuses with the named null-arm framing ("Data arm", "required").
    [Fact]
    public void AConditionWithoutItsDataArmIsRefusedByTheDryRunNamed()
    {
        var before = _w.Snapshot();

        var dry = _w.Svc.ApplyEdits(ConditionWithoutData(), "DryI", null, dryRun: true);

        Assert.False(dry.Success);
        Assert.Contains("Data arm", dry.Error);
        Assert.Contains("required", dry.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    // I: the real write refuses too (the serialize null-arm re-stamp); neither attempt left a folder behind
    // (strengthened: the whole instance, not a folder name never used; and the real refusal carries the same null-arm
    // words as the dry one, so the dry refusal predicts this failure and not some other).
    [Fact]
    public void AConditionWithoutItsDataArmIsRefusedByTheRealWriteToo()
    {
        var before = _w.Snapshot();

        var real = _w.Svc.ApplyEdits(ConditionWithoutData(), "DryI", null);

        Assert.False(real.Success);
        Assert.Contains("Data arm", real.Error);
        Assert.Contains("required", real.Error);
        Assert.Equal(before, _w.Snapshot());
    }

    BulkOp[] ConditionWithoutData() => new[] { new BulkOp { Formid = _w.FidMg, FieldPath = "Conditions", Verb = "Add",
        Compose = new StructInput { Type = "ConditionFloat", Fields = new() { ["ComparisonValue"] = "1" } } } };

    // J: full_readback dry run — in-memory read-back present and clean; no folder appeared despite the deep read-back
    // (strengthened: the whole instance, not a folder name never used).
    [Fact]
    public void AFullReadbackDryRunReadsTheInMemoryRecordAndWritesNothing()
    {
        var before = _w.Snapshot();

        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 91) }, "DryJ", null, fullReadback: true, dryRun: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.DryRun);
        var rb = Assert.Single(o.ReadBack!);
        Assert.Null(rb.Error);
        Assert.NotNull(rb.Record);
        Assert.Equal(before, _w.Snapshot());
    }

    // J: render labels the deep dump as the in-memory preview (never implying a file exists) and carries the would-be value.
    [Fact]
    public void AFullReadbackDryRunRenderLabelsTheDumpAsTheInMemoryPreview()
    {
        var o = _w.Svc.ApplyEdits(new[] { DryRunGuardWorld.DamageOp(_w.Fid, 91) }, "DryJ", null, fullReadback: true, dryRun: true);

        var text = WriteTools.Render(o, 0, fullDump: true);

        var preview = text.IndexOf("full preview", StringComparison.Ordinal);
        Assert.True(preview >= 0, text);
        Assert.Contains("nothing is on disk", text);
        Assert.DoesNotContain("re-read from the patch file on disk", text);
        Assert.Contains("91", text[preview..]);
    }

    public void Dispose() => _w.Dispose();
}
