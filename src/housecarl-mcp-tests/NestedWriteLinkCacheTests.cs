using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A write of many NESTED records (placed references) out of one plugin builds ONE source link cache for the call,
/// not one per edit, on every write lane that nests. A link cache over a real master costs seconds and gigabytes, so
/// the count is the claim.
/// </summary>
[Trait("tier", "integration")]
public sealed class NestedWriteLinkCacheTests : IDisposable
{
    const int Refs = 40;

    readonly NestedRefRig _w = new(Refs);

    static List<WritePatchBuilder.PatchEdit> SetScales(IEnumerable<FormKey> keys) =>
        keys.Select(k => WritePathRig.Set(k, "Scale", "2")).ToList();

    long Builds(Func<bool> call, Func<string?> error)
    {
        var before = _w.Order.Counters.LinkCacheBuilds;
        Assert.True(call(), error());
        return _w.Order.Counters.LinkCacheBuilds - before;
    }

    [Fact]
    public void ANestedApplyBuildsOneLinkCacheNotOnePerEdit()
    {
        var path = _w.Rig.Out("HcNestedLcApply.esp");
        WritePatchBuilder.PatchOutcome o = null!;
        var builds = Builds(() => (o = WritePatchBuilder.Apply(_w.Order, TestCorpus.Rulebook, SetScales(_w.MasterRefs), path, extend: false)).Success, () => o.Error);

        Assert.True(builds == 1, $"{Refs} nested edits out of one plugin built {builds} link caches.");
        Assert.All(_w.Scales(path, _w.MasterRefs), s => Assert.Equal(2f, s));
    }

    [Fact]
    public void ANestedForwardBuildsOneLinkCacheNotOnePerRecord()
    {
        var specs = _w.MasterRefs.Select(k => new WritePatchBuilder.ForwardSpec { Target = k, FromPlugin = NestedRefRig.MasterName }).ToList();
        var path = _w.Rig.Out("HcNestedLcForward.esp");
        WritePatchBuilder.ForwardOutcome o = null!;
        var builds = Builds(() => (o = WritePatchBuilder.ForwardRecords(_w.Order, specs, path, extend: false, sourceParam: "source")).Success, () => o.Error);

        Assert.True(builds == 1, $"{Refs} nested forwards out of one plugin built {builds} link caches.");
        Assert.All(_w.Scales(path, _w.MasterRefs), s => Assert.Equal(1f, s));
    }

    [Fact]
    public void ANestedInPlaceApplyBuildsOneLinkCacheNotOnePerEdit()
    {
        WritePatchBuilder.PatchOutcome o = null!;
        var builds = Builds(() => (o = WritePatchBuilder.ApplyInPlace(_w.Order, TestCorpus.Rulebook, SetScales(_w.UserRefs), _w.UserPath, NestedRefRig.UserName)).Success, () => o.Error);

        Assert.True(builds == 1, $"{Refs} nested in-place edits in one plugin built {builds} link caches.");
        Assert.All(_w.Scales(_w.UserPath, _w.UserRefs), s => Assert.Equal(2f, s));
    }

    [Fact]
    public void ANestedInPlaceForwardBuildsOneLinkCacheNotOnePerRecord()
    {
        var specs = _w.MasterRefs.Select(k => new WritePatchBuilder.ForwardSpec { Target = k, FromPlugin = NestedRefRig.MasterName }).ToList();
        WritePatchBuilder.ForwardOutcome o = null!;
        var builds = Builds(() => (o = WritePatchBuilder.ForwardRecordsInPlace(_w.Order, specs, _w.TargetPath, NestedRefRig.TargetName, "source=")).Success, () => o.Error);

        Assert.True(builds == 1, $"{Refs} nested in-place forwards out of one plugin built {builds} link caches.");
        Assert.All(_w.Scales(_w.TargetPath, _w.MasterRefs), s => Assert.Equal(1f, s));
    }

    [Fact]
    public void ANestedCreateUnderOneCellBuildsOneLinkCacheNotOnePerRecord()
    {
        using var c = new NestedCreateRig();
        var specs = Enumerable.Range(0, Refs).Select(i => NestedCreateRig.Under(c.Cell.ToString(), "PlacedObject", "HcNestedLcNew" + i) with { IntoCollection = "Temporary" }).ToArray();
        var before = c.Order.Counters.LinkCacheBuilds;
        var (o, path) = c.Create("HcNestedLcCreate.esp", specs);
        var builds = c.Order.Counters.LinkCacheBuilds - before;

        Assert.True(o.Success, o.Error);
        Assert.True(builds == 1, $"{Refs} nested creates under one plugin's cell built {builds} link caches.");
        Assert.Equal(Refs, c.Open(path).EnumerateMajorRecords<IPlacedObjectGetter>().Count());
    }

    public void Dispose() => _w.Dispose();
}
