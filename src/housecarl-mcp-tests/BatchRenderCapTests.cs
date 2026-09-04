using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The shared batch-render skeleton's cap contract, on both of its callers: a max_chars the header and
/// alarms alone exhaust still renders the FIRST item's answer, and the omitted-count cut is named with one marker.
/// asset_status used to cut before its first path; nif_inspect never did.</summary>
[Trait("tier", "unit")]
public class BatchRenderCapTests
{
    // Small enough that the header plus the read-failure alarm alone pass it, so the loop's cap check fires
    // on the first item.
    const int TightCap = 100;

    static AssetStatusData ThreePaths() => new(
        new[]
        {
            Absent("meshes/a/first.nif"),
            Absent("meshes/b/second.nif"),
            Absent("meshes/c/third.nif"),
        },
        new[] { "Broken - Textures.bsa (header refused)" },
        true,
        Array.Empty<string>(),
        "TestProfile");

    static AssetPathResult Absent(string path) =>
        new(path, new AssetHit(path, false, null, Array.Empty<AssetProvider>(), false), null);

    static NifInspectBatchData ThreeMeshes() => new(
        new[]
        {
            NifInspectData.Fail("meshes/a/first.nif", "ABSENT — no active mod or BSA provides this mesh"),
            NifInspectData.Fail("meshes/b/second.nif", "ABSENT — no active mod or BSA provides this mesh"),
            NifInspectData.Fail("meshes/c/third.nif", "ABSENT — no active mod or BSA provides this mesh"),
        },
        new[] { "Broken - Textures.bsa (header refused)" },
        Array.Empty<string>(),
        "TestProfile");

    static string RenderNif(int cap) =>
        NifWire.Render(ThreeMeshes(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>(), cap);

    [Fact]
    public void AssetStatusUnderATightCapStillRendersItsFirstPath()
    {
        var text = AssetWire.Render(ThreePaths(), TightCap);

        Assert.Contains("meshes/a/first.nif", text);
        Assert.DoesNotContain("meshes/b/second.nif", text);
        Assert.Contains("2 more path(s) omitted at max_chars=100", text);
    }

    [Fact]
    public void NifInspectUnderATightCapStillRendersItsFirstMesh()
    {
        var text = RenderNif(TightCap);

        Assert.Contains("meshes/a/first.nif", text);
        Assert.DoesNotContain("meshes/b/second.nif", text);
        Assert.Contains("2 more mesh(es) omitted at max_chars=100", text);
    }

    [Fact]
    public void BothBatchRendersCutWithTheSameMarker()
    {
        var asset = AssetWire.Render(ThreePaths(), TightCap);
        var nif = RenderNif(TightCap);

        Assert.Contains("  … [", asset);
        Assert.Contains("  … [", nif);
        Assert.DoesNotContain("... [", asset);
        Assert.DoesNotContain("... [", nif);
    }
}
