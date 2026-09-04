using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The scan lane's PROJECTIONS and TRANSPORTS on <c>housecarl_records</c>: the aggregate count table, the
/// dense columnar render, container expansion at depth, the reverse-lookup un-merge, exact-window paging,
/// and the scoped-body-vs-winner notes on both the match axis and the display axis.
///
/// <para>The rule that a projection form scopes which pairings are legal is asserted in
/// <c>RecordsScanLaneTests</c>, not here.</para>
/// </summary>
[Collection("bulk-records")]
[Trait("tier", "integration")]
public sealed class RecordsScanProjectionTests : BulkRecordsTestBase
{
    public RecordsScanProjectionTests(BulkRecordsFixture f) : base(f) { }

    static readonly string[] Weap = { "WEAP" };
    static readonly string DamagePath = "BasicStats.Damage";

    RecordsTools.RecordsScope MasterScope => new() { names = new[] { W.MasterName } };
    RecordsTools.RecordsScope BothScope => new() { names = new[] { W.MasterName, W.ReplName } };

    // ---- the scan's own accounting ----------------------------------------------------------------

    [Fact]
    public void TheScanSummaryJsonCarriesTheTrueTotalAndOneMatchPerRecord()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json"));
        Assert.Equal(3, doc.GetProperty("total").GetInt32());
        Assert.Equal(3, doc.GetProperty("matches").GetArrayLength());
        Assert.False(doc.GetProperty("capped").GetBoolean());
    }

    /// <summary>The aggregation is not limit-capped: `total` counts every match, not the rendered rows.</summary>
    [Fact]
    public void TheAggregateJsonNamesItsCountKeyAndTotalsEveryMatch()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", project: Aggregate("winner"), limit: 1));
        Assert.Equal("winner", doc.GetProperty("group_by").GetString());
        Assert.Equal(3, doc.GetProperty("total").GetInt32());
        Assert.Equal(2, doc.GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public void TheAggregateRenderIsACountTableOverEveryMatchNotPerMatchLines()
    {
        var r = RecordsTools.Records(Svc, types: Weap, project: Aggregate("winner"));
        Served(r, "grouped by winner", $"{W.ReplName} = 2", $"{W.MasterName} = 1");
        Assert.DoesNotContain("override_depth", r);   // a count table, not the per-match summary lines
    }

    /// <summary>The row LIST clips; the header total is computed before rendering and stays exact.</summary>
    [Fact]
    public void AnAggregateTableClippedByMaxCharsStillReportsTheExactTotal()
    {
        var r = RecordsTools.Records(Svc, plugins: BothScope, project: Aggregate("type"), max_chars: 60);
        Served(r, "7 matches across 3 groups", "the total above is exact");
        Assert.Contains("before hitting max_chars=", r);
    }

    /// <summary>The same note must reach BOTH transports — json is never the degraded one — so the json
    /// sentence is compared verbatim against the text render rather than re-spelled here.</summary>
    [Fact]
    public void AWrongPredicatePathsAccountingNoteReachesBothTransportsVerbatim()
    {
        var bad = new[] { "NoSuchField = 5" };
        var note = Doc(RecordsTools.Records(Svc, types: Weap, where: bad, format: "json"))
                   .GetProperty("notes")[0].GetString()!;
        Assert.Contains("NOT A FIELD", note);
        Assert.Contains(note, RecordsTools.Records(Svc, types: Weap, where: bad));
    }

    // ---- reverse lookup over several targets ------------------------------------------------------

    /// <summary>W3 carries both keywords; its row has to say so, or a multi-target lookup cannot be
    /// un-merged back into per-target answers.</summary>
    [Fact]
    public void AMultiTargetReverseLookupRecordsWhichTargetsEachMatchHit()
    {
        var r = RecordsTools.Records(Svc, types: Weap, references: new[] { Fid(W.KwA), Fid(W.KwB) },
                                     project: Fields(DamagePath));
        Served(r, $"matches={Fid(W.KwA)}, {Fid(W.KwB)}");   // W3, both targets, in input order
        Assert.Contains($"{Fid(W.W2)}  matches={Fid(W.KwB)}\n", r);
    }

    // ---- exact-window paging ----------------------------------------------------------------------

    [Fact]
    public void ThePagedScanNamesTheWindowItRenderedAndTheNextOffset() =>
        Served(RecordsTools.Records(Svc, types: Weap, limit: 1, offset: 1),
               "showing matches 2–2", "continue with offset=2");

    [Fact]
    public void AZeroMatchScanWithAnOffsetBlamesTheFilterNotThePaging()
    {
        var r = RecordsTools.Records(Svc, types: Weap, where: new[] { "editorid contains zzz-no-such-record" },
                                     offset: 5);
        Served(r, "NO records match at any offset");
        Assert.DoesNotContain("lower offset=", r);
    }

    // ---- the dense columnar transport -------------------------------------------------------------

    JsonElement DenseSummary() => Doc(RecordsTools.Records(Svc, types: Weap, format: "dense"));
    JsonElement DenseDetail() => Doc(RecordsTools.Records(Svc, types: Weap, format: "dense", project: Fields(DamagePath)));
    JsonElement DenseScoped() => Doc(RecordsTools.Records(Svc, plugins: BothScope, format: "dense", project: Fields(DamagePath)));

    [Fact]
    public void TheDenseSummarysColumnsAreTheSixIdentityColumnsInOrder() =>
        Assert.Equal(new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth" }, DenseColumns(DenseSummary()));

    [Fact]
    public void EveryDenseSummaryRowIsAPositionalArrayOfExactlyOneCellPerColumn()
    {
        var doc = DenseSummary();
        int width = DenseColumns(doc).Length;
        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(width, r.GetArrayLength()));
        Assert.Equal(3, doc.GetProperty("rendered").GetInt32());
    }

    [Fact]
    public void ADenseSummaryRowsWinnerCellNamesTheOverridingPlugin()
    {
        var row = DenseRow(DenseSummary(), Fid(W.W1));
        Assert.Equal("Weapon", row[2].GetString());
        Assert.Equal(W.ReplName, row[4].GetString());
    }

    [Fact]
    public void TheDenseDetailColumnsAreFormidRuntimeEditoridThenTheRequestedPaths() =>
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", DamagePath }, DenseColumns(DenseDetail()));

    [Fact]
    public void TheDenseDetailValueCellsCarryTheWinnersValuesUnderATypeScope()
    {
        var doc = DenseDetail();
        Assert.Equal("15", DenseRow(doc, Fid(W.W1))[3].GetString());   // the override, not the master's 10
        Assert.Equal("20", DenseRow(doc, Fid(W.W2))[3].GetString());
        Assert.Equal("30", DenseRow(doc, Fid(W.W3))[3].GetString());
    }

    /// <summary>The point of the transport: the same query, materially fewer characters than json.</summary>
    [Fact]
    public void TheDenseRenderIsMateriallySmallerThanJsonForTheSameQuery()
    {
        var dense = RecordsTools.Records(Svc, types: Weap, format: "dense", project: Fields(DamagePath));
        var json = RecordsTools.Records(Svc, types: Weap, format: "json", project: Fields(DamagePath));
        Assert.True(dense.Length < json.Length, $"dense {dense.Length} chars vs json {json.Length}");
    }

    [Fact]
    public void UnderAPluginsScopeTheDenseColumnsGainASourceColumn() =>
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", DamagePath, "source" }, DenseColumns(DenseScoped()));

    [Fact]
    public void TheScopedDenseRowCarriesTheScopedBodysValueBesideTheSourceThatProducedIt()
    {
        var row = DenseRow(DenseScoped(), Fid(W.W1));
        Assert.Equal("10", row[3].GetString());            // the master's own body
        Assert.Equal(W.MasterName, row[4].GetString());    // ... attributed in-row, so it cannot read as live truth
    }

    [Fact]
    public void TheScopedValuesNoteRidesTheDenseRenderInBandToo() =>
        Assert.Contains(Notes(DenseScoped()), n => n.Contains("SCOPED plugin's OWN version"));

    [Fact]
    public void TheDenseRenderCarriesItsOffsetInBandAndFlagsTheMatchesBeyondTheWindow()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "dense", limit: 1, offset: 1));
        Assert.Equal(1, doc.GetProperty("offset").GetInt32());
        Assert.Equal(1, doc.GetProperty("rows").GetArrayLength());
        Assert.True(doc.GetProperty("capped").GetBoolean());
    }

    [Fact]
    public void TheColumnarTransportRefusesTheTreeFormNamingTheMissingFixedColumnSet() =>
        Refused(RecordsTools.Records(Svc, types: Weap, format: "dense", project: Form("tree")),
                "dense", "'tree'", "no fixed column set");

    /// <summary>The refusal has to list what the parser DOES take, and the population is the parser's own
    /// enum — a hand-typed trio would be short by whatever a later transport adds.</summary>
    public static TheoryData<string> DeclaredFormats
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var n in Enum.GetNames(typeof(Wire.QueryFormat))) d.Add(n.ToLowerInvariant());
            return d;
        }
    }

    [Theory]
    [MemberData(nameof(DeclaredFormats))]
    public void TheUnrecognizedFormatRefusalNamesEveryTransportTheParserAccepts(string transport) =>
        Refused(RecordsTools.Records(Svc, types: Weap, format: "csv"), transport);

    // ---- the scoped-body vs winner axes -----------------------------------------------------------

    static string[] Notes(JsonElement doc) =>
        doc.TryGetProperty("notes", out var n) ? n.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();

    string ScopedText(string? fieldsSource = null) =>
        RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, fields_source: fieldsSource,
                             project: Fields(DamagePath));

    string ScopedJson(string? fieldsSource = null) =>
        RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, fields_source: fieldsSource,
                             format: "json", project: Fields(DamagePath));

    /// <summary>The trap is that a scoped value reads as load-order truth, so the note has to name a lever
    /// this tool actually carries.</summary>
    [Fact]
    public void TheScopedValuesNoteNamesTheDisplayRetargetLeverThisToolHas() =>
        Served(ScopedText(), "SCOPED plugin's OWN version", "fields_source=\"winner\"");

    [Fact]
    public void WithTheDisplayPoleRetargetedTheNoteSaysTheValuesAreTheWinners() =>
        Served(ScopedText("winner"), "field values are the load-order WINNER's");

    [Fact]
    public void AnUnscopedTypeScanShowsWinnersAlreadySoNoScopedValuesNoteIsEmitted()
    {
        var r = RecordsTools.Records(Svc, types: Weap, project: Fields(DamagePath));
        Served(r, "BasicStats.Damage = 15");
        Assert.DoesNotContain("SCOPED plugin's OWN version", r);
    }

    [Fact]
    public void TheScopedValuesNoteRidesTheJsonNotesArrayToo() =>
        Assert.Contains(Notes(Doc(ScopedJson())), n => n.Contains("SCOPED plugin's OWN version"));

    [Fact]
    public void TheScopedJsonMatchNamesTheScopedPluginAsItsSourceAndCarriesItsOwnValue()
    {
        var m = Match(Doc(ScopedJson()), Fid(W.W1));
        Assert.Equal(W.MasterName, m.GetProperty("source").GetString());
        Assert.Equal("10", Field(m, DamagePath).GetProperty("value").GetString());
    }

    [Fact]
    public void WithFieldsSourceWinnerTheJsonMatchNamesTheWinnerAsSourceAndCarriesItsValue()
    {
        var m = Match(Doc(ScopedJson("winner")), Fid(W.W1));
        Assert.Equal(W.ReplName, m.GetProperty("source").GetString());
        Assert.Equal("15", Field(m, DamagePath).GetProperty("value").GetString());
    }

    // ---- where_source= (the MATCH axis) decoupled from fields_source= (the DISPLAY axis) ----------

    string WinnerMatched(string? fieldsSource) =>
        RecordsTools.Records(Svc, types: Weap, plugins: MasterScope, where: new[] { $"{DamagePath} = 15" },
                             where_source: "winner", fields_source: fieldsSource, format: "dense",
                             project: Fields(DamagePath));

    [Fact]
    public void WhereSourceWinnerMatchesOnTheWinnerWhileTheRowStillDisplaysTheScopedBody()
    {
        var doc = Doc(WinnerMatched(null));
        Assert.Equal(1, doc.GetProperty("rows").GetArrayLength());   // matched on the winner's 15...
        var row = DenseRow(doc, Fid(W.W1));
        Assert.Equal("10", row[3].GetString());                      // ...and still shows the master's 10
        Assert.Equal(W.MasterName, row[4].GetString());
    }

    [Fact]
    public void TheDecoupledNoteSaysTheMatchWasOnTheWinnerAndTheValuesAreScoped() =>
        Assert.Contains(Notes(Doc(WinnerMatched(null))),
                        n => n.Contains("where_source=winner") && n.Contains("SCOPED"));

    [Fact]
    public void AddingTheWinnerDisplayPoleMovesTheValuesToTheWinnerToo() =>
        Assert.Equal("15", DenseRow(Doc(WinnerMatched("winner")), Fid(W.W1))[3].GetString());

    [Fact]
    public void TheBothPolesNoteSaysTheMatchAndTheValuesAreBothTheWinners() =>
        Assert.Contains(Notes(Doc(WinnerMatched("winner"))),
                        n => n.Contains("where_source=winner") && n.Contains("fields_source=\"winner\""));

    // ---- container expansion across a scan --------------------------------------------------------

    RecordsTools.RecordsProject Keywords(int? depth = null, bool names = false) =>
        new() { form = "fields", fields = new[] { "Keywords" }, depth = depth, resolve_names = names };

    [Fact]
    public void ACollapsedContainerCellHintsTheExpansionKnobThisToolActuallySpells() =>
        Served(RecordsTools.Records(Svc, types: Weap, project: Keywords()),
               "[list: 2 item(s)] — pass project.depth=2 to expand");

    [Fact]
    public void TheContainerHintRidesTheJsonRenderAsTheFieldsNote()
    {
        var m = Match(Doc(RecordsTools.Records(Svc, types: Weap, format: "json", project: Keywords())), Fid(W.W1));
        Assert.Contains("pass project.depth=2 to expand", Field(m, "Keywords").GetProperty("note").GetString());
    }

    [Fact]
    public void AtDepthTwoTheScanExpandsContainerElementsWithNoOutOfBoundsNoise()
    {
        var r = RecordsTools.Records(Svc, types: Weap, project: Keywords(2));
        Served(r, "Keywords[0]", "Keywords[1]");
        Assert.DoesNotContain("out of bounds", r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheExpandedLeafCarriesItsRoundTripFormKeyToken() =>
        Served(RecordsTools.Records(Svc, types: Weap, project: Keywords(2)), $"Keywords[0] = {Fid(W.KwA)}");

    [Fact]
    public void DepthExpansionComposesWithResolveNamesAcrossTheWholeScan() =>
        Served(RecordsTools.Records(Svc, types: Weap, project: Keywords(2, names: true)), "(→ HcBulkKwA)");

    /// <summary>Per-match element counts, not a bare "some path exists": W2 carries one keyword and W3
    /// two, so an expansion that emitted a fixed index range would fail here and pass a presence check.</summary>
    [Fact]
    public void EachJsonMatchCarriesExactlyItsOwnExpandedElementPaths()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: Weap, format: "json", project: Keywords(2)));
        string[] Paths(string fid) =>
            Match(doc, fid).GetProperty("fields").EnumerateArray()
                 .Select(f => f.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(new[] { "Keywords", "Keywords[0]" }, Paths(Fid(W.W2)));
        Assert.Equal(new[] { "Keywords", "Keywords[0]", "Keywords[1]" }, Paths(Fid(W.W3)));
    }

    /// <summary>The whole-body form takes the same knob — the collapsed cell's hint has to lead somewhere
    /// that works, not into a refusal.</summary>
    [Fact]
    public void TheWholeBodyFormExpandsItsContainersAtDepthTwo() =>
        Served(RecordsTools.Records(Svc, types: Weap,
                                    project: new RecordsTools.RecordsProject { form = "everything", depth = 2 }),
               "Keywords[0]");

    [Fact]
    public void ADenseContainerCellHintsTheFormatHopRatherThanABlindKnob() =>
        Assert.Contains("pass project.depth=2 with format=text/json to expand",
                        DenseRow(Doc(RecordsTools.Records(Svc, types: Weap, format: "dense", project: Keywords())),
                                 Fid(W.W1))[3].GetString());
}
