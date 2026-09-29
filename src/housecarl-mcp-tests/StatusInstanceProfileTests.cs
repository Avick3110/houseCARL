using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>load_order_status names the MO2 instance it reads, and profile= inspects a sibling profile without
/// switching to it: the service data and the text the caller sees, arm by arm. Migrated from the
/// loadorder-status-guard probe (arms A to I, and J2).</summary>
[Trait("tier", "integration")]
public sealed class StatusInstanceProfileTests : IClassFixture<StatusProfileWorld>
{
    readonly StatusProfileWorld _w;
    public StatusInstanceProfileTests(StatusProfileWorld w) => _w = w;

    string Status(LoadOrderService svc, string? filter = null, string? profile = null)
        => StatusTools.LoadOrderStatus(svc, _w.Tools, filter: filter, profile: profile);

    static string LineStartingWith(string text, string start)
        => text.Split('\n').Single(l => l.StartsWith(start, StringComparison.Ordinal));

    // A: "InstanceDir == the configured instance folder"; "ProfileName == the ini's selected profile"; "rendered header carries the 'instance: <path>' line"
    [Fact]
    public void InstanceModeNamesTheConfiguredInstanceAndItsSelectedProfile()
    {
        using var svc = _w.Instance(_w.InstA);
        var data = svc.StatusData();

        Assert.Equal(_w.InstA, data.InstanceDir);
        // The ini selects "Primary", not "Default", so a hard-coded default cannot pass.
        Assert.Equal("Primary", data.ProfileName);
        Assert.Contains("instance: " + _w.InstA + "\n", Status(svc));
    }

    // B: "explicit-paths mode → InstanceDir is null"; "rendered header shows 'explicit-paths mode' (not a bogus path)"
    [Fact]
    public void ExplicitPathsModeHasNoInstanceAndSaysSo()
    {
        using var svc = _w.Explicit();

        Assert.Null(svc.StatusData().InstanceDir);
        Assert.StartsWith("instance: explicit-paths mode", LineStartingWith(Status(svc), "instance: "));
    }

    // C: "Second resolved (instance mode, composition present)"; "Second sees 2 enabled mods"; "Second's active plugins
    // include the extra plugin"; "the active profile is STILL 'Default'"; "Default sees 1 enabled mod"
    [Fact]
    public void ANamedReadReturnsThatProfilesCompositionWithoutSwitching()
    {
        using var svc = _w.Instance(_w.InstC);

        var second = svc.NamedProfileComposition("Second");
        Assert.True(second.InstanceMode);
        Assert.NotNull(second.Composition);
        Assert.Equal(2, second.Composition!.EnabledMods.Count);
        Assert.Contains(StatusProfileWorld.ExtraName, second.Composition.ActivePluginNames);

        Assert.Equal("Default", svc.ProfileName);
        // The active composition is still Default's one mod, not Second's two.
        Assert.Single(svc.StatusData().Composition.EnabledMods);
        Assert.Single(svc.NamedProfileComposition("Default").Composition!.EnabledMods);
    }

    // C: "name match is case-insensitive ('second' finds 'Second')"
    [Fact]
    public void ANamedReadMatchesTheProfileNameCaseInsensitively()
    {
        using var svc = _w.Instance(_w.InstC);

        var r = svc.NamedProfileComposition("second");

        Assert.NotNull(r.Composition);
        Assert.Equal("Second", r.RequestedName);
    }

    // C: "render shows the named inspection and states the active profile is unchanged"
    [Fact]
    public void TheNamedInspectionRenderSaysTheActiveProfileIsUnchanged()
    {
        using var svc = _w.Instance(_w.InstC);

        var text = Status(svc, profile: "Second");

        Assert.Contains("inspecting profile 'Second'", text);
        Assert.Contains("active profile is unchanged", text);
        Assert.Contains("  mods:    2 enabled", text);
        Assert.StartsWith("load order status — profile 'Default'", text);
        // The discovery line is left out when a profile was asked for.
        Assert.DoesNotContain("profiles available", text);
    }

    // D: "unknown name → no composition (not a silent empty)"; "not-found result NAMES the available profiles";
    // "render says 'not found' and lists the real options"
    [Fact]
    public void AnUnknownProfileNameListsTheAvailableOnes()
    {
        using var svc = _w.Instance(_w.InstC);

        var nf = svc.NamedProfileComposition("Nope");
        Assert.True(nf.InstanceMode);
        Assert.Null(nf.Composition);
        Assert.Equal(new[] { "Default", "Second" }, nf.AvailableProfiles);

        // The names are asserted on the not-found line itself: the header already carries 'Default'.
        var line = LineStartingWith(Status(svc, profile: "Nope"), "profile 'Nope' not found");
        Assert.Contains("Default", line);
        Assert.Contains("Second", line);
    }

    // F: "no name → discovery list of both profiles"; "default status renders the 'profiles available' discovery line"
    [Fact]
    public void TheDefaultStatusListsTheAvailableProfiles()
    {
        using var svc = _w.Instance(_w.InstC);

        var disc = svc.NamedProfileComposition(null);
        Assert.True(disc.InstanceMode);
        Assert.Null(disc.RequestedName);
        Assert.Equal(2, disc.AvailableProfiles.Count);

        var line = LineStartingWith(Status(svc), "profiles available");
        Assert.Contains("Default", line);
        Assert.Contains("Second", line);
    }

    // E: "explicit mode → named read refused (InstanceMode false, no composition)"; "render explains the named read needs instance mode"
    [Fact]
    public void ExplicitPathsModeRefusesANamedRead()
    {
        using var svc = _w.Explicit();

        var r = svc.NamedProfileComposition("whatever");
        Assert.False(r.InstanceMode);
        Assert.Null(r.Composition);

        Assert.Contains("MO2-instance mode", Status(svc, profile: "whatever"));
    }

    // G: "a profile under the redirected base_directory is found and read correctly"; "the profiles root resolved
    // through base_directory (both siblings enumerated)"
    [Fact]
    public void ProfilesUnderARedirectedBaseDirectoryAreFound()
    {
        using var svc = _w.Instance(_w.InstG);

        var second = svc.NamedProfileComposition("Second");

        Assert.True(second.InstanceMode);
        Assert.Equal(2, second.Composition!.EnabledMods.Count);
        Assert.Equal(new[] { "Default", "Second" }, second.AvailableProfiles);
    }

    // H: "the stray folder is excluded from the available profiles"; "requesting the stray folder is a clean not-found"
    [Fact]
    public void AFolderWithNoLoadOrderIsNotOfferedOrMatched()
    {
        using var svc = _w.Instance(_w.InstH);

        var disc = svc.NamedProfileComposition(null);
        Assert.Contains("Default", disc.AvailableProfiles);
        Assert.DoesNotContain(StatusProfileWorld.Stray, disc.AvailableProfiles);

        var miss = svc.NamedProfileComposition(StatusProfileWorld.Stray);
        Assert.True(miss.InstanceMode);
        Assert.Null(miss.Composition);
    }

    // I: "the partial profile still reads"; "the missing modlist.txt is surfaced as a read warning";
    // "render shows the warning under the inspection block"
    [Fact]
    public void AnInspectedProfileWithNoModlistCarriesAWarning()
    {
        using var svc = _w.Instance(_w.InstI);

        var partial = svc.NamedProfileComposition("Partial");
        Assert.NotNull(partial.Composition);
        Assert.Contains(partial.Warnings, w => w.Contains("modlist.txt", StringComparison.OrdinalIgnoreCase));

        var lines = Status(svc, profile: "Partial").Split('\n');
        int block = Array.FindIndex(lines, l => l.Contains("inspecting profile 'Partial'"));
        int warn = Array.FindIndex(lines, l => l.StartsWith("  [!] ") && l.Contains("modlist.txt"));
        Assert.True(block >= 0 && warn > block, "the modlist.txt warning is not under the inspection block");
    }

    // J2: "filter='<name>' (no extension) → the plugin-miss line suggests '<name>.esm'"
    [Fact]
    public void AFilterWithoutTheExtensionSuggestsThePlugin()
    {
        using var svc = _w.Instance(_w.InstJ);

        var line = LineStartingWith(Status(svc, filter: "HcLosMaster"), "  as a plugin:");

        Assert.Contains("not in the load order", line);
        Assert.Contains("Did you mean `" + StatusProfileWorld.MasterName + "`", line);
    }

    // J2: "an unrelated filter renders the miss with NO suggestion (no spurious 'did you mean')"
    [Fact]
    public void AnUnrelatedFilterGetsNoSuggestion()
    {
        using var svc = _w.Instance(_w.InstJ);

        var line = LineStartingWith(Status(svc, filter: "ZzzNothingLikeIt.esp"), "  as a plugin:");

        Assert.Contains("not in the load order", line);
        Assert.DoesNotContain("Did you mean", line);
    }
}

/// <summary>Synthetic MO2 instances sharing one game folder and one master plugin, each served from an enabled mod
/// folder: A (active profile "Primary"), B (read in explicit-paths mode), C (Default plus a distinct Second), G (mods
/// and profiles under a redirected base_directory), H (a stray folder under profiles/), I (a profile with no
/// modlist.txt), J (a plain one for filter lookups). The extra plugin Second ticks is served too, so no arm reads a
/// ticked plugin that no mod folder provides.</summary>
public sealed class StatusProfileWorld : IDisposable
{
    public const string MasterName = "HcLosMaster.esm";
    public const string ExtraName = "HcLosExtra.esp";
    public const string Stray = "_NotAProfile";

    public string Root { get; }
    public string InstA { get; }
    public string InstB { get; }
    public string InstC { get; }
    public string InstG { get; }
    public string InstH { get; }
    public string InstI { get; }
    public string InstJ { get; }
    public UserConfigStore Store { get; }
    public ToolPathResolver Tools { get; }

    readonly string _masterFile, _extraFile;

    public StatusProfileWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-status-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcLosMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcLosW0";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _masterFile = Path.Combine(Root, MasterName);
        master.BeginWrite.ToPath(_masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var extra = new SkyrimMod(new ModKey("HcLosExtra", ModType.Plugin), SkyrimRelease.SkyrimSE);
        extra.Weapons.AddNew().EditorID = "HcLosW1";
        _extraFile = Path.Combine(Root, ExtraName);
        extra.BeginWrite.ToPath(_extraFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        InstA = MakeInstance("inst-a", "Primary");
        WriteProfile(Path.Combine(InstA, "profiles", "Primary"), Plain);

        InstB = MakeInstance("inst-b");
        WriteProfile(Path.Combine(InstB, "profiles", "Default"), Plain);

        InstC = MakeInstance("inst-c");
        WriteProfile(Path.Combine(InstC, "profiles", "Default"), Plain);
        WriteProfile(Path.Combine(InstC, "profiles", "Second"), WithExtra);

        var baseG = Path.Combine(Root, "base-g");
        InstG = MakeInstance("inst-g", baseDir: baseG);
        WriteProfile(Path.Combine(baseG, "profiles", "Default"), Plain);
        WriteProfile(Path.Combine(baseG, "profiles", "Second"), WithExtra);

        InstH = MakeInstance("inst-h");
        WriteProfile(Path.Combine(InstH, "profiles", "Default"), Plain);
        Directory.CreateDirectory(Path.Combine(InstH, "profiles", Stray));

        InstI = MakeInstance("inst-i");
        WriteProfile(Path.Combine(InstI, "profiles", "Default"), Plain);
        var partial = Path.Combine(InstI, "profiles", "Partial");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(partial, "plugins.txt"), "*" + MasterName + "\r\n");

        InstJ = MakeInstance("inst-j");
        WriteProfile(Path.Combine(InstJ, "profiles", "Default"), Plain);

        Store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Tools = new ToolPathResolver(Store);
    }

    public LoadOrderService Instance(string inst) => LoadOrderService.WithInstance(inst, 0, Store);

    public LoadOrderService Explicit() => LoadOrderService.WithExplicitPaths(
        Path.Combine(Root, "game", "Data"), Path.Combine(InstB, "mods"), Path.Combine(InstB, "profiles", "Default"), 0, Store);

    static readonly (string[] Order, string[] Ticked, string[] Mods) Plain =
        (new[] { MasterName }, new[] { "*" + MasterName }, new[] { "+MasterMod" });
    static readonly (string[] Order, string[] Ticked, string[] Mods) WithExtra =
        (new[] { MasterName, ExtraName }, new[] { "*" + MasterName, "*" + ExtraName }, new[] { "+MasterMod", "+ExtraMod" });

    string MakeInstance(string name, string profile = "Default", string? baseDir = null)
    {
        var inst = Path.Combine(Root, name);
        var b = baseDir ?? inst;
        foreach (var (mod, file) in new[] { ("MasterMod", _masterFile), ("ExtraMod", _extraFile) })
        {
            Directory.CreateDirectory(Path.Combine(b, "mods", mod));
            File.Copy(file, Path.Combine(b, "mods", mod, Path.GetFileName(file)));
        }
        Directory.CreateDirectory(inst);
        var settings = baseDir is null ? "" : "[Settings]\r\nbase_directory=@ByteArray(" + baseDir.Replace(@"\", @"\\") + ")\r\n";
        File.WriteAllText(Path.Combine(inst, Mo2Instance.IniFileName),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n" + settings);
        return inst;
    }

    static void WriteProfile(string dir, (string[] Order, string[] Ticked, string[] Mods) p)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", p.Order) + "\r\n");
        File.WriteAllText(Path.Combine(dir, "plugins.txt"), string.Join("\r\n", p.Ticked) + "\r\n");
        File.WriteAllText(Path.Combine(dir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", p.Mods) + "\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp scratch */ }
    }
}
