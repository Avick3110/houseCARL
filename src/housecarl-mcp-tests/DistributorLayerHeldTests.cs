using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>SPID's and KID's parts of housecarl_layer compared whole, byte for byte, to answers held from the build
/// before the two formats shared one reader and one renderer (distributor-share). A change made to both formats at
/// once fails here, where a test comparing two calls of one build would pass. These pin unchanged behaviour: each held
/// answer is the one the separate SPID and KID code gave. With distributor omitted, BOS's part follows them (bos-lines,
/// pick (1) Q1 (b)), then FLM's (flm-lines): each is held in a file of its own, captured on its lane's build, and the
/// over-cap sentence that names them is held whole (omitted-cap-flm). The held files are in DistributorLayerHeld/, LF only.</summary>
[Trait("tier", "integration")]
public sealed class DistributorLayerHeldTests : IDisposable
{
    const string Aranea = "000810:HcSpid.esp", Boots = "000800:HcArmorSrc.esp";

    readonly SpidWorld _w = new(new[]
    {
        ("HcA_DISTR.ini",
            "Perk = HcPerk\r\n"                                              // :1
            + "Perk = HcNoSuchPerk\r\n"                                      // :2 dropped
            + "Spell = HcSpell|NONE|0x1000810,HcDeerRace\r\n"                // :3
            + "Outfit = HcOutfit|Skald\r\n"                                  // :4 first wins
            + "Spell = HcSpell||HcOther.esp\r\n"                             // :5 a plugin term
            + "ExclusiveGroup = Mine|HcKeyword,HcPerk\r\n"                   // :6
            + "Perk = 0x812~HcSpid.esp~more\r\n"                             // :7 two '~': SPID reads the address at the first
            + "no assignment here\r\n"                                       // :8 skipped
            + "Outfit = 0x815~HcSpid.esp|HcAranea\r\n"                       // :9 first wins
            + "Perk = HcGone | NONE\r\n"),                                   // :10 a parse note, then the drop
        ("HcB_DISTR.ini", "Outfit = 0x815~HcSpid.esp\r\n"),
        ("HcA_KID.ini",
            "Keyword = HcVisor|Armor|HcBoots\r\n"                            // :1 a created keyword
            + "Keyword = 0x814~HcSpid.esp|Armor|HcKeyword,-HcOther.esp\r\n"  // :2 a plugin term
            + "Keyword = HcSkull|Armor|HcAbsent.esp\r\n"                     // :3 dropped
            + "Keyword = HcTwo|Armor|0x800~HcArmorSrc.esp~more\r\n"          // :4 two '~': KID reads a name
            + "Keyword = HcTag|FormList|HcBoots\r\n"                         // :5 dropped without a log
            + "no assignment here\r\n"                                       // :6 skipped
            + "Keyword = HcSkull|Armor|HcAbsent.esp|NONE|100|extra\r\n"),    // :7 the drop, then the parse note
        ("HcB_KID.ini", "Keyword = HcVisor|Weapon\r\n"),
        (SpidWorld.SkyNpcIni, "filterByNpcs=HcSpid.esp|810:weight=5\r\n"),
    }, shadowed: new[] { ("HcB_DISTR.ini", "Outfit = HcLoserLine\r\n"), ("HcB_KID.ini", "Keyword = HcLoserLine|Armor\r\n") });

    public void Dispose() => _w.Dispose();

    string Layer(string[]? distributor = null, string[]? formids = null, string[]? plugins = null, string? filter = null, int maxChars = 0)
        => Layer(_w, distributor, formids, plugins, filter, maxChars);

    static string Layer(SpidWorld w, string[]? distributor = null, string[]? formids = null, string[]? plugins = null, string? filter = null, int maxChars = 0)
        => SkyPatcherTools.Layer(w.Svc, distributor: distributor, filter: filter, formids: formids, max_chars: maxChars,
               plugins: plugins is null ? null : new SkyPatcherTools.LayerScope { names = plugins })
           .Replace(w.Root, "<root>", StringComparison.OrdinalIgnoreCase);

    /// <summary>The answer against its held file, whole.</summary>
    internal static void Held(string name, string actual)
        => Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DistributorLayerHeld", name + ".txt")), actual);

    /// <summary>An answer with distributor omitted: SkyPatcher's, SPID's and KID's parts against the answer held before
    /// BOS was read, then BOS's part and FLM's, which follow them, each against its own held file.</summary>
    static void HeldThenBosThenFlm(string name, string bos, string flm, string actual)
    {
        int at = actual.IndexOf("\n\nBOS layer (", StringComparison.Ordinal);
        int flmAt = actual.IndexOf("\n\nFLM layer (", StringComparison.Ordinal);
        Assert.True(at > 0, "no BOS part");
        Assert.True(flmAt > at, "no FLM part after BOS's");
        Held(name, actual[..at]);
        Held(bos, actual[at..flmAt]);
        Held(flm, actual[flmAt..]);
    }

    static readonly string[] Spid = { "spid" }, Kid = { "kid" }, Both = { "spid", "kid" };

    // The bare overview: SkyPatcher's layer, then SPID's and KID's summary lines as held, then BOS's and FLM's (pick (1) Q1 (b)).
    [Fact]
    public void TheBareOverviewIsHeld() => HeldThenBosThenFlm("omitted", "omitted-bos", "omitted-flm", Layer());

    // SPID's skipped clause in its header and the → filter= hint.
    [Fact]
    public void SpidsFileListWithItsSkippedClauseAndHintIsHeld() => Held("spid", Layer(Spid));

    // KID's skipped clause and its → filter= hint.
    [Fact]
    public void KidsFileListWithItsSkippedClauseAndHintIsHeld() => Held("kid", Layer(Kid));

    // "expanded to their lines with what", SPID's order: line, its first-wins rows, "SPID skips this line", a drop, and
    // a term with two '~' read as an address.
    [Fact]
    public void SpidsExpandedLinesWithItsOrderLineAndSkipAreHeld() => Held("spid-filter", Layer(Spid, filter: "HcA_"));

    // KID's expanded lines: a drop, a parse-time skip, a created keyword, and a term with two '~' read as a name.
    [Fact]
    public void KidsExpandedLinesWithATwoTildeNameAreHeld() => Held("kid-filter", Layer(Kid, filter: "HcA_"));

    // "only those files are listed", the plugin-term rows of both formats, SPID first, then KID.
    [Fact]
    public void BothFormatsUnderPluginsAreHeld() => Held("spid-kid-plugins", Layer(Both, plugins: new[] { SpidWorld.Other }));

    // formids= labels on both formats, three parts joined.
    [Fact]
    public void ThreePartsUnderFormIdsAreHeld() => HeldThenBosThenFlm("omitted-formids", "omitted-formids-bos", "omitted-formids-flm", Layer(formids: new[] { Aranea, Boots }));

    // SPID's and KID's selector zero: "nothing matched: no SPID line" and its KID twin.
    [Fact]
    public void BothSelectorZerosAreHeld() => Held("spid-kid-formids-zero", Layer(Both, formids: new[] { "000800:HcOther.esp" }));

    // A filter= no file of the format matches.
    [Fact]
    public void AFilterNoFileMatchesIsHeld() => Held("spid-kid-filter-nomatch", Layer(Both, filter: "zzznomatch"));

    // SPID's own file cut.
    [Fact]
    public void SpidsOwnFileCutIsHeld() => Held("spid-cap", Layer(Spid, maxChars: 800));

    // KID's own file cut.
    [Fact]
    public void KidsOwnFileCutIsHeld() => Held("kid-cap", Layer(Kid, maxChars: 1000));

    // SPID's own line cut.
    [Fact]
    public void SpidsOwnLineCutIsHeld() => Held("spid-filter-cap", Layer(Spid, filter: "HcA_", maxChars: 1800));

    // KID's own line cut.
    [Fact]
    public void KidsOwnLineCutIsHeld() => Held("kid-filter-cap", Layer(Kid, filter: "HcA_", maxChars: 1500));

    // KID's joined line cut after SPID's part.
    [Fact]
    public void KidsJoinedLineCutIsHeld() => Held("spid-kid-filter-cap", Layer(Both, filter: "HcA_", maxChars: 4200));

    // The summary path past its cap: the plural over-cap sentence, which names BOS's and FLM's parts after SPID's and KID's.
    [Fact]
    public void ThePluralOverCapIsHeld() => Held("omitted-cap-flm", Layer(maxChars: 900));

    // "no SPID files in the active order" and "no KID files".
    [Fact]
    public void NoFilesOfEitherFormatIsHeld()
    {
        using var w = new SpidWorld(new[] { (SpidWorld.SkyNpcIni, "filterByNpcs=HcSpid.esp|810:weight=5\r\n") });
        Held("spid-kid-none", Layer(w, Both));
    }

    // r1 #9: KID's lines are read across every file at once, so file 2's filter finds the keyword file 1 creates.
    [Fact]
    public void AFilterNamingAKeywordAnotherFileCreatesIsHeld()
    {
        using var w = new SpidWorld(new[]
        {
            ("HcA_KID.ini", "Keyword = HcVisor|Armor|HcBoots\r\n"),
            ("HcB_KID.ini", "Keyword = HcTag|Armor|HcVisor\r\n"),
            (SpidWorld.SkyNpcIni, "filterByNpcs=HcSpid.esp|810:weight=5\r\n"),
        });
        Held("kid-filter-across-files", Layer(w, Kid, filter: "HcB_"));
    }

    // A file that cannot be read: its read-error row, on each format.
    [Fact]
    public void AFileThatCannotBeReadIsHeld()
    {
        Layer(Both);
        using var a = HeldOpen.Hold(Path.Combine(_w.IniMod, "HcB_DISTR.ini"));
        using var b = HeldOpen.Hold(Path.Combine(_w.IniMod, "HcB_KID.ini"));
        Held("spid-kid-filter-unread", Layer(Both, filter: "HcB_"));
    }

    // The EditorID lookup's unreadable-plugin notes: SPID's part says them, KID's skips what SPID's holds; KID alone says them.
    [Fact]
    public void TheUnreadablePluginNotesAreHeld()
    {
        Layer(Both);
        using var hold = HeldOpen.Hold(Path.Combine(Path.GetDirectoryName(_w.IniMod)!, "HcSpidPlugins", SpidWorld.Other));
        Held("spid-kid-plugins-unreadable", Layer(Both, plugins: new[] { SpidWorld.Spid }));
        Held("kid-plugins-unreadable", Layer(Kid, plugins: new[] { SpidWorld.Spid }));
    }
}
