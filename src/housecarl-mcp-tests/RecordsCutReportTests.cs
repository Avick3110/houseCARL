using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A deep read cut at its line budget is said at the top of the answer — inline, in the spill block and on the
/// file's manifest — and a to_file= read runs under the file budget (30,000 lines) while every inline read keeps
/// 2,000. BigList (2,001 elements) is over the inline budget and under the file one; HugeList (30,001) is over both.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsCutReportTests : RecordsTestBase
{
    public RecordsCutReportTests(RecordsFixture f) : base(f) { }

    const int InlineBudget = 2000, FileBudget = 30_000;

    static RecordsTools.RecordsProject Deep(string form = "fields", string field = "Items", int? depth = 2) =>
        new() { form = form, fields = form == "everything" ? null : new[] { field }, depth = depth };

    static JsonElement Manifest(string art) => Je(System.IO.File.ReadLines(art).First());

    static IEnumerable<JsonElement> Rows(string art) => System.IO.File.ReadLines(art).Skip(1).Select(Je);

    static JsonElement Row(string art, string editorId) =>
        Rows(art).First(r => r.TryGetProperty("editorid", out var e) && e.GetString() == editorId);

    static bool EndsCut(JsonElement row) =>
        row.GetProperty("fields").EnumerateArray().LastOrDefault() is { ValueKind: JsonValueKind.Object } last
        && last.GetProperty("path").GetString() == ReadEngine.CutPath;

    // ---- the budgets ------------------------------------------------------------------------------------

    [Fact]
    public void TheBudgetsAreTheNumbersChosen()
    {
        Assert.Equal(FileBudget, RecordsTools.ToFileLineBudget);
        Assert.Equal(InlineBudget, ReadEngine.MaxExpandNodes);
    }

    [Fact]
    public void AFileReadOfAListOverTheInlineBudgetComesBackWhole()
    {
        var art = W.Scratch("results", "cut-biglist-file.jsonl");
        var r = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep(), format: "json", to_file: art));
        var row = Row(art, "HcRecBigList");
        Assert.False(EndsCut(row));
        Assert.Contains(row.GetProperty("fields").EnumerateArray(), f => f.GetProperty("path").GetString() == $"Items[{InlineBudget}]");
        Assert.False(Manifest(art).TryGetProperty("records_cut", out _));
        Assert.True(r.GetProperty("spilled").GetProperty("complete").GetBoolean());
        Assert.False(r.GetProperty("spilled").TryGetProperty("records_cut", out _));
    }

    [Fact]
    public void TheSameReadInlineIsCutAtTheInlineBudget()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep(), max_chars: 400_000);
        Served(r, $"expansion truncated at {InlineBudget} lines");
        Assert.DoesNotContain($"Items[{InlineBudget}] =", r);
    }

    /// <summary>Every reading form a to_file= call can write reads under the file budget, from every source door.</summary>
    [Theory]
    [InlineData("fields", "Items", null, false)]
    [InlineData("fields", "Items[*]", null, false)]
    [InlineData("rows", "Items", null, false)]
    [InlineData("everything", "", null, false)]
    [InlineData("fields", "Items[*]", "dense", false)]
    [InlineData("fields", "Items", null, true)]
    public void EveryReadingFormReadsUnderTheFileBudget(string form, string field, string? format, bool namedSource)
    {
        var art = W.Scratch("results", $"cut-form-{form}-{field.Replace("[*]", "fold")}-{format}-{namedSource}.jsonl");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: format == "dense" ? new[] { "FLST" } : null,
                                     source: namedSource ? Plugin(W.MasterName) : null,
                                     project: Deep(form, field, field.EndsWith("[*]") ? null : 2),
                                     format: format, to_file: art);
        Served(r);
        Assert.False(EndsCut(Row(art, "HcRecBigList")));
        Assert.False(Manifest(art).TryGetProperty("records_cut", out _));
    }

    [Fact]
    public void TheScanLaneWritesUnderTheFileBudgetToo()
    {
        var art = W.Scratch("results", "cut-scan-file.jsonl");
        RecordsTools.Records(Svc, types: new[] { "FLST" }, project: Deep(), format: "json", to_file: art);
        Assert.False(EndsCut(Row(art, "HcRecBigList")));
        Assert.True(EndsCut(Row(art, "HcRecHugeList")));
        Assert.Equal(1, Manifest(art).GetProperty("records_cut").GetInt32());
    }

    /// <summary>A scan selection (types=, no formids=) whose form reads bodies on the scan's own body lane.</summary>
    [Theory]
    [InlineData("rows", "Items", 2)]
    [InlineData("everything", "", 2)]
    [InlineData("fields", "Items[*]", null)]
    public void TheScanBodyLaneWritesUnderTheFileBudget(string form, string field, int? depth)
    {
        var art = W.Scratch("results", $"cut-scanbody-{form}.jsonl");
        Served(RecordsTools.Records(Svc, types: new[] { "FLST" }, project: Deep(form, field, depth), format: "json", to_file: art));
        Assert.False(EndsCut(Row(art, "HcRecBigList")));
        Assert.Equal(new[] { Fid(W.HugeList) }, Manifest(art).GetProperty("cut_records").EnumerateArray().Select(x => x.GetString()));
    }

    /// <summary>The SkyPatcher overlay pole and an off-order file pole read under the file budget too.</summary>
    [Fact]
    public void TheOverlayAndOffOrderPolesWriteUnderTheFileBudget()
    {
        foreach (var (name, src) in new[] { ("overlay", Overlay("post")), ("offorder", Plugin(W.OldName)) })
        {
            var art = W.Scratch("results", $"cut-pole-{name}.jsonl");
            Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, source: src, project: Deep(), format: "json", to_file: art));
            Assert.False(EndsCut(Row(art, "HcRecBigList")), name);
            Assert.False(Manifest(art).TryGetProperty("records_cut", out _), name);
        }
    }

    /// <summary>A statement names three records inline and ten in the manifest notes, and counts the rest; the
    /// manifest's cut_records lists every one.</summary>
    [Fact]
    public void TheCutStatementCountsTheRecordsItDoesNotName()
    {
        RecordFields Cut(int i) => new("FormList", $"{i:X6}:Hc.esm", $"HcCut{i}", Array.Empty<FieldValue>(), CutAt: InlineBudget);
        var inline = new RecordCuts();
        for (int i = 0; i < 4; i++) inline.Add(Cut(i));
        Assert.Contains("HcCut2) and 1 more —", inline.Sentence());
        Assert.DoesNotContain("HcCut3", inline.Sentence());

        var file = W.Scratch("results", "cut-eleven.jsonl");
        using (var writer = new ResultArtifact.Writer())
        {
            for (int i = 0; i < 11; i++)
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteEndObject(); }, "FormList", Cut(i));
            Assert.Null(writer.Save(ArtifactTarget.Named(file), ToolNames.Records, Array.Empty<KeyValuePair<string, string>>(),
                                    "formid", new[] { "formid" }, "input order", total: 11, epoch: "e2-deadbeef").Error);
        }
        var m = Manifest(file);
        Assert.Equal(11, m.GetProperty("cut_records").GetArrayLength());
        var note = m.GetProperty("notes").EnumerateArray().Single().GetString()!;
        Assert.Contains("HcCut9) and 1 more —", note);
        Assert.DoesNotContain("HcCut10", note);
    }

    // ---- the report on a file ----------------------------------------------------------------------------

    [Fact]
    public void ARecordOverTheFileBudgetIsNamedOnTheManifestAndTheSpillBlock()
    {
        var art = W.Scratch("results", "cut-huge-json.jsonl");
        var r = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.HugeList) }, project: Deep(), format: "json", to_file: art));
        var m = Manifest(art);
        Assert.Equal(1, m.GetProperty("records_cut").GetInt32());
        Assert.Equal(FileBudget, m.GetProperty("cut_budget").GetInt32());
        Assert.Equal(new[] { Fid(W.HugeList) }, m.GetProperty("cut_records").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains(m.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains($"cut at {FileBudget} field lines")
                                                                     && n.GetString()!.Contains("HcRecHugeList"));
        Assert.Equal(m.GetProperty("row_count").GetInt32(), m.GetProperty("total").GetInt32());   // the row count is unchanged
        Assert.True(EndsCut(Row(art, "HcRecHugeList")));
        var s = r.GetProperty("spilled");
        Assert.False(s.GetProperty("complete").GetBoolean());
        Assert.Equal(1, s.GetProperty("records_cut").GetInt32());
        Assert.Equal(FileBudget, s.GetProperty("cut_budget").GetInt32());
        Assert.Contains("HcRecHugeList", s.GetProperty("cut_note").GetString());
        Assert.Contains($"expansion truncated at {FileBudget} lines", System.IO.File.ReadAllText(art));
    }

    [Fact]
    public void TheTextSpillBlockSaysTheFileIsNotWhole()
    {
        var art = W.Scratch("results", "cut-huge-text.jsonl");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.HugeList) }, project: Deep(), to_file: art);
        Served(r, "spilled: every row, NOT every row whole (1 row, 1 cut)", $"  cut: 1 record was cut at {FileBudget} field lines", "HcRecHugeList");
        Assert.DoesNotContain("spilled: complete result", r);
    }

    [Fact]
    public void ADenseFileOverTheFileBudgetIsNamedInItsSpillBlock()
    {
        var art = W.Scratch("results", "cut-huge-dense.jsonl");
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.HugeList) }, types: new[] { "FLST" }, format: "dense",
                                          project: Fields("Items[*]"), to_file: art));
        Assert.Equal(1, doc.GetProperty("spilled").GetProperty("records_cut").GetInt32());
        Assert.False(doc.GetProperty("spilled").GetProperty("complete").GetBoolean());
    }

    // ---- the report inline ---------------------------------------------------------------------------

    [Fact]
    public void AnInlineJsonAnswerCountsItsCutRecordAtTheTop()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep(), format: "json", max_chars: 400_000));
        Assert.Equal(1, doc.GetProperty("records_cut").GetInt32());
        Assert.Equal(InlineBudget, doc.GetProperty("cut_budget").GetInt32());
        Assert.Contains("HcRecBigList", doc.GetProperty("cut_note").GetString());
        Assert.False(doc.GetProperty("truncated").GetBoolean());   // no row was held back
    }

    [Fact]
    public void AnInlineScanAnswerCountsItsCutRecordsAtTheTop()
    {
        var doc = Je(RecordsTools.Records(Svc, types: new[] { "FLST" }, project: Fields("Items"), format: "json", max_chars: 4_000_000));
        Assert.False(doc.TryGetProperty("records_cut", out _));   // depth 1 reads no element, and an uncut answer carries no count
        var deep = RecordsTools.Records(Svc, types: new[] { "FLST" }, project: Deep(), max_chars: 4_000_000);
        Served(deep, "cut: 2 records were cut at 2000 field lines", "HcRecBigList", "HcRecHugeList");
        var deepJson = Je(RecordsTools.Records(Svc, types: new[] { "FLST" }, project: Deep(), format: "json", max_chars: 4_000_000));
        Assert.Equal(2, deepJson.GetProperty("records_cut").GetInt32());
    }

    /// <summary>The <c>x1</c> shape: the default max_chars drops the '…' line, so only the header says the read was cut.</summary>
    [Fact]
    public void TheTextHeaderSaysSoWhenMaxCharsDropsTheCutLine()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep());
        Assert.DoesNotContain("expansion truncated", r);
        var lines = r.Split('\n');
        int batch = Array.FindIndex(lines, l => l.StartsWith("batch: ", StringComparison.Ordinal));
        Assert.StartsWith($"cut: 1 record was cut at {InlineBudget} field lines", lines[batch + 1]);
        Assert.Contains($"field lines at max_chars", r);
        Assert.Contains($" of {InlineBudget} field lines", r);   // the '…' line is not counted as a field line
    }

    /// <summary>The max_chars notice's "showing k" counts the field lines rendered, never the cut line, whether a fold
    /// put that line first or the read left it last.</summary>
    [Theory]
    [InlineData("Items[*]", null)]
    [InlineData("Items", 2)]
    public void TheMaxCharsNoticeCountsTheFieldLinesItShowed(string field, int? depth)
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) },
                                     project: Deep(field: field, depth: depth), max_chars: 20_000);
        var lines = r.Split('\n');
        int from = Array.FindIndex(lines, l => l.StartsWith("fields (from ", StringComparison.Ordinal));
        int at = Array.FindIndex(lines, l => l.StartsWith("  ... [truncated: showing ", StringComparison.Ordinal));
        Assert.True(from >= 0 && at > from, r);
        int rendered = lines[(from + 1)..at].Count(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("  … ", StringComparison.Ordinal));
        var m = System.Text.RegularExpressions.Regex.Match(lines[at], @"showing (\d+) of (\d+) field lines");
        Assert.Equal(rendered, int.Parse(m.Groups[1].Value));
        if (field.EndsWith("[*]")) Assert.Contains(lines[(from + 1)..at], l => l.StartsWith("  … ", StringComparison.Ordinal));   // the fold's cut line rendered first
    }

    /// <summary>A max_chars that shows every field line and drops only the cut line gives no truncation notice; the
    /// header still says the read was cut.</summary>
    [Fact]
    public void NoNoticeWhenOnlyTheCutLineIsDropped()
    {
        var full = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep(), max_chars: 400_000);
        int whole = full.Length;
        var fullLines = full.Split('\n');
        var lastField = fullLines[Array.FindIndex(fullLines, l => l.StartsWith("  … ", StringComparison.Ordinal)) - 1];
        bool seen = false;
        // The render holds room for its notices and the cut statement, so the window sits above the whole answer's length.
        for (int cap = whole; cap < whole + 1500; cap += 4)
        {
            var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Deep(), max_chars: cap);
            if (!r.Contains(lastField + "\n") || r.Contains("expansion truncated")) continue;
            seen = true;
            Assert.DoesNotContain("[truncated: showing", r);
            Assert.Contains($"cut: 1 record was cut at {InlineBudget} field lines", r);
        }
        Assert.True(seen);   // some max_chars in the sweep showed every field line and only the cut line was dropped
    }

    /// <summary>A fold column the cut read never reached says it was not read, not "(absent)"; the dev shape is
    /// MQ101 <c>Aliases[*]</c>, <c>Stages[*]</c>, <c>Objectives[*count]</c>. The same lines uncut answer as before.</summary>
    [Fact]
    public void AFoldColumnPastTheCutSaysItWasNotRead()
    {
        var (plan, err) = FieldFolds.Parse(new[] { "Aliases[*]", "Stages[*]", "Objectives[*count]", "EditorID" });
        Assert.Null(err);
        var read = new List<FieldValue>
        {
            new("Aliases", false, null, "(2 elements)", Count: 2),
            new("Aliases[0]", true, "A", null),
            new("Aliases[1]", true, "B", null),
        };
        var cut = new RecordFields("Quest", "000800:Hc.esm", "HcQuest",
                                   read.Append(new FieldValue(ReadEngine.CutPath, false, null, ReadEngine.CutNote(InlineBudget))).ToList(),
                                   CutAt: InlineBudget);
        var (cols, _, cerr) = plan!.Columns(cut);
        Assert.Null(cerr);
        foreach (var (col, path) in new[] { (cols![1], "Stages[*]"), (cols[2], "Objectives[*count]"), (cols[3], "EditorID") })
        {
            var only = Assert.Single(col);
            Assert.Equal(path, only.Path);
            Assert.StartsWith($"(expansion truncated at {InlineBudget} lines —", only.Note);
            Assert.Contains("NOT read", only.Note);
        }
        Assert.Equal(2, cols[0].Count);   // the column the read reached keeps its elements

        var (whole, _, _) = plan.Columns(new RecordFields("Quest", "000800:Hc.esm", "HcQuest", read));
        Assert.Equal(ReadEngine.AbsentNote, Assert.Single(whole![1]).Note);
        Assert.Equal(ReadEngine.AbsentNote, Assert.Single(whole[2]).Note);
        Assert.Empty(whole[3]);
    }

    /// <summary>The same through the tool: a plain column after a fold that ran the budget out.</summary>
    [Fact]
    public void AColumnPastTheCutSaysItWasNotReadInEveryForm()
    {
        var p = Fields("Items[*]", "EditorID");
        var text = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: p, max_chars: 400_000);
        Served(text, "EditorID = (expansion truncated at 2000 lines — the read stopped before this path, so it was NOT read");
        var dense = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" }, format: "dense",
                                            project: p, max_chars: 400_000));
        Assert.Contains(dense.GetProperty("rows").EnumerateArray(), row => row[4].GetString()?.Contains("NOT read") == true);
    }

    [Fact]
    public void AnInlineDenseAnswerKeepsItsReadNote()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" },
                                          format: "dense", project: Fields("Items[*]"), max_chars: 400_000));
        Assert.Contains("expansion truncated", doc.GetProperty("read_note").GetString()!);
    }

    [Fact]
    public void AnInlineDenseAnswerCountsAndNamesItsCutRecordAtTheTop()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" },
                                          format: "dense", project: Fields("Items[*]"), max_chars: 400_000));
        Assert.Equal(1, doc.GetProperty("records_cut").GetInt32());
        Assert.Equal(InlineBudget, doc.GetProperty("cut_budget").GetInt32());
        Assert.Contains("HcRecBigList", doc.GetProperty("cut_note").GetString());
        var whole = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" },
                                            format: "dense", project: Fields("Items[*count]"), max_chars: 400_000));
        Assert.False(whole.TryGetProperty("records_cut", out _));
    }

    [Fact]
    public void AnAutoSpillReadsAtTheInlineBudgetAndCountsTheCut()
    {
        // Two wide rows: the first fills max_chars, so the second is held back and the answer spills.
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList), Fid(W.HugeList) }, project: Deep(), format: "json"));
        var s = doc.GetProperty("spilled");
        Assert.Equal("over_inline_ceiling", s.GetProperty("reason").GetString());
        Assert.Equal(2, s.GetProperty("records_cut").GetInt32());
        Assert.Equal(InlineBudget, s.GetProperty("cut_budget").GetInt32());
        Assert.False(s.GetProperty("complete").GetBoolean());
        Assert.True(EndsCut(Row(s.GetProperty("path").GetString()!, "HcRecBigList")));
    }

    [Fact]
    public void ADeltaAndATreeCountTheirCutRowsAtTheTopAndOnFile()
    {
        foreach (var form in new[] { "delta", "tree" })
        {
            var p = new RecordsTools.RecordsProject { form = form, fields = new[] { "Items" } };
            JsonElement? versus = form == "delta" ? Plugin(W.MasterName) : null;
            var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p, format: "json"));
            Assert.Equal(1, doc.GetProperty("records_cut").GetInt32());
            var text = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p);
            Served(text, "1 cut by the deep read's line budget");
            var art = W.Scratch("results", $"cut-{form}.jsonl");
            var f = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p, format: "json", to_file: art));
            var m = Manifest(art);
            Assert.Equal(1, m.GetProperty("records_cut").GetInt32());
            Assert.Equal(InlineBudget, m.GetProperty("cut_budget").GetInt32());   // a comparison keeps the inline budget on file
            Assert.False(f.GetProperty("spilled").GetProperty("complete").GetBoolean());
            Assert.Contains("compared on a read cut", f.GetProperty("spilled").GetProperty("cut_note").GetString());
        }
    }

    /// <summary>An inline delta or tree names its cut row at the top, with the budget and the remedy; an uncut one says nothing.</summary>
    [Fact]
    public void ADeltaAndATreeNameTheirCutRowAtTheTop()
    {
        foreach (var form in new[] { "delta", "tree" })
        {
            JsonElement? versus = form == "delta" ? Plugin(W.MasterName) : null;
            var p = new RecordsTools.RecordsProject { form = form, fields = new[] { "Items" } };
            var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p, format: "json"));
            Assert.Equal(InlineBudget, doc.GetProperty("cut_budget").GetInt32());
            Assert.Contains("HcRecBigList", doc.GetProperty("cut_note").GetString());
            Assert.Contains("narrower field path", doc.GetProperty("cut_note").GetString());
            var text = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p);
            Served(text, "(each such row says TRUNCATED or INCOMPLETE)",
                   $"cut: 1 record was compared on a read cut at {InlineBudget} field lines", "HcRecBigList", "narrower field path");
            if (form == "delta") Assert.Contains("TRUNCATED at the cap", text);   // the word the header names for a no-delta row

            var whole = new RecordsTools.RecordsProject { form = form, fields = new[] { "EditorID" } };
            var wdoc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: whole, format: "json"));
            Assert.False(wdoc.TryGetProperty("cut_note", out _));
            Assert.False(wdoc.TryGetProperty("cut_budget", out _));
            Assert.DoesNotContain("cut: ", RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: whole));
        }
    }

    /// <summary>A census over a cut comparison counts the cut row, and one over an uncut comparison carries no such count.</summary>
    [Fact]
    public void ADeltaAndATreeCensusCountTheirCutRows()
    {
        foreach (var form in new[] { "delta", "tree" })
        {
            JsonElement? versus = form == "delta" ? Plugin(W.MasterName) : null;
            foreach (var (field, cut) in new[] { ("Items", true), ("EditorID", false) })
            {
                var p = new RecordsTools.RecordsProject { form = form, fields = new[] { field } };
                var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p, format: "json", counts_only: true));
                Assert.Equal(cut, doc.TryGetProperty("records_cut", out var n) && n.GetInt32() == 1);
                var text = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, versus: versus, project: p, counts_only: true);
                Assert.Equal(cut, text.Contains(" records_cut=1"));
                Assert.DoesNotContain("records_cut=0", text);
            }
        }
    }

    [Fact]
    public void AReadUnderTheBudgetSaysNothingAboutACut()
    {
        var art = W.Scratch("results", "cut-count.jsonl");
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.HugeList) }, project: Fields("Items[*count]"), format: "json", to_file: art));
        Assert.True(doc.GetProperty("spilled").GetProperty("complete").GetBoolean());
        Assert.False(Manifest(art).TryGetProperty("records_cut", out _));
        var inline = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.HugeList) }, project: Fields("Items[*count]"), format: "json"));
        Assert.False(inline.TryGetProperty("records_cut", out _));
    }
}
