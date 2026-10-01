using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The service's create batch over an MO2 instance: a flat create, a child under a master parent or a
/// same-call sibling, an all-or-nothing refusal, the no-parent guidance, and cell creates with their shell report.
/// Migrated from the bulk-create-guard probe.</summary>
[Trait("tier", "integration")]
public sealed class BulkCreateServiceTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-bulk-create-");
    readonly LoadOrderService _svc;
    readonly FormKey _topic, _world;

    public BulkCreateServiceTests()
    {
        var key = new ModKey("HcBcGdMaster", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var topic = m.DialogTopics.AddNew(); topic.EditorID = "HcBcGdTopic";
        var world = m.Worldspaces.AddNew(); world.EditorID = "HcBcGdWorld";
        (_topic, _world) = (topic.FormKey, world.FormKey);
        m.BeginWrite.ToPath(_mo2.InMod("MasterMod", key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(key.FileName + "\r\n", "*" + key.FileName + "\r\n", "+MasterMod\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    WritePatchBuilder.CreateOutcome Create(string patch, params CreateOp[] records) => _svc.CreateRecordsBatch(records, patch, null);

    static List<FormKey> Responses(string path, Func<IDialogTopicGetter, bool> which)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.DialogTopics.Single(which).Responses.Select(x => x.FormKey).ToList();
    }

    // FLAT single flat create still works
    [Fact]
    public void AFlatKeywordCreateLandsAtTheFloor()
    {
        var o = Create("HcBcFlat", new CreateOp { RecordType = "Keyword", Editorid = "HcBcGdKw" });
        Assert.True(o.Success, o.Error);
        Assert.True(Assert.Single(o.Created).FormKey.ID >= 0x800);
    }

    // SINGLE-PARENT INFO into existing topic via parent=
    [Fact]
    public void ALineUnderAMasterTopicLandsUnderIt()
    {
        var o = Create("HcBcSingleParent", new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcN2Info", Parent = _topic.ToString() });
        Assert.True(o.Success, o.Error);
        var line = Assert.Single(o.Created).FormKey;
        Assert.True(line.ID >= 0x800);
        Assert.Contains(line, Responses(o.OutputPath, t => t.FormKey == _topic));
    }

    // ONESHOT bulk_create topic + line (sibling parent)
    [Fact]
    public void ATopicAndItsLineInOneBatchLandTheLineUnderTheNewTopic()
    {
        var o = Create("HcBcOneShot",
            new CreateOp { RecordType = "DialogTopic", Editorid = "HcBcOsTopic" },
            new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcOsL1", Parent = "HcBcOsTopic",
                Operations = new[] { new BulkOp { FieldPath = "Prompt", Verb = "Set", Value = "houseCARL one-shot" } } });
        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.Created.Count);
        Assert.Contains(o.Created[1].FormKey, Responses(o.OutputPath, t => t.EditorID == "HcBcOsTopic"));
    }

    // BATCH-AON one bad spec refuses the whole batch, nothing written
    [Fact]
    public void OneUncreatableSpecRefusesTheWholeBatchAndWritesNothing()
    {
        var o = Create("HcBcAon",
            new CreateOp { RecordType = "Keyword", Editorid = "HcBcAonKw" },
            new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcAonBad" });
        Assert.False(o.Success);
        Assert.Contains("HcBcAonBad", o.Error);
        Assert.Empty(Directory.EnumerateDirectories(_mo2.ModsDir, "houseCARL - HcBcAon*"));
    }

    // GUIDANCE nested-with-no-parent refused + guides to parent= INSIDE the records= element of housecarl_create
    [Fact]
    public void ALineWithNoParentIsToldToPassParentInsideTheRecordsElement()
    {
        var o = Create("HcBcGuidance", new CreateOp { RecordType = "DialogResponses", Editorid = "HcBcNoParent" });
        Assert.False(o.Success);
        Assert.Contains("parent", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(ToolNameMatch.ReferencedAtBoundary(o.Error!, "housecarl_create"), o.Error);
        Assert.Contains("records= element", o.Error);
        Assert.DoesNotContain("or use housecarl_create", o.Error);
    }

    // EXTERIOR-WIRE Cell via parent=worldspace + grid → created + EXTERIOR shell report
    [Fact]
    public void AnExteriorCellByGridIsCreatedWithAnExteriorShellReport()
    {
        var o = Create("HcBcExt", new CreateOp { RecordType = "Cell", Editorid = "HcBcExtCell", Parent = _world.ToString(), Grid = "1000,-1000" });
        Assert.True(o.Success, o.Error);
        Assert.True(Assert.Single(o.Created).FormKey.ID >= 0x800);
        var shell = Assert.Single(o.CellShell!.Cells);
        Assert.False(shell.Interior);
        Assert.NotEmpty(shell.MustProvide);
    }

    // INTERIOR-WIRE Cell with no parent → created + INTERIOR shell report
    [Fact]
    public void AParentlessCellIsCreatedWithAnInteriorShellReport()
    {
        var o = Create("HcBcInt", new CreateOp { RecordType = "Cell", Editorid = "HcBcIntCell" });
        Assert.True(o.Success, o.Error);
        Assert.True(Assert.Single(o.Created).FormKey.ID >= 0x800);
        var shell = Assert.Single(o.CellShell!.Cells);
        Assert.True(shell.Interior);
        Assert.NotEmpty(shell.MustProvide);
    }

    // MULTI bulk_create exterior + interior → shell lists BOTH
    [Fact]
    public void AnExteriorAndAnInteriorCellInOneBatchAreBothInTheShellReport()
    {
        var o = Create("HcBcMulti",
            new CreateOp { RecordType = "Cell", Editorid = "HcBcMExt", Parent = _world.ToString(), Grid = "200,200" },
            new CreateOp { RecordType = "Cell", Editorid = "HcBcMInt" });
        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.Created.Count);
        Assert.Equal(2, o.CellShell!.Cells.Count);
        Assert.Contains(o.CellShell.Cells, c => c.Interior);
        Assert.Contains(o.CellShell.Cells, c => !c.Interior);
    }
}
