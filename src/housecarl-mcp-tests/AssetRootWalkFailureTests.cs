using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A loose root the resolver cannot read is NAMED in the answer (#820): it sets read_incomplete and the
/// render says which root was not read, instead of an answer that silently omits what it could not reach. Both the
/// shapes a denial takes are covered — a blocked subdirectory under a readable mod, and a blocked mod folder, whose
/// subtrees do not even stat.</summary>
[Trait("tier", "integration")]
public sealed class AssetRootWalkFailureTests : IDisposable
{
    readonly UnreadableRootWorld _w = new();

    public void Dispose() => _w.Dispose();

    static string Named(AssetStatusData d) => string.Join(" | ", d.RootFailures);

    [Fact]
    public void ALooseRootThatThrowsMidWalkIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { UnreadableRootWorld.SweepDir });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, Named(d));
        // The render is where the modder reads it: the root is named above the rows the walk did return.
        Assert.Contains("could NOT be read this build", AssetWire.Render(d, 80_000));
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, AssetWire.Render(d, 80_000));
    }

    /// <summary>The single-path lane rides a different read — the per-subtree listing, not the recursive walk — and
    /// names the root the same way, so an ABSENT on a blocked root is hedged rather than stated flat.</summary>
    [Fact]
    public void AnExplicitPathUnderAnUnreadableRootIsNamedInTheAnswer()
    {
        Assert.True(_w.Denied, UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(new[] { UnreadableRootWorld.SubBlockedPath });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.SubBlockedMod, Named(d));
        Assert.Contains("could NOT be read this build", AssetWire.Render(d, 80_000));
    }

    /// <summary>A mod folder blocked at its TOP never throws: its subtrees do not stat, so Directory.Exists answers
    /// "not there" and the walk never starts. The sweep must still name it — the common permissions shape, where a
    /// whole folder belongs to another account.</summary>
    [Fact]
    public void TheSweepNamesAModFolderBlockedAtItsTop()
    {
        Assert.True(_w.TopDenied, UnreadableRootWorld.NotStaged);
        // The precondition the redesign turns on: nothing throws here, the directory simply does not stat.
        Assert.False(Directory.Exists(_w.TopSweepDir), UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(Array.Empty<string>(), new[] { UnreadableRootWorld.SweepDir });

        Assert.True(d.ReadIncomplete);
        Assert.Contains(UnreadableRootWorld.TopBlockedMod, Named(d));
    }

    /// <summary>The single-path lane names it too, over a subtree no sweep in this class touches, so the assert
    /// stands on its own entry rather than one a sweep left on the build.</summary>
    [Fact]
    public void TheSinglePathLaneNamesAModFolderBlockedAtItsTop()
    {
        Assert.True(_w.TopDenied, UnreadableRootWorld.NotStaged);
        Assert.False(Directory.Exists(_w.TopTextureDir), UnreadableRootWorld.NotStaged);

        var d = _w.Svc.AssetStatus(new[] { UnreadableRootWorld.TopBlockedTexture });

        Assert.True(d.ReadIncomplete);
        var named = Assert.Single(d.RootFailures);              // ONE entry, and it is this lane's own
        Assert.Contains(UnreadableRootWorld.TopBlockedMod, named);
        Assert.Contains(UnreadableRootWorld.TextureDir, named);
    }
}

/// <summary>The other half of the rule: an absence the resolver CAN prove stays silent. Two shapes look like an
/// unreadable directory from a distance — a path that names a file, and a directory deleted after its parent's
/// listing was cached for the build — and both must answer as plain absence, never as a root failure a modder has no
/// way to clear. Driven straight at <see cref="AssetResolver"/>, because what is under test is the build's own memory.</summary>
[Trait("tier", "integration")]
public sealed class AssetProvedAbsenceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-absence-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;
    readonly string _mod;

    public AssetProvedAbsenceTests()
    {
        _mods = Path.Combine(_root, "mods");
        _mod = Path.Combine(_mods, "AssetMod");
        Directory.CreateDirectory(_mod);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    AssetResolver Build() =>
        AssetResolver.Build(overwriteDir: "", _mods, dataDir: "", new[] { "AssetMod" }, Array.Empty<ActiveArchive>());

    /// <summary>A selector that names a FILE is a supported shape (asset_status takes a pasted path under under=).
    /// Every root that provides it lists the name and stats it as no directory, which is "no such directory here",
    /// not "a root that would not read".</summary>
    [Fact]
    public void ASelectorThatNamesAFileRaisesNoRootFailure()
    {
        Directory.CreateDirectory(Path.Combine(_mod, "meshes"));
        File.WriteAllText(Path.Combine(_mod, @"meshes\a.nif"), "x");

        using var r = Build();
        r.EnumerateUnder(@"meshes\a.nif");

        Assert.Empty(r.RootFailures);
        Assert.False(r.ReadIncomplete);
    }

    /// <summary>A directory holding no files is in no warmed subtree and carries no stamp, so deleting it moves
    /// nothing the freshness check watches. Asked about afterwards it must read as absent: the parent listing the
    /// build cached earlier is not evidence that it is still there.</summary>
    [Fact]
    public void ADirectoryDeletedAfterItsParentWasListedIsAbsent()
    {
        var meshes = Path.Combine(_mod, "meshes");
        var empty = Path.Combine(meshes, "hcempty");
        Directory.CreateDirectory(empty);

        using var r = Build();
        r.EnumerateUnder(@"meshes\hcnothere");        // matches nothing, and caches the meshes\ listing holding hcempty

        Directory.Delete(empty, true);

        r.EnumerateUnder(@"meshes\hcempty");

        Assert.Empty(r.RootFailures);
        Assert.False(r.ReadIncomplete);
    }
}

/// <summary>Its own instance, not a shared fixture: it denies itself access to mod folders, which a world other
/// tests read must never carry. One mod provides a readable file under the sweep folder; a second blocks a
/// subdirectory of its copy; a third blocks its whole mod folder.</summary>
sealed class UnreadableRootWorld : IDisposable
{
    /// <summary>The folder the sweep is aimed at — provided by all three mods below.</summary>
    public const string SweepDir = @"meshes\hcwalk";

    /// <summary>The mod whose copy of <see cref="SweepDir"/> holds a subdirectory that will not enumerate.</summary>
    public const string SubBlockedMod = "SubBlockedMod";

    /// <summary>The mod whose whole folder is blocked, so nothing under it even stats.</summary>
    public const string TopBlockedMod = "TopBlockedMod";

    /// <summary>A file that IS on disk inside the unreadable subtree — what the single-path lane asks about.</summary>
    public const string SubBlockedPath = SweepDir + @"\locked\hidden.nif";

    /// <summary>A file that IS on disk inside the blocked mod folder, under the folder the sweep covers.</summary>
    public const string TopBlockedPath = SweepDir + @"\top.nif";

    /// <summary>A second subtree inside the blocked mod folder that no sweep here touches, so the single-path lane
    /// asserts on an entry only it can have written.</summary>
    public const string TextureDir = @"textures\hcwalk";

    /// <summary>The file the single-path lane asks about, inside that second subtree.</summary>
    public const string TopBlockedTexture = TextureDir + @"\top.dds";

    /// <summary>What a test says when the host would not let the fixture be built — never a quiet pass.</summary>
    public const string NotStaged = "the deny ACE did not bite on this host, so the unreadable root was never staged";

    public string Root { get; }
    public LoadOrderService Svc { get; }   // set once, inside the constructor's try

    /// <summary>Whether each deny ACE actually took on this host — a fixture that did not build is a failure, never a pass.</summary>
    public bool Denied { get; }
    public bool TopDenied { get; }

    /// <summary>The blocked mod's copy of the sweep folder — the stat that must fail for the redesign to be exercised.</summary>
    public string TopSweepDir { get; }

    /// <summary>The blocked mod's copy of the single-path subtree, same precondition.</summary>
    public string TopTextureDir { get; }

    readonly string _lockedDir;
    readonly string _topDir;

    public UnreadableRootWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-unreadable-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(mods, "PluginMod");
        var assetMod = Path.Combine(mods, "AssetMod");
        _topDir = Path.Combine(mods, TopBlockedMod);
        foreach (var d in new[] { profile, Path.Combine(Root, "game", "Data"), pluginMod, assetMod, _topDir })
            Directory.CreateDirectory(d);

        // Two masters, so the order resolves: an instance with no active plugin answers differently.
        foreach (var name in new[] { "HcWalkOne", "HcWalkTwo" })
        {
            var key = new ModKey(name, ModType.Master);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = name + "Weapon";
            mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        var readable = Path.Combine(assetMod, SweepDir);
        Directory.CreateDirectory(readable);
        File.WriteAllText(Path.Combine(readable, "ok.nif"), "x");

        // The blocked mod's two subtrees are written BEFORE any deny, so no failure below can leave an ACE behind.
        TopSweepDir = Path.Combine(_topDir, SweepDir);
        TopTextureDir = Path.Combine(_topDir, TextureDir);
        Directory.CreateDirectory(TopSweepDir);
        Directory.CreateDirectory(TopTextureDir);
        File.WriteAllText(Path.Combine(TopSweepDir, "top.nif"), "x");
        File.WriteAllText(Path.Combine(TopTextureDir, "top.dds"), "x");

        // One mod blocks a subdirectory of its own copy: the recursive walk starts and throws part way through.
        _lockedDir = Path.Combine(mods, SubBlockedMod, SweepDir, "locked");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "hidden.nif"), "x");

        // From the FIRST deny on, anything that throws leaves the object unbuilt and Dispose unrun, so an ACE would
        // outlive the fixture and block the temp tree's own cleanup. Take them off before the failure leaves here.
        try
        {
            Denied = DenyAce.TryDeny(_lockedDir);
            // The other mod blocks its whole folder: nothing under it stats, so no read ever starts.
            TopDenied = DenyAce.TryDeny(_topDir);
            Svc = Stage(instance, profile);
        }
        catch
        {
            DenyAce.Undeny(_lockedDir);
            DenyAce.Undeny(_topDir);
            throw;
        }
    }

    /// <summary>The profile files and the service, in one place so the constructor can undo the denies if this throws.</summary>
    LoadOrderService Stage(string instance, string profile)
    {
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcWalkOne.esm\r\nHcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcWalkOne.esm\r\n*HcWalkTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + TopBlockedMod + "\r\n+" + SubBlockedMod + "\r\n+AssetMod\r\n+PluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        return LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }


    public void Dispose()
    {
        Svc.Dispose();
        DenyAce.Undeny(_lockedDir);
        DenyAce.Undeny(_topDir);
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
