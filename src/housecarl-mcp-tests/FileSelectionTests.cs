using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="AssetSelectWorld"/> plus what a profile holds but does not load: a mod folder left unticked,
/// with a loose file and its own archive, and a separator.</summary>
public sealed class UnloadedProviderWorld : IDisposable
{
    public AssetSelectWorld W { get; } = new();

    public const string UntickedMod = "HcUnticked";
    public const string UntickedArchive = "HcUnticked.bsa";
    public const string Separator = "Hc Extras_separator";

    public UnloadedProviderWorld()
    {
        var unticked = Path.Combine(W.ModsDir, UntickedMod);
        Directory.CreateDirectory(Path.Combine(unticked, "meshes", "hcunticked"));
        File.WriteAllText(Path.Combine(unticked, "meshes", "hcunticked", "x.nif"), "x");
        File.WriteAllBytes(Path.Combine(unticked, UntickedArchive),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[] { (@"meshes\hcunticked", new[] { ("y.nif", BsaBuilder.Bytes("NIF-Y", 48)) }) }));
        var separator = Path.Combine(W.ModsDir, Separator);
        Directory.CreateDirectory(separator);
        File.WriteAllText(Path.Combine(separator, "meta.ini"), "[General]\r\n");
        File.AppendAllText(Path.Combine(W.ProfileDir, "modlist.txt"), "-" + UntickedMod + "\r\n+" + Separator + "\r\n");
    }

    public void Dispose() => W.Dispose();
}

/// <summary>The shared file selection (#1318): <see cref="FileSelection"/> selects what asset_status selects, through
/// the view its caller captured, and refuses a provider the order does not load with asset_status's one sentence.</summary>
[Trait("tier", "unit")]
public sealed class FileSelectionTests : IClassFixture<UnloadedProviderWorld>
{
    readonly AssetSelectWorld _w;
    public FileSelectionTests(UnloadedProviderWorld u) => _w = u.W;

    static string Refused(string name)
        => $"error: provider '{name}' is not in the active load order — name an enabled mod folder, 'overwrite', 'Data' " +
           "or an active archive's filename, as a row's providers line spells it.";

    /// <summary>A mod folder on disk the profile leaves unticked, that mod's own archive, and a separator each get the
    /// sentence a name nothing has gets, word for word, with nothing selected (dev: C01, C03, C05).</summary>
    [Theory]
    [InlineData(UnloadedProviderWorld.UntickedMod)]
    [InlineData(UnloadedProviderWorld.UntickedArchive)]
    [InlineData(UnloadedProviderWorld.Separator)]
    public void AProviderTheOrderDoesNotLoadIsRefusedWithTheOneSentence(string name)
    {
        Assert.Equal(Refused(name), AssetTools.AssetStatus(_w.Svc, provider: name));
        Assert.Equal(Refused(name), AssetTools.AssetStatus(_w.Svc, under: new[] { @"meshes\hcunticked" }, provider: name));

        var capture = _w.Svc.AssetArea.CaptureForSelection(null, null);
        var chosen = FileSelection.Select(capture, Array.Empty<string>(), null, " " + name + " ", null);
        Assert.Equal(Refused(name), chosen.Refusal);
        Assert.Empty(chosen.Entries);
        Assert.Empty(chosen.Notes);
    }

    /// <summary>The same name as a plain under= selector still answers: the unticked mod's files are not in the order,
    /// so the glob matches nothing, and only provider= refuses.</summary>
    [Fact]
    public void TheUntickedModsFilesAreNotSelectedWithoutAProvider()
    {
        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { @"meshes\hcunticked" });

        Assert.Null(d.Refusal);
        Assert.Empty(d.Results);
        Assert.Contains("matched no file in the active load order", Assert.Single(d.SelectorNotes!));
    }

    static readonly string[] Paths = { @" meshes\hcnothing.nif ", @"..\escape.nif", AssetSelectWorld.FaceGeomDir + @"\0001.nif" };
    static readonly string[] Under = { AssetSelectWorld.FaceGeomDir, "", @"meshes\hcnothing", "[" };
    static FaceGenSeed[] Seeds() => new[]
    {
        new FaceGenSeed(AssetSelectWorld.SplitFormId, FormKey.Factory(AssetSelectWorld.SplitFormId), null),
        new FaceGenSeed("nope", null, "'nope' is not a FormID."),
        new FaceGenSeed("0F0F0F:" + AssetSelectWorld.PairMaster, FormKey.Factory("0F0F0F:" + AssetSelectWorld.PairMaster), null),
    };

    /// <summary>A second caller of the selection gets asset_status's own entries and notes, in its order, for one input
    /// with every selector kind: explicit paths good and bad, under= folders, misses, an empty and an invalid selector,
    /// formids= seeds that pair, fail to parse and name no record, and provider=.</summary>
    [Fact]
    public void ASecondCallerSelectsWhatAssetStatusSelects()
    {
        var status = _w.Svc.AssetStatus(Paths, Under, seeds: Seeds(), provider: " FaceBase ");

        var capture = _w.Svc.AssetArea.CaptureForSelection(Seeds(), null);
        var chosen = FileSelection.Select(capture, Paths, Under, " FaceBase ", null);

        Assert.Null(chosen.Refusal);
        Assert.Null(chosen.StoppedAt);
        Assert.Equal("FaceBase", chosen.Provider);
        Assert.Equal(status.Provider, chosen.Provider);
        Assert.Equal(status.SelectorNotes, chosen.Notes);
        Assert.Equal(status.Results.Select(r => (r.RelPath, r.FormId, r.Slot, r.PairPath)),
                     chosen.Entries.Select(e => (e.Path.Trim(), e.FormId, e.Slot, e.PairPath)));
        // Every kind landed: the bad seeds and bad path as their own rows, a pair, and the folder's matches.
        Assert.Contains(chosen.Entries, e => e.FormId == "nope" && e.Error is not null);
        Assert.Contains(chosen.Entries, e => e.FormId == "0F0F0F:" + AssetSelectWorld.PairMaster && e.Error is not null);
        Assert.Contains(chosen.Entries, e => e.FormId == AssetSelectWorld.SplitFormId && e.Slot == FaceGenSlot.Tint);
        Assert.Contains(status.Results, r => r.RelPath == @"..\escape.nif" && r.Error is not null);
        Assert.Contains(chosen.Entries, e => string.Equals(e.Path, _w.Rel("0003.nif"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, chosen.Notes.Count);
    }

    /// <summary>The selection lists nothing beyond what asset_status's call lists today: over a fresh world each, a
    /// capture and a selection take exactly the listings an asset_status call takes for the same input when its window
    /// is past the end (so it resolves no row: the same span, capture and selection), and run again over the same view
    /// the selection takes none.</summary>
    [Fact]
    public void TheSelectionListsNoMoreThanAssetStatusDoes()
    {
        using var statusWorld = new AssetSelectWorld();
        var status = statusWorld.Svc.AssetStatus(Paths, Under, limit: 1, offset: int.MaxValue, seeds: Seeds());
        var statusListed = ((ILoadOrderHost)statusWorld.Svc).Assets.WarmListingCount;

        using var selectWorld = new AssetSelectWorld();
        var capture = selectWorld.Svc.AssetArea.CaptureForSelection(Seeds(), null);
        var resolver = ((ILoadOrderHost)selectWorld.Svc).Assets;
        var atCapture = resolver.WarmListingCount;
        var first = FileSelection.Select(capture, Paths, Under, null, null);
        var selectListed = resolver.WarmListingCount;
        var again = FileSelection.Select(capture, Paths, Under, null, null);

        Assert.Empty(status.Results);
        Assert.Equal(first.Entries.Count, status.Total);
        Assert.True(selectListed > atCapture, "the under= folders are listed through the view");
        Assert.Equal(statusListed, selectListed);
        Assert.Equal(selectListed, resolver.WarmListingCount);
        Assert.Equal(first.Entries, again.Entries);
        Assert.Equal(first.Notes, again.Notes);
    }
}
