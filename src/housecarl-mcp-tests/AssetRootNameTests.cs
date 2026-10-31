using System.ComponentModel;
using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2 leaves a root meta.ini, readme.txt and fomod\ out of its Data view whatever the skip lists say, so they
/// are not listed, counted or found by name; the same names below the root, and in archives, are normal files.</summary>
[Trait("tier", "integration")]
public sealed class AssetRootNameTests : IDisposable
{
    const string Meta = "meta.ini";
    const string Readme = "readme.txt";
    const string FomodInfo = @"fomod\info.xml";
    const string Nif = @"meshes\a.nif";
    const string NestedMeta = @"meshes\meta.ini";

    readonly PlaceInstance _p = new();

    public AssetRootNameTests()
    {
        foreach (var mod in new[] { "A", "B" })
            foreach (var rel in new[] { Meta, Readme, FomodInfo, Nif, NestedMeta })
                PlaceInstance.Loose(_p.Mod(mod), rel, "x"u8.ToArray());
        _p.Profile(Array.Empty<string>(), Array.Empty<string>(), new[] { "+B", "+A" });
    }

    public void Dispose() => _p.Dispose();

    static IEnumerable<string> Under(LoadOrderService svc, string sel)
        => svc.AssetStatus(Array.Empty<string>(), new[] { sel }).Results.Select(r => r.RelPath);

    static AssetHit Hit(LoadOrderService svc, string rel) => svc.AssetStatus(new[] { rel }).Results[0].Hit!;

    [Theory]
    [InlineData("meta.ini", false)]
    [InlineData("readme.txt", false)]
    [InlineData("META.INI", false)]
    [InlineData(@"fomod\info.xml", false)]
    [InlineData(@"FOMOD\a\b.png", false)]
    [InlineData(@"meshes\meta.ini", true)]
    [InlineData(@"meshes\fomod\x.xml", true)]
    [InlineData(@"root\readme.txt", true)]
    [InlineData("meta.ini.bak", true)]
    public void TheRootNamesAreHiddenUnderTheDefaultAndEmptyLists(string rel, bool shown)
    {
        Assert.Equal(shown, Mo2SkipRules.Default.Shows(rel));
        Assert.Equal(shown, new Mo2SkipRules(Array.Empty<string>(), Array.Empty<string>()).Shows(rel));
    }

    [Fact]
    public void AProviderCountsNoRootFileAndNoShadowedLayerFromOne()
    {
        var text = AssetTools.AssetStatus(_p.Open(), provider: "A", counts_only: true);

        Assert.Contains("provides=2 ", text);
    }

    [Fact]
    public void ARootGlobListsNoRootFile()
    {
        var svc = _p.Open();

        Assert.Empty(Under(svc, "*"));
        Assert.Empty(Under(svc, "*.ini"));
        Assert.Equal(new[] { NestedMeta }, Under(svc, "**/meta.ini"));
    }

    [Theory]
    [InlineData("fomod")]
    [InlineData("fomod/**")]
    public void ARootFomodFolderMatchesNothingAndSaysSo(string sel)
    {
        var text = AssetTools.AssetStatus(_p.Open(), under: new[] { sel });

        Assert.Contains("matched no file", text);
        Assert.DoesNotContain("info.xml", text);
    }

    [Fact]
    public void ARootMetaIniAskedByNameIsAbsent()
    {
        var svc = _p.Open();

        var hit = Hit(svc, Meta);
        Assert.False(hit.Exists);
        Assert.Empty(hit.Providers);
        Assert.DoesNotContain(Meta, Under(svc, Meta));
    }

    [Fact]
    public void AMetaIniBelowTheRootStillResolves()
    {
        var hit = Hit(_p.Open(), NestedMeta);

        Assert.True(hit.Exists);
        Assert.Equal("B", hit.Winner!.Source);
        Assert.Contains(hit.Providers, p => p.Source == "A");
    }

    [Fact]
    public void ARootMetaIniInOverwriteOrTheDataFolderIsAbsentToo()
    {
        using var p = new PlaceInstance();
        PlaceInstance.Loose(Path.Combine(p.Inst, "overwrite"), Meta, "x"u8.ToArray());
        PlaceInstance.Loose(p.Data, Readme, "x"u8.ToArray());
        PlaceInstance.Loose(Path.Combine(p.Inst, "overwrite"), NestedMeta, "x"u8.ToArray());
        PlaceInstance.Loose(p.Data, @"meshes\readme.txt", "x"u8.ToArray());
        p.Profile(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var svc = p.Open();

        Assert.False(Hit(svc, Meta).Exists);
        Assert.False(Hit(svc, Readme).Exists);
        Assert.Equal("Overwrite", Hit(svc, NestedMeta).Winner!.Source, ignoreCase: true);
        Assert.True(Hit(svc, @"meshes\readme.txt").Exists);
    }

    [Fact]
    public void ASkipListTheInstanceSetsCannotUnhideTheRootNames()
    {
        File.AppendAllText(Path.Combine(_p.Inst, "ModOrganizer.ini"),
            "[Settings]\r\nskip_file_suffixes=@Invalid()\r\nskip_directories=@Invalid()\r\n");
        var svc = _p.Open();

        Assert.False(Hit(svc, Meta).Exists);
        Assert.False(Hit(svc, FomodInfo).Exists);
    }

    [Fact]
    public void ExplicitPathsModeHidesARootMetaIni()
    {
        using var svc = LoadOrderService.WithExplicitPaths(_p.Data, _p.Mods, _p.Prof, 0,
            new UserConfigStore(Path.Combine(_p.Root, "user-explicit.json")));

        Assert.False(Hit(svc, Meta).Exists);
        Assert.True(Hit(svc, NestedMeta).Exists);
    }

    /// <summary>Pins unchanged behaviour: the root names are a loose-layer rule, so an archive's root readme.txt still lists.</summary>
    [Fact]
    public void AnArchiveRootReadmeIsStillListed()
    {
        using var p = new PlaceInstance();
        var body = HousecarlGenerator.BsaBuilder.Bytes("RDM", 8);
        File.WriteAllBytes(Path.Combine(p.Mod("ArchHost"), "Dummy.bsa"), HousecarlGenerator.BsaBuilder.Build(105,
            HousecarlGenerator.BsaBuilder.HasFolderNames | HousecarlGenerator.BsaBuilder.HasFileNames, new[]
            {
                ("", new[] { (Readme, body) }),
            }));
        p.ProfileWithDummy("ArchHost", "+ArchHost");

        Assert.Contains(Readme, Under(p.Open(), "*"));
    }

    [Fact]
    public void TheAssetStatusDescriptionNamesTheHiddenRootNames()
    {
        var text = typeof(AssetTools).GetMethod(nameof(AssetTools.AssetStatus))!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var name in new[] { "root meta.ini", "root readme.txt", "root fomod folder" })
            Assert.Contains(name, text);
    }
}

/// <summary>A root's top-level files drop a root meta.ini and readme.txt; a root plugin is still listed.</summary>
[Trait("tier", "unit")]
public sealed class LooseRootFilesRootNameTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-looseroot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ARootMetaIniIsNotListedAndARootPluginIs()
    {
        string overwrite = Path.Combine(_root, "overwrite"), mods = Path.Combine(_root, "mods"), data = Path.Combine(_root, "Data");
        foreach (var d in new[] { overwrite, Path.Combine(mods, "ModA"), data }) Directory.CreateDirectory(d);
        foreach (var f in new[] { "A.esp", "meta.ini", "readme.txt" }) File.WriteAllText(Path.Combine(mods, "ModA", f), "x");
        using var r = AssetResolver.Build(overwrite, mods, data, new[] { "ModA" }, Array.Empty<ActiveArchive>());

        Assert.Equal(new[] { "A.esp" }, r.Capture().LooseRootFiles("ModA"));
    }
}
