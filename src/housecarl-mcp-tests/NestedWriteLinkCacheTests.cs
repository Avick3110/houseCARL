using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A write of many NESTED records (placed references) out of one plugin builds ONE source link cache for the call,
/// not one per edit. A link cache over a real master costs seconds and gigabytes, so the count is the claim.
/// </summary>
[Trait("tier", "integration")]
public sealed class NestedWriteLinkCacheTests : IDisposable
{
    const int Refs = 40;

    readonly string _root;
    readonly LoadOrderResolver _resolver;
    readonly string _masterName;
    readonly List<FormKey> _refs = new();

    public NestedWriteLinkCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-nestedlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var master = new SkyrimMod(new ModKey("HcNestedLcMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        _masterName = master.ModKey.FileName.String;
        var cell = new Cell(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcNestedLcCell" };
        for (int i = 0; i < Refs; i++)
        {
            var r = new PlacedObject(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcNestedLcRef" + i, Scale = 1.0f };
            cell.Temporary.Add(r);
            _refs.Add(r.FormKey);
        }
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        master.Cells.Records.Add(block);
        var masterFile = Path.Combine(_root, _masterName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        _resolver = LoadOrderResolver.Build(new[] { masterFile });
    }

    float[] WrittenScales(string path)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var byKey = mod.EnumerateMajorRecords<IPlacedObjectGetter>().ToDictionary(r => r.FormKey, r => r.Scale ?? 0f);
        return _refs.Select(k => byKey.TryGetValue(k, out var s) ? s : 0f).ToArray();
    }

    [Fact]
    public void ANestedApplyBuildsOneLinkCacheNotOnePerEdit()
    {
        var edits = _refs.Select(k => new WritePatchBuilder.PatchEdit
        {
            Target = k, Path = new[] { "Scale" }, Verb = "Set", Value = "2",
        }).ToList();
        var path = Path.Combine(_root, "HcNestedLcApply.esp");

        var before = _resolver.Counters.LinkCacheBuilds;
        var outcome = WritePatchBuilder.Apply(_resolver, TestCorpus.Rulebook, edits, path, extend: false);
        var builds = _resolver.Counters.LinkCacheBuilds - before;

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(builds == 1, $"{Refs} nested edits out of one plugin built {builds} link caches.");
        Assert.All(WrittenScales(path), s => Assert.Equal(2f, s));
    }

    [Fact]
    public void ANestedForwardBuildsOneLinkCacheNotOnePerRecord()
    {
        var specs = _refs.Select(k => new WritePatchBuilder.ForwardSpec { Target = k, FromPlugin = _masterName }).ToList();
        var path = Path.Combine(_root, "HcNestedLcForward.esp");

        var before = _resolver.Counters.LinkCacheBuilds;
        var outcome = WritePatchBuilder.ForwardRecords(_resolver, specs, path, extend: false, sourceParam: "source");
        var builds = _resolver.Counters.LinkCacheBuilds - before;

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(builds == 1, $"{Refs} nested forwards out of one plugin built {builds} link caches.");
        Assert.All(WrittenScales(path), s => Assert.Equal(1f, s));
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
