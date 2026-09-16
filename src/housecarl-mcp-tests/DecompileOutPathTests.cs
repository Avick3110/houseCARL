using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_decompile_script</c>'s <c>out_path=</c> lane: the .psc lands in the folder the caller
/// names, no mod folder is cut, and the two ways of naming a destination cannot be mixed. Driven on the shared
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
    public void OutPathTogetherWithIntoIsRefused()
    {
        var dest = FreshDir();
        try
        {
            var r = DecompileTools.DecompileScript(W.Svc, Pex, into: W.PluginName, out_path: dest);

            Assert.StartsWith("error:", r);
            Assert.Contains("out_path=", r);
            Assert.Contains("into=", r);
            Assert.False(Directory.Exists(dest));
        }
        finally { try { Directory.Delete(dest, true); } catch { /* temp cleanup */ } }
    }
}
