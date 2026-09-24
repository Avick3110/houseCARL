using Xunit;
using static HousecarlMcpTests.NativePairingRenderFixtures;

namespace HousecarlMcpTests;

/// <summary>Migrated from the native-pairing-guard probe, part 2: the runtime compare that decides whether a
/// version-locked SKSE plugin loads. Zero-padded numeric equality, and a garbage segment never passes a lock.</summary>
[Trait("tier", "unit")]
public sealed class NativePairingRuntimeCompareTests
{
    [Fact] // probe: "versions equal under zero-padding: 1.6.1170 == 1.6.1170.0"
    public void VersionsAreEqualUnderZeroPadding() => Assert.True(SksePluginReader.VersionsEqual("1.6.1170", "1.6.1170.0"));

    [Fact] // probe: "versions differ: 1.6.640 != 1.6.1170.0"
    public void DifferentVersionsAreNotEqual() => Assert.False(SksePluginReader.VersionsEqual("1.6.640", "1.6.1170.0"));

    [Fact] // probe: "garbage segment never PASSES a lock (Q3)"
    public void AGarbageSegmentIsNeverEqual() => Assert.False(SksePluginReader.VersionsEqual("1.6.x", "1.6.0"));

    [Fact] // probe: "locked plugin + unlisted runtime → NOT compatible"
    public void ALockedPluginIsNotCompatibleWithAnUnlistedRuntime() =>
        Assert.False(SksePluginReader.RuntimeCompatible(Ver(false, "1.5.97", "1.6.640"), "1.6.1170.0"));

    [Fact] // probe: "locked plugin + listed runtime (padded) → compatible"
    public void ALockedPluginIsCompatibleWithAListedRuntimeUnderPadding() =>
        Assert.True(SksePluginReader.RuntimeCompatible(Ver(false, "1.5.97", "1.6.640"), "1.6.640.0"));

    [Fact] // probe: "version-independent plugin → compatible anywhere"
    public void AVersionIndependentPluginIsCompatibleAnywhere() =>
        Assert.True(SksePluginReader.RuntimeCompatible(Ver(true), "9.9.9"));

    [Fact] // probe: "AE runtime boundary: 1.6.1170 IS AE, 1.5.97 is NOT, garbage is NOT (unknown never claims)"
    public void TheAeBoundaryIsOnePointSixAndGarbageIsNotAe()
    {
        Assert.True(SksePluginReader.IsAeRuntime("1.6.1170.0"));
        Assert.False(SksePluginReader.IsAeRuntime("1.5.97.0"));
        Assert.False(SksePluginReader.IsAeRuntime("bogus"));
    }
}
