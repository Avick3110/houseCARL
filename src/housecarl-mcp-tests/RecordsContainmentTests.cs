using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
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

    // ---- every child kind the SPEC amendment names, not a third of them ---------------------------
    //
    // The amendment's set is seven properties over three types, and it is derived rather than typed
    // (WriteEngine.ChildBearingProperties). One row each here, so a Mutagen bump that drops one is a failure
    // rather than a quiet coverage hole.

    /// <summary>ACHR — named in the issue title and the SPEC amendment, and the fixture had none.</summary>
    [Fact]
    public void CellPersistent_TheIndexKnowsTheCellAPlacedNpcSitsIn() =>
        Assert.Equal(_w.CellI, Svc.CaptureView().ParentOf(_w.PlacedNpc));

    [Fact]
    public void CellPersistent_TheIndexKnowsTheCellAPersistentReferenceSitsIn() =>
        Assert.Equal(_w.CellA, Svc.CaptureView().ParentOf(_w.PersistentRef));

    /// <summary>Cell.Landscape is a SINGULAR child property — one record, not a collection.</summary>
    [Fact]
    public void CellLandscape_TheIndexKnowsTheCellALandscapeBelongsTo() =>
        Assert.Equal(_w.CellA, Svc.CaptureView().ParentOf(_w.LandscapeRec));

    /// <summary>The one child kind that ALSO names its parent on its own body (<c>Data.Parent</c>, through the
    /// CellNavmeshParent arm) — so it is the only place the index and the record could disagree. Both are read
    /// here, and they must say the same cell.</summary>
    [Fact]
    public void CellNavigationMeshes_TheIndexAndTheNavmeshsOwnBodyNameTheSameCell()
    {
        Assert.Equal(_w.CellI, Svc.CaptureView().ParentOf(_w.Navmesh));
        Assert.Contains(OwnedChildWorld.Fid(_w.CellI), Read(_w.Navmesh, "Data.Parent.Parent"));
    }

    /// <summary>Worldspace.TopCell — the second, singular worldspace child property.</summary>
    [Fact]
    public void WorldspaceTopCell_TheIndexKnowsTheWorldspaceATopCellBelongsTo() =>
        Assert.Equal(_w.TopCellWorldspace, Svc.CaptureView().ParentOf(_w.TopCell));

    // ---- the later-wins merge, both halves --------------------------------------------------------

    /// <summary>The reason the map is built per plugin and merged rather than filled once: across a real order
    /// thousands of children end up under a different parent than the first plugin put them under. Mid declares
    /// base's reference under a cell of its own, and the later declaration stands.</summary>
    [Fact]
    public void ALaterPluginThatMovesAChildToAnotherCellWins() =>
        Assert.Equal(_w.CellJ, Svc.CaptureView().ParentOf(_w.ReparentedRef));

    /// <summary>…and the inverse, which matters as much: Mid re-declares CellI carrying NOTHING, and that must
    /// not erase the edges base staged for the children it did declare there. Absent is not empty.</summary>
    [Fact]
    public void AReDeclarationCarryingNoChildrenErasesNoEdge()
    {
        var view = Svc.CaptureView();
        Assert.Equal(_w.CellI, view.ParentOf(_w.PlacedNpc));
        Assert.Equal(_w.CellI, view.ParentOf(_w.Navmesh));
    }

    /// <summary>The swap from Mutagen's flat <c>EnumerateMajorRecords</c> to <c>EnumerateMajorRecordContexts</c>
    /// is not scoped to <c>*parent</c> — the winner index, the overrider lists and the whole resolution surface
    /// are built from it now. The comment at the swap asserts the two walks yield the same record set; this is
    /// what pins it, per plugin and as a multiset, so a kind the context walk ever dropped or duplicated fails
    /// here rather than becoming a silently mis-resolved order.</summary>
    [Fact]
    public void TheContextWalkYieldsTheSameRecordSetAsTheFlatWalk()
    {
        foreach (var path in _w.PluginPaths)
        {
            var ov = LoadOrderResolver.OpenOverlay(path, null);   // the same door the index build opens
            try
            {
                var flat = ov.EnumerateMajorRecords().Select(r => r.FormKey).OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
                var ctx = ov.EnumerateMajorRecordContexts().Select(c => c.Record.FormKey).OrderBy(k => k.ToString(), StringComparer.Ordinal).ToList();
                Assert.Equal(flat, ctx);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    // ---- reading it ------------------------------------------------------------------------------

    [Fact]
    public void ProjectFieldsReadsTheOwningTopicsEditorIdOffTheInfo() =>
        Assert.Contains("HcOcTopic", Read(Info, "*parent.EditorID"));

    /// <summary>The crash-log chain end to end against the real index, not a hand-built dictionary: a placed
    /// reference to its cell to its worldspace. One hop lands on the cell, two on the worldspace — both asserted,
    /// so a chain that quietly takes one hop and stops cannot pass.</summary>
    [Fact]
    public void TheChainReadsThroughTwoHops()
    {
        Assert.Contains("HcOcWsCell0", Read(_w.WorldCellRef, "*parent.EditorID"));
        Assert.Contains("HcOcWrld", Read(_w.WorldCellRef, "*parent.*parent.EditorID"));
    }

    /// <summary>Never a null: a record nothing contains says so, and says what containment runs from.</summary>
    [Fact]
    public void AReadOnARecordNothingContainsNamesTheChildBearingProperties()
    {
        var r = Read(_w.Weapon, "*parent.EditorID");
        Assert.Contains("no record contains", r);
        Assert.Contains("DialogTopic.Responses", r);
    }

    /// <summary>Reading a child-bearing field THROUGH the hop is the same question as reading it on the container
    /// directly, so it must carry the same owned-child note. Without it the crash-log reading this PR headlines —
    /// a placed reference up to its cell's contents — would be the one spelling that reports the winner's contents
    /// as the whole story.</summary>
    [Fact]
    public void AReadThroughTheHopCarriesTheContainingCellsOwnedChildNote()
    {
        Assert.Contains(ReadSentences.UnionLabel, Read(_w.CellA, "Temporary"));
        Assert.Contains(ReadSentences.UnionLabel, Read(new FormKey(_w.CellA.ModKey, 0xC10), "*parent.Temporary"));
    }

    // ---- every in-order read lane answers it ------------------------------------------------------

    static JsonElement Pole(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();

    /// <summary>The comparison forms take fields= and hold the order's own index, so the hop answers on their
    /// in-order arms too — the refusal naming "read the record through the load order instead" must not fire on a
    /// read that IS through the load order. Measured against an off-order arm, which genuinely carries no
    /// containment map and keeps saying so, so both halves are on one call and neither passes by accident.</summary>
    [Theory]
    [InlineData("delta")]
    [InlineData("tree")]
    public void TheComparisonFormsReadTheHopOnTheirInOrderArm(string form)
    {
        var copy = _w.Scratch(form + "-offorder", _w.BaseName);
        File.Copy(_w.PluginPaths[0], copy, overwrite: true);
        var r = RecordsTools.Records(Svc, formids: new[] { OwnedChildWorld.Fid(_w.ReparentedRef) },
                                     source: form == "delta" ? Pole(_w.MidName) : null, versus: Pole(copy),
                                     project: new RecordsTools.RecordsProject { form = form, fields = new[] { "*parent.EditorID" } });
        Assert.Contains("HcOcCellJ", r);
        Assert.Contains("needs the load-order index", r);
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

    /// <summary>The case the changelog headlines and the one a deleted-record skip would silently swallow: a
    /// patch DELETED this placed reference, and patches delete placed references constantly. The hop reads its
    /// FormKey and nothing else, and the term below the hop reads the CELL's body, which is live — so a
    /// *parent-only predicate is header-only for the child and must still see it.</summary>
    [Fact]
    public void ADeletedPlacedReferenceIsStillFilteredByTheCellThatHoldsIt()
    {
        // The record really is deleted: a predicate that reads its BODY drops it, which is the standing rule.
        Assert.DoesNotContain(OwnedChildWorld.Fid(_w.DeletedRef), Query("PlacedObject", "Base exists"));
        // The hop is not a body read, so the same record answers the containment question.
        Assert.Contains(OwnedChildWorld.Fid(_w.DeletedRef), Query("PlacedObject", "*parent.EditorID = HcOcCellI"));
    }

    /// <summary>The two-hop filter, against the same machinery, so the chain is pinned on the where= surface
    /// too and not only on the read.</summary>
    [Fact]
    public void WhereFiltersAPlacedReferenceByTheWorldspaceTwoStepsAboveIt() =>
        Assert.Contains(OwnedChildWorld.Fid(_w.WorldCellRef), Query("PlacedObject", "*parent.*parent.EditorID = HcOcWrld"));

    /// <summary>An ACHR filters by its cell exactly as a REFR does — one step, no per-type rule.</summary>
    [Fact]
    public void WhereFiltersAPlacedNpcByTheCellThatHoldsIt() =>
        Assert.Contains(OwnedChildWorld.Fid(_w.PlacedNpc), Query("PlacedNpc", "*parent.EditorID = HcOcCellI"));

    // ---- the grammar, one validator over every surface --------------------------------------------
    //
    // The where= surface refuses a misspelled hop by name. The read surface must give the SAME sentence for the
    // same mistake, or a caller who mistypes on project.fields gets a typo hint where where= would have named
    // the real problem.

    static string WhereRefusal(LoadOrderService svc, string term) =>
        RecordsTools.Records(svc, types: new[] { "DialogResponses" }, where: new[] { term },
                             project: new RecordsTools.RecordsProject { form = "summary" });

    [Theory]
    [InlineData("Responses.*parent.EditorID", "it can only lead a path")]
    [InlineData("*parent[*any].EditorID", "takes no quantifier")]
    [InlineData("*owner.EditorID", "is not a path token")]
    [InlineData("*parent", "not a value")]
    public void TheReadSurfaceRefusesTheSameHopMistakeTheWhereSurfaceDoes(string path, string fragment)
    {
        Assert.Contains(fragment, Read(Info, path));
        Assert.Contains(fragment, WhereRefusal(Svc, path + " = X"));
    }

    // ---- the off-order lane -----------------------------------------------------------------------

    /// <summary>The containment map is built from the ACTIVE order's plugins only. A same-named copy of an active
    /// plugin resolves in that map, so a *parent filter over the copy would answer from the OTHER file's edges —
    /// which is the second cornerstone's case exactly. Refused by name instead.</summary>
    [Fact]
    public void AParentFilterOverAnOutOfLoadOrderFileRefusesRatherThanAnsweringFromTheActiveIndex()
    {
        var copy = _w.Scratch("offorder", _w.BaseName);
        File.Copy(_w.PluginPaths[0], copy, overwrite: true);
        var r = RecordsTools.Records(Svc, types: new[] { "DialogResponses" },
                                     where: new[] { "*parent.EditorID = HcOcTopic" },
                                     source: JsonDocument.Parse(JsonSerializer.Serialize(copy)).RootElement.Clone(),
                                     project: new RecordsTools.RecordsProject { form = "summary" });
        Assert.Contains("error:", r);
        Assert.Contains("out-of-load-order", r);
        Assert.DoesNotContain(OwnedChildWorld.Fid(Info) + " ", r);
    }

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
