using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The <c>*parent</c> containment edge end to end, against the real plugins <see cref="OwnedChildWorld"/> writes:
/// the index build captures each record's containing record from Mutagen's context walk, and the token then reads,
/// filters and walks across it. This is the reported gap — an INFO found by content, and no way back to its owning
/// DIAL, because group nesting is not a FormLink and <c>references=</c> correctly returns nothing.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsContainmentTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;

    /// <summary>The owned-child world supplies the records; the records collection's fixture is taken only for the
    /// generated corpus a types= scan resolves record-type names against.</summary>
    public RecordsContainmentTests(OwnedChildFixture f, RecordsFixture corpus) { _w = f.W; _ = corpus; }

    LoadOrderService Svc => _w.Svc;

    /// <summary>An INFO the base master declares under HcOcTopic, and only the base master.</summary>
    FormKey Info => new(_w.Topic.ModKey, 0xD11);

    string Read(FormKey fk, params string[] fields) =>
        RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(fk) },
                             project: new RecordsTools.RecordsProject { form = "fields", fields = fields });

    string Query(string type, params string[] where) =>
        RecordsTools.Records(Svc, types: new[] { type }, where: where,
                             project: new RecordsTools.RecordsProject { form = "summary" });

    // ---- the index captured it -------------------------------------------------------------------

    [Fact]
    public void TheIndexKnowsTheOwningTopicOfAnInfo()
    {
        var view = Svc.CaptureView();
        Assert.Equal(_w.Topic, view.ParentOf(Info));
    }

    [Fact]
    public void TheIndexKnowsTheCellAPlacedReferenceSitsIn()
    {
        var view = Svc.CaptureView();
        Assert.Equal(_w.CellA, view.ParentOf(new FormKey(_w.CellA.ModKey, 0xC10)));
    }

    /// <summary>The chain, which is what the crash-log case needs: a placed reference to its cell to its
    /// worldspace, through the block and sub-block contexts that carry no record of their own.</summary>
    [Fact]
    public void TheIndexClimbsBlockContextsToPutAWorldspacesCellUnderTheWorldspace()
    {
        var view = Svc.CaptureView();
        Assert.Equal(_w.Worldspace, view.ParentOf(new FormKey(_w.Worldspace.ModKey, 0xF10)));
    }

    [Fact]
    public void ATopLevelRecordHasNoContainingRecord() =>
        Assert.Null(Svc.CaptureView().ParentOf(_w.Weapon));

    // ---- reading it ------------------------------------------------------------------------------

    [Fact]
    public void ProjectFieldsReadsTheOwningTopicsEditorIdOffTheInfo() =>
        Assert.Contains("HcOcTopic", Read(Info, "*parent.EditorID"));

    [Fact]
    public void TheChainReadsThroughTwoHops() =>
        Assert.Contains("HcOcWrld", Read(new FormKey(_w.Worldspace.ModKey, 0xF10), "*parent.EditorID"));

    /// <summary>Never a null: a record nothing contains says so, and says what containment runs from.</summary>
    [Fact]
    public void AReadOnARecordNothingContainsNamesTheChildBearingProperties()
    {
        var r = Read(_w.Weapon, "*parent.EditorID");
        Assert.Contains("no record contains", r);
        Assert.Contains("DialogTopic.Responses", r);
    }

    // ---- filtering on it -------------------------------------------------------------------------

    [Fact]
    public void WhereFiltersInfosByTheirOwningTopic()
    {
        var r = Query("DialogResponses", "*parent.EditorID = HcOcTopic");
        Assert.Contains(OwnedChildWorld.Fid(Info), r);
    }

    [Fact]
    public void WhereOnAWrongOwningTopicMatchesNothing() =>
        Assert.DoesNotContain(OwnedChildWorld.Fid(Info), Query("DialogResponses", "*parent.EditorID = HcOcCellA"));

    /// <summary>The whole point of the class-scoped fix, not the DIAL row alone: the same token answers the
    /// crash-log question — which cell holds this placed reference.</summary>
    [Fact]
    public void WhereFiltersPlacedReferencesByTheCellThatHoldsThem() =>
        Assert.Contains(OwnedChildWorld.Fid(new FormKey(_w.CellA.ModKey, 0xC10)),
                        Query("PlacedObject", "*parent.EditorID = HcOcCellA"));

    // ---- walking it ------------------------------------------------------------------------------

    [Fact]
    public void AWalkSeededOnParentReachesTheOwningTopic()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(Info) },
                                     walk: new RecordsTools.RecordsWalk { seed_paths = new[] { "*parent" }, depth = 1 },
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.Contains(OwnedChildWorld.Fid(_w.Topic), r);
    }
}
