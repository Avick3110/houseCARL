using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>ResolveExplicitScriptFolder</c>, the compile rider's <c>out_path=</c> lane (migrated from the
/// <c>compile-ergonomics-guard</c> probe, part B3): the folder is the caller's, no houseCARL patch folder is cut, and
/// residue cleanup never deletes it.
/// </summary>
[Trait("tier", "unit")]
public sealed class ExplicitScriptFolderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-explicit-script-folder-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;
    readonly LoadOrderService _svc;

    public ExplicitScriptFolderTests()
    {
        _mods = Path.Combine(_root, "mods");
        var data = Path.Combine(_root, "game", "Data");
        Directory.CreateDirectory(_mods);
        Directory.CreateDirectory(data);
        _svc = LoadOrderService.WithExplicitPaths(data, _mods, "", 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    string Outside => Path.Combine(_root, "elsewhere", "MyMod");

    // Probe B3: "output path = out_path\Scripts (the chosen contract)" and "the Scripts\ folder is created under out_path".
    [Fact]
    public void TheOutputIsOutPathScriptsAndItIsCreated()
    {
        var rf = _svc.ResolveExplicitScriptFolder(Outside, out _);
        Assert.Equal(Path.Combine(Outside, "Scripts"), rf.OutputDir);
        Assert.True(Directory.Exists(rf.OutputDir));
    }

    // Probe B3: "the folder is USER-OWNED (CreatedFresh=false), so residue cleanup never deletes it".
    [Fact]
    public void TheFolderIsUserOwned()
    {
        Assert.False(_svc.ResolveExplicitScriptFolder(Outside, out _).CreatedFresh);
    }

    // Probe B3: "ResolvePatchModFolder was NOT called — no houseCARL patch folder cut under ModsDir".
    [Fact]
    public void NoPatchFolderIsCutUnderTheModsDir()
    {
        _svc.ResolveExplicitScriptFolder(Outside, out _);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_mods));
    }

    // Probe B3: "out_path outside the mods tree carries the deploy warning".
    [Fact]
    public void AnOutPathOutsideTheModsTreeWarns()
    {
        _svc.ResolveExplicitScriptFolder(Outside, out var warn);
        Assert.Contains("deploy", warn);
    }

    // Probe B3: "residue cleanup never deletes a user-owned out_path folder (CreatedFresh=false: returns null, folder survives)".
    [Fact]
    public void ResidueCleanupLeavesTheUserFolderStanding()
    {
        var rf = _svc.ResolveExplicitScriptFolder(Outside, out _);
        Assert.Null(_svc.RemoveOrNameRiderResidue(rf));
        Assert.True(Directory.Exists(rf.OutputDir));
    }

    // Probe B3: "an out_path under the MO2 mods folder deploys cleanly (no warning)".
    [Fact]
    public void AnOutPathUnderTheModsFolderDoesNotWarn()
    {
        var rf = _svc.ResolveExplicitScriptFolder(Path.Combine(_mods, "MyPatch"), out var warn);
        Assert.Equal(Path.Combine(_mods, "MyPatch", "Scripts"), rf.OutputDir);
        Assert.Null(warn);
    }

    // Probe B3: "a file at <out_path>\Scripts yields a friendly InvalidOperationException, not a raw IOException".
    [Fact]
    public void AFileWhereScriptsShouldGoIsAPlainRefusal()
    {
        var coll = Path.Combine(_root, "collision");
        Directory.CreateDirectory(coll);
        File.WriteAllText(Path.Combine(coll, "Scripts"), "a file where the Scripts folder should go");
        var ex = Assert.Throws<InvalidOperationException>(() => _svc.ResolveExplicitScriptFolder(coll, out _));
        Assert.Contains("couldn't create", ex.Message);
    }
}
