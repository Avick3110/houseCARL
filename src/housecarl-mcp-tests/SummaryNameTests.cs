using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Summary rows carry the record's Name on text, json and dense, on the scan and list lanes; a type with no
/// Name renders as before; a named pole's row carries the pole's Name; and form='identity' answers exactly as
/// summary. A master defines a named sword, a nameless sword and a keyword; an override renames the sword.</summary>
[Trait("tier", "integration")]
public sealed class SummaryNameTests : IClassFixture<SummaryNameTests.World>
{
    const string MasterName = "hcSnMaster.esp", OverName = "hcSnOver.esp";
    const string MasterSword = "Iron Sword", WinnerSword = "Renamed Sword";
    const string QuotedName = "The \"Edge\" of C:\\Blades";

    public sealed class World : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-summary-name-" + Guid.NewGuid().ToString("N"));
        public LoadOrderResolver Resolver { get; }
        public LoadOrderService Svc { get; }
        public FormKey Sword { get; }
        public FormKey Nameless { get; }
        public FormKey Kw { get; }
        public FormKey Quoted { get; }

        public World()
        {
            _ = TestCorpus.Path;
            Directory.CreateDirectory(_dir);
            var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
            var sword = master.Weapons.AddNew(); sword.EditorID = "hcSnSword"; sword.Name = MasterSword; Sword = sword.FormKey;
            var nameless = master.Weapons.AddNew(); nameless.EditorID = "hcSnNameless"; Nameless = nameless.FormKey;
            var kw = master.Keywords.AddNew(); kw.EditorID = "hcSnKw"; Kw = kw.FormKey;
            var quoted = master.Weapons.AddNew(); quoted.EditorID = "hcSnQuoted"; quoted.Name = QuotedName; Quoted = quoted.FormKey;
            master.BeginWrite.ToPath(Path.Combine(_dir, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var over = new SkyrimMod(ModKey.FromNameAndExtension(OverName), SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(over, sword)).Name = WinnerSword;
            over.BeginWrite.ToPath(Path.Combine(_dir, OverName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Resolver = LoadOrderResolver.Build(new[] { Path.Combine(_dir, MasterName), Path.Combine(_dir, OverName) });
            Svc = LoadOrderService.ForGuard(Resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    readonly World _w;
    public SummaryNameTests(World w) => _w = w;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement;
    static JsonElement Pole(string plugin) => Je(JsonSerializer.Serialize(plugin));

    /// <summary>A response with its measured timings masked, so two calls that answer the same compare equal.</summary>
    internal static string Timeless(string response) =>
        Regex.Replace(Regex.Replace(response, @" in \d+ ms", " in N ms"), "\"render_ms\": \\d+", "\"render_ms\": N");

    string Scan(string? format = null, string? form = null, JsonElement? source = null, string type = "WEAP") =>
        RecordsTools.Records(_w.Svc, types: new[] { type }, format: format, source: source,
                             project: form is null ? null : new RecordsTools.RecordsProject { form = form });

    string List(FormKey[] ids, string? format = null, string? form = null, JsonElement? source = null) =>
        RecordsTools.Records(_w.Svc, formids: ids.Select(Fid).ToArray(), format: format, source: source,
                             project: form is null ? null : new RecordsTools.RecordsProject { form = form });

    static string LineOf(string text, string editorId) =>
        text.Split('\n').Single(l => l.Contains(editorId, StringComparison.Ordinal));

    static JsonElement JsonRow(string json, string member, FormKey fk) =>
        Je(json).GetProperty(member).EnumerateArray().Single(r => r.GetProperty("formid").GetString() == Fid(fk));

    static string? DenseName(string dense, FormKey fk)
    {
        var doc = Je(dense);
        int at = doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList().IndexOf("name");
        Assert.True(at >= 0, "no name column: " + dense);
        return doc.GetProperty("rows").EnumerateArray().Single(r => r[0].GetString() == Fid(fk))[at].GetString();
    }

    // ---- Name on summary rows, three formats, both lanes -------------------------------------------

    [Fact]
    public void AScanSummaryRowCarriesTheWinnersNameInText() =>
        Assert.Contains($"name=\"{WinnerSword}\"", LineOf(Scan(), "hcSnSword"));

    [Fact]
    public void AScanSummaryRowCarriesTheWinnersNameInJson() =>
        Assert.Equal(WinnerSword, JsonRow(Scan("json"), "matches", _w.Sword).GetProperty("name").GetString());

    [Fact]
    public void AScanSummaryRowCarriesTheWinnersNameInDense() =>
        Assert.Equal(WinnerSword, DenseName(Scan("dense"), _w.Sword));

    [Fact]
    public void AListSummaryRowCarriesTheWinnersNameInText() =>
        Assert.Contains($"name=\"{WinnerSword}\"", LineOf(List(new[] { _w.Sword }), "hcSnSword"));

    [Fact]
    public void AListSummaryRowCarriesTheWinnersNameInJson() =>
        Assert.Equal(WinnerSword, JsonRow(List(new[] { _w.Sword }, "json"), "records", _w.Sword).GetProperty("name").GetString());

    [Fact]
    public void AListSummaryRowCarriesTheWinnersNameInDense() =>
        Assert.Equal(WinnerSword, DenseName(List(new[] { _w.Sword }, "dense"), _w.Sword));

    // ---- a record with no Name renders as before ---------------------------------------------------

    [Theory]
    [InlineData("KYWD", "hcSnKw")]
    [InlineData("WEAP", "hcSnNameless")]
    public void ARecordWithNoNameCarriesNoNameInTextOrJson(string type, string editorId)
    {
        Assert.DoesNotContain("name=", LineOf(Scan(type: type), editorId));
        var fk = editorId == "hcSnKw" ? _w.Kw : _w.Nameless;
        Assert.False(JsonRow(Scan("json", type: type), "matches", fk).TryGetProperty("name", out _));
        Assert.DoesNotContain("name=", LineOf(List(new[] { fk }), editorId));
        Assert.False(JsonRow(List(new[] { fk }, "json"), "records", fk).TryGetProperty("name", out _));
    }

    [Fact]
    public void ARecordWithNoNameHasANullDenseNameCell()
    {
        Assert.Null(DenseName(Scan("dense", type: "KYWD"), _w.Kw));
        Assert.Null(DenseName(List(new[] { _w.Nameless }, "dense"), _w.Nameless));
    }

    // ---- under a named pole the Name is the pole's copy --------------------------------------------

    [Fact]
    public void UnderANamedPoleTheListRowCarriesThePolesName()
    {
        Assert.Contains($"name=\"{MasterSword}\"", LineOf(List(new[] { _w.Sword }, source: Pole(MasterName)), "hcSnSword"));
        Assert.Equal(MasterSword, JsonRow(List(new[] { _w.Sword }, "json", source: Pole(MasterName)), "records", _w.Sword)
                                    .GetProperty("name").GetString());
        Assert.Equal(MasterSword, DenseName(List(new[] { _w.Sword }, "dense", source: Pole(MasterName)), _w.Sword));
    }

    [Fact]
    public void UnderANamedPoleTheScanRowCarriesThePolesName()
    {
        Assert.Contains($"name=\"{MasterSword}\"", LineOf(Scan(source: Pole(MasterName)), "hcSnSword"));
        Assert.Equal(MasterSword, DenseName(Scan("dense", source: Pole(MasterName)), _w.Sword));
    }

    // ---- a quote or backslash inside a Name is escaped on text, so the token ends where it should --

    [Fact]
    public void ATextNameEscapesItsQuotesAndBackslashesOnBothLanes()
    {
        const string escaped = "name=\"The \\\"Edge\\\" of C:\\\\Blades\"";
        Assert.Contains(escaped, LineOf(Scan(), "hcSnQuoted"));
        Assert.Contains(escaped, LineOf(List(new[] { _w.Quoted }), "hcSnQuoted"));
        Assert.Equal(QuotedName, JsonRow(List(new[] { _w.Quoted }, "json"), "records", _w.Quoted).GetProperty("name").GetString());
    }

    // ---- the summary artifact carries name on both lanes -------------------------------------------

    /// <summary>The artifact a summary call writes with to_file=, read back as its row for <paramref name="fk"/> and its manifest text.</summary>
    (JsonElement Row, string Text) SummaryArtifact(bool list, FormKey fk, string form = "summary")
    {
        var path = Path.Combine(Path.GetTempPath(), "hc-summary-name-art-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var project = new RecordsTools.RecordsProject { form = form };
            var r = list
                ? RecordsTools.Records(_w.Svc, formids: new[] { Fid(fk), Fid(_w.Kw) }, project: project, to_file: path)
                : RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, project: project, to_file: path);
            Assert.True(File.Exists(path), r);
            var text = File.ReadAllText(path);
            var row = text.Split('\n').Where(l => l.TrimStart().StartsWith('{')).Select(l => Je(l))
                          .Single(e => e.TryGetProperty("formid", out var f) && f.GetString() == Fid(fk));
            return (row, text);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true, "summary")]
    [InlineData(true, "identity")]
    [InlineData(false, "summary")]
    public void ASummaryArtifactRowCarriesTheNameAndItsSchemaSaysSo(bool list, string form)
    {
        var (row, text) = SummaryArtifact(list, _w.Sword, form);
        Assert.Equal(WinnerSword, row.GetProperty("name").GetString());
        Assert.Equal("hcSnSword", row.GetProperty("editorid").GetString());
        Assert.Contains("\"name?\"", text);
    }

    [Fact]
    public void AListSummaryArtifactUnderAPoleCarriesThePolesNameAndSource()
    {
        var path = Path.Combine(Path.GetTempPath(), "hc-summary-name-art-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            RecordsTools.Records(_w.Svc, formids: new[] { Fid(_w.Sword) }, source: Pole(MasterName), to_file: path);
            var row = File.ReadAllText(path).Split('\n').Where(l => l.TrimStart().StartsWith('{')).Select(l => Je(l))
                          .Single(e => e.TryGetProperty("formid", out var f) && f.GetString() == Fid(_w.Sword));
            Assert.Equal((MasterSword, MasterName, OverName),
                         (row.GetProperty("name").GetString(), row.GetProperty("source").GetString(), row.GetProperty("winner").GetString()));
        }
        finally { File.Delete(path); }
    }

    // ---- identity is a spelling of summary ---------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void IdentityAnswersExactlyAsSummaryOnBothLanes(string? format)
    {
        var ids = new[] { _w.Sword, _w.Nameless, _w.Kw };
        Assert.Equal(Timeless(List(ids, format, "summary")), Timeless(List(ids, format, "identity")));
        Assert.Equal(Timeless(Scan(format, "summary")), Timeless(Scan(format, "identity")));
        Assert.Equal(Timeless(List(ids, format, "summary", Pole(MasterName))), Timeless(List(ids, format, "identity", Pole(MasterName))));
    }
}
