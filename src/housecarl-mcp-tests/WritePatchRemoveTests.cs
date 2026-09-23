using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Removing a singular owned child that has records under it: the remove lane refuses, lists the records it would take
/// along, and says to name them in the same call. Stryker row T42 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchRemoveTests : IDisposable
{
    readonly WritePathRig _rig = new();

    [Fact]
    public void DetachingAChildWithUnnamedDescendantsListsThemAndSaysToNameThem()
    {
        var master = new SkyrimMod(new ModKey("HcWpRemoveMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        master.Keywords.AddNew().EditorID = "HcWpRemoveKeyword";
        var order = _rig.Order(_rig.Write(master));

        // A patch defining a worldspace whose top cell holds one placed reference.
        var patch = new SkyrimMod(ModKey.FromFileName("HcWpRemovePatch.esp"), SkyrimRelease.SkyrimSE);
        var ws = patch.Worldspaces.AddNew();
        ws.EditorID = "HcWpRemoveWorld";
        var cell = new Cell(patch.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcWpRemoveTopCell" };
        var placed = new PlacedObject(patch.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcWpRemoveRef" };
        cell.Temporary.Add(placed);
        ws.TopCell = cell;
        var path = _rig.Out("HcWpRemovePatch.esp");
        patch.BeginWrite.ToPath(path).WithNoLoadOrder().Write();

        var o = WritePatchBuilder.RemoveRecords(order, new[] { cell.FormKey }, path);

        Assert.False(o.Success);
        Assert.Contains(FormIdToken.Of(placed.FormKey), o.Error);
        Assert.Contains("Name them in the same call", o.Error);
    }

    public void Dispose() => _rig.Dispose();
}
