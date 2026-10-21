using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.ResolverArchives;

namespace HousecarlMcpTests;

/// <summary>Which archives load and at what rank: X.bsa and "X - Textures.bsa" bind to X.esp at one rank, Skyrim.ini
/// base archives rank below every plugin archive, a later plugin outranks an earlier one, a duplicate archive name
/// resolves to the higher-priority mod's copy, and a missing Skyrim.ini is a warning rather than a silent gap.</summary>
[Trait("tier", "unit")]
[Collection(MyGamesIsolation.Serial)]
public sealed class ArchiveDiscoveryTests : IDisposable
{
    readonly string _root, _mods, _data, _prof;

    public ArchiveDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-archive-discovery-" + Guid.NewGuid().ToString("N"));
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "game", "Data");
        _prof = Path.Combine(_root, "profiles", "Default");
        foreach (var d in new[] { _mods, _data, _prof }) Directory.CreateDirectory(d);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    void Profile(string[] loadorder, string[] modlist, string? archiveList)
    {
        File.WriteAllText(Path.Combine(_prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(_prof, "plugins.txt"), string.Join("\r\n", loadorder.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(_prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
        if (archiveList is not null)
            File.WriteAllText(Path.Combine(_prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=" + archiveList + "\r\n");
    }

    ArchiveDiscoveryResult Discover() => ArchiveDiscovery.Discover(_prof, _mods, _data, "");

    /// <summary>PluginA ships PluginA.bsa and its Textures archive, PluginB ships PluginB.bsa, Data holds a base
    /// archive listed in Skyrim.ini; PluginB loads last.</summary>
    IReadOnlyList<ActiveArchive> TwoPluginsAndABase()
    {
        var modA = Path.Combine(_mods, "ModA");
        var modB = Path.Combine(_mods, "ModB");
        Write(modA, "PluginA.bsa", A());
        Write(modA, "PluginA - Textures.bsa", A());
        Write(modB, "PluginB.bsa", B());
        Write(_data, "Skyrim - Textures.bsa", B());
        Profile(new[] { "PluginA.esp", "PluginB.esp" }, new[] { "+ModA", "+ModB" }, "Skyrim - Textures.bsa");
        return Discover().Archives;
    }

    // Probe: "4 active archives discovered (1 base + 2 for PluginA + 1 for PluginB)", "PluginA binds BOTH 'PluginA.bsa'
    // and 'PluginA - Textures.bsa'", "a plugin's two archives share its rank" and "PluginB binds 'PluginB.bsa'".
    [Fact]
    public void APluginBindsItsCoNamedAndTexturesArchivesAtOneRank()
    {
        var arc = TwoPluginsAndABase();
        var a = arc.Where(x => x.OwningPlugin == "PluginA.esp").ToList();

        Assert.Equal(4, arc.Count);
        Assert.Equal(new[] { "PluginA - Textures.bsa", "PluginA.bsa" }, a.Select(x => Path.GetFileName(x.Path)).Order());
        Assert.Equal(a[0].PluginRank, a[1].PluginRank);
        Assert.Equal("PluginB.bsa", Path.GetFileName(Assert.Single(arc, x => x.OwningPlugin == "PluginB.esp").Path));
    }

    // Probe: "the Skyrim.ini base archive is discovered, from the Data folder", "base archives rank BELOW every plugin
    // archive" and "a later-loaded plugin's archive OUTRANKS an earlier one".
    [Fact]
    public void BaseArchivesRankBelowPluginsAndALaterPluginOutranksAnEarlierOne()
    {
        var arc = TwoPluginsAndABase();
        var baseArc = Assert.Single(arc, x => x.OwningPlugin == ArchiveDiscovery.IniArchiveOwner);
        int aRank = arc.Where(x => x.OwningPlugin == "PluginA.esp").Min(x => x.PluginRank);
        int bRank = arc.Single(x => x.OwningPlugin == "PluginB.esp").PluginRank;

        Assert.StartsWith(_data, baseArc.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        Assert.True(baseArc.PluginRank < aRank, $"{baseArc.PluginRank} vs {aRank}");
        Assert.True(aRank < bRank, $"{aRank} vs {bRank}");
    }

    // Probe: "the higher-priority mod's copy of 'PluginA.bsa' is the discovered winning path (VFS, not
    // look-beside-the-esp)".
    [Fact]
    public void ADuplicateArchiveNameResolvesToTheHigherPriorityMod()
    {
        var hi = Path.Combine(_mods, "HiMod");
        Write(hi, "PluginA.bsa", A());
        Write(Path.Combine(_mods, "LoMod"), "PluginA.bsa", B());
        Profile(new[] { "PluginA.esp" }, new[] { "+HiMod", "+LoMod" }, "");

        var pa = Discover().Archives.Single(x => x.OwningPlugin == "PluginA.esp");

        Assert.StartsWith(hi, pa.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "a missing Skyrim.ini base list is a LOUD warning" and "no base archive is invented, but plugin co-name
    // discovery still works".
    [Fact]
    public void AMissingSkyrimIniIsAWarningAndInventsNoBaseArchive()
    {
        Write(Path.Combine(_mods, "ModA"), "PluginA.bsa", A());
        Profile(new[] { "PluginA.esp" }, new[] { "+ModA" }, archiveList: null);

        var res = Discover();

        Assert.Contains(res.Warnings, w => w.Contains("Skyrim.ini", StringComparison.OrdinalIgnoreCase)
                                           && w.Contains("base", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(res.Archives, x => x.OwningPlugin == ArchiveDiscovery.IniArchiveOwner);
        Assert.Contains(res.Archives, x => x.OwningPlugin == "PluginA.esp");
    }

    /// <summary>The base list is read from the INI the game reads: SkyrimCustom.ini's list over Skyrim.ini's.</summary>
    [Fact]
    public void SkyrimCustomIniListsTheBaseArchivesOverSkyrimIni()
    {
        Write(_data, "Custom.bsa", A());
        Write(_data, "Plain.bsa", B());
        Profile(Array.Empty<string>(), Array.Empty<string>(), "Plain.bsa");
        File.WriteAllText(Path.Combine(_prof, "SkyrimCustom.ini"), "[Archive]\r\nsResourceArchiveList=Custom.bsa\r\n");

        var baseArc = Assert.Single(Discover().Archives, x => x.OwningPlugin == ArchiveDiscovery.IniArchiveOwner);
        Assert.Equal("Custom.bsa", Path.GetFileName(baseArc.Path));
    }

    /// <summary>A profile MO2 runs on the My Games INIs (LocalSettings=false) takes the base list from there, not from
    /// a Skyrim.ini left in the profile.</summary>
    [Fact]
    public void LocalSettingsFalseReadsTheBaseListFromMyGames()
    {
        Write(_data, "MyGames.bsa", A());
        Write(_data, "Profile.bsa", B());
        Profile(Array.Empty<string>(), Array.Empty<string>(), "Profile.bsa");
        File.WriteAllText(Path.Combine(_prof, "settings.ini"), "[General]\r\nLocalSettings=false\r\n");
        var myGames = Path.Combine(_root, "MyGames");
        Directory.CreateDirectory(myGames);
        File.WriteAllText(Path.Combine(myGames, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=MyGames.bsa\r\n");

        var res = ArchiveDiscovery.Discover(Mo2LoadOrder.ReadComposition(_prof), MyGamesIsolation.Pointed(myGames, () => GameIni.For(_prof)), _mods, _data, "");

        var baseArc = Assert.Single(res.Archives, x => x.OwningPlugin == ArchiveDiscovery.IniArchiveOwner);
        Assert.Equal("MyGames.bsa", Path.GetFileName(baseArc.Path));
    }

    /// <summary>Which folder answers: LocalSettings=true is the profile even with no Skyrim.ini in it; a profile with no
    /// flag and no Skyrim.ini (not one MO2 wrote) falls to My Games.</summary>
    [Fact]
    public void GameIniFollowsTheLocalSettingsFlagAndFallsToMyGames()
    {
        var myGames = Path.Combine(_root, "MyGames");
        File.WriteAllText(Path.Combine(_prof, "settings.ini"), "[General]\r\nLocalSettings=true\r\n");
        var flagged = MyGamesIsolation.Pointed(myGames, () => GameIni.For(_prof));
        Assert.Equal(_prof, flagged.Folder);
        Assert.True(flagged.ProfileLocal);

        File.Delete(Path.Combine(_prof, "settings.ini"));
        var unflagged = MyGamesIsolation.Pointed(myGames, () => GameIni.For(_prof));
        Assert.Equal(myGames, unflagged.Folder);
        Assert.False(unflagged.ProfileLocal);
    }
}
