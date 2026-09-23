using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>The write renders' budgets on built outcomes: the post-write report blocks (voice coverage, cell shell) and
/// write_seq's quest list are cut with an explicit notice on both transports, and none of those notices prescribes
/// re-issuing the write. Migrated from write-surface-guard's transport arm, which rendered these off the same outcomes.</summary>
[Trait("tier", "unit")]
public sealed class WriteSurfaceReportRenderTests
{
    static readonly WritePatchBuilder.CreateOutcome Voiced = new(
        true, null, @"C:\mods\W2Rep\W2Rep.esp", false,
        new[] { new WritePatchBuilder.CreatedRecord(default, "DialogResponses", "W2RepL1", Array.Empty<WritePatchBuilder.OpResult>()) },
        Array.Empty<string>(), 512)
    {
        Stamp = new OrderStamp("deadbeefdeadbeef", Array.Empty<string>()),
        Voice = new VoiceReport(Enumerable.Range(0, 40).Select(i => new VoiceLine(
            default, "W2VoiceTopic", i,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.fuz", false, null, false,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.lip", false,
            false)).ToList(), Array.Empty<VoiceUndetermined>()),
    };

    static readonly WritePatchBuilder.CreateOutcome Shelled = Voiced with
    {
        Voice = null,
        CellShell = new CellShellReport(Enumerable.Range(0, 30).Select(i => new CellShell(
            default, $"W2CellShell{i:D2}", i % 2 == 0,
            new[] { "lighting template", "terrain / landscape", "water height", "navmesh", "an encounter zone" })).ToList()),
    };

    static readonly SeqOutcome Seq = new(true, null, @"C:\mods\HcSeq\SEQ\HcSeq.seq", "HcSeq",
        new[]
        {
            new SeqFile.SeqQuest(default, "HcSeqQuestAlpha", 0x01000800),
            new SeqFile.SeqQuest(default, "HcSeqQuestBravo", 0x01000801),
            new SeqFile.SeqQuest(default, "HcSeqQuestCharlie", 0x01000802),
        },
        "HcSeq.esp", false);

    static JsonElement Root(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // probe: json create: the voice-coverage report is emitted in full when the budget allows (truncated:false)
    // probe: json create: an UNCUT voice block still carries the census, with truncated:false
    [Fact]
    public void UncutVoiceBlockIsWholeWithItsCensus()
    {
        var d = Root(JsonWire.RenderCreateOutcome(Voiced, 0, false, "patch"));
        var v = d.GetProperty("voice_coverage");
        Assert.Equal(40, v.GetProperty("lines").GetArrayLength());
        Assert.False(d.GetProperty("truncated").GetBoolean());
        Assert.Equal(40, v.GetProperty("total_lines").GetInt32());
        Assert.Equal(40, v.GetProperty("rendered_lines").GetInt32());
        Assert.False(v.GetProperty("truncated").GetBoolean());
    }

    // probe: json create: a ceiling the REPORTS blow past is reported as truncated:true, with the rows dropped
    [Fact]
    public void CappedReportIsTruncated()
    {
        var d = Root(JsonWire.RenderCreateOutcome(Voiced, 1200, false, "patch"));
        Assert.True(d.GetProperty("truncated").GetBoolean());
        Assert.True(d.GetProperty("voice_coverage").GetProperty("lines").GetArrayLength() < 40);
    }

    // probe: json create: a CUT voice block carries its own census (total vs rendered) and names the stakes
    [Fact]
    public void CutVoiceBlockCarriesItsCensusAndStake()
    {
        var v = Root(JsonWire.RenderCreateOutcome(Voiced, 1200, false, "patch")).GetProperty("voice_coverage");
        Assert.Equal(40, v.GetProperty("total_lines").GetInt32());
        Assert.True(v.GetProperty("rendered_lines").GetInt32() < 40);
        Assert.True(v.GetProperty("truncated").GetBoolean());
        var note = v.GetProperty("truncated_note").GetString();
        Assert.Contains(WriteSentences.Twins.VoiceStake, note);
        Assert.DoesNotContain("raise max_chars", note, StringComparison.OrdinalIgnoreCase);
    }

    // probe: json create: truncated_note points at the read-back call, never at raising max_chars
    [Fact]
    public void JsonCreateCapNotePointsAtTheReadBack()
    {
        var note = Root(JsonWire.RenderCreateOutcome(Voiced, 1200, false, "patch")).GetProperty("truncated_note").GetString();
        Assert.Contains("housecarl_records source=", note);
        Assert.Contains("types=[", note);
        Assert.Contains("allocates the records AGAIN", note);
        Assert.DoesNotContain("raise max_chars", note);
    }

    // probe: create text: the voice-coverage cut notice refuses to prescribe re-issuing the create
    [Fact]
    public void TextVoiceCutNoticeRefusesTheReissue()
    {
        var r = WriteTools.RenderCreate(Voiced, maxChars: 900);
        Assert.Contains("voice coverage truncated", r);
        var notice = LineAfter(r, "voice coverage truncated");
        Assert.NotNull(notice);
        Assert.Contains("Do NOT re-issue the create", WriteSentences.Twins.ReportBlockCut);
        Assert.Contains(WriteSentences.Twins.ReportBlockCut, notice);
        Assert.DoesNotContain("raise max_chars to see the rest", notice);
    }

    // probe: create text: the cell-shell block is BUDGETED, with an explicit notice (it was the last unbudgeted one)
    // probe: create text: a CUT cell-shell block still renders the grid-occupancy seam below it
    [Fact]
    public void CellShellBlockIsBudgetedAndKeepsTheSeam()
    {
        var r = WriteTools.RenderCreate(Shelled, maxChars: 700);
        Assert.Contains("cell shell truncated: rendered ", r);
        Assert.Contains(" of 30 cell(s)", r);
        Assert.DoesNotContain("W2CellShell29", r);
        Assert.Contains(WriteSentences.Twins.GridOccupancy, r);
    }

    // probe: create text: without a cap every cell renders (the budget is not a permanent cut)
    [Fact]
    public void UncappedCellShellRendersEveryCell()
    {
        var r = WriteTools.RenderCreate(Shelled);
        Assert.Contains("W2CellShell29", r);
        Assert.DoesNotContain("cell shell truncated", r);
    }

    // probe: write_seq text render: max_chars= drops trailing quest rows with an explicit notice
    // probe: write_seq's truncation notice prices the re-run instead of prescribing 'raise max_chars'
    [Fact]
    public void SeqTextCapPricesTheRerun()
    {
        var r = SeqTools.Render(Seq, maxChars: 80);
        Assert.Contains("[truncated:", r);
        Assert.Contains("max_chars=80", r);
        Assert.Contains(WriteSentences.Twins.SeqListCutRemedy, r);
        Assert.DoesNotContain("HcSeqQuestCharlie", r);
        Assert.Contains("nothing is missing from the FILE", WriteSentences.Twins.SeqListCutRemedy);
        Assert.Contains("writes the .seq again", WriteSentences.Twins.SeqListCutRemedy);
        Assert.DoesNotContain("raise max_chars", r);
    }

    // probe: write_seq text render: without a cap every quest row is listed (the notice is not a permanent cut)
    // probe: write_seq text render: the ABSENT epoch is stated with its reason, like the json twin (D2)
    [Fact]
    public void SeqTextUncappedListsEveryQuestAndStatesTheEpoch()
    {
        var r = SeqTools.Render(Seq);
        Assert.Contains("HcSeqQuestCharlie", r);
        Assert.DoesNotContain("[truncated:", r);
        Assert.Contains("no epoch on this call", r);
        Assert.Contains("load-order-independent", r);
    }

    // probe: write_seq format=json: the truncation note prices the re-run, like its text twin
    [Fact]
    public void SeqJsonCapNotePricesTheRerun()
    {
        var d = Root(JsonWire.RenderSeqOutcome(Seq, 260));
        Assert.True(d.GetProperty("truncated").GetBoolean());
        var note = d.GetProperty("truncated_note").GetString();
        Assert.Contains(WriteSentences.Twins.SeqListCutRemedy, note);
        Assert.DoesNotContain("raise max_chars", note);
    }
}
