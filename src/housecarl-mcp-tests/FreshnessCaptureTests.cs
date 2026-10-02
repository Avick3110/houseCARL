using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Freshness sees every change and each answer comes from one build: a restored backup with older mtimes
/// is re-read, a backdated ini profile switch is followed, a status line never mixes two builds, one write resolves
/// every edit against one build, and a read during a write serves the last snapshot and defers its refresh.</summary>
[Trait("tier", "integration")]
[Collection(SerialCollection.Name)]   // parks writes on the process-wide write seams
public sealed class FreshnessCaptureTests : IDisposable
{
    const int Pad = 1500;   // master weapons, so a write has a real window to park in
    const int OvN = 150;    // the overridden subset the write edits

    static readonly ModKey MasterKey = new("HcFcgMaster", ModType.Master);
    static readonly ModKey OvKey = new("HcFcgOverride", ModType.Plugin);
    static readonly ModKey ExtraKey = new("HcFcgExtra", ModType.Plugin);
    static readonly string MasterName = MasterKey.FileName.String, OvName = OvKey.FileName.String, ExtraName = ExtraKey.FileName.String;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    readonly string _root;
    readonly string _masterFile, _fullFile, _emptyFile, _extraFile;
    readonly List<FormKey> _fks = new(Pad);

    public FreshnessCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-freshness-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        var bytes = Path.Combine(_root, "plugin-bytes");
        Directory.CreateDirectory(Path.Combine(bytes, "full"));
        Directory.CreateDirectory(Path.Combine(bytes, "empty"));

        var master = new SkyrimMod(MasterKey, SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Pad; i++)
        {
            var w = master.Weapons.AddNew(); w.EditorID = $"HcFcgW{i:D3}";
            w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
            _fks.Add(w.FormKey);
        }
        _masterFile = Path.Combine(bytes, MasterName);
        master.BeginWrite.ToPath(_masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // FULL override: the first OvN weapons at Damage 20, the build marker the write test reads back.
        var full = new SkyrimMod(OvKey, SkyrimRelease.SkyrimSE);
        foreach (var w in master.Weapons.Take(OvN))
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(full, w)).BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
        _fullFile = Path.Combine(bytes, "full", OvName);
        full.BeginWrite.ToPath(_fullFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        // EMPTY override: same name, no records, so the master wins everything.
        _emptyFile = Path.Combine(bytes, "empty", OvName);
        new SkyrimMod(OvKey, SkyrimRelease.SkyrimSE).BeginWrite.ToPath(_emptyFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var extra = new SkyrimMod(ExtraKey, SkyrimRelease.SkyrimSE);
        extra.Weapons.AddNew().EditorID = "HcFcgExtraW";
        _extraFile = Path.Combine(bytes, ExtraName);
        extra.BeginWrite.ToPath(_extraFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose()
    {
        WritePatchBuilder.InsidePhase1ResolveForGuard = null;
        RecordWrites.InsideWriteGateForGuard = null;
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    string NewInstance(string name)
    {
        var inst = Path.Combine(_root, name);
        var mods = Path.Combine(inst, "mods");
        Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
        File.Copy(_masterFile, Path.Combine(mods, "MasterMod", MasterName));
        Directory.CreateDirectory(Path.Combine(mods, "ExtraMod"));
        File.Copy(_extraFile, Path.Combine(mods, "ExtraMod", ExtraName));
        WriteIni(inst, "Default");
        return inst;
    }

    void WriteIni(string inst, string profile) =>
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

    static void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(profDir);
        File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void MasterOnly(string prof) => WriteProfile(prof, new[] { MasterName }, new[] { "*" + MasterName }, new[] { "+MasterMod" });
    static void MasterAndExtra(string prof) => WriteProfile(prof, new[] { MasterName, ExtraName },
        new[] { "*" + MasterName, "*" + ExtraName }, new[] { "+ExtraMod", "+MasterMod" });

    LoadOrderService NewService(string inst, string tag) =>
        LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(_root, $"user-{tag}.json")));

    // Probe arm 1: "the restored profile (different content, older mtimes) was re-resolved — 2/2 plugins".
    [Fact]
    public void ARestoredBackupProfileWithOlderMtimes_IsReResolved()
    {
        var inst = NewInstance("inst-f8");
        var prof = Path.Combine(inst, "profiles", "Default");
        MasterOnly(prof);
        using var svc = NewService(inst, "f8");
        Assert.Equal(1, svc.Stats().plugins);

        MasterAndExtra(prof);
        foreach (var f in new[] { "loadorder.txt", "plugins.txt", "modlist.txt" })
            File.SetLastWriteTimeUtc(Path.Combine(prof, f), DateTime.UtcNow.AddHours(-2));
        Assert.Equal(2, svc.Stats().plugins);
    }

    // Probe arm 2: "the backdated ini's profile switch was followed — profile=Second, 2/2 plugins".
    [Fact]
    public void ABackdatedIniProfileSwitchAfterSetInstance_IsFollowed()
    {
        var inst = NewInstance("inst-f7");
        MasterOnly(Path.Combine(inst, "profiles", "Default"));
        MasterAndExtra(Path.Combine(inst, "profiles", "Second"));
        using var svc = NewService(inst, "f7");
        svc.SetInstance(inst);                                // this path stamps the ini baseline
        Assert.Equal(1, svc.Stats().plugins);
        Assert.Equal("Default", svc.ProfileName);

        WriteIni(inst, "Second");
        File.SetLastWriteTimeUtc(Path.Combine(inst, "ModOrganizer.ini"), DateTime.UtcNow.AddHours(-2));
        Assert.Equal(2, svc.Stats().plugins);
        Assert.Equal("Second", svc.ProfileName);
    }

    // Probe arm 3: "no torn status line across N concurrent reads". Strengthened: the run must also have read
    // both builds, so a hammer whose flips never landed cannot pass on zero observations. It hammers for the probe's
    // 4 s, then keeps going until both builds have been read, up to a minute, so a starved runner waits instead of failing.
    [Fact]
    public async Task AStatusLineUnderConcurrentFlips_NeverMixesTwoBuilds()
    {
        var inst = NewInstance("inst-f6");
        var prof = Path.Combine(inst, "profiles", "Default");
        MasterAndExtra(prof);
        using var svc = NewService(inst, "f6");
        svc.Stats();
        var lo = Path.Combine(prof, "loadorder.txt");
        // Every whole build is (1 plugin, Ghost warning) or (2 plugins, no warning); any other pair is torn.
        var stateX = "# header\r\n" + MasterName + "\r\nGhost.esp\r\n";
        var stateY = "# header\r\n" + MasterName + "\r\n" + ExtraName + "\r\n";
        int torn = 0, sawOne = 0, sawTwo = 0;
        using var stop = new CancellationTokenSource();
        var flipper = Task.Run(() =>
        {
            bool x = true;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    File.WriteAllText(lo + ".tmp", x ? stateX : stateY);
                    File.Move(lo + ".tmp", lo, overwrite: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                x = !x;
                Thread.Sleep(2);
            }
        });
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                LoadOrderStatusData s;
                try { s = svc.StatusData(); }
                catch (IOException) { continue; }                  // a read colliding with the replace fails loud, not torn
                catch (UnauthorizedAccessException) { continue; }
                bool ghost = s.Warnings.Any(w => w.Contains("Ghost.esp", StringComparison.OrdinalIgnoreCase));
                if ((s.ResolvedPluginCount == 2 && ghost) || (s.ResolvedPluginCount == 1 && !ghost)) Interlocked.Increment(ref torn);
                if (s.ResolvedPluginCount == 1) Interlocked.Increment(ref sawOne);
                if (s.ResolvedPluginCount == 2) Interlocked.Increment(ref sawTwo);
            }
        })).Append(flipper).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(1)
               && (clock.Elapsed < TimeSpan.FromSeconds(4) || Volatile.Read(ref sawOne) == 0 || Volatile.Read(ref sawTwo) == 0))
            await Task.Delay(50);
        stop.Cancel();
        await Task.WhenAll(tasks);

        Assert.Equal(0, torn);
        Assert.True(sawOne > 0 && sawTwo > 0, $"both builds read: 1-plugin {sawOne}, 2-plugin {sawTwo}");
    }

    static bool TryCopy(string src, string dst)
    {
        for (int i = 0; i < 40; i++)
        {
            try { File.Copy(src, dst, overwrite: true); return true; }
            catch (IOException) { Thread.Sleep(5); }
        }
        return false;
    }

    // Probe arm 4: "every round landed the flip inside Apply's Phase-1 loop", "every round produced a patch to judge",
    // and "no successful patch mixed two builds' winners".
    [Fact]
    public async Task OneMultiEditWrite_ResolvesEveryEditAgainstOneBuild()
    {
        var dir = Path.Combine(_root, "core-f5");
        Directory.CreateDirectory(dir);
        var mPath = Path.Combine(dir, MasterName); File.Copy(_masterFile, mPath);
        var oPath = Path.Combine(dir, OvName); File.Copy(_emptyFile, oPath);
        using var resolver = LoadOrderResolver.Build(new[] { mPath, oPath });
        var edits = _fks.Take(OvN).Select(fk => new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = new[] { "BasicStats", "Weight" }, Verb = "Set", Value = "7",
        }).ToList();
        var want = _fks.Take(OvN).ToHashSet();

        const int Rounds = 12;
        int staged = 0, mixed = 0, successes = 0;
        for (int r = 0; r < Rounds; r++)
        {
            TryCopy(_emptyFile, oPath);                         // reset: the master wins everything (Damage 10)
            resolver.RefreshIfStale();
            var outDir = Path.Combine(dir, $"out_{r:D3}");
            Directory.CreateDirectory(outDir);
            WritePatchBuilder.PatchOutcome o;
            using (var inLoop = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                WritePatchBuilder.InsidePhase1ResolveForGuard = () => { inLoop.Set(); release.Wait(); };
                Task<WritePatchBuilder.PatchOutcome> wt;
                try
                {
                    wt = Task.Run(() => WritePatchBuilder.Apply(resolver, TestCorpus.Rulebook, edits, Path.Combine(outDir, "HcFcgOut.esp"), extend: false));
                    bool parked = WaitHandle.WaitAny(new[] { inLoop.WaitHandle, ((IAsyncResult)wt).AsyncWaitHandle }, Timeout) == 0;
                    // Counted on the rebuild: a copy that changed nothing swaps no build and stages nothing.
                    if (parked && TryCopy(_fullFile, oPath) && resolver.RefreshIfStale()) staged++;
                }
                finally
                {
                    WritePatchBuilder.InsidePhase1ResolveForGuard = null;
                    release.Set();
                }
                o = await wt.WaitAsync(Timeout);                  // a write that never joins fails the test by timing out
            }
            Assert.True(o.Success, $"round {r}: {o.Error}");
            successes++;
            using var back = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
            var marks = back.Weapons.Where(w => want.Contains(w.FormKey)).Select(w => w.BasicStats?.Damage ?? 0).ToHashSet();
            if (marks.Count > 1) mixed++;
        }
        Assert.Equal(Rounds, staged);
        Assert.Equal(Rounds, successes);
        Assert.Equal(0, mixed);
    }

    // Probe arm 5: "the mid-write read served the last good snapshot (refresh deferred, no mid-write rebuild)" and
    // "the deferred refresh ran on the NEXT call — 2/2 plugins".
    [Fact]
    public async Task AReadDuringAWrite_ServesTheLastSnapshotAndTheNextCallRefreshes()
    {
        var inst = NewInstance("inst-defer");
        var prof = Path.Combine(inst, "profiles", "Default");
        MasterOnly(prof);
        using var svc = NewService(inst, "defer");
        Assert.Equal(1, svc.Stats().plugins);
        var ops = _fks.Skip(OvN).Take(250).Select(fk => new BulkOp
        {
            Formid = $"{fk.ID:X6}:{fk.ModKey.FileName}", FieldPath = "BasicStats.Weight", Verb = "Set", Value = "9",
        }).ToList();

        int during = -1; bool parked, midFlight = false;
        Task<WritePatchBuilder.PatchOutcome> wt;
        WritePatchBuilder.PatchOutcome outcome;
        using (var inGate = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            RecordWrites.InsideWriteGateForGuard = () => { inGate.Set(); release.Wait(); };
            try
            {
                wt = Task.Run(() => svc.ApplyEdits(ops, "HcFcgDefer", null));
                parked = WaitHandle.WaitAny(new[] { inGate.WaitHandle, ((IAsyncResult)wt).AsyncWaitHandle }, Timeout) == 0;
                if (parked)
                {
                    MasterAndExtra(prof);                       // an MO2 toggle arrives mid-write
                    during = svc.Stats().plugins;
                    midFlight = !wt.IsCompleted;
                }
            }
            finally
            {
                RecordWrites.InsideWriteGateForGuard = null;
                release.Set();
            }
            outcome = await wt.WaitAsync(Timeout);              // joins once released, or fails by timing out
        }
        Assert.True(parked && midFlight, "a read completed while the write was in flight");
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(1, during);
        Assert.Equal(2, svc.Stats().plugins);
    }
}
