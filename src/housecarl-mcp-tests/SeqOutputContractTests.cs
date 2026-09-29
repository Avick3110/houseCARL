using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The pure <c>out_path=</c> contract for a .seq (migrated from the <c>seq-write-guard</c> probe, #312 arms):
/// <c>SEQ\</c> is appended once, and a path the game will not read SEQ files from carries a warning that names the
/// silent-death consequence.
/// </summary>
[Trait("tier", "unit")]
public sealed class SeqOutputContractTests
{
    const string Mods = @"C:\MO2\mods", Data = @"C:\Game\Skyrim Special Edition\Data", Over = @"C:\MO2\overwrite";

    static (string seqDir, bool appendedSeq, string? deployWarning) Contract(string outDir, string overwrite = "")
        => LoadOrderService.SeqOutputContract(outDir, Mods, Data, overwrite);

    // Probe PURE-SEQ-CONTRACT: "SEQ\ appended once" (a bare root).
    [Fact]
    public void ABareRootGetsSeqAppended()
    {
        var c = Contract(@"C:\MyMod");
        Assert.Equal(@"C:\MyMod\SEQ", c.seqDir);
        Assert.True(c.appendedSeq);
    }

    // Probe PURE-SEQ-CONTRACT: "double-SEQ guard case-insensitive + trailing-separator tolerant".
    [Theory]
    [InlineData(@"C:\MyMod\SEQ", @"C:\MyMod\SEQ")]
    [InlineData(@"C:\MyMod\seq\", @"C:\MyMod\seq")]
    public void ARootAlreadyEndingInSeqIsNotDoubled(string given, string want)
    {
        var c = Contract(given);
        Assert.Equal(want, c.seqDir);
        Assert.False(c.appendedSeq);
    }

    // Probe PURE-SEQ-OVERWRITE: "<overwrite>\SEQ deploys when the overwrite root is known, and is judged only on that root (not by name)".
    [Fact]
    public void TheOverwriteFolderDeploysOnlyWhenItsRootIsKnown()
    {
        Assert.Null(Contract(Over, Over).deployWarning);
        Assert.NotNull(Contract(Over).deployWarning);
    }

    // Probe PURE-SEQ-ROOT: "a drive root appends correctly (want C:\SEQ, never the drive-relative C:SEQ)".
    [Fact]
    public void ADriveRootKeepsItsSeparator()
    {
        Assert.Equal(@"C:\SEQ", Contract(@"C:\").seqDir);
    }

    // Probe PURE-SEQ-DEPLOY: "<mods>\<mod>\SEQ and <data>\SEQ deploy".
    [Theory]
    [InlineData(@"C:\MO2\mods\MyMod")]
    [InlineData(Data)]
    public void AModFolderOrTheDataFolderIsDeployable(string given)
    {
        Assert.Null(Contract(given).deployWarning);
    }

    // Probe PURE-SEQ-DEPLOY: "the mods root, a nested path and an outside path warn".
    [Theory]
    [InlineData(@"C:\MO2\mods")]
    [InlineData(@"C:\MO2\mods\X\Sub")]
    [InlineData(@"C:\Elsewhere\MyMod")]
    public void APathTheGameDoesNotReadSeqFromWarns(string given)
    {
        Assert.NotNull(Contract(given).deployWarning);
    }

    // Probe PURE-SEQ-DEPLOY: "the warning names the silent-death consequence".
    [Fact]
    public void TheWarningNamesTheSilentDeathConsequence()
    {
        Assert.Contains("silently", Contract(@"C:\Elsewhere\MyMod").deployWarning);
    }
}
