using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A bounded whole pass stops within one unit of its bound inside a row too, not one row past it: a single
/// row whose list (chain nodes, delta lines, tree providers, effect carriers, INFO lines, a mesh's sections) is far
/// wider than the cap lays a few of its units, records the stop, and never the whole list. Hand-built rows, since no
/// fixture holds a row this wide.</summary>
[Trait("tier", "unit")]
public sealed class WholePassRowTests
{
    const int Bound = 300;
    const int Wide = 20_000;
    // Room for the one unit the pass lays past its bound, plus the tail a row writes after its list stops.
    const int Slack = 1_200;

    /// <summary>The pass at Bound lays at most Bound + Slack chars and says it stopped, where the whole row is at least 50 times wider.</summary>
    static void AssertBounded(Func<WholePass?, string> render, string what)
    {
        int whole = render(null).Length;
        Assert.True(whole > 50 * (Bound + Slack), $"{what}: the row ({whole} chars) is not wide enough to tell");
        var pass = new WholePass(Bound);
        var laid = render(pass);
        Assert.True(pass.Stopped, $"{what}: the pass laid {laid.Length} chars and did not record a stop");
        Assert.True(laid.Length <= Bound + Slack, $"{what}: the pass at {Bound} laid {laid.Length} of {whole} chars");
    }

    [Fact]
    public void AOneSeedChainLaysAFewNodes()
    {
        var nodes = Enumerable.Range(0, Wide).Select(i =>
            new RecordReads.WalkNodeRow($"{0x800 + i:X6}:A.esm", "Weapon", "HcRecW" + i, 1, "Effects[].BaseEffect", "reached", null)).ToArray();
        var row = new RecordReads.WalkSeedResult("000800:A.esm", "Npc", "HcRecNpc", nodes, Array.Empty<string>(), null, null, null);
        AssertBounded(w => RecordsTools.RenderRecordsChain(new[] { row }, 1, nodes.Length, 0, "records  form=chain", null,
                                                           RenderCap.Whole, null, out _, w), "chain");
    }

    [Fact]
    public void AOneRecordDeltaLaysAFewLines()
    {
        var diff = new FieldsDiff.Result(Enumerable.Range(0, Wide).Select(i => $"Keywords[{i}]: 0x{i:X6} (reference absent)").ToArray(),
                                         Complete: true, AgreedCount: 0, AgreedSample: Array.Empty<string>(), NoVerdictCount: 0);
        var row = new RecordReads.DeltaRow("000800:A.esm",
            new RecordReads.DiffPole("B.esp", "active", true, "Weapon", "HcWeap"),
            new RecordReads.DiffPole("A.esm", "active", true, "Weapon", "HcWeap"), diff, null, null, null);
        AssertBounded(w => RecordsTools.RenderRecordsDelta(new[] { row }, 1, 1, 0, 0, 0, "records  form=delta", null,
                                                           RenderCap.Whole, null, out _, w), "delta");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AOneRecordTreeLaysAFewProviders(bool wideTouchers)
    {
        var plugins = Enumerable.Range(0, Wide).Select(i => $"Provider{i:D5}.esp").ToArray();
        var nodes = plugins.Select((p, i) => new RecordReads.TreeNodeDelta(p, i == Wide - 1, i == Wide - 1,
            new[] { $"Value: {i} (reference {Wide})" }, 0, true, null)).ToArray();
        var row = new RecordReads.TreeRow("000800:A.esm", "Weapon", "HcWeap",
            wideTouchers ? plugins : new[] { plugins[0], plugins[^1] }, plugins[^1], nodes, null,
            Array.Empty<ChildDeclarers>());
        AssertBounded(w => RecordsTools.RenderRecordsTree(new[] { row }, 1, 1, 0, false, "records  form=tree", null,
                                                          RenderCap.Whole, null, out _, w),
                      wideTouchers ? "tree touchers" : "tree providers");
    }

    [Fact]
    public void AOneSeedEffectChainLaysAFewCarriers()
    {
        var rows = Enumerable.Range(0, Wide).Select(i =>
            new EffectChainRow(FormKey.Factory($"{0x800 + i:X6}:HcRecMaster.esm"), "Spell", "HcRecSpell" + i,
                               "HcRecMaster.esm", 0, 1, 5f, 0, 0)).ToArray();
        var result = new EffectChainResult(FormKey.Factory("000805:HcRecMaster.esm"), "HcRecMgefFire", rows, rows.Length,
                                           false, null, null);
        AssertBounded(w => RecordsTools.RenderRecordsEffectChains(new[] { ("000805:HcRecMaster.esm", result) }, 1,
                                                                  rows.Length, rows.Length, 0, "records  form=chain", null,
                                                                  RenderCap.Whole, null, out _, w), "effect chain");
    }

    [Fact]
    public void AOneTopicInfoOrderLaysAFewLines()
    {
        var lines = Enumerable.Range(1, Wide).Select(i => new InfoLine(FormKey.Factory($"{i:X6}:big.esp"), null, false)).ToList();
        var view = DialogueInfoOrder.Compute(
            new List<(string, IReadOnlyList<InfoLine>)> { ("big.esp", lines), ("patch.esp", new[] { lines[0] }) },
            _ => null, Array.Empty<string>());
        var row = new RecordReads.InfoOrderRow("000001:big.esp", "DialogTopic", "HcBigTopic", "patch.esp", view, null);
        AssertBounded(w => RecordsTools.RenderRecordsInfoOrder(new[] { row }, 1, 1, 0, "records  form=info_order", null,
                                                               RenderCap.Whole, null, out _, w), "info order");
    }

    /// <summary>"blocks" and "shapes" widen the one-line lists every mesh prints; "nodes" and "strings" a detail section.</summary>
    [Theory]
    [InlineData("nodes")]
    [InlineData("strings")]
    [InlineData("shapes")]
    [InlineData("blocks")]
    public void AOneMeshLaysAFewSectionLines(string section)
    {
        var prov = new NifProvider("A Mod", "loose");
        var mesh = new NifInspect("20.2.0.7", 12, 100, true, Wide,
            Enumerable.Range(0, section == "blocks" ? Wide : 1).Select(i => new NifBlockTypeCount($"BlockType{i:D5}", 1)).ToList(),
            false, Array.Empty<string>(),
            Enumerable.Range(0, section == "shapes" ? Wide : 1).Select(i => new NifShape(
                $"ShapeNumber{i:D5}", 14u, 1f, "BSTriShape", 14u, "NiAVObject", Array.Empty<NifPartition>(), null,
                Array.Empty<NifTexture>(), new[] { "NPC Spine [Spn0]" })).ToList(),
            Enumerable.Range(0, section == "nodes" ? Wide : 1).Select(i =>
                new NifNode(1, $"NodeNumber{i:D5}", 14u, "NiNode", 14u, "NiAVObject")).ToList(),
            Enumerable.Range(0, section == "strings" ? Wide : 1).Select(i => $"string table entry number {i:D5}").ToList());
        var d = new NifInspectBatchData(
            new[] { new NifInspectData("meshes/wide/mesh.nif", prov, new[] { prov }, false, false, mesh, null) },
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "TestProfile");
        var want = new HashSet<string>(new[] { section }, StringComparer.OrdinalIgnoreCase);
        AssertBounded(w => NifWire.RenderAt(d, want, Array.Empty<string>(), RenderCap.Whole, w), "nif " + section);
    }
}
