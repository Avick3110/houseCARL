using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The read-back line for a composes= Add of N elements reports the whole appended run, <c>(+N), new [0..N-1]</c>, not
/// the last element as if one was added; a one-element compose keeps the single form <c>(+1), new [0] = …</c>. Both the
/// new-patch and the in-place lane. Each test writes its own masterless plugin with empty leveled lists.
/// </summary>
[Trait("tier", "integration")]
public sealed class ComposeAddReadbackCountTests : IDisposable
{
    const string PluginName = "hcReadbackCount.esp";
    const int Batch = 6;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-readback-count-" + Guid.NewGuid().ToString("N"));
    readonly string _plugin;
    readonly string _weapFid;
    readonly FormKey _batchLl, _singleLl, _newLaneLl;

    public ComposeAddReadbackCountTests()
    {
        Directory.CreateDirectory(_dir);
        _plugin = Path.Combine(_dir, PluginName);
        var mod = new SkyrimMod(ModKey.FromNameAndExtension(PluginName), SkyrimRelease.SkyrimSE);
        var w = mod.Weapons.AddNew(); w.EditorID = "hcRbWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        _batchLl = mod.LeveledItems.AddNew("hcRbBatchLL").FormKey;
        _singleLl = mod.LeveledItems.AddNew("hcRbSingleLL").FormKey;
        _newLaneLl = mod.LeveledItems.AddNew("hcRbNewLaneLL").FormKey;
        mod.BeginWrite.ToPath(_plugin).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _weapFid = $"{w.FormKey.ID:X6}:{w.FormKey.ModKey.FileName}";
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    StructSpec Entry(int level) => new()
    {
        Type = "LeveledItemEntry",
        Sets = new List<WriteRequest>
        {
            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Level" },     Verb = "Set", Value = level.ToString() },
            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Count" },     Verb = "Set", Value = "1" },
            new() { RecordType = "LeveledItemEntry", Path = new[] { "Data", "Reference" }, Verb = "Set", Value = _weapFid },
        },
    };

    WritePatchBuilder.PatchEdit Compose(FormKey target, int count) => new()
    {
        Target = target, Path = new[] { "Entries" }, Verb = "Add",
        Structs = Enumerable.Range(1, count).Select(Entry).ToArray(),
    };

    // Probe arm NEW-PLUGIN LANE: a composes= Add of 6 into a new patch reports (+6), new [0..5].
    [Fact]
    public void ABatchComposeIntoANewPatchReportsTheWholeAppendedRun()
    {
        using var r = LoadOrderResolver.Build(new[] { _plugin });

        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, new[] { Compose(_newLaneLl, Batch) }, Path.Combine(_dir, "hcReadbackPatch.esp"), extend: false);

        Assert.True(o.Success, o.Error);
        var landed = o.Ops.Single(x => x.Target == _newLaneLl).Landed ?? "";
        Assert.Contains($"(+{Batch})", landed, StringComparison.Ordinal);
        Assert.Contains($"new [0..{Batch - 1}]", landed, StringComparison.Ordinal);
    }

    // Probe arm BATCH: in place, a composes= Add of 6 reports (+6) and the index range [0..5], never (+1).
    [Fact]
    public void ABatchComposeInPlaceReportsTheWholeAppendedRun()
    {
        var landed = InPlace().Single(x => x.Target == _batchLl).Landed ?? "";

        Assert.Contains($"(+{Batch})", landed, StringComparison.Ordinal);
        Assert.DoesNotContain("(+1)", landed, StringComparison.Ordinal);
        Assert.Contains($"new [0..{Batch - 1}]", landed, StringComparison.Ordinal);
    }

    // Probe arm SINGLE: a 1-element compose still reports (+1), new [0] = <element>, not a range.
    [Fact]
    public void AOneElementComposeKeepsTheSingleForm()
    {
        var landed = InPlace().Single(x => x.Target == _singleLl).Landed ?? "";

        Assert.Contains("(+1)", landed, StringComparison.Ordinal);
        Assert.Contains("new [0] = ", landed, StringComparison.Ordinal);
        Assert.DoesNotContain("..", landed, StringComparison.Ordinal);
    }

    // Probe arm GROUND TRUTH: on disk the batch list holds 6 entries and the single list 1.
    [Fact]
    public void TheListsOnDiskHoldEveryComposedEntry()
    {
        InPlace();

        using var ov = SkyrimMod.CreateFromBinaryOverlay(_plugin, SkyrimRelease.SkyrimSE);
        Assert.Equal(Batch, ov.LeveledItems.First(x => x.FormKey == _batchLl).Entries?.Count ?? 0);
        Assert.Equal(1, ov.LeveledItems.First(x => x.FormKey == _singleLl).Entries?.Count ?? 0);
    }

    IReadOnlyList<WritePatchBuilder.OpResult> InPlace()
    {
        using var r = LoadOrderResolver.Build(new[] { _plugin });
        var o = WritePatchBuilder.ApplyInPlace(r, TestCorpus.Rulebook, new[] { Compose(_batchLl, Batch), Compose(_singleLl, 1) },
            _plugin, PluginName, fullReadback: false);
        Assert.True(o.Success, o.Error);
        return o.Ops;
    }
}
