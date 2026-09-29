using HousecarlMcp;
using Xunit;
using S = HousecarlMcpTests.NifSourceSoleWorld;

namespace HousecarlMcpTests;

/// <summary>#545: over a build whose scan is incomplete (an enabled archive will not read), a source_provider= answer
/// keeps the scan-incomplete hedge, and a nif_set write from a sole off-order provider lands with no current winner,
/// so the render must not tell the caller to sort above one.</summary>
[Trait("tier", "integration")]
public sealed class NifSourceLaneSoleTests : IClassFixture<NifSourceSoleWorld>
{
    readonly NifSourceSoleWorld _w;
    public NifSourceLaneSoleTests(NifSourceSoleWorld w) => _w = w;

    // Probe: "the fixture's scan really is incomplete, and a plain ABSENT hedges on it".
    [Fact]
    public void ThePlainAbsentHedgesOnTheUnreadableArchive()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { S.SoleRel });

        Assert.Contains("could NOT be read this build", text);
        Assert.Contains("may be incomplete", text);
    }

    // Probe: "the winner pole over an empty universe is still an ABSENT".
    [Fact]
    public void TheWinnerPoleOverAnEmptyUniverseIsStillAnAbsent()
        => Assert.Contains("ABSENT — no active mod or BSA provides",
                           NifTools.NifInspect(_w.Svc, new[] { S.SoleRel }, source_provider: HousecarlCore.AssetSourceChoice.WinnerToken));

    // Probe: "…and it carries the same scan-incomplete hedge the plain ABSENT does [RED arm]".
    [Fact]
    public void TheWinnerPoleAbsentCarriesTheScanIncompleteHedge()
        => Assert.Contains("may be incomplete",
                           NifTools.NifInspect(_w.Svc, new[] { S.SoleRel }, source_provider: HousecarlCore.AssetSourceChoice.WinnerToken));

    // Probe: "…while a named miss stays a named miss, not an ABSENT".
    [Fact]
    public void ANamedMissStaysANamedMissNotAnAbsent()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { S.SoleRel }, source_provider: "NoSuchMod545");

        Assert.Contains("does not supply", text);
        Assert.DoesNotContain("no active mod or BSA provides", text);
    }

    // Probe: "nif_set writes a sole off-order provider's copy", "…and does NOT tell the caller to sort above a winner
    // that does not exist [RED arm]", "…it says what place_asset says instead, naming the folder to enable".
    // It writes a mod folder, so it gets its own instance.
    [Fact]
    public void ASoleOffOrderProviderWriteNamesTheFolderToEnableAndNoWinner()
    {
        using var own = new NifSourceSoleWorld();

        var text = NifTools.NifSet(own.Svc, mesh_path: S.SoleRel, op: "set_flags", target: "GuardShape",
                                   flags: "0x800000E", source_provider: S.SoleMod, patch: "NifSole");

        Assert.Contains("wrote the verified mesh into a new mod folder", text);
        Assert.DoesNotContain("the current winner", text);
        Assert.Contains("nothing else provides this path", text);
        Assert.Contains("NifSole", text);
    }
}
