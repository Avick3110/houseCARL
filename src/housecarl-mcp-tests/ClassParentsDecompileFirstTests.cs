using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The decompiler's class-parent map when it is the first thing a fresh instance-mode service is asked for:
/// the instance paths derive before the map is built, so the mods tree's script headers are in it from the first call
/// and stay in it after the paths are derived again.</summary>
[Trait("tier", "unit")]
public sealed class ClassParentsDecompileFirstTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-class-parents-first-" + Guid.NewGuid().ToString("N"));
    readonly string _instance;
    readonly string _mods;

    public ClassParentsDecompileFirstTests()
    {
        _instance = Path.Combine(_root, "instance");
        var profile = Path.Combine(_instance, "profiles", "Default");
        _mods = Path.Combine(_instance, "mods");
        var source = Path.Combine(_mods, "SourceMod", "scripts", "source");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        File.WriteAllText(Path.Combine(_instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+SourceMod\r\n");
        File.WriteAllText(Path.Combine(source, "HcGuardChild.psc"), "ScriptName HcGuardChild extends HcGuardParent\r\n");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    LoadOrderService Service(string user) =>
        LoadOrderService.WithInstance(_instance, 0, new UserConfigStore(Path.Combine(_root, user)));

    // Probe: "FIRST call already sees the mods-tree edge (paths derive before the build)", "output folder resolves under
    // the instance's mods dir", "the cache stays correct after derivation (no poisoned survivor)".
    [Fact]
    public void AFirstCallBeforeAnyPathDerivationAlreadySeesTheModsTreeEdge()
    {
        using var svc = Service("userA.json");

        Assert.Equal("HcGuardParent", svc.ClassParentsForDecompile().Edges.GetValueOrDefault("HcGuardChild"));
        Assert.StartsWith(_mods, svc.ResolveDecompiledSourceFolder(null, null).OutputDir, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("HcGuardParent", svc.ClassParentsForDecompile().Edges.GetValueOrDefault("HcGuardChild"));
    }

    // Probe: "derive-first still sees the mods-tree edge".
    [Fact]
    public void DerivingThePathsFirstStillSeesTheModsTreeEdge()
    {
        using var svc = Service("userB.json");
        svc.ResolveDecompiledSourceFolder(null, null);

        Assert.Equal("HcGuardParent", svc.ClassParentsForDecompile().Edges.GetValueOrDefault("HcGuardChild"));
    }
}
