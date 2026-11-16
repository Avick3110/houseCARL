using Xunit;

namespace HousecarlMcpTests;

/// <summary>FLM's part of housecarl_layer compared whole, byte for byte, to answers held from this lane's build, as
/// BosLayerHeldTests holds BOS's: the file list, the expanded lines with the order line and add rows, both selectors,
/// the selector zero, FLM's own file and line cuts, and no files. The held files are in DistributorLayerHeld/, LF only.</summary>
[Trait("tier", "integration")]
public sealed class FlmLayerHeldTests : IDisposable
{
    readonly FlmWorld _w = new(new[]
    {
        ("HcA_FLM.ini",
            "; a comment\n"
            + "FormList = HcList|HcOld,HcCoin,HcCoin\n"                     // :2 one in the record, one repeated
            + "FormList = HcList2|#Mine,*HcHolder,HcNoSuch,\n"               // :3 a group, a list, a miss, an empty item
            + "Group = Mine|HcFar\n"                                          // :4 read before :2 and :3
            + "Filter = hasOther|+HcFlmOther.esp\n"                           // :5
            + "FormList = HcList|HcGem|#hasOther\n"                           // :6 a named filter, true
            + "FormList = HcList|HcFar|-HcFlmOther.esp\n"                     // :7 filtered out
            + "FormList = HcCoin|HcGem\n"                                     // :8 a target that is no list
            + "Collection = Shiny|Misc|HcKw\n"                                // :9
            + "FormList = HcList2|#Shiny\n"                                   // :10 a collection's keywords
            + "Plant = HcSeed|HcBush\n"                                       // :11 implied lists
            + "FromList = HcList|HcCoin\n"                                    // :12 an unknown key
            + "Remove = HcList|HcOld\n"                                       // :13 a no-op
            + "FormList = HcList|0x02000803\n"),                              // :14 the Dawnguard rewrite
        ("HcB_FLM.ini", "Alias = Both|HcList,HcList2\nFormList = #Both|HcFar\n"),
    }, shadowed: new[] { ("HcB_FLM.ini", "FormList = HcList|HcLoser\n") });

    public void Dispose() => _w.Dispose();

    static readonly string[] Flm = { "flm" };

    static void Held(string name, string actual) => DistributorLayerHeldTests.Held(name, actual);

    // The file list: FLM's header with its skipped clause, the shadowed copy, and the → filter= hint.
    [Fact]
    public void FlmsFileListIsHeld() => Held("flm", _w.Layer(Flm));

    // The expanded lines: FLM's order line and add rows, each drop, verdict and note.
    [Fact]
    public void FlmsExpandedLinesAreHeld() => Held("flm-filter", _w.Layer(Flm, filter: "HcA_"));

    // plugins= across both files: through a group and an alias, each selected form labelled.
    [Fact]
    public void FlmUnderPluginsIsHeld() => Held("flm-plugins", _w.Layer(Flm, plugins: new[] { FlmWorld.Other }));

    // formids= on a collection's keyword, a filter label, and an implied list, a target.
    [Fact]
    public void FlmUnderFormIdsIsHeld() => Held("flm-formids", _w.Layer(Flm, formids: new[] { "000805:HcFlm.esp", "000808:HcFlm.esp" }));

    // FLM's selector zero: its own close, naming no plugin term.
    [Fact]
    public void FlmsSelectorZeroIsHeld()
    {
        using var w = new FlmWorld(new[] { ("Hc_FLM.ini", "FormList = HcList|HcCoin\n") });
        Held("flm-formids-zero", w.Layer(Flm, formids: new[] { "000803:HcFlm.esp" }));
    }

    // FLM's own file cut.
    [Fact]
    public void FlmsOwnFileCutIsHeld() => Held("flm-cap", _w.Layer(Flm, maxChars: 900));

    // FLM's own line cut.
    [Fact]
    public void FlmsOwnLineCutIsHeld() => Held("flm-filter-cap", _w.Layer(Flm, filter: "HcA_", maxChars: 2500));

    // "no FLM files in the active order", naming both folders.
    [Fact]
    public void NoFlmFilesIsHeld()
    {
        using var w = new FlmWorld(Array.Empty<(string, string)>());
        Held("flm-none", w.Layer(Flm));
    }
}
