using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcp.Tests;

/// <summary>A world for the two tools whose patch= names an ARTIFACT whose basename is load-bearing: merge_plugins
/// (the merged plugin) and bsa_repack (the .bsa). One active donor plugin, a mods dir to collide in, a folder of
/// loose files to repack, and a saved BSArch path (a stub .exe — every assertion here refuses before BSArch would
/// run).</summary>
public sealed class PatchArtifactWorld : IDisposable
{
    public string Root { get; }
    public string Mods { get; }
    public string SourceFolder { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }

    public const string Donor = "HcArtDonor.esp";

    public PatchArtifactWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-patch-artifact-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        Mods = Path.Combine(instance, "mods");
        SourceFolder = Path.Combine(Root, "loose", "MeshesA");
        foreach (var d in new[] { profile, Mods, Path.Combine(Root, "game", "Data"), Path.Combine(SourceFolder, "meshes") })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(SourceFolder, "meshes", "thing.nif"), "not really a nif");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var key = new ModKey("HcArtDonor", ModType.Plugin);
        var dir = Path.Combine(Mods, "DonorMod");
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcArtDonorWeap" });
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Donor + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Donor + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+DonorMod\r\n");

        // A stub BSArch: Validate only asks for an existing .exe whose name carries 'bsarch', and every case here
        // refuses before the pack would run.
        var bsarch = Path.Combine(Root, "bsarch.exe");
        File.WriteAllText(bsarch, "");
        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        store.Update(c => c.ToolPaths = new Dictionary<string, string> { ["bsarch"] = bsarch });
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    /// <summary>A mod folder already sitting under the mods dir, houseCARL's or a user's — the collision both tools
    /// must refuse rather than suffix around.</summary>
    public string PlantFolder(string stem)
    {
        var folder = Path.Combine(Mods, "houseCARL - " + stem);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>patch= on merge_plugins and bsa_repack names an artifact whose EXACT basename is what resolves it — a
/// _DISTR.ini or a master entry for the merged plugin, the game's archive auto-load for the .bsa — so a collision
/// refuses by name, the way create_plugin's does, and nothing is written under an invented suffix.</summary>
[Trait("tier", "integration")]
public sealed class PatchArtifactCollisionTests : IClassFixture<PatchArtifactWorld>
{
    readonly PatchArtifactWorld _w;
    public PatchArtifactCollisionTests(PatchArtifactWorld w) => _w = w;

    [Fact]
    public void Merge_folder_collision_refuses_by_name_and_writes_nothing()
    {
        var folder = _w.PlantFolder("HcArtMerge");

        var o = _w.Svc.MergePlugins(new[] { PatchArtifactWorld.Donor }, "HcArtMerge");

        Assert.False(o.Success);
        Assert.Contains("houseCARL - HcArtMerge", o.Error);
        Assert.Contains("merged plugin", o.Error);
        Assert.DoesNotContain("HcArtMerge_001", o.Error);
        Assert.Empty(Directory.GetFileSystemEntries(folder));                      // the planted folder is untouched
        Assert.False(Directory.Exists(Path.Combine(_w.Mods, "houseCARL - HcArtMerge_001")));
    }

    [Fact]
    public void Repack_folder_collision_refuses_by_name_rather_than_renaming_the_archive()
    {
        _w.PlantFolder("HcArtBsa");

        var r = BsaTools.BsaRepack(_w.Svc, _w.Tools, _w.SourceFolder, patch: "HcArtBsa");

        Assert.StartsWith("error:", r);
        Assert.Contains("houseCARL - HcArtBsa", r);
        Assert.Contains(".bsa", r);
        Assert.False(Directory.Exists(Path.Combine(_w.Mods, "houseCARL - HcArtBsa_001")));
    }

    [Fact]
    public void Repack_refuses_an_archive_already_in_the_into_folder_instead_of_replacing_it()
    {
        var rf = _w.Svc.ResolvePatchModFolder("HcArtInto", into: null, "HcArtDefault", BsaTools.RepackNaming);
        var existing = Path.Combine(rf.OutputDir, "HcArtInto.bsa");
        File.WriteAllText(existing, "the first repack's archive");

        var r = BsaTools.BsaRepack(_w.Svc, _w.Tools, _w.SourceFolder, into: "HcArtInto");

        Assert.StartsWith("error:", r);
        Assert.Contains("HcArtInto.bsa", r);
        Assert.Equal("the first repack's archive", File.ReadAllText(existing));    // no clobber, no backup
    }

    [Fact]
    public void Repack_refuses_patch_and_into_together_naming_both()
    {
        var r = BsaTools.BsaRepack(_w.Svc, _w.Tools, _w.SourceFolder, patch: "HcArtNew", into: "HcArtOld");

        Assert.StartsWith("error:", r);
        Assert.Contains("HcArtNew", r);
        Assert.Contains("HcArtOld", r);
        Assert.Contains("exclusive", r);
        Assert.False(Directory.Exists(Path.Combine(_w.Mods, "houseCARL - HcArtNew")));
    }
}
