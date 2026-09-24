using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.SkseVersionBlobFixture;

namespace HousecarlMcpTests;

/// <summary>The <c>SKSEPlugin_Version</c> decode, moved from the skse-reader-guard probe (arms A to F): the offset map,
/// with supportEmail at 252 bytes so viEx sits at 0x304 and vi at 0x308, the flag decode, the zero-terminated
/// compatibleVersions list, and the REL::Version unpack. Synthetic blobs in memory; no world.</summary>
[Trait("tier", "unit")]
public sealed class SkseVersionBlobDecodeTests
{
    // Arm A, the OAR shape: Address Library in vi, NoStructs in viEx, one compatible runtime, a 3.0.0 version.
    static SksePluginReader.SkseVersionInfo AddressLibraryPlugin() => SksePluginReader.DecodeVersionBlob(Blob(
        pluginVersion: Pack(3, 0, 0, 0), name: "Test Plugin", author: "Tester", email: "t@example.com",
        viEx: 0x1, vi: 0x1, compat: [Pack(1, 6, 1170, 0)], xseMin: 0));

    // Arm B, the fiss shape: no independence flag, so it loads only on its listed runtime.
    static SksePluginReader.SkseVersionInfo LockedPlugin() => SksePluginReader.DecodeVersionBlob(Blob(
        pluginVersion: Pack(0, 0, 8, 13), name: "Locked", author: "", email: "",
        viEx: 0, vi: 0, compat: [Pack(1, 6, 640, 0)], xseMin: 0));

    // Arm C: signature scanning and updated structs in vi, and an XSE floor.
    static SksePluginReader.SkseVersionInfo SignaturePlugin() => SksePluginReader.DecodeVersionBlob(Blob(
        pluginVersion: Pack(1, 2, 3, 0), name: "Sig", author: "", email: "",
        viEx: 0, vi: 0x2 | 0x4, compat: [], xseMin: Pack(2, 2, 3, 0)));

    // Probe A: "Name == 'Test Plugin'".
    [Fact]
    public void TheNameIsReadAt0x008() => Assert.Equal("Test Plugin", AddressLibraryPlugin().Name);

    // Probe A: "Author == 'Tester'".
    [Fact]
    public void TheAuthorIsReadAt0x108() => Assert.Equal("Tester", AddressLibraryPlugin().Author);

    // Probe A: "SupportEmail decoded".
    [Fact]
    public void TheSupportEmailIsReadAt0x208() => Assert.Equal("t@example.com", AddressLibraryPlugin().SupportEmail);

    // Probe A: "PluginVersion == '3.0.0'".
    [Fact]
    public void ThePluginVersionIsUnpacked() => Assert.Equal("3.0.0", AddressLibraryPlugin().PluginVersion);

    // Probe A: "UsesAddressLibrary true (vi bit0 @ 0x308)".
    [Fact]
    public void AddressLibraryIsBit0OfVi() => Assert.True(AddressLibraryPlugin().UsesAddressLibrary);

    // Probe A: "DeclaresNoStructs true (viEx bit0 @ 0x304 — proves email is 252, not 256)".
    [Fact]
    public void NoStructsIsBit0OfViExAt0x304() => Assert.True(AddressLibraryPlugin().DeclaresNoStructs);

    // Probe A: "VersionIndependent true (Address Library ⇒ not runtime-locked)".
    [Fact]
    public void AnAddressLibraryPluginIsVersionIndependent() => Assert.True(AddressLibraryPlugin().VersionIndependent);

    // Probe A: "CompatibleVersions == [1.6.1170]".
    [Fact]
    public void TheCompatibleRuntimeIsUnpacked() => Assert.Equal(["1.6.1170"], AddressLibraryPlugin().CompatibleVersions);

    // Probe A: "MinimumXseVersion null when xseMinimum == 0".
    [Fact]
    public void AZeroXseMinimumIsNoMinimum() => Assert.Null(AddressLibraryPlugin().MinimumXseVersion);

    // Probe B: "VersionIndependent false (no AddrLib / no SigScan)".
    [Fact]
    public void APluginWithNoIndependenceFlagIsVersionLocked() => Assert.False(LockedPlugin().VersionIndependent);

    // Probe B: "no independence flags set".
    [Fact]
    public void AZeroViSetsNeitherIndependenceFlag()
    {
        var b = LockedPlugin();

        Assert.False(b.UsesAddressLibrary);
        Assert.False(b.UsesSignatureScanning);
    }

    // Probe B: "CompatibleVersions == [1.6.640] (the hard target)".
    [Fact]
    public void ALockedPluginListsItsHardTarget() => Assert.Equal(["1.6.640"], LockedPlugin().CompatibleVersions);

    // Probe B: "PluginVersion keeps a non-zero build".
    [Fact]
    public void APluginVersionKeepsANonZeroBuild() => Assert.Equal("0.0.8.13", LockedPlugin().PluginVersion);

    // A blob with only these vi bits set, so a swapped bit mask cannot hide behind the other flag.
    static SksePluginReader.SkseVersionInfo ViOnly(uint vi) =>
        SksePluginReader.DecodeVersionBlob(Blob(0, "V", "", "", viEx: 0, vi: vi, compat: [], xseMin: 0));

    // Probe C: "UsesSignatureScanning true (vi bit1)".
    [Fact]
    public void SignatureScanningIsBit1OfVi()
    {
        var v = ViOnly(0x2);

        Assert.True(v.UsesSignatureScanning);
        Assert.False(v.UsesUpdatedStructs);
    }

    // Probe C: "UsesUpdatedStructs true (vi bit2)".
    [Fact]
    public void UpdatedStructsIsBit2OfVi()
    {
        var v = ViOnly(0x4);

        Assert.True(v.UsesUpdatedStructs);
        Assert.False(v.UsesSignatureScanning);
    }

    // Probe C: "UsesAddressLibrary false".
    [Fact]
    public void Bits1And2OfViAreNotAddressLibrary() => Assert.False(SignaturePlugin().UsesAddressLibrary);

    // Probe C: "VersionIndependent true (signature scanning also frees it from the runtime list)".
    [Fact]
    public void ASignatureScanningPluginIsVersionIndependent() => Assert.True(SignaturePlugin().VersionIndependent);

    // Probe C: "MinimumXseVersion == '2.2.3'".
    [Fact]
    public void TheXseMinimumIsUnpacked() => Assert.Equal("2.2.3", SignaturePlugin().MinimumXseVersion);

    // Probe D: "stops at the zero terminator — 2 entries, not 4" and "the two pre-terminator versions decode".
    [Fact]
    public void CompatibleVersionsStopAtTheZeroTerminator()
    {
        var d = SksePluginReader.DecodeVersionBlob(Blob(
            pluginVersion: 0, name: "Z", author: "", email: "",
            viEx: 0, vi: 0, compat: [Pack(1, 5, 97, 0), Pack(1, 6, 640, 0), 0u, Pack(9, 9, 9, 0)], xseMin: 0));

        Assert.Equal(["1.5.97", "1.6.640"], d.CompatibleVersions);
    }

    // Probe E: "0x07000000 → 7.0.0 (SPID major-only)", "0x01064920 → 1.6.1170 (RUNTIME_SSE_LATEST)",
    // "0x0000008D → 0.0.8.13 (build nibble preserved)".
    [Theory]
    [InlineData(0x07000000u, "7.0.0")]
    [InlineData(0x01064920u, "1.6.1170")]
    [InlineData(0x0000008Du, "0.0.8.13")]
    public void RelVersionUnpacksMajorMinorPatchAndBuild(uint packed, string expected) =>
        Assert.Equal(expected, SksePluginReader.UnpackVersion(packed));

    // Probe F: "viEx=1,vi=0 → NoStructs only (0x304 is viEx)".
    [Fact]
    public void ViExAlone_SetsNoStructsOnly()
    {
        var f1 = SksePluginReader.DecodeVersionBlob(Blob(0, "F1", "", "", viEx: 0x1, vi: 0x0, compat: [], xseMin: 0));

        Assert.True(f1.DeclaresNoStructs);
        Assert.False(f1.UsesAddressLibrary);
    }

    // Probe F: "viEx=0,vi=1 → AddressLibrary only (0x308 is vi)".
    [Fact]
    public void ViAlone_SetsAddressLibraryOnly()
    {
        var f2 = SksePluginReader.DecodeVersionBlob(Blob(0, "F2", "", "", viEx: 0x0, vi: 0x1, compat: [], xseMin: 0));

        Assert.False(f2.DeclaresNoStructs);
        Assert.True(f2.UsesAddressLibrary);
    }
}
