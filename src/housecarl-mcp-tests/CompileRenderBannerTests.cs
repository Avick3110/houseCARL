using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.CompileRenderFixtures;

namespace HousecarlMcpTests;

/// <summary>
/// What <c>CompileTools.Render</c> leads with (migrated from the <c>compile-ergonomics-guard</c> probe, parts C and D):
/// the success line names the real destination, and a failure dominated by unresolved symbols leads with the
/// import-path banner, gated on three diagnostics and a two-thirds share.
/// </summary>
[Trait("tier", "unit")]
public sealed class CompileRenderBannerTests
{
    const string Banner = "INCOMPLETE import path";

    // Probe C: "default destination: success names the houseCARL patch-mod folder + the MO2-enable step".
    [Fact]
    public void TheDefaultDestinationNamesThePatchModFolderAndTheEnableStep()
    {
        var msg = Render(Ok, Plan(), userChoseOutputDir: false);
        Assert.Contains("houseCARL patch-mod folder", msg);
        Assert.Contains("enable it in MO2", msg);
    }

    // Probe C: "out_path= destination: success names the user's chosen folder, NOT a houseCARL patch folder".
    [Fact]
    public void AChosenOutPathNamesTheCallersFolderNotAPatchFolder()
    {
        var msg = Render(Ok, Plan(), userChoseOutputDir: true);
        Assert.Contains("output folder you chose", msg);
        Assert.DoesNotContain("houseCARL patch-mod folder", msg);
    }

    // Probe D1: the four resolution-error shapes are import-class ("unknown type …", "… is undefined",
    // "… is not a known user-defined type", "… is not a function or does not exist").
    [Theory]
    [InlineData("unknown type po3_sksefunctions")]
    [InlineData("variable JValue is undefined")]
    [InlineData("none is not a known user-defined type")]
    [InlineData("HC_ImportOrderProbeExt is not a function or does not exist")]
    public void AResolutionErrorIsAnUnresolvedSymbol(string message)
    {
        Assert.True(PapyrusCompile.IsUnresolvedSymbol(message));
    }

    // Probe D1: syntax shapes are NOT import-class ("no viable alternative …", "missing EOF …", "Unknown user flag …").
    [Theory]
    [InlineData("no viable alternative at character '@'")]
    [InlineData("missing EOF at 'EndFunction'")]
    [InlineData("Unknown user flag papyrus")]
    public void ASyntaxErrorIsNotAnUnresolvedSymbol(string message)
    {
        Assert.False(PapyrusCompile.IsUnresolvedSymbol(message));
    }

    // Probe D2: "dominated failure LEADS with the 'INCOMPLETE import path' banner" and
    // "the banner names the count (6 of 6) and precedes the diagnostic list".
    [Fact]
    public void AMissingImportFailureLeadsWithTheBannerAndItsCount()
    {
        var msg = Render(MissingImports, Plan());
        Assert.Contains("6 of 6", msg);
        Assert.True(msg.IndexOf(Banner, StringComparison.Ordinal) is >= 0 and var at
                    && at < msg.IndexOf("diagnostic(s)", StringComparison.Ordinal));
    }

    // Probe D3: "a syntax-only failure does NOT trigger the missing-imports banner" and
    // "a syntax-only failure keeps the generic import-path tail as the fallback hint".
    [Fact]
    public void ASyntaxFailureGetsNoBannerButKeepsTheImportTail()
    {
        var msg = Render(SyntaxFail, Plan());
        Assert.DoesNotContain(Banner, msg);
        Assert.Contains("import_dirs=", msg);
    }

    // Probe D4-D6: "2 unresolved (100% but < 3) → no banner", "3-of-6 (50%) → NO banner",
    // "4-of-6 (exactly 2/3) → banner fires", "5-of-6 (>2/3) → banner fires".
    [Theory]
    [InlineData(2, 0, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 2, true)]
    [InlineData(5, 1, true)]
    public void TheBannerNeedsThreeUnresolvedAndATwoThirdsShare(int unresolved, int syntax, bool banner)
    {
        Assert.Equal(banner, Render(Fail(unresolved, syntax), Plan()).Contains(Banner));
    }
}
