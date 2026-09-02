using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Freshness + write-capture guard (2026-06-12 adversarial hunt F5–F8 + the PR #51 review note): the freshness
/// machinery is what lets houseCARL promise "the answer reflects the CURRENT MO2 state" without a daemon — so a
/// freshness check that misses a change (F7/F8), or an answer composed from TWO adjacent builds (F5/F6), is a
/// silent wrong answer (Q3), not a perf nit. Arms:
///
///   1  F8/profile — MO2 "Restore Backup" rewrites the profile files with OLDER mtimes; the wall-clock
///      `mtime &gt; builtUtc` check is blind to a regression, so the restored order stays invisible for the
///      process lifetime. The fix compares last-SEEN mtimes by VALUE (!=), like the resolver always has.
///      Deterministic RED.
///   2  F7+F8/ini — SetInstance stamped its ini baseline AFTER reading the instance (the one stamp-after in
///      the file), and the profile-switch check used `&lt;=`; a backdated/restored ModOrganizer.ini profile
///      switch is invisible forever. Deterministic RED.
///   3  F6/status — housecarl_load_order_status read the resolver's view and then the per-build fields
///      (warnings / staleness / profile dir) OUTSIDE the gate, with file I/O in between — a concurrent
///      freshness rebuild lands in that gap and the one status line mixes two builds. Hammer: concurrent
///      readers + an atomic loadorder flipper; ANY (count, warning) pair that no single build produces = torn.
///   4  F5/write — WritePatchBuilder.Apply Phase 1 resolved winner + body PER EDIT (a fresh capture each), so
///      a freshness rebuild landing mid-loop resolved two edits of ONE call against two builds' winners — a
///      silently MIXED patch. Hammer at the core layer: a flipper rewrites the override plugin + refreshes
///      mid-Apply; every SUCCESSFUL patch must carry ONE build's bodies (uniform marker values).
///   5  Deferral (PR #51 review note) — a concurrent read's freshness refresh used to rebuild/swap the index
///      UNDER an in-flight write (transiently mmap-opening every plugin INCLUDING the file the write is
///      serializing — the #24 "no mapped handle on the target survives the serialize" invariant, breached
///      from the read path). The fix defers the read-path refresh while a write holds the write gate: a
///      mid-write read serves the last good snapshot; the NEXT call refreshes. Deterministic given a write
///      long enough to straddle the read (retried; judged only when the read provably finished mid-write).
///
/// Self-contained: synthetic MO2 instances + synthesized plugins in temp; generates its own corpus. No game data.
///
/// Standalone by necessity: in the warm ci-all runner (hot JIT, memoized corpus) the write finishes before the
/// read can land inside it, so arm 5 holds only in a cold process and CI gives this guard its own step.
/// </summary>
internal static class FreshnessCaptureProbe
{
    [CiProbe("freshness-capture-guard", Standalone = true)]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" freshness-capture guard — one build per answer; no missed change");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-freshness-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));   // the ini's gamePath target

            // ---- synthesized plugin bytes, built once and copied into per-arm fixtures ----
            var masterKey = new ModKey("HcFcgMaster", ModType.Master);
            var ovKey = new ModKey("HcFcgOverride", ModType.Plugin);
            var extraKey = new ModKey("HcFcgExtra", ModType.Plugin);
            string masterName = masterKey.FileName.String, ovName = ovKey.FileName.String, extraName = extraKey.FileName.String;
            const int Pad = 1500;   // master record count — pads per-edit body fetches so arms 4/5 have a real time window
            const int OvN = 150;    // the overridden/edited subset arm 4 judges

            var bytes = Path.Combine(root, "plugin-bytes");
            Directory.CreateDirectory(Path.Combine(bytes, "full"));
            Directory.CreateDirectory(Path.Combine(bytes, "empty"));

            var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var fks = new List<FormKey>(Pad);
            for (int i = 0; i < Pad; i++)
            {
                var w = master.Weapons.AddNew(); w.EditorID = $"HcFcgW{i:D3}";
                w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
                fks.Add(w.FormKey);
            }
            var masterFile = Path.Combine(bytes, masterName);
            master.BeginWrite.ToPath(masterFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // override variant FULL: the first OvN weapons re-declared with Damage=20 (the build marker arm 4 reads)
            var fullMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
            foreach (var w in master.Weapons.Take(OvN))
                ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(fullMod, w)).BasicStats = new WeaponBasicStats { Damage = 20, Weight = 1 };
            var fullFile = Path.Combine(bytes, "full", ovName);
            fullMod.BeginWrite.ToPath(fullFile).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            // override variant EMPTY: same ModKey, no records — the master wins everything
            var emptyMod = new SkyrimMod(ovKey, SkyrimRelease.SkyrimSE);
            var emptyFile = Path.Combine(bytes, "empty", ovName);
            emptyMod.BeginWrite.ToPath(emptyFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var extraMod = new SkyrimMod(extraKey, SkyrimRelease.SkyrimSE);
            var xw = extraMod.Weapons.AddNew(); xw.EditorID = "HcFcgExtraW"; xw.BasicStats = new WeaponBasicStats { Damage = 5 };
            var extraFile = Path.Combine(bytes, extraName);
            extraMod.BeginWrite.ToPath(extraFile).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // The service's pre-flight rulebook loads from the static CorpusPath — generate one fresh (CI has no repo state).
            var genDir = Path.Combine(root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            // ---- per-arm fixture helpers ----
            string NewInstance(string name)
            {
                var inst = Path.Combine(root, name);
                var mods = Path.Combine(inst, "mods");
                Directory.CreateDirectory(Path.Combine(mods, "MasterMod"));
                File.Copy(masterFile, Path.Combine(mods, "MasterMod", masterName));
                Directory.CreateDirectory(Path.Combine(mods, "ExtraMod"));
                File.Copy(extraFile, Path.Combine(mods, "ExtraMod", extraName));
                WriteIni(inst, "Default");
                return inst;
            }
            void WriteIni(string inst, string profile) =>
                File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + profile + ")\r\ngamePath=@ByteArray("
                    + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
            void WriteProfile(string profDir, string[] loadorder, string[] plugins, string[] modlist)
            {
                Directory.CreateDirectory(profDir);
                File.WriteAllText(Path.Combine(profDir, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
                File.WriteAllText(Path.Combine(profDir, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
                File.WriteAllText(Path.Combine(profDir, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
            }
            string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

            // ---- 1: F8/profile — a restored-backup profile (newer content, OLDER mtimes) must be seen ----
            Console.WriteLine("--- 1: MO2 'Restore Backup' on the profile files (older mtimes) is picked up (hunt F8) ---");
            {
                var inst = NewInstance("inst-f8");
                var prof = Path.Combine(inst, "profiles", "Default");
                WriteProfile(prof, new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
                var store = new UserConfigStore(Path.Combine(root, "user-f8.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                var before = svc.Stats().plugins;
                Check(before == 1, $"baseline order resolved — {before}/1 plugin");

                var backdate = DateTime.UtcNow.AddHours(-2);          // the restored backup pre-dates the build baseline
                WriteProfile(prof, new[] { masterName, extraName },
                             new[] { "*" + masterName, "*" + extraName }, new[] { "+ExtraMod", "+MasterMod" });
                foreach (var f in new[] { "loadorder.txt", "plugins.txt", "modlist.txt" })
                    File.SetLastWriteTimeUtc(Path.Combine(prof, f), backdate);
                var after = svc.Stats().plugins;
                Check(after == 2, $"the restored profile (different content, older mtimes) was re-resolved — {after}/2 plugins");
            }

            // ---- 2: F7+F8/ini — a backdated ModOrganizer.ini profile switch after SetInstance must be seen ----
            Console.WriteLine();
            Console.WriteLine("--- 2: restored-backup ModOrganizer.ini profile switch after SetInstance is followed (hunt F7+F8) ---");
            {
                var inst = NewInstance("inst-f7");
                WriteProfile(Path.Combine(inst, "profiles", "Default"),
                             new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
                WriteProfile(Path.Combine(inst, "profiles", "Second"), new[] { masterName, extraName },
                             new[] { "*" + masterName, "*" + extraName }, new[] { "+MasterMod", "+ExtraMod" });
                var store = new UserConfigStore(Path.Combine(root, "user-f7.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                svc.SetInstance(inst);                                // the F7 path: THIS stamps the ini baseline
                var before = svc.Stats().plugins;
                Check(before == 1 && svc.ProfileName == "Default", $"baseline on profile 'Default' — {before}/1 plugin, profile={svc.ProfileName}");

                WriteIni(inst, "Second");                             // the switch arrives via a restored/backdated ini
                File.SetLastWriteTimeUtc(Path.Combine(inst, "ModOrganizer.ini"), DateTime.UtcNow.AddHours(-2));
                var after = svc.Stats().plugins;
                Check(svc.ProfileName == "Second" && after == 2,
                      $"the backdated ini's profile switch was followed — profile={svc.ProfileName}, {after}/2 plugins");
            }

            // ---- 3: F6/status — concurrent flips never tear one status line across two builds ----
            Console.WriteLine();
            Console.WriteLine("--- 3: status line composes ONE build under concurrent profile flips (hunt F6) ---");
            {
                var inst = NewInstance("inst-f6");
                var prof = Path.Combine(inst, "profiles", "Default");
                // plugins.txt + modlist.txt constant; loadorder.txt flips ATOMICALLY (temp+move) between
                //   X: [master, Ghost.esp] → 1 resolved + the "no enabled mod provides Ghost.esp" warning
                //   Y: [master, extra]     → 2 resolved + no warning
                // so EVERY complete build is (1, ghost-warning) or (2, none) — any other pair is a torn line.
                WriteProfile(prof, new[] { masterName, extraName },
                             new[] { "*" + masterName, "*" + extraName }, new[] { "+MasterMod", "+ExtraMod" });
                var store = new UserConfigStore(Path.Combine(root, "user-f6.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                svc.Stats();                                          // warm the lazy index off the clock
                string lo = Path.Combine(prof, "loadorder.txt");
                string stateX = "# header\r\n" + masterName + "\r\nGhost.esp\r\n";
                string stateY = "# header\r\n" + masterName + "\r\n" + extraName + "\r\n";
                int torn = 0; long reads = 0; long contended = 0;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var flipper = Task.Run(() =>
                {
                    bool x = true;
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            var tmp = lo + ".tmp";
                            File.WriteAllText(tmp, x ? stateX : stateY);
                            File.Move(tmp, lo, overwrite: true);      // atomic: a reader sees old or new, never partial
                        }
                        catch (IOException) { }                       // reader had it open — flip again next tick
                        catch (UnauthorizedAccessException) { }       // same race, surfaced as access-denied on Windows
                        x = !x;
                        Thread.Sleep(2);
                    }
                });
                var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        LoadOrderStatusData s;
                        // A read colliding with the flipper's in-progress replace fails LOUD (an IOException out of the
                        // profile-file read) — the honest MO2-mid-write transient, not a torn answer; skip and re-read.
                        try { s = svc.StatusData(); }
                        catch (IOException) { Interlocked.Increment(ref contended); continue; }
                        catch (UnauthorizedAccessException) { Interlocked.Increment(ref contended); continue; }
                        bool ghost = s.Warnings.Any(w => w.Contains("Ghost.esp", StringComparison.OrdinalIgnoreCase));
                        if ((s.ResolvedPluginCount == 2 && ghost) || (s.ResolvedPluginCount == 1 && !ghost))
                            Interlocked.Increment(ref torn);
                        Interlocked.Increment(ref reads);
                    }
                })).ToList();
                readers.Add(flipper);
                Task.WaitAll(readers.ToArray());
                Check(torn == 0,
                      $"no torn status line across {Interlocked.Read(ref reads)} concurrent reads (torn={torn}, loud transients={Interlocked.Read(ref contended)})");
            }

            // ---- 4: F5/write — every successful multi-op patch carries ONE build's winners ----
            Console.WriteLine();
            Console.WriteLine("--- 4: one multi-op write resolves EVERY edit against ONE build (hunt F5) ---");
            {
                var dir = Path.Combine(root, "core-f5");
                Directory.CreateDirectory(dir);
                var mPath = Path.Combine(dir, masterName); File.Copy(masterFile, mPath);
                var oPath = Path.Combine(dir, ovName); File.Copy(emptyFile, oPath);
                using var resolver = LoadOrderResolver.Build(new[] { mPath, oPath });
                var rulebook = CorpusRulebook.Load();
                var edits = fks.Take(OvN).Select(fk => new WritePatchBuilder.PatchEdit
                {
                    Target = fk, Path = new[] { "BasicStats", "Weight" }, Verb = "Set", Value = "7",
                }).ToList();

                static bool TryCopy(string src, string dst)
                {
                    for (int i = 0; i < 40; i++)
                    {
                        try { File.Copy(src, dst, overwrite: true); return true; }
                        catch (IOException) { Thread.Sleep(5); }
                    }
                    return false;
                }

                int mixed = 0, successes = 0, refusals = 0;
                const int Rounds = 12;
                for (int r = 0; r < Rounds; r++)
                {
                    TryCopy(emptyFile, oPath);                        // reset: master wins everything (Damage=10)
                    resolver.RefreshIfStale();
                    int delay = 5 + r * 17 % 130;                     // sweep the flip across the Phase-1 loop
                    var flip = Task.Run(() =>
                    {
                        Thread.Sleep(delay);
                        if (TryCopy(fullFile, oPath))                 // the override now wins the OvN subset (Damage=20)
                            resolver.RefreshIfStale();                // the concurrent read's freshness path, mid-Apply
                    });
                    var outDir = Path.Combine(dir, $"out_{r:D3}");
                    Directory.CreateDirectory(outDir);
                    var o = WritePatchBuilder.Apply(resolver, rulebook, edits, Path.Combine(outDir, "HcFcgOut.esp"), extend: false);
                    flip.Wait();
                    if (!o.Success) { refusals++; continue; }         // an honest named refusal is fine — mixing silently is not
                    successes++;
                    ISkyrimModGetter? back = null;
                    HashSet<ushort> marks;
                    try
                    {
                        back = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                        var want = fks.Take(OvN).ToHashSet();
                        marks = back.Weapons.Where(w => want.Contains(w.FormKey)).Select(w => w.BasicStats?.Damage ?? 0).ToHashSet();
                    }
                    finally { (back as IDisposable)?.Dispose(); }
                    if (marks.Count > 1) mixed++;
                }
                Check(mixed == 0,
                      $"no successful patch mixed two builds' winners — {successes} success(es), {refusals} honest refusal(s), mixed={mixed}");
            }

            // ---- 5: deferral — a read's freshness refresh waits out an in-flight write ----
            Console.WriteLine();
            Console.WriteLine("--- 5: a concurrent read defers its freshness refresh while a write is in flight (PR #51 review note) ---");
            {
                var inst = NewInstance("inst-defer");
                var prof = Path.Combine(inst, "profiles", "Default");
                WriteProfile(prof, new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
                var store = new UserConfigStore(Path.Combine(root, "user-defer.json"));
                using var svc = LoadOrderService.WithInstance(inst, 0, store);
                Check(svc.Stats().plugins == 1, "baseline order resolved — 1 plugin");

                var ops = fks.Skip(OvN).Take(250).Select(fk => new BulkOp
                {
                    Formid = Fid(fk), FieldPath = "BasicStats.Weight", Verb = "Set", Value = "9",
                }).ToList();

                int duringCount = -1; bool judged = false;
                WritePatchBuilder.PatchOutcome? outcome = null;
                for (int attempt = 0; attempt < 5 && !judged; attempt++)
                {
                    WriteProfile(prof, new[] { masterName }, new[] { "*" + masterName }, new[] { "+MasterMod" });
                    svc.Stats();                                      // settle back on the 1-plugin order
                    var started = new ManualResetEventSlim();
                    var wt = Task.Run(() => { started.Set(); return svc.ApplyEdits(ops, $"HcFcgDefer{attempt}", null); });
                    started.Wait();
                    Thread.Sleep(100);                                // let the write get into its resolve/serialize body
                    if (wt.IsCompleted) { outcome = wt.Result; continue; }   // write finished too fast to straddle — retry
                    WriteProfile(prof, new[] { masterName, extraName },     // a real MO2 toggle arrives MID-write
                                 new[] { "*" + masterName, "*" + extraName }, new[] { "+MasterMod", "+ExtraMod" });
                    var dc = svc.Stats().plugins;                     // the concurrent read
                    bool midFlight = !wt.IsCompleted;                 // judge only a read that provably finished mid-write
                    outcome = wt.Result;
                    if (!midFlight) continue;
                    duringCount = dc; judged = true;
                }
                Check(judged, "landed a read that completed while the write was still in flight");
                Check(outcome is { Success: true }, $"the in-flight write succeeded — {outcome?.Error ?? "ok"}");
                Check(duringCount == 1,
                      $"the mid-write read served the last good snapshot (refresh deferred, no mid-write rebuild) — saw {duringCount} plugin(s)");
                var afterCount = svc.Stats().plugins;
                Check(afterCount == 2, $"the deferred refresh ran on the NEXT call — {afterCount}/2 plugins (freshness deferred, never lost)");
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
