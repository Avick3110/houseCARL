using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Releasing a plugin's overlay drops its memoised link cache, so a later LinkCacheFor in the same session builds a
/// fresh one over the reopened overlay instead of handing back a cache over the disposed one.
/// </summary>
[Trait("tier", "integration")]
public sealed class LinkCacheReleaseTests : IDisposable
{
    readonly string _root;
    readonly LoadOrderResolver _resolver;
    readonly string _masterName;
    readonly FormKey _ref;

    public LinkCacheReleaseTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-lcrelease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var master = new SkyrimMod(new ModKey("HcLcReleaseMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        _masterName = master.ModKey.FileName.String;
        var cell = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcLcReleaseCell" };
        var r = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcLcReleaseRef" };
        cell.Temporary.Add(r);
        _ref = r.FormKey;
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        master.Cells.Records.Add(block);
        var masterFile = Path.Combine(_root, _masterName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        _resolver = LoadOrderResolver.Build(new[] { masterFile });
    }

    [Fact]
    public void ReleasingAnOverlayDropsItsLinkCacheSoTheNextCallBuildsAFreshOne()
    {
        using var session = _resolver.OpenSession();
        var before = _resolver.Counters.LinkCacheBuilds;

        var first = session.LinkCacheFor(_masterName);
        var again = session.LinkCacheFor(_masterName);
        session.ReleaseOverlay(_masterName);
        var after = session.LinkCacheFor(_masterName);
        var builds = _resolver.Counters.LinkCacheBuilds - before;

        Assert.Same(first, again);
        Assert.True(builds == 2, $"LinkCacheFor, LinkCacheFor, ReleaseOverlay, LinkCacheFor built {builds} link caches, not 2.");
        Assert.NotSame(first, after);
        Assert.True(after!.TryResolve<IPlacedObjectGetter>(_ref, out var hit), "The rebuilt cache did not resolve the placed reference.");
        Assert.Equal("HcLcReleaseRef", hit.EditorID);
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
