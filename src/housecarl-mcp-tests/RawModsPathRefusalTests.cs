using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A raw path into MO2's mods folder reads and writes past the virtual file system, so place and
/// nif_inspect refuse it and hand back the address form instead — the mod folder NAMED in source_provider=, with the
/// Data-relative path (#617). Nothing is placed by any of these arms, so the shared world is untouched.</summary>
[Trait("tier", "integration")]
public sealed class RawModsPathRefusalTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public RawModsPathRefusalTests(AssetSelectWorld w) => _w = w;

    string InFaceBase(string rel) => Path.Combine(_w.ModsDir, "FaceBase", rel);

    [Fact]
    public void PlaceRefusesARawModsPathAsTheSourceAndNamesTheModFolderToUseInstead()
    {
        var raw = InFaceBase(_w.Rel("0001.nif"));

        var text = PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Path = @"meshes\hcraw\dest.nif", Source = raw } });

        Assert.Contains("raw path into MO2's mods folder", text);
        Assert.Contains("source_provider='FaceBase'", text);
        Assert.Contains("source='" + _w.Rel("0001.nif") + "'", text);
    }

    [Fact]
    public void PlaceRefusesARawModsPathAsTheDestinationToo()
    {
        var raw = InFaceBase(_w.Rel("0001.nif"));

        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = raw } });

        Assert.Contains("raw path into MO2's mods folder", text);
        Assert.Contains("source_provider='FaceBase'", text);
    }

    [Fact]
    public void NifInspectRefusesARawModsPathOnThatPathAndStillReadsTheRest()
    {
        var raw = InFaceBase(_w.Rel("0001.nif"));

        var text = NifTools.NifInspect(_w.Svc, mesh_paths: new[] { raw, _w.Rel("0002.nif") });

        Assert.Contains("raw path into MO2's mods folder", text);
        Assert.Contains("source_provider='FaceBase'", text);
        Assert.Contains("mesh_paths='" + _w.Rel("0001.nif") + "'", text);
        // Per-path isolation holds: the second path is still answered.
        Assert.Contains(_w.Rel("0002.nif"), text);
    }
}
