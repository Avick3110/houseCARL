using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The DialogTopic SNAM marker (#131) on the write and validate paths: a create fills a blank marker from the subtype
/// and says so, never overrides an explicit one, and refuses an unmodeled subtype; an edit of the subtype syncs the
/// marker; the validator calls a blank marker a Problem, or a Warning on an override. Migrated from the
/// dialogue-subtype-marker-guard probe.
/// </summary>
[Trait("tier", "integration")]
public sealed class DialogueSubtypeMarkerWriteTests : IClassFixture<DialogueSubtypeMarkerWorld>
{
    readonly DialogueSubtypeMarkerWorld _w;
    public DialogueSubtypeMarkerWriteTests(DialogueSubtypeMarkerWorld w) => _w = w;

    WritePatchBuilder.CreateOutcome CreateTopic(string edid, string patch, params BulkOp[] ops) =>
        _w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "DialogTopic", Editorid = edid, Operations = ops } }, patch, null);

    static BulkOp Op(string field, string value, FormKey? on = null) =>
        new() { Formid = on?.ToString(), FieldPath = field, Verb = "Set", Value = value };

    /// <summary>The topic's SNAM marker as written to the file on disk.</summary>
    static string? Snam(string patchPath, FormKey topic)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        return ov.DialogTopics.FirstOrDefault(t => t.FormKey == topic)?.SubtypeName.Type;
    }

    // AUTOFILL Subtype=Hello, no marker -> SNAM auto-set to HELO + reported
    [Fact]
    public void ACreatedHelloTopicGetsTheHeloMarkerAndSaysSo()
    {
        var o = CreateTopic("HcSnamAutofill", "HcSnamAF", Op("Subtype", "Hello"));
        Assert.True(o.Success, o.Error);
        var created = Assert.Single(o.Created);
        Assert.Equal("HELO", Snam(o.OutputPath, created.FormKey));
        Assert.Contains(created.Ops, op => op.Label.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
    }

    // DEFAULT-CUST bare topic (Subtype defaults Custom) -> SNAM auto-set to CUST
    [Fact]
    public void ABareCreatedTopicGetsTheCustMarker()
    {
        var o = CreateTopic("HcSnamBare", "HcSnamBare");
        Assert.True(o.Success, o.Error);
        Assert.Equal("CUST", Snam(o.OutputPath, Assert.Single(o.Created).FormKey));
    }

    // EXPLICIT-WINS explicit SubtypeName=GBYE kept (not overridden to CUST)
    [Fact]
    public void AnExplicitMarkerOnCreateIsKept()
    {
        var o = CreateTopic("HcSnamExplicit", "HcSnamEx", Op("SubtypeName", "GBYE"));
        Assert.True(o.Success, o.Error);
        Assert.Equal("GBYE", Snam(o.OutputPath, Assert.Single(o.Created).FormKey));
    }

    // UNMODELED-REFUSE out-of-range Subtype refused loud, nothing written
    [Fact]
    public void AnUnmodeledSubtypeWithNoMarkerIsRefusedAndNothingWritten()
    {
        var o = CreateTopic("HcSnamOob", "HcSnamOob", Op("Subtype", "105"));
        Assert.False(o.Success);
        Assert.Contains("modeled", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marker", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(_w.ModsDir, "*HcSnamOob*"));
    }

    // EDIT-SYNC set Subtype=Goodbye -> SNAM synced to GBYE + reported
    [Fact]
    public void EditingTheSubtypeSyncsTheMarkerAndSaysSo()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op("Subtype", "Goodbye", _w.MarkedTopic) }, "HcSnamEdit", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal("GBYE", Snam(o.OutputPath, _w.MarkedTopic));
        Assert.Contains(o.Ops, op => op.Label.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
    }

    // EDIT-EXPLICIT-WINS Subtype+SubtypeName in one call keeps explicit IDLE (not synced to GBYE)
    [Fact]
    public void AnExplicitMarkerInTheSameEditIsKept()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op("Subtype", "Goodbye", _w.MarkedTopic), Op("SubtypeName", "IDLE", _w.MarkedTopic) },
            "HcSnamEditExplicit", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal("IDLE", Snam(o.OutputPath, _w.MarkedTopic));
    }

    // EDIT-NO-TOUCH non-Subtype edit leaves SNAM untouched (HELO)
    [Fact]
    public void AnEditOfAnotherFieldLeavesTheMarker()
    {
        var o = _w.Svc.ApplyEdits(new[] { Op("Priority", "80", _w.MarkedTopic) }, "HcSnamEditPriority", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal("HELO", Snam(o.OutputPath, _w.MarkedTopic));
        Assert.DoesNotContain(o.Ops, op => op.Label.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
    }

    // VALIDATE-BLANK blank marker -> Problem
    [Fact]
    public void ABlankMarkerIsAProblem()
    {
        var topic = Assert.Single(_w.Svc.ValidateDialogue(_w.BlankTopic).Topics);
        Assert.Contains(topic.Issues, i => i.Severity == DialogueIssueSeverity.Problem
                                           && i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase)
                                           && i.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    // VALIDATE-BLANK HELO marker -> clean
    [Fact]
    public void AMarkedTopicRaisesNoMarkerIssue()
    {
        var topic = Assert.Single(_w.Svc.ValidateDialogue(_w.MarkedTopic).Topics);
        Assert.DoesNotContain(topic.Issues, i => i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
    }

    // OVERRIDE-WARN blank-SNAM override -> Warning (not Problem), names 'override'
    [Fact]
    public void ABlankMarkerOnAnOverrideIsAWarningNamingTheOverride()
    {
        var topic = Assert.Single(_w.Svc.ValidateDialogue(_w.OverriddenTopic).Topics);
        var issue = Assert.Single(topic.Issues, i => i.Message.Contains("SubtypeName", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DialogueIssueSeverity.Warning, issue.Severity);
        Assert.Contains("override", issue.Message, StringComparison.OrdinalIgnoreCase);
    }
}
