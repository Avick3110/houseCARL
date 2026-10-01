using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A captured index view keeps answering from the build it was captured on after a rebuild, so one operation
/// never mixes two builds; a new capture sees the new build; and the service's reads agree with the build that answered
/// them. A master defines W1 and W2; an override plugin first overrides W1, then is rewritten to define W3 instead,
/// which flips the winner, depth, touching list and every counter.</summary>
[Trait("tier", "integration")]
public sealed class SnapshotViewPinningTests : IDisposable
{
    const string MasterName = "hcSnapMaster.esp", OvrName = "hcSnapOvr.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-snapshot-view-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderResolver _resolver;
    readonly LoadOrderResolver.IndexView _old;
    readonly FormKey _w1;
    readonly bool _rebuilt;

    public SnapshotViewPinningTests()
    {
        Directory.CreateDirectory(_dir);
        var masterPath = Path.Combine(_dir, MasterName);
        var ovrPath = Path.Combine(_dir, OvrName);
        var master = new SkyrimMod(ModKey.FromNameAndExtension(MasterName), SkyrimRelease.SkyrimSE);
        var w1 = master.Weapons.AddNew(); w1.EditorID = "hcSnapW1"; _w1 = w1.FormKey;
        master.Weapons.AddNew().EditorID = "hcSnapW2";
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var ovr1 = new SkyrimMod(ModKey.FromNameAndExtension(OvrName), SkyrimRelease.SkyrimSE);
        WriteEngine.GenericGetOrAddAsOverride(ovr1, w1);
        ovr1.BeginWrite.ToPath(ovrPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        _resolver = LoadOrderResolver.Build(new[] { masterPath, ovrPath });
        _old = _resolver.Capture();

        var ovr2 = new SkyrimMod(ModKey.FromNameAndExtension(OvrName), SkyrimRelease.SkyrimSE);
        ovr2.Weapons.AddNew().EditorID = "hcSnapW3";
        ovr2.BeginWrite.ToPath(ovrPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.SetLastWriteTimeUtc(ovrPath, DateTime.UtcNow.AddSeconds(2));
        _rebuilt = _resolver.RefreshIfStale();
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
    }

    static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Probe: "RefreshIfStale rebuilt the index (the mutation was seen)"; CONTROL "a fresh read now answers from the NEW
    // build (winner(W1) = master)"; CONTROL "old view vs fresh read DISAGREE".
    [Fact]
    public void TheRebuildSwapsTheBuildSoTheOldViewAndAFreshReadDisagree()
    {
        Assert.True(_rebuilt);
        var fresh = _resolver.ResolveWinner(_w1);
        Assert.True(fresh is { } f && Eq(f.WinnerPlugin, MasterName) && f.OverrideDepth == 1);
        Assert.False(Eq(_old.ResolveWinner(_w1)?.WinnerPlugin, fresh!.Value.WinnerPlugin));
    }

    // Probe: "old build: winner(W1) = override, depth 2" / "PINNED: viewOld.ResolveWinner(W1) still = override, depth 2";
    // "touching(W1) = [master, override]" / "PINNED: … still = [master, override]"; "counters = 2 records / 1 conflict /
    // maxDepth 2" / "PINNED: … still"; "PINNED: viewOld.ConflictKeys() still yields exactly [W1]". The pre-rebuild checks
    // are the same values read after the rebuild; a view that re-read the live build fails every one.
    [Fact]
    public void TheOldViewStillAnswersAllOldAfterTheRebuild()
    {
        Assert.True(_old.ResolveWinner(_w1) is { } w && Eq(w.WinnerPlugin, OvrName) && w.OverrideDepth == 2);
        var touching = _old.TouchingPlugins(_w1);
        Assert.Equal(2, touching.Count);
        Assert.True(Eq(touching[0], MasterName) && Eq(touching[1], OvrName));
        Assert.Equal((2, 1, 2), (_old.RecordCount, _old.ConflictCount, _old.MaxDepth));
        Assert.Equal(new[] { _w1 }, _old.ConflictKeys().ToList());
    }

    // Probe: "PINNED SCAN: viewOld.RecordsIn(master) yields W1 at the OLD depth 2 (a live re-deref would say 1)";
    // "PINNED SCAN: viewOld.WinnerRecordsOfType yields ONLY W2".
    [Fact]
    public void TheOldViewsScanStreamsRideTheOldBuild()
    {
        var pinnedIn = Assert.Single(_old.RecordsIn(new[] { MasterName }, null).Where(x => x.fk == _w1));
        Assert.Equal(2, pinnedIn.depth);
        var pinnedWin = Assert.Single(_old.WinnerRecordsOfType(new[] { typeof(IWeaponGetter) }));
        Assert.NotEqual(_w1, pinnedWin.fk);
    }

    // Probe: "FRESH: viewNew.ResolveWinner(W1) = master, depth 1"; "FRESH: viewNew.TouchingPlugins(W1) = [master] only";
    // "FRESH: viewNew counters = 3 records / 0 conflicts / maxDepth 0".
    [Fact]
    public void ANewCaptureAnswersAllNew()
    {
        var view = _resolver.Capture();
        Assert.True(view.ResolveWinner(_w1) is { } w && Eq(w.WinnerPlugin, MasterName) && w.OverrideDepth == 1);
        Assert.True(Eq(Assert.Single(view.TouchingPlugins(_w1)), MasterName));
        Assert.Equal((3, 0, 0), (view.RecordCount, view.ConflictCount, view.MaxDepth));
    }

    // Probe: "SERVICE Stats(): one consistent counter set (2 plugins / 3 records / 0 conflicts / maxDepth 0 / 0 failures)";
    // "SERVICE ResolveRead(W1): no error, winner = master, depth 1"; "winner agrees with its OWN touching list";
    // "SERVICE CrossQuery(plugins=[master,ovr]): no error, 3 matches"; "every per-match winner/depth agrees with the build
    // that scanned"; "W1's row reads from the NEW build"; "SERVICE CrossQuery(conflicts_only): 0 matches on the new build".
    [Fact]
    public void TheServiceAnswersOffTheNewBuildConsistently()
    {
        var svc = LoadOrderService.ForGuard(_resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));

        var stats = svc.Stats();
        Assert.Equal((2, 3, 0, 0, 0), (stats.plugins, stats.records, stats.conflicts, stats.maxDepth, stats.loadFailures.Count));

        var read = svc.ReadArea.ResolveRead(_w1, null, null, conflictTree: true);
        Assert.Null(read.Error);
        Assert.True(Eq(read.WinnerPlugin, MasterName) && read.OverrideDepth == 1);
        Assert.True(Eq(Assert.Single(read.TouchingPlugins!), MasterName));

        var q = svc.ReadArea.CrossQuery(type: null, references: null, editoridContains: null, conflictsOnly: false,
                                        plugins: new[] { MasterName, OvrName }, where: null, limit: 500);
        Assert.Null(q.Error);
        Assert.Equal(3, q.Total);
        Assert.Equal(3, q.Prefilled!.Count);
        for (var i = 0; i < q.Keys.Count; i++)
        {
            var cw = _resolver.ResolveWinner(q.Keys[i])!.Value;
            Assert.True(Eq(q.Prefilled[i].Winner, cw.WinnerPlugin) && q.Prefilled[i].OverrideDepth == cw.OverrideDepth);
        }
        var w1Row = q.Prefilled[q.Keys.ToList().IndexOf(_w1)];
        Assert.True(Eq(w1Row.Winner, MasterName) && w1Row.OverrideDepth == 1);

        var conflicts = svc.ReadArea.CrossQuery(type: null, references: null, editoridContains: null, conflictsOnly: true,
                                                plugins: null, where: null, limit: 500);
        Assert.Null(conflicts.Error);
        Assert.Equal(0, conflicts.Total);
    }
}
