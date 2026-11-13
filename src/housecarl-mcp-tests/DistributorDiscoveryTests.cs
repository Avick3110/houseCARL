using HousecarlCore;
using HousecarlGenerator;
using Xunit;
using static HousecarlMcpTests.ResolverArchives;

namespace HousecarlMcpTests;

/// <summary>The four distributor formats' discovery, every rule a value of their rows: Data's top level and no deeper,
/// the name token and the extension case-sensitive, one ordinal sort, FLM's second folder appended, the MO2 winner per
/// name, and a copy only inside an archive said to be not applied.</summary>
[Trait("tier", "unit")]
public sealed class DistributorDiscoveryTests : IDisposable
{
    static readonly string[] Enabled = { "HighMod", "LowMod" };   // highest priority first

    readonly string _root, _overwrite, _mods, _data, _high, _low;

    public DistributorDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-distr-discovery-" + Guid.NewGuid().ToString("N"));
        _overwrite = Path.Combine(_root, "overwrite");
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "Data");
        _high = Path.Combine(_mods, "HighMod");
        _low = Path.Combine(_mods, "LowMod");
        foreach (var d in new[] { _overwrite, _high, _low, _data }) Directory.CreateDirectory(d);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }


    AssetResolver Build(params ActiveArchive[] archives) => AssetResolver.Build(_overwrite, _mods, _data, Enabled, archives);

    IReadOnlyList<DistributorDiscovery.ConfigFile> Scan(DistributorFormat f, params ActiveArchive[] archives)
    {
        using var r = Build(archives);
        return DistributorDiscovery.Scan(r.Capture(), f);
    }

    static string[] Paths(IEnumerable<DistributorDiscovery.ConfigFile> files) => files.Select(f => f.RelPath).ToArray();

    // Probe: Data's top level and no deeper — the DLL's directory_iterator is not recursive.
    [Fact]
    public void AFileBelowDataIsNotRead()
    {
        Loose(_high, "Top_DISTR.ini");
        Loose(_high, @"nested\Deep_DISTR.ini");

        Assert.Equal(new[] { "Top_DISTR.ini" }, Paths(Scan(ShippedRows.Distributor("spid"))));
    }

    // Probe: the same row with depth 'descends' reads the nested file — the depth is the row's, not the code's.
    [Fact]
    public void ARowThatDescendsReadsTheNestedFile()
    {
        Loose(_high, "Top_DISTR.ini");
        Loose(_high, @"nested\Deep_DISTR.ini");

        Assert.Contains(@"nested\Deep_DISTR.ini",
            Paths(Scan(ShippedRows.Distributor("spid", ("\"depth\": \"topLevelOnly\"", "\"depth\": \"descends\"")))));
    }

    // Probe: a rule is read off the row it is written on — put 'descends' on KID's row and KID descends while
    // SPID, whose row still says top level only, does not.
    [Fact]
    public void ARuleIsReadOffTheRowItIsWrittenOn()
    {
        Loose(_high, "Top_DISTR.ini");
        Loose(_high, @"nested\Deep_DISTR.ini");
        Loose(_high, "Top_KID.ini");
        Loose(_high, @"nested\Deep_KID.ini");

        var source = ShippedRows.EditedRow("kid", ("\"depth\": \"topLevelOnly\"", "\"depth\": \"descends\""));
        var rows = RuntimeFormats.DistributorsFrom(source);

        Assert.Equal(new[] { "Top_DISTR.ini" }, Paths(Scan(rows.Single(d => d.Key == "spid"))));
        Assert.Contains(@"nested\Deep_KID.ini", Paths(Scan(rows.Single(d => d.Key == "kid"))));
    }

    // Probe: the name token is compared as written, so '_distr' is not '_DISTR'.
    [Fact]
    public void TheNameTokenIsCaseSensitive()
    {
        Loose(_high, "Good_DISTR.ini");
        Loose(_high, "bad_distr.ini");

        Assert.Equal(new[] { "Good_DISTR.ini" }, Paths(Scan(ShippedRows.Distributor("spid"))));
    }

    // Probe: the extension is compared as written, so '.INI' is not '.ini'.
    [Fact]
    public void TheExtensionIsCaseSensitive()
    {
        Loose(_high, "Good_DISTR.ini");
        Loose(_high, "Bad_DISTR.INI");

        Assert.Equal(new[] { "Good_DISTR.ini" }, Paths(Scan(ShippedRows.Distributor("spid"))));
    }

    // Probe: both comparisons are the row's — set them to ignore case and the two near misses are read.
    [Fact]
    public void ARowThatIgnoresCaseReadsBothNearMisses()
    {
        // Two near misses with different stems: Windows has one file per name whatever its case.
        Loose(_high, "Good_DISTR.ini");
        Loose(_high, "bad_distr.ini");
        Loose(_high, "Other_DISTR.INI");

        var read = Paths(Scan(ShippedRows.Distributor("spid",
            ("\"tokenCase\": \"ordinal\"", "\"tokenCase\": \"ordinalIgnoreCase\""),
            ("\"extensionCase\": \"ordinal\"", "\"extensionCase\": \"ordinalIgnoreCase\""))));
        Assert.Equal(3, read.Length);
    }

    // Probe: one ordinal sort over the whole list — every upper-case name before every lower-case one, where an
    // ignore-case sort would put 'a' first.
    [Fact]
    public void TheFilesComeBackInOneOrdinalSort()
    {
        Loose(_high, "c_DISTR.ini");
        Loose(_high, "B_DISTR.ini");
        Loose(_high, "a_DISTR.ini");

        Assert.Equal(new[] { "B_DISTR.ini", "a_DISTR.ini", "c_DISTR.ini" }, Paths(Scan(ShippedRows.Distributor("spid"))));
    }

    // Probe: FLM's second folder is appended after the sorted root list, and every file out of it says its place is not the DLL's.
    [Fact]
    public void FlmsSecondFolderIsAppendedAfterTheRootListAndSaysItsOrderIsNotTheDlls()
    {
        Loose(_high, "z_FLM.ini");
        Loose(_high, @"FLM\a.ini");
        Loose(_high, @"FLM\b.ini");

        var files = Scan(ShippedRows.Distributor("flm"));

        Assert.Equal(new[] { "z_FLM.ini", @"FLM\a.ini", @"FLM\b.ini" }, Paths(files));
        Assert.Null(files[0].OrderNote);
        Assert.All(files.Skip(1), f => Assert.Equal(DistributorDiscovery.UnsortedFolderNote, f.OrderNote));
    }

    // Probe: the files of a folder the DLL does not sort still come back in one ordinal sort — the listing's own
    // order is not reproducible, so the answer states that in its note rather than in its order.
    [Fact]
    public void AnUnsortedFoldersFilesStillComeBackOrdinallySorted()
    {
        // The filesystem lists these case-insensitively, where an ordinal sort puts every upper-case name first.
        Loose(_high, @"FLM\c.ini");
        Loose(_high, @"FLM\B.ini");
        Loose(_high, @"FLM\a.ini");

        var files = Scan(ShippedRows.Distributor("flm"));

        Assert.Equal(new[] { @"FLM\B.ini", @"FLM\a.ini", @"FLM\c.ini" }, Paths(files));
        Assert.All(files, f => Assert.Equal(DistributorDiscovery.UnsortedFolderNote, f.OrderNote));
    }

    // Probe: one name in two mods is one file, won by the higher-priority mod, with the loser named as shadowed.
    [Fact]
    public void OneNameInTwoModsIsOneFileWonByTheHigherMod()
    {
        Loose(_high, "Shared_DISTR.ini");
        Loose(_low, "Shared_DISTR.ini");

        var file = Assert.Single(Scan(ShippedRows.Distributor("spid")));
        Assert.Equal("HighMod", file.WinningProvider);
        Assert.Equal(new[] { "LowMod" }, file.ShadowedProviders);
        Assert.Null(file.NotApplied);
    }

    // Probe: the roots a format is read out of are MO2's own — the overwrite folder wins over the mods (#934's
    // named miss), the game's Data folder provides like any other root, and a mod MO2 does not load is not read.
    [Fact]
    public void TheOverwriteFolderWinsAndAModNotInTheOrderIsNotRead()
    {
        Loose(_overwrite, "Shared_DISTR.ini");
        Loose(_high, "Shared_DISTR.ini");
        Loose(_data, "Data_DISTR.ini");
        Loose(Path.Combine(_mods, "OffMod"), "Off_DISTR.ini");

        var files = Scan(ShippedRows.Distributor("spid"));

        Assert.Equal(new[] { "Data_DISTR.ini", "Shared_DISTR.ini" }, Paths(files));
        var shared = files.Single(f => f.RelPath == "Shared_DISTR.ini");
        Assert.Equal("overwrite", shared.WinningProvider);
        Assert.Equal(new[] { "HighMod" }, shared.ShadowedProviders);
        Assert.Equal("Data", files.Single(f => f.RelPath == "Data_DISTR.ini").WinningProvider);
    }

    // Probe: a copy only inside an archive is listed and said not to be applied (R40).
    [Fact]
    public void ACopyOnlyInsideAnArchiveIsSaidNotApplied()
    {
        var path = Write(_root, "Packed.bsa", BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
            new[] { ("FLM", new[] { ("packed.ini", BsaBuilder.Bytes("PACKED", 16)) }) }));

        var file = Assert.Single(Scan(ShippedRows.Distributor("flm"), new ActiveArchive(path, "Packed.esp", 0)));
        Assert.Equal(@"FLM\packed.ini", file.RelPath);
        Assert.Null(file.LooseFilePath);
        Assert.Contains("only inside a BSA", file.NotApplied);
    }

    // Probe: KID's folder is read by KID's own values — its depth and its extension, each off its row.
    [Fact]
    public void KidsOwnFolderValuesDriveWhatItReads()
    {
        Loose(_high, "Top_KID.ini");
        Loose(_high, @"nested\Deep_KID.ini");
        Loose(_high, "Shout_KID.INI");

        Assert.Equal(new[] { "Top_KID.ini" }, Paths(Scan(ShippedRows.Distributor("kid"))));
        Assert.Contains(@"nested\Deep_KID.ini",
            Paths(Scan(ShippedRows.Distributor("kid", ("\"depth\": \"topLevelOnly\"", "\"depth\": \"descends\"")))));
        Assert.Equal(new[] { "Shout_KID.INI" },
            Paths(Scan(ShippedRows.Distributor("kid", ("\"extension\": \".ini\"", "\"extension\": \".INI\"")))));
    }

    // Probe: the four rows are the four formats the layer names, with the counts of a token each.
    [Fact]
    public void TheSourceCarriesTheFourFormatsAndTheirNameTokens()
        => Assert.Equal(new[] { ("spid", "_DISTR"), ("kid", "_KID"), ("bos", "_SWAP"), ("flm", "_FLM") },
            RuntimeFormats.Distributors.Select(d => (d.Key, d.Folders[0].NameToken)));

    // Probe: a value the server has no rule for fails the load, naming the field and the value.
    [Theory]
    [InlineData("\"depth\": \"topLevelOnly\"", "\"depth\": \"halfway\"", "depth", "halfway")]
    [InlineData("\"order\": \"sortedOrdinal\"", "\"order\": \"byMtime\"", "order", "byMtime")]
    [InlineData("\"tokenCase\": \"ordinal\"", "\"tokenCase\": \"fuzzy\"", "tokenCase", "fuzzy")]
    [InlineData("\"addressOrder\": \"formIdFirst\"", "\"addressOrder\": \"either\"", "addressOrder", "either")]
    [InlineData("\"formId\": \"runtimeAdd\"", "\"formId\": \"guess\"", "formId", "guess")]
    [InlineData("\"bareFormId\": \"runtimeFormId\"", "\"bareFormId\": \"maybe\"", "bareFormId", "maybe")]
    [InlineData("\"pluginTerm\": \"containsExtensionText\"", "\"pluginTerm\": \"sometimes\"", "pluginTerm", "sometimes")]
    [InlineData("\"missingName\": \"createsKeyword\"", "\"missingName\": \"invents\"", "missingName", "invents")]
    [InlineData("\"editorIds\": \"everyType\"", "\"editorIds\": \"someTypes\"", "editorIds", "someTypes")]
    public void AValueTheServerHasNoRuleForFailsTheLoad(string from, string to, string prop, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ShippedRows.Distributor("spid", (from, to)));
        Assert.Contains("[distributors.spid", ex.Message);
        Assert.Contains($"'{prop}' = '{value}'", ex.Message);
    }

    // Probe: the row the message names is the row the value is on, not the first row read.
    [Fact]
    public void TheRowNamedInTheFailureIsTheRowTheValueIsOn()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ShippedRows.Distributor("kid", ("\"depth\": \"topLevelOnly\"", "\"depth\": \"halfway\"")));

        Assert.Contains("[distributors.kid.folders]", ex.Message);
    }
}
