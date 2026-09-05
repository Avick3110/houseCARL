using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The owned-child lifecycle DRIVEN through the tool surface (#350): <c>housecarl_create</c> with parent= and
/// collection=, and <c>housecarl_remove</c> on the child's own FormID, against a real load order on disk. The
/// engine-level twin (<c>OwnedChildLifecycleTests</c>) proves the primitives; this proves the calls the refusals
/// name actually accept the arguments those sentences give, which is the half a unit test cannot see — pre-flight
/// runs in the patch builder, not the engine.
/// </summary>
[Trait("tier", "integration")]
public sealed class OwnedChildLifecycleDrivenTests
{
    static string Fid(FormKey fk) => OwnedChildWorld.Fid(fk);

    /// <summary>Re-open a written patch and answer for one cell's singular terrain slot.</summary>
    static FormKey? LandscapeUnder(string patchPath, FormKey cell)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        return back.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.FormKey == cell)?.Landscape?.FormKey;
    }

    static FormKey? TopCellUnder(string patchPath, FormKey worldspace)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        return back.Worldspaces.FirstOrDefault(w => w.FormKey == worldspace)?.TopCell?.FormKey;
    }

    /// <summary>A cell whose parent carries no terrain gets some: the create lands, and the re-opened patch shows
    /// the cell holding the new record.</summary>
    [Fact]
    public void CreatingAnAbsentSingularChildLandsItUnderTheParent()
    {
        using var w = new OwnedChildWorld();
        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Landscape", Editorid = "HcOcNewLand", Parent = Fid(w.CellC) } },
            "HcOcCreateLand", null);

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal(LandscapeUnder(o.OutputPath, w.CellC), o.Created[0].FormKey);
    }

    /// <summary>The occupied-slot refusal is decided from the PARENT'S REAL BODY, not from the patch's fresh
    /// override — which carries no children, so a guard that asked the copy would let this through and ship a cell
    /// with a second, empty terrain record. CellA's winner declares nothing; its definer declares the LAND.</summary>
    [Fact]
    public void AnOccupiedSingularSlotIsRefusedEvenThoughThePatchCopyCarriesNoChild()
    {
        using var w = new OwnedChildWorld();
        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Landscape", Editorid = "HcOcSecondLand", Parent = Fid(w.CellA) } },
            "HcOcSecondLandPatch", null);

        Assert.False(o.Success);
        Assert.Contains("already holds", o.Error);
        Assert.Contains("NOTHING created", o.Error);
    }

    /// <summary>…and the same refusal holds when the patch ALREADY carries the parent: that copy is an override and
    /// carries none of the parent's children, so reading it alone would read the slot free and ship a second terrain
    /// record under a cell that already declares one.</summary>
    [Fact]
    public void AnOccupiedSingularSlotIsRefusedWhenThePatchAlreadyCarriesTheParent()
    {
        using var w = new OwnedChildWorld();
        var first = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "PlacedObject", Editorid = "HcOcCarriedRef", Parent = Fid(w.CellA), Collection = "Persistent" } },
            "HcOcCarriedParent", null);
        Assert.True(first.Success, "refused: " + first.Error);
        var patchFile = Path.GetFileName(first.OutputPath);

        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Landscape", Editorid = "HcOcCarriedLand", Parent = Fid(w.CellA) } },
            null, patchFile);

        Assert.False(o.Success);
        Assert.Contains("already holds", o.Error);
        Assert.Contains("NOTHING created", o.Error);
    }

    /// <summary>The route the rewritten refusals name — parent= plus collection='TopCell' — is reachable from the
    /// tool. It was not: every Cell spec with a parent was intercepted before the slot resolver ran and refused for
    /// a missing grid=.</summary>
    [Fact]
    public void TheTopCellRouteTheRefusalsNameIsReachableThroughTheTool()
    {
        using var w = new OwnedChildWorld();
        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Cell", Editorid = "HcOcNewTopCell", Parent = Fid(w.Worldspace), Collection = "TopCell" } },
            "HcOcTopCellPatch", null);

        Assert.True(o.Success, "refused: " + o.Error);
        Assert.Equal(TopCellUnder(o.OutputPath, w.Worldspace), o.Created[0].FormKey);
    }

    /// <summary>…and naming NEITHER route is the ambiguity, which now renders on the production path and names both
    /// moves — the single slot and the coordinate-keyed block tree.</summary>
    [Fact]
    public void ACellUnderAWorldspaceWithNeitherRouteNamedNamesBoth()
    {
        using var w = new OwnedChildWorld();
        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Cell", Editorid = "HcOcAmbiguousCell", Parent = Fid(w.Worldspace) } },
            "HcOcAmbiguousPatch", null);

        Assert.False(o.Success);
        Assert.Contains("TopCell", o.Error);
        Assert.Contains("grid=", o.Error);
    }

    /// <summary>Naming both routes at once is a contradiction, not a precedence rule to resolve silently.</summary>
    [Fact]
    public void NamingBothCellRoutesIsRefused()
    {
        using var w = new OwnedChildWorld();
        var o = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Cell", Editorid = "HcOcBothRoutes", Parent = Fid(w.Worldspace), Collection = "TopCell", Grid = "1,2" } },
            "HcOcBothRoutesPatch", null);

        Assert.False(o.Success);
        Assert.Contains("name one", o.Error);
    }

    /// <summary>The delete half, driven: a created owned child is removed by its own FormID, and the re-opened
    /// patch shows the parent holding nothing. Mutagen's typed remove reaches no singular owned child, so before
    /// the detach path this call reported "did not drop 1 record".</summary>
    [Fact]
    public void RemovingAnOwnedChildByItsOwnFormIdDropsIt()
    {
        using var w = new OwnedChildWorld();
        var made = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Landscape", Editorid = "HcOcDoomedLand", Parent = Fid(w.CellC) } },
            "HcOcRemoveLand", null);
        Assert.True(made.Success, "refused: " + made.Error);
        var patchFile = Path.GetFileName(made.OutputPath);

        var gone = w.Svc.RemoveRecords(new[] { Fid(made.Created[0].FormKey) }, patch: patchFile);

        Assert.True(gone.Success, "refused: " + gone.Error);
        Assert.Single(gone.Removed);
        Assert.Null(LandscapeUnder(gone.OutputPath, w.CellC));
    }

    /// <summary>A detached child takes everything under it, so one that still carries records the caller did not
    /// name is refused rather than deleted with the report accounting for one. Naming them all is the way through,
    /// and then every dropped record is in the report.</summary>
    [Fact]
    public void RemovingAnOwnedChildThatCarriesUnnamedRecordsIsRefusedUntilTheyAreNamedToo()
    {
        using var w = new OwnedChildWorld();
        var top = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "Cell", Editorid = "HcOcSubtreeTop", Parent = Fid(w.Worldspace), Collection = "TopCell" } },
            "HcOcSubtree", null);
        Assert.True(top.Success, "refused: " + top.Error);
        var patchFile = Path.GetFileName(top.OutputPath);
        var topCell = top.Created[0].FormKey;

        var child = w.Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = "PlacedObject", Editorid = "HcOcSubtreeRef", Parent = Fid(topCell), Collection = "Persistent" } },
            null, patchFile);
        Assert.True(child.Success, "refused: " + child.Error);

        var refused = w.Svc.RemoveRecords(new[] { Fid(topCell) }, patch: patchFile);
        Assert.False(refused.Success);
        Assert.Contains("you named none of them", refused.Error);
        Assert.Contains("UNTOUCHED", refused.Error);

        var both = w.Svc.RemoveRecords(new[] { Fid(topCell), Fid(child.Created[0].FormKey) }, patch: patchFile);
        Assert.True(both.Success, "refused: " + both.Error);
        Assert.Equal(2, both.Removed.Count);
        Assert.Null(TopCellUnder(both.OutputPath, w.Worldspace));
    }
}
