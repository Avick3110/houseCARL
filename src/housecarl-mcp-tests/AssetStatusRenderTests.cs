using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The asset_status render over constructed data: the archive-read alarm before the per-path list, the
/// "absent may be incomplete" hedge for a read failure and for a discovery gap, the explicit cut notice, and
/// contention rendered as a verify signal.</summary>
[Trait("tier", "unit")]
public sealed class AssetStatusRenderTests
{
    static AssetPathResult Absent(string rel) => new(rel, new AssetHit(rel, false, null, Array.Empty<AssetProvider>(), false), null);

    static AssetStatusData Data(AssetPathResult[] results, string[] bsaFailures, bool incomplete, string[] warnings)
        => new(results, bsaFailures, Array.Empty<string>(), incomplete, warnings, "Default");

    // Probe: "the read-failure alarm renders BEFORE the per-path list" and "an ABSENT carries the read-incomplete
    // caveat at the point of use".
    [Fact]
    public void AReadFailureAlarmsFirstAndHedgesTheAbsent()
    {
        var o = AssetWire.Render(Data(new[] { Absent(@"meshes\x.nif") },
            new[] { "Bad.bsa (loaded by P.esp): could not read the archive table — truncated" }, true, Array.Empty<string>()), 80_000);

        int alarm = o.IndexOf("could NOT be read", StringComparison.Ordinal);
        Assert.True(alarm >= 0 && alarm < o.IndexOf(@"meshes\x.nif", StringComparison.Ordinal), o);
        Assert.Contains("\"absent\" may be incomplete", o);
    }

    // Probe: "an ABSENT also hedges when base archives weren't DISCOVERED (missing Skyrim.ini)".
    [Fact]
    public void ADiscoveryGapAlsoHedgesTheAbsent()
    {
        var o = AssetWire.Render(Data(new[] { Absent(@"meshes\y.nif") }, Array.Empty<string>(), false,
            new[] { "could not read the [Archive] sResourceArchiveList from a Skyrim.ini ..." }), 80_000);

        Assert.Contains("not scanned this build", o);
        Assert.Contains("\"absent\" may be incomplete", o);
    }

    // Probe: "the per-path list is cut with an explicit notice at max_chars".
    [Fact]
    public void ASmallCapCutsWithANotice()
    {
        var many = Enumerable.Range(0, 60).Select(i => Absent($@"meshes\m{i}.nif")).ToArray();

        var o = AssetWire.Render(Data(many, Array.Empty<string>(), false, Array.Empty<string>()), 300);

        Assert.Contains("omitted at max_chars=", o);
    }

    // Probe: "ambiguity is rendered NEUTRALLY (a 'verify' signal, not a 'problem')".
    [Fact]
    public void ContentionRendersAsAVerifySignal()
    {
        var hit = new AssetHit(@"meshes\z.nif", true, new AssetProvider("ModA", AssetKind.Loose),
            new[] { new AssetProvider("ModA", AssetKind.Loose), new AssetProvider("X.bsa", AssetKind.Bsa) }, true);

        var o = AssetWire.Render(Data(new[] { new AssetPathResult(@"meshes\z.nif", hit, null) },
            Array.Empty<string>(), false, Array.Empty<string>()), 80_000);

        Assert.Contains("Verify only if", o);
    }
}
