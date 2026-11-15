using Xunit;

namespace HousecarlMcpTests;

/// <summary>BOS's part of housecarl_layer compared whole, byte for byte, to answers held from this lane's build, as
/// DistributorLayerHeldTests holds SPID's and KID's: the file list, the expanded lines with the order line and rows,
/// both selectors, the selector zero, BOS's own file and line cuts, and no files. The held files are in
/// DistributorLayerHeld/, LF only.</summary>
[Trait("tier", "integration")]
public sealed class BosLayerHeldTests : IDisposable
{
    readonly BosWorld _w = new(new[]
    {
        ("HcA_SWAP.ini",
            "; a comment\r\n"
            + "[Forms]\r\n"
            + "HcRock|HcRockMossy\r\n"                                   // :3 kept
            + "HcRock|HcLantern,HcNoRock|NONE|chanceR(50)\r\n"           // :4 a set member naming nothing
            + "HcNoRock|HcRock\r\n"                                       // :5 base not found
            + "HcRock|HcRock\r\n"                                         // :6 base == swap
            + "hcrock|hcrockmossy\r\n"                                    // :7 a repeat
            + "[References]\r\n"
            + "0x14EC94~Midwood Isle.esp|HcRock\r\n"                     // :9 an absent plugin
            + "0x2000800~HcBosOther.esp|HcRock\r\n"                      // :10 a load index
            + "[Forms|HcTownLocation,-WhiterunLocation]\r\n"
            + "HcRockMossy|HcFarRock\r\n"                                 // :12 conditional
            + "[Transforms]\r\n"
            + "HcRock|0x3B~Skyrim.esm|posA(1,2,3)\r\n"                   // :14 names no property
            + "HcRock\r\n"),                                              // :15 no '|'
        ("HcB_SWAP.ini", "[Forms]\r\nHcRock|HcFarRock\r\n"),
    }, shadowed: new[] { ("HcB_SWAP.ini", "[Forms]\r\nHcRock|HcLoser\r\n") });

    public void Dispose() => _w.Dispose();

    static readonly string[] Bos = { "bos" };

    static void Held(string name, string actual) => DistributorLayerHeldTests.Held(name, actual);

    // The file list: BOS's header with its skipped clause, the shadowed copy, and the → filter= hint.
    [Fact]
    public void BossFileListIsHeld() => Held("bos", _w.Layer(Bos));

    // The expanded lines: BOS's order line and rows, each drop and note.
    [Fact]
    public void BossExpandedLinesAreHeld() => Held("bos-filter", _w.Layer(Bos, filter: "HcA_"));

    // plugins= across both files: the last-wins rows count across files, each selected form labelled.
    [Fact]
    public void BosUnderPluginsIsHeld() => Held("bos-plugins", _w.Layer(Bos, plugins: new[] { BosWorld.Other }));

    // formids= on a condition, a filter label.
    [Fact]
    public void BosUnderFormIdsIsHeld() => Held("bos-formids", _w.Layer(Bos, formids: new[] { "000804:HcBos.esp" }));

    // BOS's selector zero: its own close, naming no plugin term.
    [Fact]
    public void BossSelectorZeroIsHeld() => Held("bos-formids-zero", _w.Layer(Bos, formids: new[] { "000805:HcBos.esp" }));

    // BOS's own file cut.
    [Fact]
    public void BossOwnFileCutIsHeld() => Held("bos-cap", _w.Layer(Bos, maxChars: 1080));

    // BOS's own line cut.
    [Fact]
    public void BossOwnLineCutIsHeld() => Held("bos-filter-cap", _w.Layer(Bos, filter: "HcA_", maxChars: 2500));

    // "no BOS files in the active order".
    [Fact]
    public void NoBosFilesIsHeld()
    {
        using var w = new BosWorld(Array.Empty<(string, string)>());
        Held("bos-none", w.Layer(Bos));
    }
}
