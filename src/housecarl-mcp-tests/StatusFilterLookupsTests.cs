using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>load_order_status' filter= also reads its value as a Nexus id and lists the mod folders whose meta.ini
/// carries it, shows a named mod's Nexus id, and lists the plugins naming a plugin as a master: the loaded order, then
/// copies on disk the game does not load, marked (#1388).</summary>
[Trait("tier", "integration")]
public sealed class StatusFilterLookupsTests : IClassFixture<StatusFilterLookupsWorld>
{
    readonly StatusFilterLookupsWorld _w;
    public StatusFilterLookupsTests(StatusFilterLookupsWorld w) => _w = w;

    string Lookup(string name, int maxChars = 0) => StatusTools.LoadOrderStatus(_w.Svc, _w.Tools, filter: name, max_chars: maxChars);

    static string Line(string text, string start) => text.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith(start));

    /// <summary>A number reads as a Nexus id, and every folder carrying it is listed with its state: enabled, disabled, and on disk but not in modlist.txt.</summary>
    [Fact]
    public void ASharedNexusIdListsEveryFolderCarryingIt()
    {
        var text = Lookup("4242");

        Assert.Equal("as a Nexus id: 3 mod folders: NexusA (enabled), NexusB (disabled), NexusC (not in modlist.txt)", Line(text, "as a Nexus id"));
    }

    /// <summary>A named mod folder's "as a mod" line carries its meta.ini Nexus id.</summary>
    [Fact]
    public void AModFoldersLineShowsItsNexusId()
    {
        var text = Lookup("NexusB");

        Assert.Contains("  as a mod:    DISABLED (mod present but switched OFF — houseCARL excludes it) · Nexus id 4242\n", text);
    }

    /// <summary>MO2 reads an id of 0 or below as no Nexus id: a folder carrying one shows none, and 0 and -1 as values read as no id, so their answers stay as before.</summary>
    [Fact]
    public void ZeroAndMinusOneAreNoNexusId()
    {
        Assert.DoesNotContain("Nexus id", Lookup("ZeroMod"));
        Assert.DoesNotContain("Nexus id", Lookup("NegMod"));
        Assert.DoesNotContain("as a Nexus id", Lookup("0"));
        Assert.DoesNotContain("as a Nexus id", Lookup("-1"));
    }

    /// <summary>A folder named with digits is both a mod folder and a Nexus id: both lines answer, each for its own reading.</summary>
    [Fact]
    public void AFolderNamedWithDigitsReadsBothWays()
    {
        var text = Lookup("777");

        Assert.Contains("  as a mod:    ENABLED (mod present + switched on) · Nexus id 555\n", text);
        Assert.Equal("as a Nexus id: 1 mod folder: Seven (enabled)", Line(text, "as a Nexus id"));
    }

    /// <summary>A number no folder carries says so.</summary>
    [Fact]
    public void ANexusIdNoFolderCarriesSaysSo()
    {
        Assert.Equal("as a Nexus id: no mod folder's meta.ini carries it", Line(Lookup("31337"), "as a Nexus id"));
    }

    /// <summary>A meta.ini that will not open is named, so a missing folder is not taken for none.</summary>
    [Fact]
    public void AnUnreadableMetaIniIsNamed()
    {
        string text;
        using (new FileStream(_w.LockedMeta, FileMode.Open, FileAccess.Read, FileShare.None))
            text = Lookup("9999");

        Assert.Equal("as a Nexus id: no mod folder's meta.ini carries it", Line(text, "as a Nexus id"));
        Assert.Contains("[!] 1 meta.ini could not be read", text);
        Assert.Contains("'LockedMeta'", text);
    }

    /// <summary>A named mod whose meta.ini will not open says its id is unknown, so a missing id is not taken for none.</summary>
    [Fact]
    public void AModWhoseMetaIniWillNotReadSaysItsIdIsUnknown()
    {
        string text;
        using (new FileStream(_w.LockedMeta, FileMode.Open, FileAccess.Read, FileShare.None))
            text = Lookup("LockedMeta");

        Assert.Equal("as a mod:    ENABLED (mod present + switched on) · Nexus id unknown — its meta.ini could not be read", Line(text, "as a mod:"));
    }

    /// <summary>A mod folder that will not open (a dangling junction) is named, so a folder carrying the id is not taken for none.</summary>
    [Fact]
    public void AModFolderThatWillNotOpenIsNamed()
    {
        var link = Path.Combine(_w.Mods, "GoneFolder");
        var target = Path.Combine(_w.Root, "gone-folder");
        Directory.CreateDirectory(target);
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            mk!.WaitForExit();
        Directory.Delete(target);
        string text;
        try
        {
            Assert.True(new DirectoryInfo(link).LinkTarget is not null, "the junction was not made");
            text = Lookup("31337");
        }
        finally { Directory.Delete(link); }

        Assert.Equal("[!] 1 meta.ini could not be read, so a folder carrying this id may be missing: 'GoneFolder'", Line(text, "[!]"));
    }

    /// <summary>The loaded plugins naming the master come in load order, not the order their folders were walked.</summary>
    [Fact]
    public void LoadedDependentsAreListedInLoadOrder()
    {
        var line = Line(Lookup(StatusFilterLookupsWorld.Master), "named as a master by");

        Assert.StartsWith("named as a master by (2 in the order · 3 on disk, not loaded): HcLkDep.esp, HcLkDep2.esp, ", line);
    }

    /// <summary>A plugin in a disabled mod that names the master is listed after the loaded ones and marked not loaded.</summary>
    [Fact]
    public void ADependentInADisabledModIsListedAndMarked()
    {
        var line = Line(Lookup(StatusFilterLookupsWorld.Master), "named as a master by");

        Assert.Contains("HcLkOff.esp [not loaded: mod 'OffMod' (DISABLED)]", line);
    }

    /// <summary>A plugin in an enabled mod the game does not load says why: unchecked in plugins.txt, or not in loadorder.txt at all.</summary>
    [Fact]
    public void AnUnloadedDependentInAnEnabledModSaysWhy()
    {
        var line = Line(Lookup(StatusFilterLookupsWorld.Master), "named as a master by");

        Assert.Contains("HcLkUnchecked.esp [not loaded: mod 'UncheckedMod' (enabled), unchecked in plugins.txt]", line);
        Assert.Contains("HcLkStray.esp [not loaded: mod 'StrayMod' (enabled), not in loadorder.txt]", line);
    }

    /// <summary>A loaded plugin is judged by the copy the game loads: a disabled copy of it that names the master does not make it a dependent.</summary>
    [Fact]
    public void AShadowedCopyOfALoadedPluginIsNotADependent()
    {
        Assert.DoesNotContain(StatusFilterLookupsWorld.Shadow, Line(Lookup(StatusFilterLookupsWorld.Master), "named as a master by"));
    }

    /// <summary>A plugin the profile lists and checks whose only copy is in a disabled mod does not load, so that copy is listed with its layer label.</summary>
    [Fact]
    public void AListedPluginWithNoEnabledCopyIsListedAsNotLoaded()
    {
        Assert.Equal("named as a master by (0 in the order · 1 on disk, not loaded): HcLkGone.esp [not loaded: mod 'GoneMod' (DISABLED)]",
                     Line(Lookup(StatusFilterLookupsWorld.Absent), "named as a master by"));
    }

    /// <summary>A plugin file whose header will not read is named, so a dependent in it is not silently missing.</summary>
    [Fact]
    public void AnUnreadablePluginHeaderIsNamed()
    {
        var text = Lookup(StatusFilterLookupsWorld.Master);

        Assert.Contains("[!] not read, so a plugin naming it there is missing above: 1 header(s) would not read (HcLkBroken.esp in mod 'BrokenHdrMod' (enabled))", text);
    }

    /// <summary>A plugin nothing names as a master says so.</summary>
    [Fact]
    public void APluginWithNoDependentsSaysSo()
    {
        Assert.Contains("  named as a master by: none, in the order or on disk\n", Lookup(StatusFilterLookupsWorld.Dep));
    }

    /// <summary>The dependents line sits under the plugin's own masters line.</summary>
    [Fact]
    public void TheDependentsLineFollowsTheMastersLine()
    {
        var lines = Lookup(StatusFilterLookupsWorld.Dep).Split('\n');
        int masters = Array.FindIndex(lines, l => l.StartsWith("  masters ("));

        Assert.StartsWith("  named as a master by", lines[masters + 1]);
    }

    /// <summary>A long dependents list is cut at max_chars with a notice saying how many were shown.</summary>
    [Fact]
    public void ALongDependentsListIsCutWithANotice()
    {
        int cap = Lookup(StatusFilterLookupsWorld.Master).IndexOf(", HcLkDep2.esp", StringComparison.Ordinal);   // the budget runs out just after the first name

        Assert.Contains("HcLkDep.esp ... [showing 1 of 5; raise max_chars to see all]", Lookup(StatusFilterLookupsWorld.Master, cap));
    }

    /// <summary>A value that is not a plugin filename gets no dependents line.</summary>
    [Fact]
    public void AModFolderGetsNoDependentsLine()
    {
        Assert.DoesNotContain("named as a master by", Lookup("DepMod"));
    }
}

/// <summary>A synthetic MO2 instance for the filter lookups: mod folders carrying Nexus ids (shared, 0, -1, a folder
/// named with digits), and one master named by loaded plugins, by plugins in a disabled mod, an unchecked plugin, a
/// plugin in no load order, a disabled copy of a loaded plugin, and a file whose header will not read; and a master no folder holds, named by a
/// listed plugin whose only copy is in a disabled mod.</summary>
public sealed class StatusFilterLookupsWorld : IDisposable
{
    public const string Master = "HcLkMaster.esm";
    public const string Dep = "HcLkDep.esp";
    public const string Shadow = "HcLkShadow.esp";
    public const string Absent = "HcLkAbsent.esm";
    public const string Gone = "HcLkGone.esp";

    public string Root { get; }
    public string Mods { get; }
    public LoadOrderService Svc { get; }
    public ToolPathResolver Tools { get; }
    public string LockedMeta { get; }

    public StatusFilterLookupsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-status-lookups-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profile, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        foreach (var (folder, id) in new[] { ("NexusA", "4242"), ("NexusB", "4242"), ("NexusC", "4242"), ("ZeroMod", "0"),
                                             ("NegMod", "-1"), ("777", "555"), ("Seven", "777"), ("LockedMeta", "9999") })
            Meta(mods, folder, id);
        LockedMeta = Path.Combine(mods, "LockedMeta", "meta.ini");

        var master = new SkyrimMod(new ModKey("HcLkMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        master.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        master.Weapons.Add(new Weapon(new FormKey(master.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcLkMasterWeap" });
        Save(mods, "MasterMod", master);

        Save(mods, "DepMod", Naming(Dep, master), master);
        Save(mods, "Dep2Mod", Naming("HcLkDep2.esp", master), master);
        Save(mods, "OffMod", Naming("HcLkOff.esp", master), master);
        Save(mods, "UncheckedMod", Naming("HcLkUnchecked.esp", master), master);
        Save(mods, "StrayMod", Naming("HcLkStray.esp", master), master);
        Save(mods, "ShadowHigh", new SkyrimMod(ModKey.FromFileName(Shadow), SkyrimRelease.SkyrimSE));
        Save(mods, "ShadowOff", Naming(Shadow, master), master);
        Directory.CreateDirectory(Path.Combine(mods, "BrokenHdrMod"));
        File.WriteAllBytes(Path.Combine(mods, "BrokenHdrMod", "HcLkBroken.esp"), "not a plugin"u8.ToArray());

        // A master no folder holds, named by a plugin the profile lists and checks whose only copy is in a disabled mod.
        var absent = new SkyrimMod(ModKey.FromFileName(Absent), SkyrimRelease.SkyrimSE);
        absent.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        absent.Weapons.Add(new Weapon(new FormKey(absent.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcLkAbsentWeap" });
        Save(mods, "GoneMod", Naming(Gone, absent), absent);

        var order = new[] { Master, Dep, "HcLkDep2.esp", "HcLkUnchecked.esp", Shadow, Gone };
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", order) + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"),
            string.Join("\r\n", order.Select(p => p == "HcLkUnchecked.esp" ? p : "*" + p)) + "\r\n");
        // Dep2Mod outranks DepMod, so the folder walk meets HcLkDep2.esp first though it loads second.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+Dep2Mod\r\n+DepMod\r\n+MasterMod\r\n+UncheckedMod\r\n+StrayMod\r\n+ShadowHigh\r\n-ShadowOff\r\n-OffMod\r\n" +
            "+BrokenHdrMod\r\n-GoneMod\r\n+NexusA\r\n-NexusB\r\n+ZeroMod\r\n+NegMod\r\n+777\r\n+Seven\r\n+LockedMeta\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
        Tools = new ToolPathResolver(store);
    }

    static void Meta(string mods, string folder, string modId)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.ini"), "[General]\r\ngameName=SkyrimSE\r\nmodid=" + modId + "\r\nversion=1.0\r\n");
    }

    /// <summary>A plugin overriding the master's one weapon, so its header lists the master.</summary>
    static SkyrimMod Naming(string name, SkyrimMod master)
    {
        var m = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(master.ModKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcLkMasterWeap" });
        return m;
    }

    static void Save(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}
