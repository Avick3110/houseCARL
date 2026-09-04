using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// An install with one plugin file in every layer <c>CandidateFolders</c> covers, plus one name in none of
/// them. It pins what "installed" means for the install-vs-enable split: the split's answer has to cover the
/// same places a locate searches, or the remedy names a file the locate cannot find.
///
/// <para>The files are empty — the split enumerates names and never opens anything. That is also why this
/// world needs no <c>LoadOrderService</c>: the name set comes from
/// <see cref="Mo2LoadOrder.AllPluginFileNames"/>, which takes the composition and the three directories.</para>
/// </summary>
public sealed class MasterSplitInstallLocationsWorld : IDisposable
{
    public string Root { get; }
    public string ModsDir { get; }
    public string DataDir { get; }
    public string OverwriteDir { get; }
    public Mo2Composition Comp { get; }

    public string InOverwrite => "HcSlOverwrite.esm";
    public string InEnabledMod => "HcSlEnabled.esm";
    public string InDisabledMod => "HcSlDisabled.esm";
    public string InUnlistedFolder => "HcSlUnlisted.esm";
    public string InGameData => "HcSlData.esm";
    /// <summary>Written outside every candidate folder — installed nowhere.</summary>
    public string Absent => "HcSlAbsent.esm";

    public MasterSplitInstallLocationsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-master-split-locations-" + Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(Root, "profiles", "Default");
        ModsDir = Path.Combine(Root, "mods");
        DataDir = Path.Combine(Root, "game", "Data");
        OverwriteDir = Path.Combine(Root, "overwrite");
        var outside = Path.Combine(Root, "not-installed");
        foreach (var d in new[] { profileDir, ModsDir, DataDir, OverwriteDir, outside }) Directory.CreateDirectory(d);

        var enabledMod = Path.Combine(ModsDir, "EnabledMod");
        var disabledMod = Path.Combine(ModsDir, "DisabledMod");
        // On disk under mods/, named in NEITHER of modlist.txt's lists — a mod folder created since MO2 last
        // rewrote the profile, which is where a fresh houseCARL patch lives until the refresh.
        var unlistedMod = Path.Combine(ModsDir, "UnlistedMod");
        foreach (var d in new[] { enabledMod, disabledMod, unlistedMod }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(OverwriteDir, InOverwrite), "");
        File.WriteAllText(Path.Combine(enabledMod, InEnabledMod), "");
        File.WriteAllText(Path.Combine(disabledMod, InDisabledMod), "");
        File.WriteAllText(Path.Combine(unlistedMod, InUnlistedFolder), "");
        File.WriteAllText(Path.Combine(DataDir, InGameData), "");
        File.WriteAllText(Path.Combine(outside, Absent), "");

        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+EnabledMod\r\n-DisabledMod\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "");

        Comp = Mo2LoadOrder.ReadComposition(profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class MasterSplitInstallLocationsFixture : IDisposable
{
    public MasterSplitInstallLocationsWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// <see cref="Mo2LoadOrder.SplitUnsatisfiedMasters"/> files each unsatisfied master by whether the install
/// provides a copy of it, reading the name set its caller already built — one install walk per sweep rather
/// than one per name.
///
/// <para>Asserted against fixture-known lists: the fixture decides where each file lives and each test names
/// the list a master has to land in, so a split that dropped one install layer fails on the name, not a count.</para>
/// </summary>
[Trait("tier", "unit")]
public sealed class MasterSplitInstallLocationsTests : IClassFixture<MasterSplitInstallLocationsFixture>
{
    readonly MasterSplitInstallLocationsWorld W;
    public MasterSplitInstallLocationsTests(MasterSplitInstallLocationsFixture f) => W = f.W;

    /// <summary>The six names, in the order the fixture states them — the split keeps the order it is given.</summary>
    string[] AllSix => new[]
    {
        W.InOverwrite, W.InEnabledMod, W.InDisabledMod, W.InUnlistedFolder, W.InGameData, W.Absent,
    };

    /// <summary>Everything installed anywhere the locate looks is the ENABLE case; the one name in none of those
    /// folders is the INSTALL case. All five layers, because a split blind to one hands out the wrong remedy for
    /// every master living there.</summary>
    [Fact]
    public void TheSplitFilesEachMasterByWhichInstallLayerHoldsItsFile()
    {
        var installed = Mo2LoadOrder.AllPluginFileNames(W.Comp, W.ModsDir, W.DataDir, W.OverwriteDir);

        var (notInstalled, inactive) = Mo2LoadOrder.SplitUnsatisfiedMasters(installed, AllSix);

        Assert.Equal(new[] { W.Absent }, notInstalled);
        Assert.Equal(new[] { W.InOverwrite, W.InEnabledMod, W.InDisabledMod, W.InUnlistedFolder, W.InGameData },
                     inactive);
    }

    /// <summary>The split compares case-insensitively whatever the collection it was handed carries. A plain
    /// list is the case a caller can build by accident; under an ordinal comparison every name here would be
    /// reported uninstalled, and "install it" for a file already on disk is a remedy that cannot work.</summary>
    [Fact]
    public void TheSplitMatchesCaseInsensitivelyEvenFromAPlainList()
    {
        var installedAsPlainList = new List<string> { "HCSLOVERWRITE.ESM", "hcslenabled.esm" };

        var (notInstalled, inactive) = Mo2LoadOrder.SplitUnsatisfiedMasters(
            installedAsPlainList, new[] { W.InOverwrite, W.InEnabledMod, W.Absent });

        Assert.Equal(new[] { W.Absent }, notInstalled);
        Assert.Equal(new[] { W.InOverwrite, W.InEnabledMod }, inactive);
    }

    /// <summary>A name is reduced to its filename, and padding trimmed, before it is looked up. Without that, a
    /// name that is not already bare is filed as uninstalled while its file sits in the install — the wrong
    /// remedy rather than a missing one.</summary>
    [Fact]
    public void ANameIsReducedToItsFilenameBeforeItIsLookedUp()
    {
        var installed = Mo2LoadOrder.AllPluginFileNames(W.Comp, W.ModsDir, W.DataDir, W.OverwriteDir);
        var padded = new[] { "  " + W.InEnabledMod + " ", Path.Combine("SomeMod", W.InGameData) };

        Assert.Equal(padded, Mo2LoadOrder.SplitUnsatisfiedMasters(installed, padded).InstalledButInactive);
    }
}
