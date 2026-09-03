using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// An install with one plugin file in EVERY layer <c>CandidateFolders</c> covers, plus one name that is in none
/// of them. It exists to pin what "installed" means for the install-vs-enable split: the split's answer has to
/// be the same set of places a locate searches, or the remedy names a file the locate cannot find.
///
/// <para>The files here are empty — the split stats and enumerates NAMES and never opens anything, so a real
/// Mutagen plugin would only make the fixture slower. That is also why this world needs no
/// <c>LoadOrderService</c>: the name set comes from <see cref="Mo2LoadOrder.AllPluginFileNames"/>, which takes
/// the composition and the three directories directly.</para>
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
/// provides a copy of it, reading the name set its caller already built.
///
/// <para><b>Why it takes the set.</b> A whole-order sweep makes this split once per report, and the form that
/// walked the install itself re-derived it per NAME: a master installed nowhere short-circuits nothing, so it
/// paid the enabled mods, the disabled mods, the unlisted-folder listing and Data before answering, and the
/// next plugin declaring the same name paid it all again. The answer never depended on which report asked, so
/// neither does the read — the caller pays one walk for the sweep and asks it per report.</para>
///
/// <para><b>What is asserted.</b> Fixture-known lists. The fixture decides where each file lives and the arms
/// name the list each master has to land in, so a change that moved the split off, say, the unlisted-folder
/// layer fails here on the name rather than on a count.</para>
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
    /// folders is the INSTALL case. Five layers, because a split blind to one of them hands out the wrong remedy
    /// for every master that lives there — and the set it reads is the one
    /// <see cref="Mo2LoadOrder.AllPluginFileNames"/> builds, so the layers it covers are the layers a locate
    /// searches.</summary>
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

    /// <summary>A name is reduced to its FILENAME, and padding trimmed, before it is looked up. The per-name
    /// stat this replaced did that reduction itself; dropping it would file a name that is not already bare as
    /// uninstalled while the file sits in the install, which is the wrong remedy rather than a missing one.</summary>
    [Fact]
    public void ANameIsReducedToItsFilenameBeforeItIsLookedUp()
    {
        var installed = Mo2LoadOrder.AllPluginFileNames(W.Comp, W.ModsDir, W.DataDir, W.OverwriteDir);
        var padded = new[] { "  " + W.InEnabledMod + " ", Path.Combine("SomeMod", W.InGameData) };

        Assert.Equal(padded, Mo2LoadOrder.SplitUnsatisfiedMasters(installed, padded).InstalledButInactive);
    }
}
