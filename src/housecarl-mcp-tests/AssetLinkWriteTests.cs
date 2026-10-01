using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An asset-link list element (SoundDescriptor.SoundFiles) passes pre-flight, applies, and round-trips its
/// paths verbatim and in order; the array-backed one (Weather.CloudTextures) is refused by name at apply. Migrated
/// from the assetlink-write-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class AssetLinkWriteTests
{
    static readonly string[] Three = { @"Sound\fx\hc\bardsong\one.wav", @"Sound\fx\hc\bardsong\two.wav", @"Sound\fx\hc\bardsong\three.wav" };
    const string Fourth = @"Sound\fx\hc\bardsong\four.wav";

    static WriteRequest[] Ops() => new[]
    {
        new WriteRequest { RecordType = "SoundDescriptor", Path = new[] { "SoundFiles" }, Verb = "ReplaceAll", Values = Three },
        new WriteRequest { RecordType = "SoundDescriptor", Path = new[] { "SoundFiles" }, Verb = "Add", Value = Fourth },
    };

    // (G) pre-flight ACCEPTS the ReplaceAll + Add the report saw REJECTED ("does not coerce to IAssetLinkGetter`1")
    [Fact]
    public void PreflightAcceptsReplaceAllAndAddOnSoundFiles()
        => Assert.All(Ops(), op => Assert.Null(TestCorpus.Rulebook.Validate(op)));

    // (A)+(S) apply coerces+adds each element; the plugin re-reads with every path round-tripped verbatim in order
    [Fact]
    public void SoundFilesRoundTripVerbatimAndInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hc-assetlink-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mod = new SkyrimMod(ModKey.FromFileName("HcAssetLinkSndr.esp"), SkyrimRelease.SkyrimSE);
            var sndr = mod.SoundDescriptors.AddNew("HC_SNDR_AssetLink");
            foreach (var op in Ops()) WriteEngine.ApplyVerb(sndr, op);

            var path = Path.Combine(dir, "HcAssetLinkSndr.esp");
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var got = back.SoundDescriptors.Single(x => x.FormKey == sndr.FormKey).SoundFiles.Select(a => a.GivenPath).ToArray();

            Assert.Equal(Three.Append(Fourth).ToArray(), got);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }

    // Weather.CloudTextures (array-backed) — refuses LOUD, not NRE: a NAMED ExpectedApplyRejectionException ("array-backed")
    [Fact]
    public void TheArrayBackedCloudTexturesListIsRefusedByName()
    {
        var wthr = new SkyrimMod(ModKey.FromFileName("HcAssetLinkWthr.esp"), SkyrimRelease.SkyrimSE).Weathers.AddNew("HC_WTHR_AssetLink");
        var op = new WriteRequest
        {
            RecordType = "Weather", Path = new[] { "CloudTextures" }, Verb = "ReplaceAll",
            Values = new[] { @"textures\hc\sky\cloud0.dds", @"textures\hc\sky\cloud1.dds" },
        };
        var ex = Assert.Throws<ExpectedApplyRejectionException>(() => WriteEngine.ApplyVerb(wthr, op));
        Assert.Contains("array-backed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
