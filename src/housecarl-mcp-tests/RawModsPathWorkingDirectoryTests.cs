using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The one raw-mods-path test that sets the process working directory, apart from the others so only it
/// runs serial (#903).</summary>
[Trait("tier", "integration")]
[Collection(SerialCollection.Name)]   // sets the process working directory, #903
public sealed class RawModsPathWorkingDirectoryTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public RawModsPathWorkingDirectoryTests(AssetSelectWorld w) => _w = w;

    /// <summary>The check reads a ROOTED path only. A Data-relative path is the normal address form, and resolving
    /// one against the server's working directory would refuse it outright in a session started inside a mod folder.
    /// </summary>
    [Fact]
    public void ADataRelativePathIsNotAMistakenRawModsPath()
    {
        var prior = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(Path.Combine(_w.ModsDir, "FaceBase"));
        try
        {
            var text = NifTools.NifInspect(_w.Svc, mesh_paths: new[] { _w.Rel("0001.nif") });

            Assert.DoesNotContain("raw path into MO2's mods folder", text);
        }
        finally { Directory.SetCurrentDirectory(prior); }
    }
}
