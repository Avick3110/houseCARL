using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A master TICKED in <c>plugins.txt</c> with no on-disk copy anywhere in the install — the state MO2 is left
/// in when a mod folder is deleted outside it and the profile keeps the stale tick.
///
/// <para>Its own world because the tick is a property of the PROFILE, not of a plugin: one
/// <c>plugins.txt</c> cannot carry a name both ticked and unticked, so this case cannot share a fixture with
/// the unticked-and-absent one in <see cref="CheckMasterRemedyWorld"/>.</para>
///
/// <list type="bullet">
/// <item><c>Skyrim.esm</c> — in <c>loadorder.txt</c>, absent from <c>plugins.txt</c>, so force-loaded and
///   SATISFIED. The control that keeps a claim about the absent master from being a blanket one.</item>
/// <item><c>HcTaAbsent.esm</c> — listed in <c>loadorder.txt</c> AND ticked in <c>plugins.txt</c>, while the
///   file itself was written outside the instance (no mod folder, no overwrite layer, not game Data). It is
///   active by the profile's account and installed nowhere. Its remedy is install.</item>
/// </list>
/// </summary>
public sealed class CheckMasterTickedButAbsentWorld : IDisposable
{
    public string Root { get; }
    public string Instance { get; }

    /// <summary>The plugin declaring the ticked-but-absent master.</summary>
    public string PatchName => "HcTaPatch.esp";
    /// <summary>Ticked in plugins.txt, installed nowhere — the stale-tick case.</summary>
    public string AbsentName => "HcTaAbsent.esm";

    public LoadOrderService Svc { get; }

    public CheckMasterTickedButAbsentWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-check-master-ticked-absent-" + Guid.NewGuid().ToString("N"));
        Instance = Path.Combine(Root, "instance");
        var profileDir = Path.Combine(Instance, "profiles", "Default");
        var mods = Path.Combine(Instance, "mods");
        var outside = Path.Combine(Root, "not-installed");   // deliberately NOT under mods/, Data, or overwrite
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var vanillaDir = Path.Combine(mods, "VanillaStub");
        var patchDir = Path.Combine(mods, "PatchMod");
        foreach (var d in new[] { vanillaDir, patchDir }) Directory.CreateDirectory(d);

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var skyRace = sky.Races.AddNew(); skyRace.EditorID = "HcTaVanillaRace";
        sky.BeginWrite.ToPath(Path.Combine(vanillaDir, "Skyrim.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var absent = new SkyrimMod(new ModKey("HcTaAbsent", ModType.Master), SkyrimRelease.SkyrimSE);
        var absentRace = absent.Races.AddNew(); absentRace.EditorID = "HcTaAbsentRace";
        absent.BeginWrite.ToPath(Path.Combine(outside, AbsentName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // One NPC per master, so Mutagen writes both into the patch's master table.
        var patch = new SkyrimMod(new ModKey("HcTaPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var pSky = patch.Npcs.AddNew(); pSky.EditorID = "HcTaPatchSkyNpc"; pSky.Race.SetTo(skyRace.FormKey);
        var pAbsent = patch.Npcs.AddNew(); pAbsent.EditorID = "HcTaPatchAbsentNpc"; pAbsent.Race.SetTo(absentRace.FormKey);
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName))
             .WithLoadOrder(new ISkyrimModGetter[] { sky, absent }).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"),
            "# header\r\nSkyrim.esm\r\n" + AbsentName + "\r\n" + PatchName + "\r\n");
        // THE STALE TICK: the profile says the absent master is checked and loading. Nothing on disk backs it.
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), "*" + AbsentName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"),
            "# header\r\n+PatchMod\r\n+VanillaStub\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"));
        Svc = LoadOrderService.WithInstance(Instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The world, built once for the class. Every test below is read-only over it.</summary>
public sealed class CheckMasterTickedButAbsentFixture : IDisposable
{
    public CheckMasterTickedButAbsentWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>
/// A declared master that is ticked in <c>plugins.txt</c> but has no on-disk copy is reported, and it is
/// reported as NOT INSTALLED.
///
/// <para>Whether a master will load is two facts, not one: the profile has to list it as active AND the
/// install has to provide a file. A stale tick asserts the first with the second false, so a filter testing
/// only the active half drops the name before anything looks for the file and the plugin's refs into it
/// dangle with nothing said.</para>
///
/// <para>Driven through <see cref="CheckTools.CheckTool"/> because it is the method the MCP server publishes
/// and binds arguments into; the stdio <c>ServerFixture</c> runs an unconfigured server that answers before
/// any value is interpreted, so no wire-driven call there reaches a sweep.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckMasterTickedButAbsentTests : IClassFixture<CheckMasterTickedButAbsentFixture>
{
    readonly CheckMasterTickedButAbsentWorld W;
    public CheckMasterTickedButAbsentTests(CheckMasterTickedButAbsentFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    const string InstallRemedy = "missing master(s) NOT installed anywhere in the MO2 install: ";
    const string EnableRemedy = "missing master(s) installed but NOT ACTIVE in the load order (in a disabled mod, or unchecked): ";

    static string? LineWith(string response, string prefix) =>
        response.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary><c>housecarl_check</c>'s missing-master remedy names the ticked-but-absent master on the INSTALL
    /// line: the built active order drops a listed name nothing provides, so the name arrives at the splitter
    /// already unsatisfied, and no copy exists anywhere, so "enable it" would be a remedy that cannot work.</summary>
    [Fact]
    public void TheCheckRemedyNamesTheTickedButAbsentMasterOnTheInstallLine()
    {
        var text = CheckTools.CheckTool(Svc, plugins: new[] { W.PatchName }, findings: new[] { "missing_masters" });

        var install = LineWith(text, InstallRemedy);
        Assert.NotNull(install);
        Assert.Contains(W.AbsentName, install);
        Assert.Contains("[install them", install);
        // The satisfied master stays out — without this the assertion above would pass over a remedy that named
        // every declared master as unsatisfied.
        Assert.DoesNotContain("Skyrim.esm", install);
        // Nothing in this install is merely disabled, so the enable line must not be printed at all.
        Assert.Null(LineWith(text, EnableRemedy));
    }
}
