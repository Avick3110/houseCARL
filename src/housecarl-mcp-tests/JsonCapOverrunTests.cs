using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The closing fact a capped json document owes when it shipped over its ceiling (#809):
/// <c>max_chars_overrun</c>, the json twin of the text lane's overrun notice. Before it the json lane said only that
/// content had been cut, so a consumer that wanted to retry had no number to retry with. One arm per document family,
/// because the member is written at each family's own root close.</summary>
static class JsonOverrun
{
    const string Member = "max_chars_overrun";

    /// <summary>Case-insensitive, so one assertion reaches the merged check's sentence too — it opens its notice
    /// mid-paragraph and capitalises where <c>RenderCap.Overran</c> does not.</summary>
    static int Stated(string notice, string marker) =>
        int.Parse(Regex.Match(notice, marker + @"(\d+)", RegexOptions.IgnoreCase).Groups[1].Value);

    /// <summary>The three numbers, asked of the document the render really returned: the cap it was given, its own
    /// length — with the member counted, because the member is part of the length it states — and the cap it names,
    /// which is that same length, the cap THIS document would have fitted in. A wider cap renders a wider answer, so
    /// the number is not a promise about the next call; what is a promise is that the member is not a dead end, which
    /// <see cref="ClearsAtAWideCap"/> checks with a cap wide enough for the whole answer.</summary>
    internal static void StatesTheThreeNumbers(string json, int cap)
    {
        var notice = StatesItsLengthAndCap(json, cap);
        // On these documents the retry number IS the length: nothing but the fixed part was over the cap.
        Assert.Equal(json.Length, Stated(notice, "raise max_chars to at least "));
    }

    /// <summary>The part of the sentence EVERY document under this member shares, read with the one matcher: the
    /// document's own length and the cap it was given. Returns the notice, because the RETRY number is the document's
    /// own business — the merged check adds the cap-print-site growth term to its, which is why one-step clearing is
    /// <c>CheckCapCharsTests</c>'s property and not this one's.</summary>
    internal static string StatesItsLengthAndCap(string json, int cap)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(json.Length > cap,
                    $"the fixture does not overrun: {json.Length} chars under max_chars={cap}");
        Assert.True(root.TryGetProperty(Member, out var m),
                    $"a {json.Length}-char document over max_chars={cap} carries no {Member}");
        var notice = m.GetString()!;

        Assert.Equal(cap, Stated(notice, "over the max_chars="));
        Assert.Equal(json.Length, Stated(notice, "this response is "));
        // Whatever it is computed from, the number it names is a cap worth retrying at.
        Assert.True(Stated(notice, "raise max_chars to at least ") > cap,
                    "the notice names a cap no wider than the one that did not fit");
        return notice;
    }

    /// <summary>The member is not a dead end: a cap wide enough for the whole answer clears it. Asked of one family
    /// rather than of all, because on a lane whose render can overshoot its cap by a row the clearing cap depends on
    /// that lane's own budget, which is not this contract.</summary>
    internal static void ClearsAtAWideCap(Func<int, string> again, int wide)
    {
        var json = again(wide);
        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty(Member, out _),
                     $"a {json.Length}-char document still carries {Member} at max_chars={wide}");
    }
}

/// <summary>The two <c>housecarl_asset_status</c> document families — the path rows and the <c>counts_only=</c>
/// census. The path-row family is the shape #809 was measured on.</summary>
[Trait("tier", "integration")]
public sealed class AssetStatusJsonOverrunTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetStatusJsonOverrunTests(AssetSelectWorld w) => _w = w;

    [Fact]
    public void ThePathRowDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(100), 100);
        JsonOverrun.ClearsAtAWideCap(Render, 80_000);
    }

    [Fact]
    public void TheCensusDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                   counts_only: true, format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(100), 100);
    }
}

/// <summary>The <c>housecarl_records</c> document families: the identity list, a record read, and the aggregate
/// count table.</summary>
[Collection("bulk-records")]
[Trait("tier", "integration")]
public sealed class RecordsJsonOverrunTests : BulkRecordsTestBase
{
    public RecordsJsonOverrunTests(BulkRecordsFixture f) : base(f) { }

    [Fact]
    public void TheIdentityListDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: Form("identity"),
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60);
    }

    [Fact]
    public void ARecordReadDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: Fields("BasicStats.Damage"),
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60);
    }

    [Fact]
    public void TheAggregateCountTableSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Aggregate("winner"),
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60);
    }

    /// <summary>The <c>counts_only=</c> census: a document with no rows to cut, so its cap can only be missed
    /// outright. Its two renderers took no cap at all, which left the json census over the ceiling in silence while
    /// the text twin settled — the asymmetry #809 opens with, on the one lane that had no rows to blame.</summary>
    [Fact]
    public void TheCensusDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, counts_only: true,
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(40), 40);
        JsonOverrun.ClearsAtAWideCap(Render, 80_000);
    }

    /// <summary>The NAMED-counter census the comparison forms render, the other renderer that took no cap.</summary>
    [Fact]
    public void TheNamedCounterCensusSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, counts_only: true, format: "json",
                                 max_chars: cap, project: Form("tree"));

        JsonOverrun.StatesTheThreeNumbers(Render(40), 40);
        JsonOverrun.ClearsAtAWideCap(Render, 80_000);
    }
}
