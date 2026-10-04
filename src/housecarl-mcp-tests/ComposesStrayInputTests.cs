using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>compose= and composes= build their elements themselves, so a value=, values= or entries= beside either is
/// refused before any work, on apply and create and every lane, rather than dropped unwritten (#1048). Empty counts as absent.</summary>
[Trait("tier", "integration")]
public sealed class ComposesStrayInputTests : IClassFixture<ComposesBatchWorld>
{
    readonly ComposesBatchWorld _w;
    public ComposesStrayInputTests(ComposesBatchWorld w) => _w = w;

    StructInput Entry(int level) => new()
    {
        Type = "LeveledItemEntry",
        Sets = new[]
        {
            new NestedSet { Path = "Data.Level", Value = level.ToString() },
            new NestedSet { Path = "Data.Count", Value = "1" },
            new NestedSet { Path = "Data.Reference", Value = _w.WeaponFid },
        },
    };

    BulkOp Op(string? value = null, string[]? values = null, Dictionary<string, string>? entries = null) => new()
    {
        Formid = _w.ListFid, FieldPath = "Entries", Verb = "Add",
        Value = value, Values = values, Entries = entries, Composes = new[] { Entry(1), Entry(2) },
    };

    void NothingWritten(WritePatchBuilder.PatchOutcome o, string patch)
    {
        Assert.False(o.Success);
        Assert.True(string.IsNullOrEmpty(o.OutputPath));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains(patch));
    }

    [Fact]
    public void ApplyWithValueBesideComposesIsRefusedAndWritesNothing()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op(value: "5") }, "HcStrayValue", null);
        NothingWritten(o, "HcStrayValue");
        Assert.Contains("no value=", o.Error);
    }

    [Fact]
    public void ApplyWithValuesBesideComposesIsRefused()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op(values: new[] { _w.WeaponFid }) }, "HcStrayValues", null);
        NothingWritten(o, "HcStrayValues");
        Assert.Contains("no values=", o.Error);
    }

    [Fact]
    public void ApplyWithEntriesBesideComposesIsRefused()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op(entries: new() { ["0"] = "1" }) }, "HcStrayEntries", null);
        NothingWritten(o, "HcStrayEntries");
        Assert.Contains("no entries=", o.Error);
    }

    // The issue's case: the dry run printed "Add Entries = 5 -> would become [list: ...]" for a value never written.
    [Fact]
    public void TheDryRunRefusesTooAndPrintsNoWouldBeRow()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op(value: "5") }, "HcStrayDry", null, dryRun: true);
        var text = WriteTools.Render(o);
        Assert.DoesNotContain("would become", text);
        Assert.StartsWith("error: ", text);
        Assert.Contains("no value=", text);
        Assert.False(o.Success);
    }

    [Fact]
    public void TheIntoLaneRefusesBeforeLookingForThePatch()
        => Assert.Contains("no value=", _w.Svc.ApplyEdits(new[] { Op(value: "5") }, null, "HcStrayIntoMissing").Error);

    [Fact]
    public void TheInPlaceLaneRefusesBeforeTouchingTheTarget()
        => Assert.Contains("no value=", _w.Svc.ApplyEdits(new[] { Op(value: "5") }, null, null,
            target: "HcW3Master.esm", inPlace: true, acknowledge: true, dryRun: true).Error);

    [Fact]
    public void ApplyWithValueBesideSingularComposeIsRefused()
    {
        var op = new BulkOp { Formid = _w.ListFid, FieldPath = "Entries", Verb = "Add", Value = "5", Compose = Entry(1) };
        var o = _w.Svc.ApplyEdits(new[] { op }, "HcStraySingular", null);
        NothingWritten(o, "HcStraySingular");
        Assert.Contains("compose= builds the element itself, so it takes no value=", o.Error);
    }

    // CopyFrom refuses every authored input, so its refusal comes first rather than a remedy that would still fail.
    [Fact]
    public void CopyFromWithComposesAndValueGetsTheCopyFromRefusal()
    {
        var op = new BulkOp { Formid = _w.ListFid, FieldPath = "Entries", Verb = "CopyFrom", FromPlugin = "HcW3Master.esm",
                              Value = "5", Composes = new[] { Entry(1) } };
        var o = _w.Svc.ApplyEdits(new[] { op }, "HcStrayCopyFrom", null, dryRun: true);
        Assert.False(o.Success);
        Assert.Contains("takes no value/values/entries/compose/composes", o.Error);
    }

    [Fact]
    public void AnEmptyValuesAndEntriesBesideComposesStillWrites()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op(values: Array.Empty<string>(), entries: new()) }, "HcStrayEmpty", null);
        Assert.True(o.Success, o.Error);
        Assert.True(File.Exists(o.OutputPath));
    }

    WritePatchBuilder.CreateOutcome Create(string patch, BulkOp op) => _w.Svc.CreateRecordsBatch(new[]
    {
        new CreateOp { RecordType = "LeveledItem", Editorid = patch + "List", Operations = new[] { op } },
    }, patch, null);

    void NothingCreated(WritePatchBuilder.CreateOutcome o, string patch, string expected)
    {
        Assert.False(o.Success);
        Assert.Contains(expected, o.Error);
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains(patch));
    }

    [Fact]
    public void CreateWithValueBesideComposesIsRefusedAndCreatesNothing()
        => NothingCreated(Create("HcStrayCreate", new BulkOp { FieldPath = "Entries", Verb = "Add", Value = "5", Composes = new[] { Entry(1) } }),
            "HcStrayCreate", "no value=");

    [Fact]
    public void CreateWithValuesBesideComposesIsRefused()
        => NothingCreated(Create("HcStrayCreateValues", new BulkOp { FieldPath = "Entries", Verb = "Add", Values = new[] { _w.WeaponFid }, Composes = new[] { Entry(1) } }),
            "HcStrayCreateValues", "no values=");

    [Fact]
    public void CreateWithEntriesBesideComposesIsRefused()
        => NothingCreated(Create("HcStrayCreateEntries", new BulkOp { FieldPath = "Entries", Verb = "Add", Entries = new() { ["0"] = "1" }, Composes = new[] { Entry(1) } }),
            "HcStrayCreateEntries", "no entries=");

    [Fact]
    public void CreateCopyFromWithComposesAndValueGetsTheCopyFromRefusal()
        => NothingCreated(Create("HcStrayCreateCopy", new BulkOp { FieldPath = "Entries", Verb = "CopyFrom", Value = "5", Composes = new[] { Entry(1) } }),
            "HcStrayCreateCopy", "isn't valid when CREATING");

    [Fact]
    public void AComposesOnlyOpStillDryRunsAndWrites()
    {
        var dry = _w.Svc.ApplyEdits(new[] { Op() }, "HcStrayCleanDry", null, dryRun: true);
        Assert.True(dry.Success, dry.Error);
        var o = _w.Svc.ApplyEdits(new[] { Op() }, "HcStrayClean", null);
        Assert.True(o.Success, o.Error);
        Assert.True(File.Exists(o.OutputPath));
    }
}
