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
    readonly NestedRefRig _w = new(1);

    [Fact]
    public void ReleasingAnOverlayDropsItsLinkCacheSoTheNextCallBuildsAFreshOne()
    {
        using var session = _w.Order.OpenSession();
        var before = _w.Order.Counters.LinkCacheBuilds;

        var first = session.LinkCacheFor(NestedRefRig.MasterName);
        var again = session.LinkCacheFor(NestedRefRig.MasterName);
        session.ReleaseOverlay(NestedRefRig.MasterName);
        var after = session.LinkCacheFor(NestedRefRig.MasterName);
        var builds = _w.Order.Counters.LinkCacheBuilds - before;

        Assert.Same(first, again);
        Assert.True(builds == 2, $"LinkCacheFor, LinkCacheFor, ReleaseOverlay, LinkCacheFor built {builds} link caches, not 2.");
        Assert.NotSame(first, after);
        Assert.True(after!.TryResolve<IPlacedObjectGetter>(_w.MasterRefs[0], out var hit), "The rebuilt cache did not resolve the placed reference.");
        Assert.Equal("HcNestedLcMasterRef0", hit.EditorID);
    }

    public void Dispose() => _w.Dispose();
}
