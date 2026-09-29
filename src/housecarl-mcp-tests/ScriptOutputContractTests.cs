using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The pure <c>out_path=</c> contract for a compiled .pex (migrated from the <c>compile-ergonomics-guard</c> probe, parts
/// B1 and B2): <c>Scripts\</c> is appended once, and a path the game will not load from carries the deploy warning.
/// </summary>
[Trait("tier", "unit")]
public sealed class ScriptOutputContractTests
{
    const string Mods = @"C:\MO2\mods", Data = @"C:\Game\Skyrim Special Edition\Data", Over = @"C:\MO2\overwrite";

    static (string scriptsDir, bool appendedScripts, string? deployWarning) Contract(string outDir, string overwrite = "")
        => OutputLocations.ScriptOutputContract(outDir, Mods, Data, overwrite);

    // Probe B1: "a bare mod-folder root gets Scripts\ appended".
    [Fact]
    public void ABareRootGetsScriptsAppended()
    {
        var c = Contract(@"C:\MyMod");
        Assert.Equal(@"C:\MyMod\Scripts", c.scriptsDir);
        Assert.True(c.appendedScripts);
    }

    // Probe B1: "double-Scripts guard: a root already ending in Scripts is NOT doubled", "…is case-insensitive",
    // "…tolerates a trailing separator".
    [Theory]
    [InlineData(@"C:\MyMod\Scripts", @"C:\MyMod\Scripts")]
    [InlineData(@"C:\MyMod\scripts", @"C:\MyMod\scripts")]
    [InlineData(@"C:\MyMod\Scripts\", @"C:\MyMod\Scripts")]
    public void ARootAlreadyEndingInScriptsIsNotDoubled(string given, string want)
    {
        var c = Contract(given);
        Assert.Equal(want, c.scriptsDir);
        Assert.False(c.appendedScripts);
    }

    // Probe B2: "a real mod folder (<mods>\MyPatch) is deployable", "the game's Data folder (-> <data>\Scripts) is deployable".
    [Theory]
    [InlineData(@"C:\MO2\mods\MyPatch")]
    [InlineData(Data)]
    public void AModFolderOrTheDataFolderIsDeployable(string given)
    {
        Assert.Null(Contract(given).deployWarning);
    }

    // Probe B2: "a path under NEITHER mods nor Data carries the Q3 deploy warning", "the mods ROOT itself WARNS",
    // "a NESTED path WARNS", "a nested Data path WARNS", "segment-boundary safe: C:\MO2\modsX is NOT 'under' C:\MO2\mods".
    [Theory]
    [InlineData(@"C:\MyMod")]
    [InlineData(@"C:\MO2\mods")]
    [InlineData(@"C:\MO2\mods\X\Sub")]
    [InlineData(@"C:\Game\Skyrim Special Edition\Data\Sub")]
    [InlineData(@"C:\MO2\modsX\Foo")]
    public void APathTheGameDoesNotLoadFromWarns(string given)
    {
        Assert.Contains("deploy", Contract(given).deployWarning);
    }

    // Probe B2: "the MO2 overwrite folder deploys when its root is known, and is judged on that root (not by folder name)".
    [Fact]
    public void TheOverwriteFolderDeploysOnlyWhenItsRootIsKnown()
    {
        Assert.Null(Contract(Over, Over).deployWarning);
        Assert.NotNull(Contract(Over).deployWarning);
    }

    // Probe B2: "a drive root appends correctly (want C:\Scripts, never C:Scripts)".
    [Fact]
    public void ADriveRootKeepsItsSeparator()
    {
        Assert.Equal(@"C:\Scripts", Contract(@"C:\").scriptsDir);
    }
}
