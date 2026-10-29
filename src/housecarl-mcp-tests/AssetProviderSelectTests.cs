using System.Text.Json;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>asset_status's provider= term (#1089): every file one mod or archive ships, each row marked with that
/// provider's standing — wins, shadowed by the winner, or not provided — and the census counted per provider.</summary>
[Trait("tier", "unit")]
public sealed class AssetProviderSelectTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetProviderSelectTests(AssetSelectWorld w) => _w = w;

    AssetStatusData Provider(string name, string[]? under = null, string[]? paths = null)
        => _w.Svc.AssetStatus(paths ?? Array.Empty<string>(), under, provider: name);

    static AssetPathResult Row(AssetStatusData d, string leaf)
        => d.Results.Single(r => Path.GetFileName(r.RelPath).Equals(leaf, StringComparison.OrdinalIgnoreCase));

    /// <summary>A mod's own loose files, every one of them and nothing another mod ships: FaceBase holds eight, one of
    /// which FaceHigher wins over.</summary>
    [Fact]
    public void AModProviderSelectsEveryFileItShipsEachMarkedWinsOrShadowed()
    {
        var d = Provider("FaceBase");

        Assert.Equal(8, d.Selected);
        Assert.Equal(ProviderStanding.Shadowed, d.StandingOf(Row(d, "0002.nif").Hit));
        Assert.Equal("FaceHigher", Row(d, "0002.nif").Hit!.Winner!.Source);
        Assert.Equal(7, d.Results.Count(r => d.StandingOf(r.Hit) == ProviderStanding.Wins));
        // Only FaceBase's own files: FaceHigher's 0004 is not in its view.
        Assert.DoesNotContain(d.Results, r => Path.GetFileName(r.RelPath) == "0004.nif");
    }

    /// <summary>The text row says the standing in one line, naming the winning copy the way the providers line does.</summary>
    [Fact]
    public void TheTextRowNamesTheCopyThatShadowsTheProvider()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, provider: "FaceBase");

        Assert.Contains("(3 paths selected, provider \"FaceBase\")", text);
        Assert.Contains("provider \"FaceBase\": shadowed by \"FaceHigher\" (loose)", text);
        Assert.Contains("provider \"FaceBase\": wins", text);
    }

    /// <summary>An archive's filename is a provider too: its entries, and only its, with the same standings.</summary>
    [Fact]
    public void AnArchiveNameSelectsItsEntriesTheSameWay()
    {
        var d = Provider("HcArch.bsa");

        Assert.Equal(3, d.Selected);
        Assert.All(d.Results, r => Assert.Contains(r.Hit!.Providers, p => p.Source == "HcArch.bsa"));
        Assert.Equal(ProviderStanding.Wins, d.StandingOf(Row(d, "0005.nif").Hit));
        var split = Row(d, Path.GetFileName(AssetSelectWorld.Face(AssetSelectWorld.SplitFormId, FaceGenSlot.Mesh)));
        Assert.Equal(ProviderStanding.Shadowed, d.StandingOf(split.Hit));
        Assert.Equal("FaceHigher", split.Hit!.Winner!.Source);
    }

    /// <summary>A mod name covers the archives it ships, as it does for place's source_provider=: ArchiveMod is both
    /// archives' entries, plus the two .bsa files themselves, which MO2 maps into Data as loose files.</summary>
    [Fact]
    public void AModNameCoversTheArchivesItShips()
    {
        var d = Provider(AssetSelectWorld.ArchiveModName);

        Assert.Equal(6, d.Selected);
        Assert.Contains(d.Results, r => r.RelPath.Equals("HcArch.bsa", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(d.Results, r => r.Hit!.Winner!.Source == "HcArch - Textures.bsa");
        Assert.Equal(1, d.Results.Count(r => d.StandingOf(r.Hit) == ProviderStanding.Shadowed));
    }

    /// <summary>A name the build does not have walks nothing, so it is refused by name rather than answered empty.</summary>
    [Fact]
    public void AnUnknownProviderIsRefusedNamingIt()
    {
        var text = AssetTools.AssetStatus(_w.Svc, provider: "Artesian Cloaks of Skyrim");

        Assert.StartsWith("error: provider 'Artesian Cloaks of Skyrim' is not in the active load order", text);
        Assert.DoesNotContain("selected", text);
    }

    /// <summary>under= scopes the provider's walk; the provider still decides what is walked.</summary>
    [Fact]
    public void UnderNarrowsTheProvidersFilesToItsSelectors()
    {
        var d = Provider("FaceBase", under: new[] { AssetSelectWorld.FaceGeomDir });

        Assert.Equal(new[] { "0001.nif", "0002.nif", "0003.nif" }, d.Results.Select(r => Path.GetFileName(r.RelPath)));

        var none = Provider("FaceBase", under: new[] { @"meshes\hcnothing" });
        Assert.Contains("matched no file in provider 'FaceBase'", Assert.Single(none.SelectorNotes!));
    }

    /// <summary>An unanchored name glob composes with the provider: where does this mod ship a file named X.</summary>
    [Fact]
    public void AnUnanchoredGlobAndAProviderComposeInOneCall()
    {
        var d = Provider("FaceHigher", under: new[] { "**/*000?.nif" });

        Assert.Equal(new[] { "0002.nif", "0004.nif" }, d.Results.Select(r => Path.GetFileName(r.RelPath)));
        Assert.All(d.Results, r => Assert.Equal(ProviderStanding.Wins, d.StandingOf(r.Hit)));
    }

    /// <summary>An explicit path the provider does not ship still answers, and says the provider does not provide it.</summary>
    [Fact]
    public void AnExplicitPathTheProviderDoesNotShipSaysSo()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0004.nif"), @"meshes\hcnothing.nif" },
                                          under: new[] { @"meshes\hcnothing" }, provider: "FaceBase");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "provider \"FaceBase\": does not provide this path").Count);
    }

    /// <summary>The census per provider: what it provides, wins and loses, and the winning-layers table says who
    /// shadows it — the "files 683 wins 665 shadowed 18" view a session once scripted by hand.</summary>
    [Fact]
    public void TheCensusCountsWhatTheProviderProvidesWinsAndLoses()
    {
        var text = AssetTools.AssetStatus(_w.Svc, provider: "FaceBase", counts_only: true);

        Assert.Contains("provider \"FaceBase\": provides=8 wins=7 shadowed=1", text);
        Assert.Matches(@"\n +1  FaceHigher", text);

        var json = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, provider: "FaceBase", counts_only: true, format: "json")).RootElement;
        var p = json.GetProperty("provider");
        Assert.Equal(8, p.GetProperty("provides").GetInt32());
        Assert.Equal(7, p.GetProperty("wins").GetInt32());
        Assert.Equal(1, p.GetProperty("shadowed").GetInt32());
    }

    /// <summary>Every census, provider or not, counts the layers that lose a path: FaceBase ships a 0002 that
    /// FaceHigher wins, so it is the one shadowed layer in the facegen folder.</summary>
    [Fact]
    public void TheCensusCountsShadowedLayersBesideWinningOnes()
    {
        var json = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                                             counts_only: true, format: "json")).RootElement;

        var axis = json.GetProperty("shadowed_by_layer");
        Assert.Equal(1, axis.GetProperty("distinct").GetInt32());
        Assert.Equal("FaceBase", axis.GetProperty("rows")[0].GetProperty("key").GetString());
        Assert.False(json.TryGetProperty("provider", out _));
    }

    /// <summary>A mod that ships archives counts their entries as its own: ArchiveMod loses one path, to FaceHigher.</summary>
    [Fact]
    public void AModsCensusCountsTheEntriesOfTheArchivesItShips()
    {
        var text = AssetTools.AssetStatus(_w.Svc, provider: AssetSelectWorld.ArchiveModName, counts_only: true);

        Assert.Contains("provides=6 wins=5 shadowed=1", text);
    }

    /// <summary>A layer is counted once per path: its loose copy beside its own archive's copy is a win, not a loss
    /// to itself, and two archives of one losing mod are one shadowed layer, not two.</summary>
    [Fact]
    public void ALayerIsShadowedOncePerPathAndNeverByItself()
    {
        using var w = new TwoCopyWorld();

        var json = JsonDocument.Parse(AssetTools.AssetStatus(w.Svc, under: new[] { "textures" }, counts_only: true,
                                                             format: "json")).RootElement;
        var rows = json.GetProperty("shadowed_by_layer").GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("Top", rows[0].GetProperty("key").GetString());
        Assert.Equal(1, rows[0].GetProperty("count").GetInt32());

        Assert.Contains("provider \"Top\": provides=2 wins=1 shadowed=1",
                        AssetTools.AssetStatus(w.Svc, under: new[] { "textures" }, provider: "Top", counts_only: true));
    }

    /// <summary>A file the provider does not ship, pasted into under=, is not that provider's file.</summary>
    [Fact]
    public void AFileNamedInUnderCountsOnlyWhenTheProviderShipsIt()
    {
        var d = Provider("FaceBase", under: new[] { _w.Rel("0004.nif") });

        Assert.Empty(d.Results);
        Assert.Contains("matched no file in provider 'FaceBase'", Assert.Single(d.SelectorNotes!));
    }

    /// <summary>Two mods over one textures folder: Top ships own.dds loose AND in its archive, and twice.dds in both
    /// of its archives; Low ships twice.dds loose, which beats Top's archive copies.</summary>
    sealed class TwoCopyWorld : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "hc-two-copy-" + Guid.NewGuid().ToString("N"));
        public LoadOrderService Svc { get; }

        public TwoCopyWorld()
        {
            var instance = Path.Combine(_root, "instance");
            var profile = Path.Combine(instance, "profiles", "Default");
            var top = Path.Combine(instance, "mods", "Top");
            var low = Path.Combine(instance, "mods", "Low");
            foreach (var d in new[] { profile, Path.Combine(_root, "game", "Data"), top, low }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
            Loose(top, @"textures\hcdup\own.dds");
            Loose(low, @"textures\hcdup\twice.dds");
            File.WriteAllBytes(Path.Combine(top, "HcDup.bsa"),
                BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                    new[] { (@"textures\hcdup", new[] { ("own.dds", BsaBuilder.Bytes("OWN", 48)), ("twice.dds", BsaBuilder.Bytes("T1", 48)) }) }));
            File.WriteAllBytes(Path.Combine(top, "HcDup - Textures.bsa"),
                BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                    new[] { (@"textures\hcdup", new[] { ("twice.dds", BsaBuilder.Bytes("T2", 48)) }) }));
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcDup.esp\r\n");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcDup.esp\r\n");
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+Top\r\n+Low\r\n");
            File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
            Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
        }

        static void Loose(string modDir, string rel)
        {
            var p = Path.Combine(modDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }

        public void Dispose()
        {
            Svc.Dispose();
            try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>The json row and the to_file= artifact carry the standing as a token, so a consumer filters on it.</summary>
    [Fact]
    public void TheJsonRowAndTheArtifactCarryTheStanding()
    {
        var json = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                                             provider: "FaceBase", format: "json")).RootElement;
        Assert.Equal("FaceBase", json.GetProperty("provider").GetString());
        var standings = json.GetProperty("results").EnumerateArray()
                            .Select(r => r.GetProperty("provider_standing").GetString()).ToList();
        Assert.Equal(new[] { "wins", "shadowed", "wins" }, standings);

        var file = Path.Combine(_w.Root, "provider-" + Guid.NewGuid().ToString("N") + ".jsonl");
        AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir }, provider: "FaceBase", to_file: file);
        var rows = File.ReadAllLines(file).Skip(1).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(new[] { "wins", "shadowed", "wins" }, rows.Select(r => r.GetProperty("provider_standing").GetString()));
    }

    /// <summary>With no provider named, a row is the row it always was: no standing line, no standing member, and
    /// the artifact's column is null.</summary>
    [Fact]
    public void WithNoProviderARowCarriesNoStanding()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0002.nif") });
        Assert.DoesNotContain("provider \"", text);
        Assert.Contains("(1 path selected)", text);

        var json = JsonDocument.Parse(AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0002.nif") }, format: "json")).RootElement;
        Assert.False(json.TryGetProperty("provider", out _));
        Assert.False(json.GetProperty("results")[0].TryGetProperty("provider_standing", out _));
    }

    /// <summary>The unanchored walk keeps the match bound: past it the call refuses, having stopped walking there.</summary>
    [Fact]
    public void AnUnanchoredGlobStillStopsAtTheMatchBound()
    {
        var prior = _w.Svc.AssetArea.MaxAssetPaths;
        _w.Svc.AssetArea.MaxAssetPaths = 2;
        try
        {
            var text = AssetTools.AssetStatus(_w.Svc, under: new[] { "**/*.nif" });

            Assert.Contains("at least 3 asset path(s)", text);
            Assert.Contains("2-path bound", text);
        }
        finally { _w.Svc.AssetArea.MaxAssetPaths = prior; }
    }
}
