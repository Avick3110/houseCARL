using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The Taste of Death bug: a plugin carrying a package whose PKCU subrecord Mutagen cannot parse threw in
/// the middle of the index build's record enumeration and took the whole load order down. The build now excludes
/// that one plugin and every other plugin still resolves.</summary>
[Trait("tier", "integration")]
public sealed class MalformedPkcuExclusionTests : IDisposable
{
    const string CleanName = "hcRegClean.esp";
    const string BadName = "hcRegBad.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-malformed-pkcu-" + Guid.NewGuid().ToString("N"));

    public MalformedPkcuExclusionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Overstate the first PKCU subrecord's declared length (the two bytes after its tag) so it claims more
    /// bytes than it carries; Mutagen then throws building the package overlay mid-enumeration.</summary>
    static void OverstatePkcuLength(string path, ushort newLength)
    {
        var b = File.ReadAllBytes(path);
        for (int i = 0; i < b.Length - 6; i++)
        {
            if (b[i] != (byte)'P' || b[i + 1] != (byte)'K' || b[i + 2] != (byte)'C' || b[i + 3] != (byte)'U') continue;
            Assert.True(newLength > BitConverter.ToUInt16(b, i + 4), "the new length must overstate the old one");
            BitConverter.GetBytes(newLength).CopyTo(b, i + 4);
            File.WriteAllBytes(path, b);
            return;
        }
        Assert.Fail("Mutagen wrote no PKCU subrecord for the package");
    }

    // Probe: "malformed plugin isolated, clean plugin resolves" — excluded via the mid-enumeration path (not the
    // could-not-be-opened one), the clean keyword resolves, the bad package does not.
    [Fact]
    public void APluginWithAMalformedPkcuPackage_IsExcludedMidEnumerationAndTheCleanPluginStillResolves()
    {
        var cleanPath = Path.Combine(_dir, CleanName);
        var badPath = Path.Combine(_dir, BadName);
        var clean = new SkyrimMod(ModKey.FromNameAndExtension(CleanName), SkyrimRelease.SkyrimSE);
        var kw = clean.Keywords.AddNew(); kw.EditorID = "hcRegKeyword";
        clean.BeginWrite.ToPath(cleanPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var bad = new SkyrimMod(ModKey.FromNameAndExtension(BadName), SkyrimRelease.SkyrimSE);
        var pkg = bad.Packages.AddNew(); pkg.EditorID = "hcRegBadPackage";
        bad.BeginWrite.ToPath(badPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        OverstatePkcuLength(badPath, 99);

        // The corrupted plugin really throws on raw enumeration, or the test proves nothing.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var raw = SkyrimMod.CreateFromBinaryOverlay(badPath, SkyrimRelease.SkyrimSE);
            foreach (var _ in raw.EnumerateMajorRecords()) { }
        });

        using var resolver = LoadOrderResolver.Build(new[] { cleanPath, badPath });

        Assert.True(resolver.ExcludedPlugins.ContainsKey(BadName));
        Assert.False(resolver.IsUnopenable(BadName));          // the mid-enumeration exclusion, not the open-time one
        Assert.Equal(CleanName, resolver.ResolveWinner(kw.FormKey)?.WinnerPlugin);
        Assert.Null(resolver.ResolveWinner(pkg.FormKey));
    }
}
