using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.ResolverArchives;

namespace HousecarlMcpTests;

/// <summary>The root-only listing door: every loose root's copy of one folder, top level only, plus the archive entries
/// in that folder — the same answer the full walk gives for that level, at one directory listing per root.</summary>
[Trait("tier", "unit")]
public sealed class AssetTopLevelListingTests : IDisposable
{
    static readonly string[] Enabled = { "HighMod", "LowMod" };   // highest priority first

    readonly string _root, _overwrite, _mods, _data, _high, _low;

    public AssetTopLevelListingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-asset-toplevel-" + Guid.NewGuid().ToString("N"));
        _overwrite = Path.Combine(_root, "overwrite");
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "Data");
        _high = Path.Combine(_mods, "HighMod");
        _low = Path.Combine(_mods, "LowMod");
        foreach (var d in new[] { _overwrite, _high, _low, _data }) Directory.CreateDirectory(d);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    AssetResolver Build(params ActiveArchive[] archives) => AssetResolver.Build(_overwrite, _mods, _data, Enabled, archives);

    // Probe: a file in the folder is listed, a file below it is not — the whole point of the door.
    [Fact]
    public void AFileInTheFolderIsListedAndOneBelowItIsNot()
    {
        Loose(_high, @"top.ini");
        Loose(_high, @"nested\under.ini");
        using var r = Build();

        Assert.Equal(new[] { "top.ini" }, r.EnumerateTopLevel("").OrderBy(x => x));
        Assert.Contains(@"nested\under.ini", r.EnumerateUnder(""));
    }

    // Probe: the same name in two roots is one path, as the walk's union gives it.
    [Fact]
    public void OneNameInTwoRootsIsListedOnce()
    {
        Loose(_high, "shared.ini");
        Loose(_low, "shared.ini");
        Loose(_data, "data.ini");
        using var r = Build();

        Assert.Equal(new[] { "data.ini", "shared.ini" }, r.EnumerateTopLevel("").OrderBy(x => x));
    }

    // Probe: the cost — one directory listing per loose root, where the full walk reads the subtree too.
    [Fact]
    public void ATopLevelListingReadsOneDirectoryPerRootAndTheWalkReadsMore()
    {
        Loose(_high, "top.ini");
        Loose(_high, @"a\b\deep.ini");
        using var r = Build();

        var top = r.Capture();
        top.EnumerateTopLevel("");
        var walk = r.Capture();
        walk.EnumerateUnder("");

        Assert.Equal(4, top.ListingCount);          // overwrite, HighMod, LowMod, Data
        Assert.True(walk.ListingCount > top.ListingCount, $"the walk read {walk.ListingCount} listings, the top level {top.ListingCount}");
    }

    // Probe: an archive entry below the folder is not in the folder, and one in it is.
    [Fact]
    public void AnArchiveEntryBelowTheFolderIsNotListedAndOneInItIs()
    {
        var path = Archive("Top.bsa", HousecarlGenerator.BsaBuilder.Build(105,
            HousecarlGenerator.BsaBuilder.HasFolderNames | HousecarlGenerator.BsaBuilder.HasFileNames,
            new[]
            {
                ("cfg", new[] { ("in.ini", HousecarlGenerator.BsaBuilder.Bytes("IN", 16)) }),
                (@"cfg\deeper", new[] { ("below.ini", HousecarlGenerator.BsaBuilder.Bytes("BELOW", 16)) }),
            }));
        using var r = Build(new ActiveArchive(path, "Top.esp", 0));

        Assert.Equal(new[] { @"cfg\in.ini" }, r.EnumerateTopLevel("cfg").OrderBy(x => x));
        Assert.Contains(@"cfg\deeper\below.ini", r.EnumerateUnder("cfg"));
    }

    string Archive(string name, byte[] bytes) => Write(_root, name, bytes);
}
