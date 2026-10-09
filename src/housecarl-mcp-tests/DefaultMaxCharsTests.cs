using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

// The server default answer size (#1093): max_chars=0 cuts or spills at 40k, on a render lane, a spill lane and a write lane.

[Trait("tier", "integration")]
public sealed class DefaultMaxCharsSpillTests : ArtifactTestBase, IClassFixture<ArtifactFixture>
{
    public DefaultMaxCharsSpillTests(ArtifactFixture f) : base(f) { }

    string IdentityOf600(int maxChars) =>
        RecordsTools.Records(Svc, formids: Enumerable.Repeat(Fid(W.BigList), 600).ToArray(),
                             project: Identity,
                             max_chars: maxChars);

    [Fact]
    public void ARecordsAnswerBetween40kAnd80kSpillsAtTheDefault()
    {
        var wide = IdentityOf600(80_000);
        Assert.DoesNotContain("spilled:", wide);
        Assert.InRange(wide.Length, 40_001, 80_000);

        var dir = SpillFolders.Emptied(Svc);
        var r = IdentityOf600(0);
        Assert.Contains("spilled:", r);
        Assert.True(r.Length <= 40_000, $"default answer is {r.Length} chars");
        Assert.Single(Directory.GetFiles(dir, "*.jsonl"));
    }
}

[Trait("tier", "integration")]
public sealed class DefaultMaxCharsRenderTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public DefaultMaxCharsRenderTests(AssetSelectWorld w) => _w = w;

    static int Omitted(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("selector_notes_omitted").GetInt32();

    [Fact]
    public void AnAssetStatusAnswerBetween40kAnd80kIsCutAtTheDefault()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"meshes/hcnothing{i}/**/*.nif").ToArray();

        var wide = AssetTools.AssetStatus(_w.Svc, under: many, format: "json", max_chars: 80_000);
        Assert.Equal(0, Omitted(wide));
        Assert.InRange(wide.Length, 40_001, 80_000);

        var r = AssetTools.AssetStatus(_w.Svc, under: many, format: "json");
        Assert.True(Omitted(r) > 0, "nothing was cut at the default");
        Assert.True(r.Length <= 40_000, $"default answer is {r.Length} chars");
    }
}

public sealed class DefaultMaxCharsWriteTests
{
    static readonly SeqOutcome Seq = new(true, null, "C:/mods/HcSeq/SEQ/HcSeq.seq", "HcSeq",
        Enumerable.Range(0, 1800).Select(i => new HousecarlCore.SeqFile.SeqQuest(default, $"HcSeqQuest{i:D4}", 0x01000800u + (uint)i)).ToArray(),
        "HcSeq.esp", false);

    [Fact]
    public void AWriteReplyBetween40kAnd80kIsCutAtTheDefault()
    {
        var wide = SeqTools.Render(Seq, maxChars: 80_000);
        Assert.DoesNotContain("[truncated:", wide);
        Assert.InRange(wide.Length, 40_001, 80_000);

        var r = SeqTools.Render(Seq);
        Assert.Contains("[truncated:", r);
        Assert.Contains("listed at max_chars=40000;", r);
    }
}
