using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A line the index skips (a connective, a second filter, a non-FormID value) still reaches every record it matches.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLineIndexTests : IDisposable
{
    // :1 all but HcHeelBoots; :2 HcBothBoots only (HcKw2); :3 HcPlainCuirass by EditorID beside an unread 0x value.
    readonly SkyPatcherRelevanceWorld _w = new(new[]
    {
        ("HcIndex.ini", "filterByArmorsExcluded=HcSpRel.esp|000810:weight=7\r\n"
                        + "filterByArmors=HcSpRel.esp|000810,HcSpRel.esp|000812:filterByKeywords=HcSpRel.esp|000802:value=9\r\n"
                        + "filterByArmors=HcSpRel.esp|0x000810,HcPlainCuirass:keywordsToAdd=HcAddKw\r\n"),
    });

    public void Dispose() => _w.Dispose();

    string Post(string fid) => RecordsTools.Records(_w.Svc, formids: new[] { fid }, source: SkyPatcherRelevanceWorld.Post(),
        project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Weight", "Value", "Keywords" } });

    [Fact]
    public void AnExcludedLineReachesEveryRecordItDoesNotName()
    {
        Assert.Contains("Weight = 7", Post(_w.Plain));
        Assert.Contains("Weight = 7", Post(_w.Both));
        Assert.Contains("Weight = 0", Post(_w.Heel));
    }

    [Fact]
    public void ATwoFilterLineReachesTheRecordBothFiltersMatch()
    {
        Assert.Contains("Value = 9", Post(_w.Both));
        Assert.Contains("Value = 0", Post(_w.Heel));
    }

    [Fact]
    public void ALineNamingByEditorIdOrA0xValueStillApplies()
    {
        Assert.Contains("Keywords = [list: 1 item(s)]", Post(_w.Plain));   // HcAddKw, by EditorID
        Assert.Contains("Keywords = [list: 1 item(s)]", Post(_w.Heel));    // its own HcHeelKw only: 0x000810 names nothing
    }
}

/// <summary>The merged line list keeps file order, and only a primary filter's FormIDs index a line.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLineIndexOrderTests : IDisposable
{
    // :1 indexed under HcBothBoots; :2 unindexed, same field, later; :3 a lone keyword filter naming HcKw2 by FormID.
    readonly SkyPatcherRelevanceWorld _w = new(new[]
    {
        ("HcOrder.ini", "filterByArmors=HcSpRel.esp|000812:weight=5\r\n"
                        + "filterByArmorsExcluded=HcSpRel.esp|000810:weight=7\r\n"
                        + "filterByKeywords=HcSpRel.esp|000802:value=11\r\n"),
    });

    public void Dispose() => _w.Dispose();

    string Post(string fid) => RecordsTools.Records(_w.Svc, formids: new[] { fid }, source: SkyPatcherRelevanceWorld.Post(),
        project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Weight", "Value" } });

    [Fact]
    public void ALaterUnindexedLineWinsOverAnEarlierIndexedOne() => Assert.Contains("Weight = 7", Post(_w.Both));

    [Fact]
    public void ALoneKeywordFilterReachesTheRecordCarryingIt()
    {
        Assert.Contains("Value = 11", Post(_w.Both));
        Assert.Contains("Value = 0", Post(_w.Heel));
    }
}

/// <summary>The layer's no-op scan reads the Armors group once for the three armors its lines name.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLayerGroupReadTests : IDisposable
{
    readonly SkyPatcherRelevanceWorld _w = new(new[]
    {
        ("HcTargets.ini", "filterByArmors=HcSpRel.esp|000810,HcSpRel.esp|000811,HcSpRel.esp|000812:weight=0\r\n"),
    });

    public void Dispose() => _w.Dispose();

    [Fact]
    public void TheTargetsGroupIsReadOnce()
    {
        var d = GroupReadDelta.Of(_w.Svc, () => SkyPatcherTools.SkyPatcherLayer(_w.Svc), "HcTargets.ini");
        Assert.Equal(new Dictionary<(string, string), int> { [("HcSpRel.esp", "Armors")] = 1 }, d);
    }
}

/// <summary>No-op rows list by file, then line, then FormKey: a line's FormKey never moves it ahead of an earlier line.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherNoOpOrderTests : IDisposable
{
    // :1 names 000812, :2 names 000810; both set the weight the armor already has.
    readonly SkyPatcherRelevanceWorld _w = new(new[]
    {
        ("HcNoOpOrder.ini", "filterByArmors=HcSpRel.esp|000812:weight=0\r\n"
                            + "filterByArmors=HcSpRel.esp|000810:weight=0\r\n"),
    });

    public void Dispose() => _w.Dispose();

    [Fact]
    public void NoOpRowsListInLineOrder()
    {
        var r = SkyPatcherTools.SkyPatcherLayer(_w.Svc);
        int first = r.IndexOf("HcNoOpOrder.ini:1 ", StringComparison.Ordinal), second = r.IndexOf("HcNoOpOrder.ini:2 ", StringComparison.Ordinal);
        Assert.True(first >= 0 && second >= 0, r);
        Assert.True(first < second, r);
    }
}
