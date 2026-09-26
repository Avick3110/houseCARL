using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The errors sweep takes its view and its roots in one hold, the off-order memo answers only for its roots,
/// and one call reads the profile's composition once.</summary>
[Trait("tier", "integration")]
public sealed class ErrorsCheckPinTests : IDisposable
{
    const string MasterName = "HcEpMaster.esm";
    const string PatchName = "HcEpPatch.esp";
    const string OffName = "HcEpOff.esp";

    readonly string _root;
    readonly string _ini;
    readonly string _gameB;
    readonly LoadOrderService _svc;
    Thread? _mover;

    public ErrorsCheckPinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-errors-pin-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(_root, "instance");
        var profileDir = Path.Combine(instance, "profiles", "Default");
        var patchDir = Path.Combine(instance, "mods", "PatchMod");
        var gameA = Path.Combine(_root, "gameA");
        _gameB = Path.Combine(_root, "gameB");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(Path.Combine(gameA, "Data"));
        Directory.CreateDirectory(Path.Combine(_gameB, "Data"));   // the second game folder: no master, no off-order plugin
        _ini = Path.Combine(instance, "ModOrganizer.ini");
        File.WriteAllText(_ini, IniFor(gameA));

        // Game A's Data holds the unchecked master and a plugin in no list; the order holds only the patch, from a mod
        // folder, so moving the game folder moves the roots and leaves the order and its epoch unchanged.
        var master = new SkyrimMod(new ModKey("HcEpMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = master.Races.AddNew(); race.EditorID = "HcEpMasterRace";
        master.BeginWrite.ToPath(Path.Combine(gameA, "Data", MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var off = new SkyrimMod(new ModKey("HcEpOff", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var offRace = off.Races.AddNew(); offRace.EditorID = "HcEpOffRace";
        off.BeginWrite.ToPath(Path.Combine(gameA, "Data", OffName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patch = new SkyrimMod(new ModKey("HcEpPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = patch.Npcs.AddNew(); npc.EditorID = "HcEpPatchNpc"; npc.Race.SetTo(race.FormKey);
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName)).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n+PatchMod\r\n");

        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        _mover?.Join();
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static string IniFor(string game)
        => "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
           + game.Replace(@"\", @"\\") + ")\r\n";

    /// <summary>Points MO2 at game B and lets another call re-derive the roots. Inside the sweep's hold that call waits
    /// for the hold; with the roots taken outside it, it lands before them.</summary>
    void MoveGameFolderFromAnotherCall()
    {
        File.WriteAllText(_ini, IniFor(_gameB));
        _mover = new Thread(() => _svc.CaptureView());
        _mover.Start();
        _mover.Join(TimeSpan.FromSeconds(1));
    }

    ErrorCheckResult SweepWithMoveAfterPin(IReadOnlyList<string>? plugins)
    {
        _svc.CheckArea.AfterCheckPinForGuard = MoveGameFolderFromAnotherCall;
        var r = _svc.CheckErrors(plugins, 1000);
        _svc.CheckArea.AfterCheckPinForGuard = null;
        _mover!.Join();
        return r;
    }

    static IReadOnlyList<string>? InstalledButInactive(ErrorCheckResult r)
        => r.Reports.Single(p => p.Plugin.Equals(PatchName, StringComparison.OrdinalIgnoreCase)).InstalledButInactiveMasters;

    [Fact]
    public void TheOffOrderLocateUsesTheRootsPinnedWithTheView()
    {
        var before = _svc.CheckErrors(new[] { OffName }, 1000);                 // warms the index
        Assert.Null(before.Error);

        var during = SweepWithMoveAfterPin(new[] { OffName });
        Assert.Null(during.Error);
        Assert.Equal(new[] { OffName }, during.OffOrderScanned);
        Assert.Equal(before.Epoch, during.Epoch);

        // The move landed for the next call: game B's Data has no copy.
        Assert.Contains("no on-disk copy", _svc.CheckErrors(new[] { OffName }, 1000).Error);
    }

    [Fact]
    public void TheMissingMasterSplitUsesTheRootsPinnedWithTheView()
    {
        Assert.Equal(new[] { MasterName }, InstalledButInactive(_svc.CheckErrors(null, 1000)));   // warms the index

        var during = SweepWithMoveAfterPin(null);
        Assert.Null(during.Error);
        Assert.Equal(new[] { MasterName }, InstalledButInactive(during));

        // The move landed for the next call: the master is no longer installed.
        Assert.Empty(InstalledButInactive(_svc.CheckErrors(null, 1000))!);
    }

    [Fact]
    public void TheOffOrderMemoRecomputesUnderOtherRoots()
    {
        var view = _svc.CaptureView();
        var rootsA = ((ILoadOrderHost)_svc).CaptureRoots();
        var rootsB = rootsA with { DataDir = Path.Combine(_gameB, "Data") };
        var memo = new SweepOffOrderMemo();
        var plugins = new[] { OffName };

        Assert.Null(SweepOffOrderScope.Split(view, plugins, rootsA, out _, out var offA, memo));
        Assert.Single(offA);

        // Same epoch, same list: only the roots differ, and game B's Data has no copy.
        var refusal = SweepOffOrderScope.Split(view, plugins, rootsB, out _, out _, memo);
        Assert.Contains("no on-disk copy", refusal?.Message);
    }

    [Fact]
    public void OneErrorsCallReadsTheCompositionOnce()
    {
        _svc.CheckErrors(null, 1000);                                           // warms the index
        var before = _svc.CheckArea.CompositionReads;

        // An off-order name, the implicit group and a missing master: three consumers of the composition.
        var r = _svc.CheckErrors(new[] { PatchName, OffName }, 1000, exclude: new[] { SweepExclusion.ImplicitToken });
        Assert.Null(r.Error);
        Assert.Equal(new[] { OffName }, r.OffOrderScanned);
        Assert.Equal(new[] { MasterName }, InstalledButInactive(r));

        Assert.Equal(1, _svc.CheckArea.CompositionReads - before);
    }
}
