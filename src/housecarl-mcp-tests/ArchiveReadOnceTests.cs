using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Each lane that reads archive bytes or probes an archive reads that archive's table once per call, however
/// many of its entries it reads (#1311). Every count here is two or more entries out of one archive in one call.</summary>
[Trait("tier", "integration")]
public sealed class ArchiveReadOnceTests : IDisposable
{
    const string MeshA = @"meshes\hcro\a.nif", MeshB = @"meshes\hcro\b.nif";
    const string PexA = @"scripts\hcroa.pex", PexB = @"scripts\hcrob.pex";
    const string DllA = @"skse\plugins\hcroa.dll", DllB = @"skse\plugins\hcrob.dll";
    const string IniA = @"skse\plugins\hcroa.ini", IniB = @"skse\plugins\hcrob.ini";

    readonly PlaceInstance _p = new();

    public void Dispose() => _p.Dispose();

    /// <summary>An active archive, Dummy.bsa in the mod Arch, holding <paramref name="paths"/>; the service over it.</summary>
    LoadOrderService Instance(params string[] paths)
    {
        var folders = paths.GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(p => (Path.GetFileName(p), BsaBuilder.Bytes(p, 32))).ToArray())).ToArray();
        File.WriteAllBytes(Path.Combine(_p.Mod("Arch"), "Dummy.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, folders));
        _p.ProfileWithDummy("Arch", "+Arch");
        return _p.Open();
    }

    /// <summary>How many archive tables <paramref name="act"/> read for bytes and probes.</summary>
    static int TablesRead(LoadOrderService svc, Action act)
    {
        var assets = ((ILoadOrderHost)svc).Assets;
        var before = assets.ArchiveTableReadCount;
        act();
        return ((ILoadOrderHost)svc).Assets.ArchiveTableReadCount - before;
    }

    [Fact]
    public void AWalkReadsItsMeshesAndScriptsOutOfOneArchiveOnce()
    {
        var svc = Instance(MeshA, MeshB, PexA, PexB);
        Assert.Equal(1, TablesRead(svc, () => svc.AssetWalk(new[] { MeshA, MeshB, PexA, PexB }, Array.Empty<FormKey>(), null, null)));
    }

    [Fact]
    public void AWalkNamingTheArchivesModProbesAndReadsItOnce()
    {
        var svc = Instance(MeshA, MeshB);
        Assert.Equal(1, TablesRead(svc, () => svc.AssetWalk(new[] { MeshA, MeshB }, Array.Empty<FormKey>(), null, "Arch")));
    }

    [Fact]
    public void LinksOnSeveralPackedMeshesReadTheArchiveOnce()
    {
        var svc = Instance(MeshA, MeshB);
        Assert.Equal(1, TablesRead(svc, () => svc.AssetStatus(new[] { MeshA, MeshB }, links: true)));
    }

    [Fact]
    public void ANifInspectBatchReadsTheArchiveOnce()
    {
        var svc = Instance(MeshA, MeshB);
        Assert.Equal(1, TablesRead(svc, () => svc.NifInspect(new[] { MeshA, MeshB }, null)));
    }

    [Fact]
    public void NativePairingReadsPackedDllsAndScriptsOutOfOneArchiveOnce()
    {
        var svc = Instance(DllA, DllB, PexA, PexB);
        Assert.Equal(1, TablesRead(svc, () => svc.NativePairingAudit()));
    }

    [Fact]
    public void TheConfigAuditReadsPackedConfigsOutOfOneArchiveOnce()
    {
        var svc = Instance(IniA, IniB);
        Assert.Equal(1, TablesRead(svc, () => svc.SkseConfigAudit()));
    }

    [Fact]
    public void APlaceBatchReadsEachArchiveOnceForResolvedAndNamedEntries()
    {
        var svc = Instance(MeshA, MeshB);
        var raw = _p.Scratch("Raw.bsa", File.ReadAllBytes(Path.Combine(_p.Mods, "Arch", "Dummy.bsa")));
        PlaceOutcome outcome = null!;
        // Dummy.bsa resolved and by name, Raw.bsa by its path twice: one table each.
        Assert.Equal(2, TablesRead(svc, () => outcome = svc.PlaceAssets(new[]
        {
            new PlaceRequest(MeshA, null),
            new PlaceRequest(@"meshes\hcro\named.nif", MeshB, "Dummy.bsa"),
            new PlaceRequest(@"meshes\hcro\rawa.nif", raw + "|" + MeshA),
            new PlaceRequest(@"meshes\hcro\rawb.nif", raw + "|" + MeshB),
        }, "HcroPlace", null)));
        Assert.All(outcome.Results, r => Assert.True(r.Placed, r.Error));
    }

    [Fact]
    public void AVoiceCarryReadsOneArchiveOnceForEveryLine()
    {
        var lines = new[] { 0x801u, 0x802u, 0x803u };
        var voice = lines.Select(id => $@"sound\voice\hcrosrc.esp\malenord\hcro_{id:X8}_1.fuz").ToArray();
        var bsa = Path.Combine(_p.Root, "Voice.bsa");
        File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new[]
            { (Path.GetDirectoryName(voice[0])!, voice.Select(v => (Path.GetFileName(v), BsaBuilder.Bytes(v, 24))).ToArray()) }));
        using var assets = AssetResolver.Build("", "", _p.Data, Array.Empty<string>(),
            new[] { new ActiveArchive(bsa, "HcroSrc.esp", 0) });
        var map = lines.ToDictionary(id => new FormKey(ModKey.FromFileName("HcroSrc.esp"), id),
                                     id => new FormKey(ModKey.FromFileName("HcroOut.esp"), id + 0x100));
        var view = assets.Capture();

        var outcome = AssetRenameService.CarryVoice(Path.Combine(_p.Root, "HcroOut.esp"), map, view,
                                                    Path.Combine(_p.Root, "out"), sourcePlugin: "HcroSrc.esp");

        Assert.Equal(3, outcome.FilesCarried);
        Assert.Equal(1, view.ArchiveTablesRead);
    }

    /// <summary>An archive that will not open is tried once for the view and answers every read with the same sentence.</summary>
    [Fact]
    public void AnUnreadableArchiveIsTriedOnceAndSaysTheSameEachRead()
    {
        var junk = _p.Scratch("Junk.bsa", BsaBuilder.Bytes("not an archive", 64));
        using var assets = AssetResolver.Build("", "", _p.Data, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        var view = assets.Capture();
        var source = new PlacementSource("Junk.bsa", AssetKind.Bsa, null, junk, MeshA);

        var first = view.ReadSource(source).Error;
        Assert.StartsWith("could not read archive 'Junk.bsa': ", first);
        Assert.Equal(first, view.ReadSource(source with { EntryPath = MeshB }).Error);
        Assert.Equal(1, view.ArchiveTablesRead);
    }

    /// <summary>Pins the re-level pick (DECISIONS 2026-11-05 (13)): a view does not stat an archive again at each read,
    /// and the next view, after the build's freshness check, reads the archive as it is now (R18).</summary>
    [Fact]
    public void AViewReadsItsTableOnceAndTheNextCallReadsTheNewArchive()
    {
        var bsa = Path.Combine(_p.Root, "Fresh.bsa");
        void Write(string tag) => File.WriteAllBytes(bsa, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
            new[] { (Path.GetDirectoryName(MeshA)!, new[] { (Path.GetFileName(MeshA), BsaBuilder.Bytes(tag, 32)) }) }));
        Write("first");
        using var assets = AssetResolver.Build("", "", _p.Data, Array.Empty<string>(), new[] { new ActiveArchive(bsa, "Fresh.esp", 0) });
        var view = assets.Capture();
        Assert.NotNull(view.ReadEntry(bsa, MeshA));

        Write("second, longer");
        File.SetLastWriteTimeUtc(bsa, DateTime.UtcNow.AddMinutes(5));
        try { view.ReadEntry(bsa, MeshA); } catch { /* what a replaced archive reads mid-call is not promised */ }
        Assert.Equal(1, view.ArchiveTablesRead);

        Assert.True(assets.RefreshIfStale());
        Assert.Equal(BsaBuilder.Bytes("second, longer", 32), assets.Capture().ReadEntry(bsa, MeshA));
    }

    /// <summary>The asset note states the mid-call contract (R17) and the door's memory rule (R15).</summary>
    [Fact]
    public void TheAssetNoteStatesTheContractAndTheMemoryRule()
    {
        var note = File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, "docs", "architecture", "assets.md"));
        Assert.Contains("Existence is the pinned build; bytes are read when used.", note);
        Assert.Contains("each archive's table is read at most once per call, and no reader, table or\n  handle is kept between calls", note.Replace("\r\n", "\n"));
    }

    // A batch of one reads as the single read did: each sentence on its trigger, unchanged from main.

    [Fact]
    public void ABatchOfOneSaysAMissingEntryAsBefore()
    {
        var svc = Instance(MeshA);
        var view = ((ILoadOrderHost)svc).Assets.Capture();
        var bsa = Path.Combine(_p.Mods, "Arch", "Dummy.bsa");
        Assert.Equal(@"entry 'meshes\hcro\none.nif' not found inside 'Dummy.bsa'",
            view.ReadSource(new PlacementSource("Dummy.bsa", AssetKind.Bsa, null, bsa, @"meshes\hcro\none.nif")).Error);
        Assert.Equal(@"entry 'meshes\hcro\none.nif' not found inside '?'",
            view.ReadSource(new PlacementSource("Dummy.bsa", AssetKind.Bsa, null, null, @"meshes\hcro\none.nif")).Error);
    }

    [Fact]
    public void ABatchOfOneSaysAMissingLooseSourceAsBefore()
    {
        using var assets = AssetResolver.Build("", "", _p.Data, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        var gone = Path.Combine(_p.Data, "gone.nif");
        Assert.Equal($"the resolved loose source '{gone}' is no longer on disk",
            assets.Capture().ReadSource(new PlacementSource("Data", AssetKind.Loose, gone, null, "gone.nif")).Error);
    }

    [Fact]
    public void APlaceOfOneSaysEachArchiveFailureAsBefore()
    {
        var svc = Instance(MeshA);
        var missing = Path.Combine(_p.Root, "Missing.bsa");
        var junk = _p.Scratch("Junk.bsa", BsaBuilder.Bytes("not an archive", 64));
        var raw = _p.Scratch("Raw.bsa", File.ReadAllBytes(Path.Combine(_p.Mods, "Arch", "Dummy.bsa")));
        string Error(string source) => svc.PlaceAssets(new[] { new PlaceRequest(MeshB, source) }, "HcroOne", null).Results[0].Error!;

        Assert.Equal($"source archive not found: '{missing}'.", Error(missing + "|" + MeshA));
        Assert.Equal(@"entry 'meshes\hcro\none.nif' not found inside archive 'Raw.bsa'.", Error(raw + @"|meshes\hcro\none.nif"));
        Assert.StartsWith("could not read archive 'Junk.bsa': ", Error(junk + "|" + MeshA));
    }
}
