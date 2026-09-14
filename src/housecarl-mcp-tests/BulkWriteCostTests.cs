using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// What #723 actually claims: a bulk write reads its record bodies with ONE walk of each source plugin, not one
/// walk per record. The resolver counts both halves — <c>CollectPasses</c> is a plugin walked for a whole set of
/// wanted keys, <c>BodySeeks</c> is the per-record whole-plugin seek — so the claim is a number, not a shape.
///
/// <para>This is the test that FAILS if the gather stops working. The equivalence and written-bytes tests cannot:
/// <see cref="BodyGather.Body"/> falls back to the per-record fetch for anything it does not hold, so a build whose
/// gather declares nothing, or whose walk faults, returns the same records and writes the same bytes — at the old
/// cost. Only the counters tell those two builds apart.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class BulkWriteCostTests : IDisposable
{
    const int Records = 300;
    const int Ops = 200;

    readonly string _root;
    readonly string _priorCorpus;
    readonly LoadOrderResolver _resolver;
    readonly CorpusRulebook _rulebook;
    readonly string _masterName;
    readonly List<FormKey> _keys = new();

    public BulkWriteCostTests()
    {
        _priorCorpus = CorpusRulebook.CorpusPath;
        _root = Path.Combine(Path.GetTempPath(), "hc-bulkcost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var masterKey = new ModKey("HcBulkCostMaster", ModType.Master);
        _masterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Records; i++)
        {
            var w = master.Weapons.AddNew();
            w.EditorID = "HcBulkCostW" + i;
            w.BasicStats = new WeaponBasicStats { Damage = (ushort)(10 + i), Weight = 1 };
            _keys.Add(w.FormKey);
        }
        var masterFile = Path.Combine(_root, _masterName);
        master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(_root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(_root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        _resolver = LoadOrderResolver.Build(new[] { masterFile });
        _rulebook = CorpusRulebook.Load();
    }

    [Fact]
    public void ABulkApplyWalksTheSourcePluginOnceNotOncePerOp()
    {
        var edits = _keys.Take(Ops).Select(k => new WritePatchBuilder.PatchEdit
        {
            Target = k, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "42",
        }).ToList();

        var passesBefore = LoadOrderResolver.CollectPasses;
        var seeksBefore = LoadOrderResolver.BodySeeks;
        var outcome = WritePatchBuilder.Apply(_resolver, _rulebook, edits, Path.Combine(_root, "HcBulkCostApply.esp"), extend: false);
        var passes = LoadOrderResolver.CollectPasses - passesBefore;
        var seeks = LoadOrderResolver.BodySeeks - seeksBefore;

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(passes == 1, $"{Ops} edits out of one plugin walked it {passes} times.");
        Assert.True(seeks == 0, $"{Ops} edits cost {seeks} per-record whole-plugin seeks; the gather answered none of them.");
    }

    [Fact]
    public void ABulkForwardWalksTheSourcePluginOnceNotOncePerRecord()
    {
        var specs = _keys.Take(Ops)
            .Select(k => new WritePatchBuilder.ForwardSpec { Target = k, FromPlugin = _masterName })
            .ToList();

        var passesBefore = LoadOrderResolver.CollectPasses;
        var seeksBefore = LoadOrderResolver.BodySeeks;
        var outcome = WritePatchBuilder.ForwardRecords(_resolver, specs, Path.Combine(_root, "HcBulkCostForward.esp"),
                                                       extend: false, sourceParam: "source");
        var passes = LoadOrderResolver.CollectPasses - passesBefore;
        var seeks = LoadOrderResolver.BodySeeks - seeksBefore;

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(passes == 1, $"{Ops} forwards out of one plugin walked it {passes} times.");
        Assert.True(seeks == 0, $"{Ops} forwards cost {seeks} per-record whole-plugin seeks; the gather answered none of them.");
    }

    /// <summary>The cost must not scale with the op count — the property the per-record fetch broke.</summary>
    [Fact]
    public void TheWalkCountDoesNotGrowWithTheOpCount()
    {
        long Cost(int ops, string name)
        {
            var edits = _keys.Take(ops).Select(k => new WritePatchBuilder.PatchEdit
            {
                Target = k, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "42",
            }).ToList();
            var before = LoadOrderResolver.CollectPasses + LoadOrderResolver.BodySeeks;
            var outcome = WritePatchBuilder.Apply(_resolver, _rulebook, edits, Path.Combine(_root, name), extend: false);
            Assert.True(outcome.Success, outcome.Error);
            return LoadOrderResolver.CollectPasses + LoadOrderResolver.BodySeeks - before;
        }

        Assert.Equal(Cost(2, "HcBulkCostTwo.esp"), Cost(Records, "HcBulkCostAll.esp"));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpus;
        _resolver.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
