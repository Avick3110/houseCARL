using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2's <c>[Settings]</c> <c>mod_directory</c>, <c>profiles_directory</c> and <c>overwrite_directory</c> (#1386,
/// #1214): each key moves its folder, <c>%BASE_DIR%</c> expands to base, an empty or relative value and a set but missing
/// base refuse naming the key, a missing overwrite does not, and the service, the profile list and a new patch folder
/// follow the moved folders. Each instance keeps a stale <c>mods/</c> and <c>profiles/</c> under itself, the way a moved
/// instance does, so reading the defaults shows as the wrong answer rather than as a refusal.</summary>
[Trait("tier", "integration")]
public sealed class Mo2DirectoryKeysTests : IDisposable
{
    string _root = Path.Combine(Path.GetTempPath(), "hc-mo2-dirkeys-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup best-effort */ } }

    string Inst(string name) => Path.Combine(_root, name, "inst");
    string Store(string name) => Path.Combine(_root, name, "store");

    /// <summary>An instance <paramref name="name"/> whose ini carries <paramref name="settings"/> under <c>[Settings]</c>
    /// ({STORE} is the store folder, forward-slashed), with ModA.esp in store/mods/ModA and a Default profile listing it
    /// under store/profiles, plus a stale instance mods/ModA (no plugin) and an instance profile listing Stale.esp. Build
    /// maps paths and never opens a plugin, so the plugin files are one byte each.</summary>
    string Stage(string name, params string[] settings) => StageWith(name, "", settings);

    string StageWith(string name, string otherSections, params string[] settings)
    {
        var inst = Inst(name); var store = Store(name);
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        File.WriteAllText(Path.Combine(_root, "game", "Data", "Skyrim.esm"), "x");
        Directory.CreateDirectory(Path.Combine(store, "mods", "ModA"));
        File.WriteAllText(Path.Combine(store, "mods", "ModA", "ModA.esp"), "x");
        WriteProfile(Path.Combine(store, "profiles", "Default"), "ModA.esp");
        Directory.CreateDirectory(Path.Combine(inst, "mods", "ModA"));
        WriteProfile(Path.Combine(inst, "profiles", "Default"), "Stale.esp");
        var s = store.Replace('\\', '/');
        File.WriteAllText(Mo2Instance.IniPath(inst),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n" + otherSections
            + "[Settings]\r\n" + string.Concat(settings.Select(l => l.Replace("{STORE}", s) + "\r\n")));
        return inst;
    }

    static void WriteProfile(string dir, string plugin, string mod = "ModA", string? master = "Skyrim.esm")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loadorder.txt"), "# header\r\n" + (master is null ? "" : master + "\r\n") + plugin + "\r\n");
        File.WriteAllText(Path.Combine(dir, "plugins.txt"), "*" + plugin + "\r\n");
        File.WriteAllText(Path.Combine(dir, "modlist.txt"), "# header\r\n+" + mod + "\r\n");
    }

    static void AssertSamePath(string expected, string actual) => Mo2InstanceResolveTests.AssertSamePath(expected, actual);

    static Mo2OrderResult Build(Mo2InstancePaths p) => Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);

    // Promise: "mod_directory absolute: ModsDir is that folder; a plugin there resolves and the stale <inst>\mods copy is
    // not read" (fixture B); "profiles_directory absolute: ProfileDir is <it>/<selected_profile>".
    [Fact]
    public void TheMovedModsAndProfilesFoldersAreReadNotTheStaleOnes()
    {
        var p = Mo2Instance.Resolve(Stage("b", "mod_directory={STORE}/mods", "profiles_directory={STORE}/profiles"));

        AssertSamePath(Path.Combine(Store("b"), "mods"), p.ModsDir);
        AssertSamePath(Path.Combine(Store("b"), "profiles", "Default"), p.ProfileDir);
        var r = Build(p);
        Assert.Contains(r.OrderedPaths, x => x.StartsWith(Path.Combine(Store("b"), "mods", "ModA"), StringComparison.OrdinalIgnoreCase)
                                             && x.EndsWith("ModA.esp", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(r.Warnings);
    }

    // Promise: "overwrite_directory absolute: OverwriteDir is that folder and a plugin there beats the same name in a mod".
    [Fact]
    public void TheMovedOverwriteFolderBeatsAMod()
    {
        var inst = Stage("ovw", "mod_directory={STORE}/mods", "profiles_directory={STORE}/profiles", "overwrite_directory={STORE}/ovw");
        Directory.CreateDirectory(Path.Combine(Store("ovw"), "ovw"));
        File.WriteAllText(Path.Combine(Store("ovw"), "ovw", "ModA.esp"), "x");

        var p = Mo2Instance.Resolve(inst);

        AssertSamePath(Path.Combine(Store("ovw"), "ovw"), p.OverwriteDir);
        Assert.Contains(Build(p).OrderedPaths, x => x.StartsWith(Path.Combine(Store("ovw"), "ovw"), StringComparison.OrdinalIgnoreCase));
    }

    // Promise: "%BASE_DIR% in each key expands to base_directory when set, else the ini's folder"; "%base_dir% lower-case is
    // not expanded (MO2's case-sensitive replace)" — it is left a relative path, so it refuses.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BaseDirExpandsToBaseDirectoryOrTheIniFolder(bool baseSet)
    {
        var inst = baseSet
            ? Stage("bd1", "base_directory={STORE}", "mod_directory=%BASE_DIR%/mymods", "profiles_directory=%BASE_DIR%/profiles")
            : Stage("bd0", "mod_directory=%BASE_DIR%/mymods");
        var expectedBase = baseSet ? Store("bd1") : inst;
        Directory.CreateDirectory(Path.Combine(expectedBase, "mymods"));

        var p = Mo2Instance.Resolve(inst);

        AssertSamePath(Path.Combine(expectedBase, "mymods"), p.ModsDir);
        AssertSamePath(Path.Combine(expectedBase, "profiles", "Default"), p.ProfileDir);
    }

    [Fact]
    public void LowerCaseBaseDirIsNotExpanded()
    {
        var (ok, _, problems) = Mo2Instance.Validate(Stage("lc", "mod_directory=%base_dir%/mods"));

        Assert.False(ok);
        Assert.Contains(problems, s => s.Contains("mod_directory='%base_dir%/mods'") && s.Contains("not a full path"));
    }

    // Promise: "Backslash and Qt-escaped values (%BASE_DIR%\\mymods, @ByteArray(...), quoted) resolve the same as /".
    [Theory]
    [InlineData(@"mod_directory=%BASE_DIR%\\mymods")]
    [InlineData(@"mod_directory=@ByteArray({STOREBS}\\mymods)")]
    [InlineData("mod_directory=\"{STORE}/mymods\"")]
    [InlineData(@"mod_directory = {STOREBS}\\mymods")]
    public void EscapedAndBackslashedValuesResolveTheSame(string line)
    {
        var inst = Stage("esc", "base_directory={STORE}", line.Replace("{STOREBS}", Store("esc").Replace(@"\", @"\\")));
        Directory.CreateDirectory(Path.Combine(Store("esc"), "mymods"));

        AssertSamePath(Path.Combine(Store("esc"), "mymods"), Mo2Instance.Resolve(inst).ModsDir);
    }

    // A key written with a trailing / or \ gives a root with none, so a prefix test under the mods folder still matches.
    [Theory]
    [InlineData("/")]
    [InlineData(@"\\")]
    public void ATrailingSeparatorOnAKeyIsDropped(string end)
    {
        var inst = Stage("trail", "mod_directory={STORE}/mods" + end, "profiles_directory={STORE}/profiles" + end,
                         "overwrite_directory={STORE}/ovw" + end);

        var p = Mo2Instance.Resolve(inst);

        Assert.Equal(Path.Combine(Store("trail"), "mods"), p.ModsDir, ignoreCase: true);
        Assert.Equal(Path.Combine(Store("trail"), "profiles", "Default"), p.ProfileDir, ignoreCase: true);
        Assert.Equal(Path.Combine(Store("trail"), "ovw"), p.OverwriteDir, ignoreCase: true);
    }

    // Promise: "Empty or relative value: refused, the sentence naming the key and saying to use a full path or
    // %BASE_DIR%/". An empty or relative base_directory refuses once, naming base_directory, for every root under it, and
    // asks only for a full path: base_directory itself is never expanded.
    [Theory]
    [InlineData("mod_directory=", "mod_directory=''", true)]
    [InlineData("mod_directory=mods", "mod_directory='mods'", true)]
    [InlineData("profiles_directory=profiles", "profiles_directory='profiles'", true)]
    [InlineData("overwrite_directory=overwrite", "overwrite_directory='overwrite'", true)]
    [InlineData("base_directory=", "base_directory=''", false)]
    [InlineData("base_directory=relbase", "base_directory='relbase'", false)]
    public void AnEmptyOrRelativeValueRefusesNamingTheKey(string line, string named, bool baseDirHint)
    {
        var (ok, paths, problems) = Mo2Instance.Validate(Stage("rel", line));

        Assert.False(ok);
        Assert.Null(paths);
        var said = Assert.Single(problems, s => s.Contains(named));
        Assert.Contains("not a full path", said);
        Assert.Equal(baseDirHint, said.Contains("%BASE_DIR%/"));
    }

    // The instance folder the caller gave is read as given, as main reads it: a relative one is not the ini's fault.
    [Fact]
    public void ARelativeInstanceFolderWithBaseDirIsReadAsGiven()
    {
        _root = Path.Combine(Environment.CurrentDirectory, "hc-mo2-dirkeys-" + Guid.NewGuid().ToString("N"));   // same drive as the cwd, so a relative path to it exists
        var inst = Stage("relinst", "mod_directory=%BASE_DIR%/mods");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, inst);
        Assert.False(Path.IsPathFullyQualified(relative));

        AssertSamePath(Path.Combine(inst, "mods"), Mo2Instance.Resolve(relative).ModsDir);
    }

    // Every %BASE_DIR% in a value is replaced, as MO2's QString::replace does; only a relative base keeps the twice-expanded
    // value a real folder path.
    [Fact]
    public void EveryBaseDirInAValueIsExpanded()
    {
        _root = Path.Combine(Environment.CurrentDirectory, "hc-mo2-dirkeys-" + Guid.NewGuid().ToString("N"));   // same drive as the cwd, so a relative path to it exists
        var inst = Stage("twice", "mod_directory=%BASE_DIR%/%BASE_DIR%/mods");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, inst);
        var expected = Path.Combine(inst, relative, "mods");
        Directory.CreateDirectory(expected);

        AssertSamePath(expected, Mo2Instance.Resolve(relative).ModsDir);
    }

    // An instance with no keys keeps the plain Path.Combine text for each root, not normalised: a forward-slashed instance
    // folder prints as given plus \mods (the dev instance's shape).
    [Fact]
    public void AnUnsetKeyKeepsTheCombinedTextExactly()
    {
        var inst = Stage("raw").Replace('\\', '/');

        var p = Mo2Instance.Resolve(inst);

        Assert.Equal(inst + @"\mods", p.ModsDir);
        Assert.Equal(inst + @"\profiles\Default", p.ProfileDir);
        Assert.Equal(inst + @"\overwrite", p.OverwriteDir);
    }

    // Promise: "base_directory set but missing: no fallback to the instance folder" — a base nothing resolves under
    // refuses nothing (all three keys absolute), and so does an empty or relative one.
    [Theory]
    [InlineData("base_directory={STORE}/gone")]
    [InlineData("base_directory=relbase")]
    [InlineData("base_directory=")]
    public void AMissingBaseNothingResolvesUnderRefusesNothing(string baseLine)
    {
        var inst = Stage("ghost", baseLine, "mod_directory={STORE}/mods", "profiles_directory={STORE}/profiles",
                         "overwrite_directory={STORE}/overwrite");

        var (ok, _, problems) = Mo2Instance.Validate(inst);

        Assert.True(ok);
        Assert.Empty(problems);
    }

    // Promise: "Missing mods or profile folder from a key: refused, naming the key".
    [Theory]
    [InlineData("mod_directory={STORE}/nomods", "the mods folder is missing", "mod_directory='")]
    [InlineData("profiles_directory={STORE}/noprofiles", "the active profile's folder is missing", "profiles_directory='")]
    public void AMissingFolderFromAKeyRefusesNamingTheKey(string line, string what, string key)
    {
        var (ok, _, problems) = Mo2Instance.Validate(Stage("miss", line));

        Assert.False(ok);
        Assert.Contains(problems, s => s.Contains(what) && s.Contains(key));
    }

    // Promise: "Missing overwrite from a key: not refused; the setup text says MO2 creates it when it next starts".
    [Fact]
    public void AMissingOverwriteFromAKeyIsNotRefused()
    {
        var p = Mo2Instance.Resolve(Stage("novw", "mod_directory={STORE}/mods", "profiles_directory={STORE}/profiles",
                                          "overwrite_directory={STORE}/notyet"));

        AssertSamePath(Path.Combine(Store("novw"), "notyet"), p.OverwriteDir);
        Assert.Contains("MO2 creates it when it next starts", SetupTools.Render(p, persisted: true, persistError: null, persistNote: null));
    }

    // Promise: "A key in a section other than [Settings] is ignored", before [Settings] or after it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKeyOutsideSettingsIsIgnored(bool before)
    {
        var other = "[Other]\r\nmod_directory=" + Store("sect").Replace('\\', '/') + "/mods\r\n";
        var inst = before ? StageWith("sect", other) : StageWith("sect", "", other);

        AssertSamePath(Path.Combine(inst, "mods"), Mo2Instance.Resolve(inst).ModsDir);
    }

    /// <summary>A service-ready moved instance: real Mutagen plugins in store/mods/MasterMod and in a second mods folder
    /// store/mods2/MasterMod, the profile under store/profiles listing the master, plus a sibling profile Second.</summary>
    (string Inst, ModKey Master) StageService(string name)
    {
        var inst = Stage(name, "mod_directory={STORE}/mods", "profiles_directory={STORE}/profiles");
        var key = new ModKey("HcDirKeysMaster", ModType.Master);
        foreach (var mods in new[] { "mods", "mods2" })
        {
            var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            m.Weapons.AddNew().EditorID = "HcDirKeysW";
            var dir = Path.Combine(Store(name), mods, "MasterMod");
            Directory.CreateDirectory(dir);
            m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        File.Delete(Path.Combine(_root, "game", "Data", "Skyrim.esm"));
        foreach (var prof in new[] { "Default", "Second" })
            WriteProfile(Path.Combine(Store(name), "profiles", prof), key.FileName.String, "MasterMod", master: null);
        return (inst, key);
    }

    // Promise: "the profile list is that folder's profiles"; "A key changed in ModOrganizer.ini while running is picked up
    // on the next call"; "A new patch's folder is created under the moved mods folder".
    [Fact]
    public void TheServiceFollowsTheMovedFolders()
    {
        var (inst, _) = StageService("svc");
        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, "user.json")));

        Assert.Equal(new[] { "Default", "Second" }, svc.NamedProfileComposition(null).AvailableProfiles);
        var status = svc.StatusData();
        Assert.Equal(1, status.ResolvedPluginCount);
        Assert.Empty(status.Warnings);

        var folder = svc.OutputArea.ResolvePatchModFolder("HcDirKeysPatch", null, "HcDirKeysDefault", null);
        Assert.StartsWith(Path.Combine(Store("svc"), "mods"), Path.GetFullPath(folder.ModFolder), StringComparison.OrdinalIgnoreCase);

        var ini = Mo2Instance.IniPath(inst);
        File.WriteAllText(ini, File.ReadAllText(ini).Replace("/mods\r\n", "/mods2\r\n"));
        File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddHours(1));
        svc.StatusData();

        AssertSamePath(Path.Combine(Store("svc"), "mods2"), svc.ModsRootOrNull!);
    }
}
