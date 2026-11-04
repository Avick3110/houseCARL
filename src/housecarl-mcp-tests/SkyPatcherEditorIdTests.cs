using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Without powerofthree's Tweaks the game keeps EditorIDs only for ten record types, so an armor named by
/// EditorID finds nothing and the warning says why; a keyword EditorID still resolves; with Tweaks both apply.</summary>
[Trait("tier", "integration")]
public sealed class SkyPatcherEditorIdTests
{
    // :1 names HcHeelBoots by EditorID (an armor); :2 finds HcBothBoots by a keyword EditorID.
    static readonly (string, string)[] Inis =
    {
        ("HcEid.ini", "filterByArmors=HcHeelBoots:keywordsToAdd=HcAddKw\r\n"
                      + "filterByKeywords=HcKw2:weight=3\r\n"),
    };

    static string Post(SkyPatcherRelevanceWorld w, string fid) => RecordsTools.Records(w.Svc, formids: new[] { fid },
        source: SkyPatcherRelevanceWorld.Post(), project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Weight", "Keywords" } });

    [Fact]
    public void WithoutTweaksAnArmorEditorIdFindsNothingAndSaysWhy()
    {
        using var w = new SkyPatcherRelevanceWorld(Inis, tweaks: false);
        var heel = Post(w, w.Heel);
        Assert.Contains("Keywords = [list: 1 item(s)]", heel);
        Assert.Contains("EditorID 'HcHeelBoots' (in a filterByArmors) finds nothing: powerofthree's Tweaks", heel);
        Assert.Contains("Weight = 3", Post(w, w.Both));
    }

    [Fact]
    public void WithTweaksAnArmorEditorIdResolves()
    {
        using var w = new SkyPatcherRelevanceWorld(Inis);
        var heel = Post(w, w.Heel);
        Assert.Contains("Keywords = [list: 2 item(s)]", heel);
        Assert.DoesNotContain("powerofthree", heel);
    }

    // the version modelled is data in the catalog, and the layer header and the overlay's source line both print it
    [Fact]
    public void TheLayerAndTheOverlayNameTheModelledVersion()
    {
        using var w = new SkyPatcherRelevanceWorld(Inis);
        Assert.StartsWith("SkyPatcher layer (SkyPatcher 8.0.1 model)", SkyPatcherTools.SkyPatcherLayer(w.Svc));
        Assert.Contains("the SkyPatcher INI layer replays (SkyPatcher 8.0.1 model)", Post(w, w.Heel));
    }
}
