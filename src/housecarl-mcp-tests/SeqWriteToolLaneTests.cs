using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_write_seq</c>'s tool body, <c>SeqTools.WriteSeq</c> (migrated from the <c>seq-write-guard</c> probe, #312):
/// <c>out_path=</c> wins over <c>patch=</c>/<c>into=</c> and the ignored lane is stated on every render, including a
/// refusal and the nothing-to-do no-op; the unchanged and replaced states render as themselves on both transports.
/// </summary>
[Trait("tier", "integration")]
public sealed class SeqWriteToolLaneTests : IDisposable
{
    readonly SeqWriteWorld W = new();
    public void Dispose() => W.Dispose();

    string Source => Path.GetFileName(W.SvcPlugin);

    // Probe TOOL-LANE: "out_path= wins over patch= and the ignored lane is STATED, with no folder cut for it".
    [Fact]
    public void OutPathWinsOverPatchAndSaysSo()
    {
        var r = SeqTools.WriteSeq(W.Svc, source: Source, patch: "HcSeqIgnored", out_path: W.UserMod);
        Assert.Contains("out_path= was given", r);
        Assert.Contains("ignored", r);
        Assert.Empty(Directory.EnumerateDirectories(W.Mods, "houseCARL - HcSeqIgnored*"));
        Assert.True(File.Exists(W.UserSeq));
    }

    // Probe RENDER-UNCHANGED: "the no-op renders as 'unchanged … NOTHING was written', and an out_path destination is
    // NOT called a houseCARL mod folder".
    [Fact]
    public void TheUnchangedTextRenderLeadsWithUnchanged()
    {
        Assert.True(W.WriteToUserMod().Success);
        var r = SeqTools.WriteSeq(W.Svc, source: Source, out_path: W.UserMod);
        Assert.StartsWith("unchanged", r);
        Assert.Contains(WriteSentences.Twins.SeqUnchanged, r);
        Assert.DoesNotContain("houseCARL mod folder — enable it", r);
    }

    // Probe JSON-UNCHANGED: "json reports written=false + unchanged=true + the path".
    [Fact]
    public void TheUnchangedJsonSaysNotWritten()
    {
        Assert.True(W.WriteToUserMod().Success);
        var d = JsonDocument.Parse(SeqTools.WriteSeq(W.Svc, source: Source, out_path: W.UserMod, format: "json")).RootElement;
        Assert.False(d.GetProperty("written").GetBoolean());
        Assert.True(d.GetProperty("unchanged").GetBoolean());
        Assert.True(d.GetProperty("user_chose_out_path").GetBoolean());
        Assert.Equal(W.UserSeq, d.GetProperty("seq_path").GetString(), ignoreCase: true);
    }

    // Probe REPLACED-JSON: "the replaced state and its note are on the json transport too".
    [Fact]
    public void TheReplacedJsonCarriesTheStateAndItsNote()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(W.UserSeq)!);
        File.WriteAllBytes(W.UserSeq, new byte[] { 9, 9, 9, 9, 9, 9 });
        var d = JsonDocument.Parse(SeqTools.WriteSeq(W.Svc, source: Source, out_path: W.UserMod, format: "json")).RootElement;
        Assert.True(d.GetProperty("replaced").GetBoolean());
        Assert.False(d.GetProperty("replaced_same_bytes").GetBoolean());
        Assert.Equal(WriteSentences.Twins.SeqReplacedUserFolder, d.GetProperty("replaced_note").GetString());
    }

    // Probe LANE-ORDER: "out_path= + patch= + into= is accepted with the ignored-lane note, not refused".
    [Fact]
    public void AllThreeLanesTogetherAreAcceptedWithTheNote()
    {
        var r = SeqTools.WriteSeq(W.Svc, source: Source, patch: "HcSeqIgnoredA", into: "HcSeqIgnoredB.esp", out_path: W.UserMod);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("out_path= was given", r);
    }

    // Probe LANE-ORDER-NEG: "patch= + into= alone is still refused BY NAME".
    [Fact]
    public void PatchAndIntoWithoutOutPathAreStillRefused()
    {
        var r = SeqTools.WriteSeq(W.Svc, source: Source, patch: "HcSeqIgnoredA", into: "HcSeqIgnoredB.esp");
        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
    }

    // Probe NOOP-LANE-NOTE: "the nothing-to-do render states the ignored lane too".
    [Fact]
    public void TheNoQuestRenderStatesTheIgnoredLane()
    {
        var r = SeqTools.WriteSeq(W.Svc, source: W.EmptyPlugin, patch: "HcSeqIgnoredC", out_path: W.UserMod);
        Assert.Contains("no start-game-enabled quests", r);
        Assert.Contains("out_path= was given", r);
    }

    // Probe LANE-NOTE-ON-REFUSAL: "a failed call still states the ignored lane".
    [Fact]
    public void ARefusalStillStatesTheIgnoredLane()
    {
        var r = SeqTools.WriteSeq(W.Svc, source: "HcSeqNoSuchPlugin.esp", patch: "HcSeqIgnoredD", out_path: W.UserMod);
        Assert.StartsWith("error:", r);
        Assert.Contains("out_path= was given", r);
    }

    // Probe LANE-NOTE-ON-REFUSAL-JSON: "…on the json transport too (D2)".
    [Fact]
    public void AJsonRefusalStillStatesTheIgnoredLane()
    {
        var d = JsonDocument.Parse(SeqTools.WriteSeq(W.Svc, source: "HcSeqNoSuchPlugin.esp", patch: "HcSeqIgnoredD",
            out_path: W.UserMod, format: "json")).RootElement;
        Assert.False(d.GetProperty("ok").GetBoolean());
        Assert.Contains("out_path= was given", d.GetProperty("lane_note").GetString());
    }
}
