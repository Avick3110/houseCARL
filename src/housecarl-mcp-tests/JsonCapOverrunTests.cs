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

    static int Stated(string notice, string marker) =>
        int.Parse(Regex.Match(notice, marker + @"(\d+)").Groups[1].Value);

    /// <summary>The three numbers, asked of the document the render really returned: the cap it was given, its own
    /// length — with the member counted, because the member is part of the length it states — and a cap that clears
    /// the notice in one step.</summary>
    internal static void StatesTheThreeNumbers(string json, int cap, Func<int, string> again)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(json.Length > cap,
                    $"the fixture does not overrun: {json.Length} chars under max_chars={cap}");
        Assert.True(root.TryGetProperty(Member, out var m),
                    $"a {json.Length}-char document over max_chars={cap} carries no {Member}");
        var notice = m.GetString()!;

        Assert.Equal(cap, Stated(notice, "over the max_chars="));
        Assert.Equal(json.Length, Stated(notice, "this response is "));

        int raiseTo = Stated(notice, "raise max_chars to at least ");
        var cleared = again(raiseTo);
        Assert.False(JsonDocument.Parse(cleared).RootElement.TryGetProperty(Member, out _),
                     $"max_chars={cap} named {raiseTo}, which did not clear the notice");
        Assert.True(cleared.Length <= raiseTo, $"the cap {raiseTo} it named still does not hold it");
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

        JsonOverrun.StatesTheThreeNumbers(Render(100), 100, Render);
    }

    [Fact]
    public void TheCensusDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                   counts_only: true, format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(100), 100, Render);
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

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60, Render);
    }

    [Fact]
    public void ARecordReadDocumentSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, formids: new[] { Fid(W.W1) }, project: Fields("BasicStats.Damage"),
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60, Render);
    }

    [Fact]
    public void TheAggregateCountTableSaysItOverranAndNamesTheCapThatClearsIt()
    {
        string Render(int cap) =>
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Aggregate("winner"),
                                 format: "json", max_chars: cap);

        JsonOverrun.StatesTheThreeNumbers(Render(60), 60, Render);
    }
}
