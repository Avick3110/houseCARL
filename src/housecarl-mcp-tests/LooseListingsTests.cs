using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One listing per loose folder per call (#1310): whatever a call lists, it resolves the same way, and a
/// change on disk is seen by the next call. A "call" here is one <see cref="AssetResolver.Capture"/> pass, or every
/// pass of one <see cref="AssetResolver.Frozen"/> copy, as the service holds one per call.</summary>
[Trait("tier", "integration")]
public sealed class LooseListingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-listings-" + Guid.NewGuid().ToString("N"));
    readonly string _ow, _mods, _data, _modA;

    public LooseListingsTests()
    {
        _ow = Path.Combine(_root, "overwrite"); _mods = Path.Combine(_root, "mods"); _data = Path.Combine(_root, "Data");
        _modA = Path.Combine(_mods, "ModA");
        Put(_modA, @"meshes\x\a.nif");
        Put(_modA, @"SKSE\Plugins\SkyPatcher\weapon\ModA.ini");
        Put(_modA, @"SKSE\Plugins\SkyPatcher.dll");
        Put(_modA, "ModA.esp");
        Directory.CreateDirectory(_ow); Directory.CreateDirectory(_data);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    static void Put(string folder, string rel)
    {
        var p = Path.Combine(folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    AssetResolver Build() => AssetResolver.Build(_ow, _mods, _data, new[] { "ModA" }, Array.Empty<ActiveArchive>());

    // ---- a listing and a resolve in one call agree ----

    [Fact]
    public void AFileAddedAfterAnEarlierCallWarmedItsFolderIsListedAndResolvedPresent()
    {
        using var r = Build();
        Assert.True(r.Resolve(@"meshes\x\a.nif").Exists);       // an earlier call warms meshes\x
        Put(_modA, @"meshes\x\b.nif");

        var view = r.Capture();                                  // no freshness check between, as under another call's write
        var listed = AssetGlob.Select(view, "meshes/x/*.nif");
        var census = view.ResolveMany(listed);

        Assert.Equal(2, listed.Count);
        Assert.All(census, h => Assert.True(h.Exists, h.RelPath));
    }

    [Fact]
    public void AFileRemovedAfterAnEarlierCallWarmedItsFolderIsNeitherListedNorResolved()
    {
        Put(_modA, @"meshes\x\b.nif");
        using var r = Build();
        Assert.True(r.Resolve(@"meshes\x\a.nif").Exists);
        File.Delete(Path.Combine(_modA, @"meshes\x\a.nif"));

        var view = r.Capture();
        Assert.Equal(new[] { @"meshes\x\b.nif" }, view.EnumerateUnder(@"meshes\x"));
        Assert.False(view.Resolve(@"meshes\x\a.nif").Exists);
        Assert.Empty(view.ResolveForPlacement(@"meshes\x\a.nif").Sources);
    }

    [Fact]
    public void AFileAddedAfterThisCallResolvedItsFolderIsNotListedEither()
    {
        using var r = Build();
        var view = r.Capture();
        Assert.True(view.Resolve(@"meshes\x\a.nif").Exists);
        Put(_modA, @"meshes\x\b.nif");

        Assert.Equal(new[] { @"meshes\x\a.nif" }, view.EnumerateUnder(@"meshes\x"));
        Assert.False(view.Resolve(@"meshes\x\b.nif").Exists);
    }

    [Fact]
    public void ARootFileListedForAModIsTheOneItsResolveSees()
    {
        using var r = Build();
        var view = r.Capture();
        Assert.True(view.Resolve("ModA.esp").Exists);            // this call lists the mod folder's top
        Put(_modA, "ModA_extra.esp");

        Assert.Equal(new[] { "ModA.esp" }, view.LooseRootFiles("ModA"));
        Assert.False(view.Resolve("ModA_extra.esp").Exists);

        var next = r.Capture();
        Assert.Contains("ModA_extra.esp", next.LooseRootFiles("ModA")!);
        Assert.True(next.Resolve("ModA_extra.esp").Exists);
    }

    [Fact]
    public void AListedSkyPatcherIniIsNeverCalledPresentOnlyInsideABsa()
    {
        using var r = Build();
        var view = r.Capture();
        SkyPatcherDiscovery.Scan(view, SkyPatcherCatalog.Load(), _ => true);
        Put(_modA, @"SKSE\Plugins\SkyPatcher\weapon\ModA_second.ini");

        var scan = SkyPatcherDiscovery.Scan(view, SkyPatcherCatalog.Load(), _ => true);
        var files = scan.Folders.SelectMany(f => f.Files).ToList();

        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.NotNull(f.WinningProvider));
    }

    [Fact]
    public void AListedVoiceFileIsCarriedNotSkipped()
    {
        const string voice = @"Sound\Voice\ModA.esp\MaleNord";
        Put(_modA, voice + @"\Topic_00000801_1.fuz");
        using var r = Build();
        Assert.True(r.Frozen().Capture().Resolve(voice + @"\Topic_00000801_1.fuz").Exists);   // an earlier call warms the folder
        Put(_modA, voice + @"\Topic_00000802_1.fuz");

        var outDir = Path.Combine(_root, "out");
        var key = ModKey.FromFileName("ModA.esp");
        var map = new Dictionary<FormKey, FormKey> { [new(key, 0x801)] = new(key, 0x901), [new(key, 0x802)] = new(key, 0x902) };
        var carry = AssetRenameService.CarryVoice(Path.Combine(_modA, "ModA.esp"), map, r.Frozen().Capture(), outDir);

        Assert.Empty(carry.Failures);
        Assert.Equal(2, carry.FilesCarried);
        Assert.True(File.Exists(Path.Combine(outDir, voice, "Topic_00000902_1.fuz")));
    }

    [Fact]
    public void AnEnabledFoldersOffOrderProbeAnswersOffTheCallsListing()
    {
        using var r = Build();
        var view = r.Capture();
        Assert.True(view.Resolve(@"meshes\x\a.nif").Exists);
        Put(_modA, @"meshes\x\late.nif");

        var late = view.TryResolveOffOrderProvider("ModA", @"meshes\x\late.nif");
        Assert.Null(late.Source);                                 // the call's listing of meshes\x does not hold it
        Assert.False(view.Resolve(@"meshes\x\late.nif").Exists);

        var held = view.TryResolveOffOrderProvider("ModA", @"meshes\x\a.nif");
        Assert.True(held.Source is { OwnerEnabled: true, Kind: AssetKind.Loose });
    }

    [Fact]
    public void AnEnabledFoldersRootArchiveAddedAfterTheCallListedItIsNotFound()
    {
        using var r = Build();
        var view = r.Capture();
        Assert.True(view.Resolve("ModA.esp").Exists);            // this call lists the mod folder's top
        File.WriteAllBytes(Path.Combine(_modA, "Late.bsa"), PlaceInstance.FaceArchive());

        Assert.Null(view.TryResolveOffOrderProvider("ModA", PlaceInstance.FacegenRel).Source);
        Assert.True(r.Capture().TryResolveOffOrderProvider("ModA", PlaceInstance.FacegenRel).Source is { OwnerEnabled: true, Kind: AssetKind.Bsa });
    }

    /// <summary>The cross-call route: a copy frozen for a call that took no freshness check (another call held the
    /// write gate) still lists and resolves as one read.</summary>
    [Fact]
    public void ACallThatSkippedTheFreshnessCheckStillListsAndResolvesAlike()
    {
        using var r = Build();
        Assert.True(r.Frozen().Capture().Resolve(@"meshes\x\a.nif").Exists);
        Put(_modA, @"meshes\x\b.nif");

        var call = r.Frozen();
        Assert.Contains(@"meshes\x\b.nif", call.Capture().EnumerateUnder(@"meshes\x"));
        Assert.True(call.Capture().Resolve(@"meshes\x\b.nif").Exists);
    }

    // ---- one directory, one listing per call ----

    [Fact]
    public void FourDataRootGlobsInOneCallTakeOneWalk()
    {
        for (int i = 0; i < 10; i++) Put(_modA, $@"textures\t{i}\a.dds");
        using var r = Build();
        var call = r.Frozen();
        var first = call.Capture();
        AssetGlob.Select(first, "**/*.nif");
        var once = first.ListingCount;
        Assert.True(once > 10, $"the walk listed {once} folders");

        foreach (var glob in new[] { "**/*.dds", "**/*.ini", "**/*.esp" }) AssetGlob.Select(call.Capture(), glob);

        Assert.Equal(once, call.Capture().ListingCount);
    }

    [Fact]
    public void TheFreshnessCheckListsIntoTheCallSoAWarmedFolderIsNotListedTwice()
    {
        using var r = Build();
        Assert.True(r.Resolve(@"meshes\x\a.nif").Exists);
        var listings = new LooseListings();

        Assert.False(r.RefreshIfStale(listings));
        var view = r.Frozen(listings).Capture();
        var checkedOnce = view.ListingCount;
        Assert.True(view.Resolve(@"meshes\x\a.nif").Exists);

        Assert.True(checkedOnce > 0);
        Assert.Equal(checkedOnce, view.ListingCount);
    }

    // ---- freshness between calls (pins unchanged behaviour) ----

    [Fact]
    public void AListingAloneWatchesNothingAndTheNextCallSeesAChangeUnderIt()
    {
        using var r = Build();
        var watched = r.WatchedDirectoryCount;
        Assert.Empty(r.Capture().EnumerateUnder("textures"));
        Assert.Equal(watched, r.WatchedDirectoryCount);

        Put(_modA, @"textures\t\new.dds");
        Assert.False(r.RefreshIfStale());
        Assert.Single(r.Capture().EnumerateUnder("textures"));

        File.Delete(Path.Combine(_modA, @"textures\t\new.dds"));
        Assert.Empty(r.Capture().EnumerateUnder("textures"));
    }
}

/// <summary>The same promises through the service: one tool call's lanes list and resolve alike, and a side lookup
/// joins its path with no file view (the "everything through the view-less join" pick).</summary>
[Trait("tier", "integration")]
public sealed class LooseListingsServiceTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-listings-svc-");
    readonly ModKey _base = new("HcLdBase", ModType.Plugin);
    readonly string _mod;
    readonly LoadOrderService _svc;

    public LooseListingsServiceTests()
    {
        new SkyrimMod(_base, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(_mo2.InMod("LdBase", _base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mod = Path.Combine(_mo2.ModsDir, "LdBase");
        Directory.CreateDirectory(Path.Combine(_mod, "SKSE", "Plugins"));
        File.WriteAllText(Path.Combine(_mod, "SKSE", "Plugins", "first.dll"), "not a real dll");
        File.WriteAllText(Path.Combine(_mod, "meta.ini"), "[General]\r\nmodid=1234\r\nversion=1.0\r\nnewestVersion=1.1\r\n");
        _mo2.Profile(_base.FileName + "\r\n", "*" + _base.FileName + "\r\n", "+LdBase\r\n");
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    bool FilesBuilt() => typeof(LoadOrderService).GetField("_assetResolver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_svc) is not null;

    static void Call(Action body)
    {
        Exception? failed = null;
        Guard.Tool("housecarl_probe", _ =>
        {
            try { body(); }
            catch (Exception e) { failed = e; }
            return ToolAnswer.Served("ok");
        });
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    static void OutsideTheCall(Action body)
    {
        Exception? failed = null;
        var t = new Thread(() => { try { Assert.Null(CallContext.Open); body(); } catch (Exception e) { failed = e; } });
        t.UnsafeStart();
        t.Join();
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
    }

    /// <summary>Another call warms SKSE\Plugins on the shared build after this call took its file view; a DLL then
    /// added is listed by this call's inventory and must not read as provided by no mod.</summary>
    [Fact]
    public void AListedSkseDllNeverReadsAsProvidedByNoMod()
    {
        Call(() =>
        {
            Assert.True(_svc.AssetStatus(new[] { _base.FileName.String }).Results[0].Hit!.Exists);   // the call's file view
            OutsideTheCall(() => Assert.Single(_svc.SkseInventory().Dlls));
            File.WriteAllText(Path.Combine(_mod, "SKSE", "Plugins", "second.dll"), "not a real dll");

            var dlls = _svc.SkseInventory().Dlls;
            Assert.Equal(2, dlls.Count);
            Assert.All(dlls, d => Assert.DoesNotContain("no active mod provides", d.Note ?? ""));
        });
    }

    /// <summary>The call's first file read runs the freshness check into the call's listings, so resolving a folder an
    /// earlier call warmed lists nothing again.</summary>
    [Fact]
    public void ACallsFreshnessCheckIsItsListing()
    {
        Call(() => Assert.Single(_svc.SkseInventory().Dlls));
        Call(() =>
        {
            var assets = ((ILoadOrderHost)_svc).Assets;
            var before = assets.Capture().ListingCount;
            Assert.True(before > 0, "the freshness check listed nothing into the call");
            Assert.True(assets.Capture().ResolveForPlacement(@"SKSE\Plugins\first.dll").Sources.Count == 1);
            Assert.Equal(before, assets.Capture().ListingCount);
        });
    }

    // Pins the pick (R8): update_status joins its meta.ini path with no file view; through the view it would build one.
    [Fact]
    public void UpdateStatusBuildsNoFileView()
    {
        var answer = UpdateStatusTools.UpdateStatus(_svc);
        Assert.Contains("LdBase", answer);
        Assert.False(FilesBuilt());
    }
}
