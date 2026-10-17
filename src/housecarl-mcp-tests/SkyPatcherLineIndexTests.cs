using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The replay checks a record only against the lines that can touch it; a line not indexed by the records it
/// names (a connective, a second filter, a value that is not a plain FormID) still reaches every record it matches.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherLineIndexTests : IDisposable
{
    // :1 every armor but HcHeelBoots; :2 HcBothBoots only (it alone of the two named carries HcKw2, 0x802);
    // :3 HcPlainCuirass by EditorID, beside a 0x-prefixed value that neither side reads as a FormID.
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

/// <summary>The layer's no-op scan reads its targets through the read door: one read of the plugin's Armors group for
/// the three armors its lines name.</summary>
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
