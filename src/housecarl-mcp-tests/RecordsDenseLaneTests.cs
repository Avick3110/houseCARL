using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>format='dense'</c> on every <c>housecarl_records</c> lane (#1073): each lane renders the dense
/// table or refuses in one sentence naming the alternative, and none answers in text.</summary>
[Collection("bulk-records")]
[Trait("tier", "integration")]
public sealed class RecordsDenseLaneTests : BulkRecordsTestBase
{
    public RecordsDenseLaneTests(BulkRecordsFixture f) : base(f) { }

    static readonly string[] Weap = { "WEAP" };
    const string DamagePath = "BasicStats.Damage";

    RecordsTools.RecordsScope MasterScope => new() { names = new[] { W.MasterName } };
    string[] Ids => new[] { Fid(W.W1), Fid(W.W2), Fid(W.W3) };

    // ---- the formids= read ----------------------------------------------------------------------------

    [Fact]
    public void ADenseFormidsReadRendersTheScansDetailColumns()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields(DamagePath)));
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", DamagePath }, DenseColumns(doc));
        Assert.Equal("15", DenseRow(doc, Fid(W.W1))[3].GetString());   // the winner's value
        Assert.Equal("30", DenseRow(doc, Fid(W.W3))[3].GetString());
        Assert.Equal(3, doc.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void ADenseFormidsReadWithNoFieldsRendersTheScansSummaryColumns()
    {
        var list = Doc(RecordsTools.Records(Svc, formids: Ids, format: "dense"));
        var scan = Doc(RecordsTools.Records(Svc, types: Weap, format: "dense"));
        Assert.Equal(DenseColumns(scan), DenseColumns(list));
        Assert.Equal(W.ReplName, DenseRow(list, Fid(W.W1))[5].GetString());
    }

    [Fact]
    public void ADenseFormidsReadOffANamedSourceCarriesThatPluginsValues()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, source: Plugin(W.MasterName),
                                           format: "dense", project: Fields(DamagePath)));
        Assert.Equal("10", DenseRow(doc, Fid(W.W1))[3].GetString());
        Assert.Contains(W.MasterName, doc.GetProperty("source").GetString());
    }

    [Fact]
    public void ADenseFormidsReadFoldsAQuantifiedPathToOneRowPerElement()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W3) }, format: "dense",
                                           project: Fields("Keywords[*]")));
        var cells = doc.GetProperty("rows").EnumerateArray().Select(r => r[3].GetString()).ToList();
        Assert.Equal(new[] { Fid(W.KwA), Fid(W.KwB) }, cells);
    }

    /// <summary>A collapsed list's hint names the format hop, since dense itself refuses the depth it would name.</summary>
    [Fact]
    public void ACollapsedListInADenseFormidsReadNamesTheFormatHop()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W3) }, format: "dense", project: Fields("Keywords")));
        Assert.Contains("format=text/json", DenseRow(doc, Fid(W.W3))[3].GetString());
    }

    [Fact]
    public void AnAbsentFormidInADenseReadIsAPerItemErrorNotADroppedRow()
    {
        var absent = "FFFFFF:" + W.MasterName;
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W2), absent }, format: "dense",
                                           project: Fields(DamagePath)));
        Assert.Equal(1, doc.GetProperty("rows").GetArrayLength());
        Assert.Equal(absent, doc.GetProperty("errors")[0].GetProperty("formid").GetString());
    }

    // ---- the lane that used to answer in text ---------------------------------------------------------

    [Fact]
    public void APluginsScopeWithANamedSourceRendersDenseNotText()
    {
        var r = RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, source: Plugin(W.MasterName),
                                     format: "dense", project: Fields(DamagePath));
        var doc = Doc(r);
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", DamagePath }, DenseColumns(doc));
        Assert.Equal("10", DenseRow(doc, Fid(W.W1))[3].GetString());   // the pole's own body, not the winner's 15
    }

    /// <summary>Every lane that serves dense answers with a document; a refusal is the only other answer.</summary>
    public static TheoryData<string> Lanes => new()
    {
        "scan summary", "scan fields", "scope plus pole", "defined_in plus pole", "formids summary",
        "formids fields", "formids pole", "formids counts", "off-order summary", "off-order fields",
    };

    [Theory]
    [MemberData(nameof(Lanes))]
    public void NoLaneAnswersDenseInText(string lane)
    {
        var defined = new RecordsTools.RecordsScope { names = new[] { W.MasterName }, defined_in = true };
        var r = lane switch
        {
            "scan summary" => RecordsTools.Records(Svc, types: Weap, format: "dense"),
            "scan fields" => RecordsTools.Records(Svc, types: Weap, format: "dense", project: Fields(DamagePath)),
            "scope plus pole" => RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, source: Plugin(W.MasterName), format: "dense", project: Fields(DamagePath)),
            "defined_in plus pole" => RecordsTools.Records(Svc, types: Weap, plugins: defined, source: Plugin(W.MasterName), format: "dense", project: Fields(DamagePath)),
            "formids summary" => RecordsTools.Records(Svc, formids: Ids, format: "dense"),
            "formids fields" => RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields(DamagePath)),
            "formids pole" => RecordsTools.Records(Svc, formids: Ids, source: Plugin(W.MasterName), format: "dense", project: Fields(DamagePath)),
            "formids counts" => RecordsTools.Records(Svc, formids: Ids, format: "dense", counts_only: true),
            "off-order summary" => RecordsTools.Records(Svc, types: Weap, source: Plugin(W.OffName), format: "dense"),
            "off-order fields" => RecordsTools.Records(Svc, types: Weap, source: Plugin(W.OffName), format: "dense", project: Fields(DamagePath)),
            _ => throw new ArgumentException(lane),
        };
        Served(r);
        var doc = Doc(r);
        Assert.True(doc.TryGetProperty("columns", out _), $"{lane}: no columns in {First(r)}");
    }

    // ---- the refusals, each naming what to use instead ------------------------------------------------

    [Fact]
    public void DenseAtDepthTwoOnAFormidsReadRefusesNamingTheQuantifiedPathAndText() =>
        Refused(RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Keywords" }, depth = 2 }),
                "project.depth=2", "[*]", "format='text'");

    [Fact]
    public void DenseOnTheRowsFormRefusesNamingTheQuantifiedFieldsPath() =>
        Refused(RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Keywords" } }),
                "'rows'", "form='fields'", "'Keywords[*]'");

    /// <summary>The rows refusal's remedy is served, so it does not send the caller to another refusal.</summary>
    [Fact]
    public void TheRowsRefusalsRemedyIsServedInDense() =>
        Served(RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields("Keywords[*]")));

    /// <summary>An element path names its list in the remedy, and that remedy serves cells, not unreadable ones.</summary>
    [Fact]
    public void DenseOnTheRowsFormOfAnElementPathNamesItsListAndThatRemedyServes()
    {
        Refused(RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Keywords[0]" } }),
                "form='fields'", "'Keywords[*]'");
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W3) }, format: "dense", project: Fields("Keywords[*]")));
        var cells = doc.GetProperty("rows").EnumerateArray().Select(r => r[3].GetString()).ToList();
        Assert.Equal(new[] { Fid(W.KwA), Fid(W.KwB) }, cells);
    }

    /// <summary>counts_only takes the scan's dense shape on every lane: the columns, no rows, the census beside them.</summary>
    [Fact]
    public void DenseCountsOnlyIsAnEmptyTableOnTheFormidsAndScanLanes()
    {
        var absent = "FFFFFF:" + W.MasterName;
        var list = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1), Fid(W.W2), absent }, format: "dense",
                                            project: Fields(DamagePath), counts_only: true));
        var scan = Doc(RecordsTools.Records(Svc, types: Weap, format: "dense", project: Fields(DamagePath), counts_only: true));
        Assert.Equal(DenseColumns(scan), DenseColumns(list));
        Assert.Equal(0, list.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, scan.GetProperty("rows").GetArrayLength());
        Assert.Equal(3, list.GetProperty("total").GetInt32());
        Assert.Equal(1, list.GetProperty("errors").GetInt32());
        Assert.True(scan.GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public void DenseOnTheIdentitySpellingAnswersExactlyAsDenseSummary()
    {
        var r = RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Form("identity"));
        Assert.DoesNotContain("error:", r);
        Assert.Equal(SummaryNameTests.Timeless(RecordsTools.Records(Svc, formids: Ids, format: "dense")), SummaryNameTests.Timeless(r));
    }

    [Fact]
    public void DenseOnAWalkRefusesNamingTextOrJson() =>
        Refused(RecordsTools.Records(Svc, formids: Ids, format: "dense", walk: new RecordsTools.RecordsWalk()),
                "walk", "format='text' or 'json'");
    // ---- the review's coverage: artifacts, the overlay pole, off-order folds, matches -------------------

    [Fact]
    public void ADenseFormidsReadToFileWritesTheArtifactAndNoInlineRows()
    {
        var art = W.Scratch("dense", "formids.jsonl");
        var doc = Doc(RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields(DamagePath), to_file: art));
        Assert.Equal(0, doc.GetProperty("rows").GetArrayLength());
        Assert.Equal("to_file", doc.GetProperty("spilled").GetProperty("reason").GetString());
        Assert.Equal(3, doc.GetProperty("spilled").GetProperty("row_count").GetInt32());
        Assert.True(File.Exists(art));
    }

    [Fact]
    public void ADenseFormidsReadPastItsCeilingSpillsTheCompleteResult()
    {
        var dir = SpillFolders.Emptied(Svc);
        var doc = Doc(RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields(DamagePath), max_chars: 300));
        Assert.True(doc.GetProperty("truncated").GetBoolean());
        Assert.Equal("over_inline_ceiling", doc.GetProperty("spilled").GetProperty("reason").GetString());
        Assert.Equal(3, doc.GetProperty("spilled").GetProperty("row_count").GetInt32());
        Assert.Single(Directory.GetFiles(dir, "*.jsonl"));
    }

    /// <summary>No SkyPatcher INI in this world, so the post-overlay body is the winner's.</summary>
    [Fact]
    public void ADenseFormidsReadOffTheOverlayPoleRendersColumns()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, format: "dense", project: Fields(DamagePath),
                                           source: Je("{\"overlay\": \"skypatcher\", \"state\": \"post\"}")));
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", DamagePath }, DenseColumns(doc));
        Assert.Equal("15", DenseRow(doc, Fid(W.W1))[3].GetString());
    }

    [Fact]
    public void AnOffOrderDenseScanFoldsAQuantifiedPathToOneRowPerElement()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, source: Plugin(W.OffName), format: "dense", project: Fields("Keywords[*]")));
        var cells = doc.GetProperty("rows").EnumerateArray().Where(r => r[0].GetString() == Fid(W.OffW2)).Select(r => r[3].GetString()).ToList();
        Assert.Equal(new[] { Fid(W.KwA), Fid(W.KwB) }, cells);
    }

    /// <summary>W1 is in the replacer, W2 is not, so W2 is an error row, and both keep the target they matched.</summary>
    [Fact]
    public void AScopePlusPoleDenseScanKeepsMatchesOnRowsAndErrors()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, source: Plugin(W.ReplName), format: "dense",
                                           project: Fields(DamagePath), references: new[] { Fid(W.KwA), Fid(W.KwB) }));
        Assert.Equal("matches", DenseColumns(doc).Last());
        Assert.Contains(Fid(W.KwA), DenseRow(doc, Fid(W.W1)).EnumerateArray().Last().GetString());
        var err = doc.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("formid").GetString() == Fid(W.W2));
        Assert.Contains(Fid(W.KwB), err.GetProperty("matches").GetString());
    }

    /// <summary>counts_only on an off-order file scan lays the same columns as the same call without it.</summary>
    [Fact]
    public void OffOrderDenseCountsOnlyKeepsTheFieldsColumns()
    {
        var full = Doc(RecordsTools.Records(Svc, types: Weap, source: Plugin(W.OffName), format: "dense", project: Fields(DamagePath)));
        var counts = Doc(RecordsTools.Records(Svc, types: Weap, source: Plugin(W.OffName), format: "dense", project: Fields(DamagePath), counts_only: true));
        Assert.Equal(DenseColumns(full), DenseColumns(counts));
        Assert.Equal(0, counts.GetProperty("rows").GetArrayLength());
        Assert.Equal(2, counts.GetProperty("total").GetInt32());
    }

    // ---- depth refusals under dense name a call dense serves -----------------------------------------

    [Fact]
    public void DenseAtDepthOneOnAQuantifiedPathNamesDroppingDepthAndThatServes()
    {
        var r = RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Keywords[*]" }, depth = 1 });
        Refused(r, "drop project.depth", "'Keywords[*count]'");
        Assert.DoesNotContain("depth >= 2", r);
        Served(RecordsTools.Records(Svc, formids: Ids, format: "dense", project: Fields("Keywords[*]")));
    }

    [Fact]
    public void DenseAtDepthOneOnTheRowsFormNamesTheQuantifiedFieldsPath()
    {
        var r = RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Keywords" }, depth = 1 });
        Refused(r, "drop project.depth", "form='fields'", "'Keywords[*]'");
        Assert.DoesNotContain("depth >= 2", r);
    }

    [Fact]
    public void DenseDepthOnAnAlreadyQuantifiedPathNamesDroppingDepthOnly()
    {
        var r = RecordsTools.Records(Svc, formids: Ids, format: "dense",
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Keywords[*]" }, depth = 3 });
        Refused(r, "'Keywords[*]' already reads one dense row per element", "drop project.depth");
        Assert.DoesNotContain("quantify a list path", r);
    }
}
