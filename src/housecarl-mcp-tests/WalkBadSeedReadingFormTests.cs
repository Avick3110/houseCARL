using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A seed that is not a FormID on the records walk's reading forms (#1317 fold 1): one error row in this
/// answer, never counted as a reached record and never sent to form='chain'.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class WalkBadSeedReadingFormTests : RecordsTestBase
{
    public WalkBadSeedReadingFormTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsWalk Carrier => new() { direction = "reverse", follow = "Effects[].BaseEffect" };

    static string Selection(string text) => Regex.Match(text, @"walk: selection = \d+ reached record\(s\)").Value;

    static List<JsonElement> Rows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("records").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    static bool IsBadRow(JsonElement r)
        => r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString()!.StartsWith("bad FormID 'ZZZ'", StringComparison.Ordinal);

    [Fact]
    public void TheCarrierReadingFormGivesABadSeedItsOwnRow()
    {
        var good = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) }, walk: Carrier);
        var text = RecordsTools.Records(Svc, formids: new[] { "ZZZ", Fid(W.MgefA) }, walk: Carrier);
        Assert.Contains("bad FormID 'ZZZ'", text);
        Assert.DoesNotContain("seed(s) failed", text);
        Assert.NotEqual("", Selection(good));
        Assert.Equal(Selection(good), Selection(text));

        var rows = Rows(RecordsTools.Records(Svc, formids: new[] { "ZZZ", Fid(W.MgefA) }, walk: Carrier, format: "json"));
        Assert.Single(rows, IsBadRow);
        Assert.Equal(Rows(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) }, walk: Carrier, format: "json")).Count, rows.Count - 1);
    }

    [Fact]
    public void TheCarrierReadingFormAnswersOnlyBadSeedsWithTheirRows()
    {
        var text = RecordsTools.Records(Svc, formids: new[] { "ZZZ" }, walk: Carrier);
        Assert.DoesNotContain("reached nothing readable", text);
        Assert.Contains("bad FormID 'ZZZ'", text);
        Assert.Single(Rows(RecordsTools.Records(Svc, formids: new[] { "ZZZ" }, walk: Carrier, format: "json")), IsBadRow);
    }
}
