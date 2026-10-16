using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A gather over cells and references on a plugin rewritten after the index build: a reference key that is now a
/// weapon misses its shared-group walk and still comes back through the flat walk.</summary>
[Trait("tier", "integration")]
public sealed class SharedGroupFallbackTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-shared-fallback-");
    readonly ModKey _key = new("HcSharedFallback", ModType.Master);
    readonly FormKey _cell, _ref;

    public SharedGroupFallbackTests()
    {
        _cell = new FormKey(_key, 0xC01); _ref = new FormKey(_key, 0xC02);
        Write(asWeapon: false);
        _mo2.Profile(_key.FileName + "\r\n", "*" + _key.FileName + "\r\n", "+SharedFallback\r\n");
    }

    public void Dispose() => _mo2.Delete();

    void Write(bool asWeapon)
    {
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var cell = new Cell(_cell, SkyrimRelease.SkyrimSE) { EditorID = "HcSfCell", Flags = Cell.Flag.IsInteriorCell };
        if (asWeapon) m.Weapons.Add(new Weapon(_ref, SkyrimRelease.SkyrimSE) { EditorID = "HcSfWeapon" });
        else cell.Temporary.Add(new PlacedObject(_ref, SkyrimRelease.SkyrimSE) { EditorID = "HcSfRef" });
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        m.BeginWrite.ToPath(_mo2.InMod("SharedFallback", _key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    // Fails when a shared-group miss is dropped instead of joining the flat walk: the weapon is then not found.
    [Fact]
    public void AReferenceThatMissesItsSharedGroupWalkComesBackThroughTheFlatWalk()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        Write(asWeapon: true);
        using var session = pin.Resolver.OpenSession();
        var sink = new Dictionary<FormKey, IMajorRecordGetter>();
        var c = svc.Counters;
        long passes = c.CollectPasses, typed = c.TypedCollectPasses;
        pin.View.CollectRecords(session, _key.FileName, new[] { _cell, _ref }, sink);
        Assert.Equal("HcSfCell", Assert.IsAssignableFrom<ICellGetter>(sink[_cell]).EditorID);
        Assert.Equal("HcSfWeapon", Assert.IsAssignableFrom<IWeaponGetter>(sink[_ref]).EditorID);
        Assert.Equal(1, c.CollectPasses - passes);
        Assert.Equal(0, c.TypedCollectPasses - typed);
    }
}
