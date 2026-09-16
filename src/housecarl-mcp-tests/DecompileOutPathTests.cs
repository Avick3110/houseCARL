using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_decompile_script</c>'s <c>out_path=</c> lane: the .psc lands in the folder the caller
/// names, no mod folder is cut, and a call that also names a patch folder lands in out_path and is told so —
/// the same rule the compile and .seq lanes carry. Driven on the shared
/// <see cref="ScriptsWorld"/>, whose Scripts folder already holds real .pex files; the out_path lane writes
/// outside the instance, so the world stays frozen.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class DecompileOutPathTests
{
    readonly ScriptsWorld W;
    public DecompileOutPathTests(ScriptsFixture f) => W = f.W;

    string Pex => Path.Combine(W.ScriptsDir, ScriptsWorld.BaseScript + ".pex");

    static string FreshDir() => Path.Combine(Path.GetTempPath(), "hc-decompile-out-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OutPathWritesThePscThereAndCreatesTheFolderItNames()
    {
        var dest = Path.Combine(FreshDir(), "sources");   // does not exist yet: out_path= creates it
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, Pex, out_path: dest);

            var psc = Path.Combine(dest, ScriptsWorld.BaseScript + ".psc");
            Assert.True(File.Exists(psc), r);
            Assert.Contains(psc, r);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dest)!, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathCutsNoModFolder()
    {
        var dest = FreshDir();
        var before = Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        try
        {
            DecompileTools.DecompileScript(W.Svc, Pex, out_path: dest);

            Assert.Equal(before, Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void ARelativeOutPathIsRefusedAndSaysToPassAnAbsoluteOne()
    {
        var r = DecompileTools.DecompileScript(W.Svc, Pex, out_path: "sources");

        Assert.StartsWith("error:", r);
        Assert.Contains("absolute", r);
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "sources")));
    }

    [Fact]
    public void ARefusalCarryingAnIgnoredPatchStillOpensWithErrorAndStatesTheIgnoredLane()
    {
        // The note is appended, never prepended: a caller (and every refusal test on this surface) reads the first
        // word, and "note:" in front of an error hides that the call failed.
        var dest = FreshDir();
        try
        {
            var r = DecompileTools.DecompileScript(
                W.Svc, Path.Combine(W.ScriptsDir, "HcSpNoSuchScript.pex"), patch: "HcIgnoredPatch", out_path: dest);

            Assert.StartsWith("error:", r);
            Assert.Contains("patch=/into= were ignored", r);
            Assert.Contains("nothing was written", r);
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathRunsWithNoInstanceConfiguredAndSaysTheHierarchyIsTheBaseline()
    {
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "psc");
        try
        {
            using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(unconfigured, Pex, out_path: dest);

            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Contains("no MO2 instance is configured", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void AConfiguredInstanceThatDoesNotResolveStillSaysTheHierarchyIsTheBaselineOnly()
    {
        // Configured is not the same as usable: the instance folder is gone, so the mods-tree top-up cannot run.
        // The degraded hierarchy is stated off what the build reports, not off whether an instance was named.
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "psc");
        try
        {
            using var broken = LoadOrderService.WithInstance(
                Path.Combine(dir, "no-such-instance"), 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(broken, Pex, out_path: dest);

            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Contains("the mods-tree sources were not read", r);
            Assert.Contains("does not resolve", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void WithoutOutPathNoInstanceConfiguredStillAsksForTheInstance()
    {
        var dir = FreshDir();
        Directory.CreateDirectory(dir);
        try
        {
            using var unconfigured = LoadOrderService.WithInstance(null, 0, new UserConfigStore(Path.Combine(dir, "user.json")));

            var r = DecompileTools.DecompileScript(unconfigured, Pex);

            Assert.Contains("Mod Organizer 2 instance", r);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup */ } }
    }

    [Fact]
    public void OutPathSupersedesIntoAndTheResponseSaysSo()
    {
        var dest = FreshDir();
        var before = Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, Pex, into: W.PluginName, out_path: dest);

            Assert.Contains("patch=/into= are ignored", r);
            Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
            Assert.Equal(before, Directory.GetDirectories(W.ModsDir).OrderBy(d => d, StringComparer.Ordinal).ToArray());
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }
}
