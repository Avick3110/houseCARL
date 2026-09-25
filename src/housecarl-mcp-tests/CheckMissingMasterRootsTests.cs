using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The errors sweep judges a missing master's installed-but-inactive standing against the roots it swept
/// with, even when MO2 moves the game folder between that capture and the end of the sweep.</summary>
[Trait("tier", "integration")]
public sealed class CheckMissingMasterRootsTests : IDisposable
{
    const string MasterName = "HcMrMaster.esm";
    const string PatchName = "HcMrPatch.esp";

    readonly string _root;
    readonly string _ini;
    readonly string _gameB;
    readonly LoadOrderService _svc;

    public CheckMissingMasterRootsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-check-master-roots-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var patchDir = Path.Combine(instance, "mods", "PatchMod");
        var gameA = Path.Combine(_root, "gameA");
        _gameB = Path.Combine(_root, "gameB");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(Path.Combine(gameA, "Data"));
        Directory.CreateDirectory(Path.Combine(_gameB, "Data"));   // the second game folder: no copy of the master
        _ini = Path.Combine(instance, "ModOrganizer.ini");
        File.WriteAllText(_ini, IniFor(gameA));

        // The master sits only in game A's Data and is unchecked, so it is installed but not active; the order holds
        // only the patch, from a mod folder, so moving the game folder leaves the order unchanged.
        var master = new SkyrimMod(new ModKey("HcMrMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = master.Races.AddNew(); race.EditorID = "HcMrMasterRace";
        var masterPath = Path.Combine(gameA, "Data", MasterName);
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patch = new SkyrimMod(new ModKey("HcMrPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = patch.Npcs.AddNew(); npc.EditorID = "HcMrPatchNpc"; npc.Race.SetTo(race.FormKey);
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+PatchMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string IniFor(string game)
        => "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
           + game.Replace(@"\", @"\\") + ")\r\n";

    /// <summary>Points MO2 at game B and makes the head re-derive now, on this thread and outside any hold.</summary>
    void MoveGameFolder()
    {
        File.WriteAllText(_ini, IniFor(_gameB));
        _svc.CaptureView();
    }

    static IReadOnlyList<string>? InstalledButInactive(ErrorCheckResult r)
        => r.Reports.Single(p => p.Plugin.Equals(PatchName, StringComparison.OrdinalIgnoreCase)).InstalledButInactiveMasters;

    [Fact]
    public void TheMissingMasterSplitIsJudgedAgainstTheRootsTheSweepCaptured()
    {
        var before = _svc.CheckErrors(null, 1000);                               // warms the index
        Assert.Equal(new[] { MasterName }, InstalledButInactive(before));

        // The exclude list is read once, after the sweep's roots capture and before the sweep, so the move lands
        // between the capture and the missing-master split.
        var during = _svc.CheckErrors(null, 1000,
            exclude: new SwitchOnFirstRead(SweepExclusion.ImplicitToken, MoveGameFolder));
        Assert.Null(during.Error);
        Assert.Equal(new[] { MasterName }, InstalledButInactive(during));

        // The move landed for the next call: game B's Data has no copy, so the master is no longer installed.
        var after = _svc.CheckErrors(null, 1000);
        Assert.Empty(InstalledButInactive(after)!);
    }

    /// <summary>A one-item list that runs <paramref name="onFirstRead"/> the first time it is enumerated.</summary>
    sealed class SwitchOnFirstRead(string item, Action onFirstRead) : IReadOnlyList<string>
    {
        bool _fired;
        public int Count => 1;
        public string this[int index] => index == 0 ? item : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            if (!_fired) { _fired = true; onFirstRead(); }
            yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
