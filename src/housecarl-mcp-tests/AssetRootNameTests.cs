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
    static readonly byte[] X = "x"u8.ToArray();

    readonly PlaceInstance _p = new();

    public AssetRootNameTests()
    {
        foreach (var mod in new[] { "A", "B" })
            foreach (var rel in new[] { Meta, Readme, FomodInfo, Nif, NestedMeta })
                PlaceInstance.Loose(_p.Mod(mod), rel, X);
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
    [InlineData(@"fomodx\a.xml", true)]
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
    [InlineData("fomod", null)]
    [InlineData("fomod/**", null)]
    [InlineData("meta.ini", null)]
    [InlineData("fomod", "A")]
    public void AHiddenRootNameMatchesNothingAndSaysMo2HidesIt(string sel, string? provider)
    {
        var text = AssetTools.AssetStatus(_p.Open(), under: new[] { sel }, provider: provider);

        Assert.Contains("matched no file", text);
        Assert.Contains("MO2 hides a root meta.ini, readme.txt or fomod folder", text);
        Assert.DoesNotContain("nothing enabled provides", text);
        Assert.DoesNotContain("info.xml", text);
    }

    [Theory]
    [InlineData("meta*", null)]
    [InlineData("readme.???", null)]
    [InlineData("*.ini", "A")]
    public void AWildcardRootGlobOverAHiddenNameSaysMo2HidesIt(string sel, string? provider)
    {
        var text = AssetTools.AssetStatus(_p.Open(), under: new[] { sel }, provider: provider);

        Assert.Contains("matched no file", text);
        Assert.Contains("MO2 hides a root meta.ini, readme.txt or fomod folder", text);
        Assert.DoesNotContain("nothing enabled provides", text);
    }

    /// <summary>Pins unchanged behaviour: a selector that matches nothing real keeps the spelling note.</summary>
    [Theory]
    [InlineData("nosuchdir")]
    [InlineData("zzz*")]
    public void AMissingFolderStillSaysCheckTheSpelling(string sel)
    {
        var text = AssetTools.AssetStatus(_p.Open(), under: new[] { sel });

        Assert.Contains("check the spelling, or nothing enabled provides that folder", text);
        Assert.DoesNotContain("MO2 hides", text);
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
    public void ARootMetaIniSpeltWithATrailingDotIsAbsent()
        => Assert.False(Hit(_p.Open(), "meta.ini.").Exists);

    [Theory]
    [InlineData("meta.ini")]
    [InlineData("readme.txt")]
    [InlineData("fomod/info.xml")]
    public void AHiddenRootPathRendersAbsentSayingMo2HidesIt(string rel)
    {
        var text = AssetTools.AssetStatus(_p.Open(), asset_paths: new[] { rel });

        Assert.Contains("ABSENT — MO2 hides a root meta.ini, readme.txt or fomod folder", text);
        Assert.DoesNotContain("no active mod or BSA provides", text);
    }

    /// <summary>Pins unchanged behaviour: a path no mod ships keeps the plain absent line, and a nested meta.ini wins.</summary>
    [Fact]
    public void AMissingPathKeepsThePlainAbsentLine()
    {
        var svc = _p.Open();

        var text = AssetTools.AssetStatus(svc, asset_paths: new[] { "meshes/nosuch.nif" });
        Assert.Contains("ABSENT — no active mod or BSA provides this path", text);
        Assert.DoesNotContain("MO2 hides", text);
        Assert.Contains("WINS", AssetTools.AssetStatus(svc, asset_paths: new[] { "meshes/meta.ini" }));
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
        var overwrite = Path.Combine(p.Inst, "overwrite");
        PlaceInstance.Loose(overwrite, Meta, X);
        PlaceInstance.Loose(overwrite, NestedMeta, X);
        PlaceInstance.Loose(p.Data, Readme, X);
        PlaceInstance.Loose(p.Data, @"meshes\readme.txt", X);
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

    /// <summary>Pins unchanged behaviour: the root names are a loose-layer rule, so an archive's root readme.txt still lists and resolves.</summary>
    [Fact]
    public void AnArchiveRootReadmeIsStillListedAndResolves()
    {
        using var p = new PlaceInstance();
        var body = HousecarlGenerator.BsaBuilder.Bytes("RDM", 8);
        File.WriteAllBytes(Path.Combine(p.Mod("ArchHost"), "Dummy.bsa"), HousecarlGenerator.BsaBuilder.Build(105,
            HousecarlGenerator.BsaBuilder.HasFolderNames | HousecarlGenerator.BsaBuilder.HasFileNames, new[]
            {
                ("", new[] { (Readme, body) }),
            }));
        p.ProfileWithDummy("ArchHost", "+ArchHost");

        var svc = p.Open();
        Assert.Contains(Readme, Under(svc, "*"));
        Assert.True(Hit(svc, Readme).Exists);
    }

    [Fact]
    public void TheAssetStatusDescriptionNamesTheHiddenRootNames()
    {
        var text = typeof(AssetTools).GetMethod(nameof(AssetTools.AssetStatus))!.GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var name in new[] { "root meta.ini", "root readme.txt", "root fomod folder" })
            Assert.Contains(name, text);
    }
}
