using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>composes= builds every element itself, so a value=, values= or entries= beside it is refused before any
/// work, on apply and create and every lane, rather than dropped unwritten (#1048).</summary>
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
            target: "HcW3Master.esm", inPlace: true, acknowledge: true).Error);

    [Fact]
    public void CreateWithValueBesideComposesIsRefusedAndCreatesNothing()
    {
        var o = _w.Svc.CreateRecordsBatch(new[]
        {
            new CreateOp
            {
                RecordType = "LeveledItem", Editorid = "HcStrayCreateList",
                Operations = new[] { new BulkOp { FieldPath = "Entries", Verb = "Add", Value = "5", Composes = new[] { Entry(1) } } },
            },
        }, "HcStrayCreate", null);
        Assert.False(o.Success);
        Assert.Contains("no value=", o.Error);
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains("HcStrayCreate"));
    }

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
