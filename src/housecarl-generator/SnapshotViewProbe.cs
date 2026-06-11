using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) for the snapshot-view capture discipline (HCBR-2026-06-11-02):
/// ONE logical operation answers EVERY question off ONE captured index build. PR #34 made each individual
/// resolver read internally consistent (the index swaps in as one immutable snapshot); the residue it left was
/// CROSS-VALUE: a service method issuing several resolver reads (Stats' four counters; ResolveRead's
/// excluded-check + winner + touching; CrossQuery's per-match winner fill next to its scan) could still mix TWO
/// adjacent builds in one response when a freshness rebuild landed mid-operation. The fix pins each operation to
/// a <see cref="LoadOrderResolver.IndexView"/> captured once at the top.
///
/// Self-contained, in the pattern of <c>pkcu-regression</c> / <c>source-display-guard</c>: synthesizes a
/// 2-plugin order ON DISK — a master with two weapons, an override plugin overriding one of them — then REWRITES
/// the override plugin (it stops overriding and brings its own new weapon instead) and triggers the REAL rebuild
/// path (<see cref="LoadOrderResolver.RefreshIfStale"/>). The mutation flips every observable: winner, depth,
/// touching list, RecordCount, ConflictCount, MaxDepth — so a value from the wrong build is provable.
///
/// Arms:
///   CONTROL — two captures straddling the rebuild DISAGREE about the winner: proves a rebuild really swaps the
///             build mid-session here, i.e. the hazard class the discipline closes is live in this environment.
///   PINNED  — the view captured BEFORE the rebuild still answers ALL-OLD after it (winner + depth + touching +
///             all counters from its own build, internally consistent). RED if any <c>IndexView</c> member ever
///             re-derefs the live <c>_snap</c> instead of the captured build — the exact regression class.
///   FRESH   — a capture taken AFTER the rebuild answers ALL-NEW, equally internally consistent (the view must
///             pin, not freeze: new operations see the new build).
///   SERVICE — drives the REAL <see cref="LoadOrderService"/> (ForGuard seam) on the post-rebuild order:
///             Stats() counters are one consistent set; ResolveRead's winner agrees with its OWN touching list
///             (winner == touching[^1]); CrossQuery's per-match winner/depth agree with the build that scanned
///             (the plugins= path — the same scan loop type= runs, no corpus needed). Behavioral cover that the
///             view refactor changed nothing on a stable order.
///
/// The mid-operation race itself can't be scheduled deterministically (no injection seam in the product paths —
/// by design); per the report, the practical gate is this structural one: the pinning mechanism proven across a
/// REAL rebuild + the service answering off it (code shape reviewed at the call sites).
///
/// Run: <c>dotnet run --project src/housecarl-generator -- snapshot-view-guard</c>
/// </summary>
public static class SnapshotViewProbe
{
    static int _pass, _fail;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — snapshot-view capture (one build per logical operation, HCBR-2026-06-11-02)  ################");
        Console.WriteLine();

        var dir = Path.Combine(Path.GetTempPath(), "hc_snapshot_view_guard");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        Directory.CreateDirectory(dir);

        const string masterName = "hcSnapMaster.esp", ovrName = "hcSnapOvr.esp";
        var masterPath = Path.Combine(dir, masterName);
        var ovrPath = Path.Combine(dir, ovrName);

        try
        {
            // ---- 1. MASTER: two weapons (W1, W2). Masterless (CI has no game files — it references nothing). ----
            var master = new SkyrimMod(ModKey.FromNameAndExtension(masterName), SkyrimRelease.SkyrimSE);
            var w1 = master.Weapons.AddNew(); w1.EditorID = "hcSnapW1";
            var w2 = master.Weapons.AddNew(); w2.EditorID = "hcSnapW2";
            var fk1 = w1.FormKey;
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // ---- 2. OVERRIDE v1: overrides W1 → the OLD build has winner(W1)=ovr, depth 2, 1 conflict. ----
            var ovr1 = new SkyrimMod(ModKey.FromNameAndExtension(ovrName), SkyrimRelease.SkyrimSE);
            WriteEngine.GenericGetOrAddAsOverride(ovr1, w1);
            ovr1.BeginWrite.ToPath(ovrPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            using var resolver = LoadOrderResolver.Build(new[] { masterPath, ovrPath });
            Console.WriteLine($"-- built order {masterName} < {ovrName} (v1 overrides W1) --");

            // ---- 3. Capture the view of the OLD build, and pin its expected facts while it is current. ----
            var viewOld = resolver.Capture();
            Check("old build: winner(W1) = override, depth 2",
                  viewOld.ResolveWinner(fk1) is { } wo && Eq(wo.WinnerPlugin, ovrName) && wo.OverrideDepth == 2);
            Check("old build: touching(W1) = [master, override]",
                  viewOld.TouchingPlugins(fk1) is { Count: 2 } to && Eq(to[0], masterName) && Eq(to[1], ovrName));
            Check("old build: counters = 2 records / 1 conflict / maxDepth 2",
                  viewOld.RecordCount == 2 && viewOld.ConflictCount == 1 && viewOld.MaxDepth == 2);

            // ---- 4. MUTATE the order on disk: override v2 stops overriding W1 and brings its OWN weapon (W3)
            //         instead — every observable flips — then drive the REAL rebuild path. ----
            var ovr2 = new SkyrimMod(ModKey.FromNameAndExtension(ovrName), SkyrimRelease.SkyrimSE);
            var w3 = ovr2.Weapons.AddNew(); w3.EditorID = "hcSnapW3";
            ovr2.BeginWrite.ToPath(ovrPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            File.SetLastWriteTimeUtc(ovrPath, DateTime.UtcNow.AddSeconds(2));   // belt-and-braces: the mtime sweep MUST see a change
            Check("RefreshIfStale rebuilt the index (the mutation was seen)", resolver.RefreshIfStale());
            Console.WriteLine($"-- rewrote {ovrName} (v2: no override, own weapon W3) and rebuilt --");
            Console.WriteLine();

            // ---- CONTROL: captures straddling the rebuild disagree — the hazard class is real here. ----
            var fresh = resolver.ResolveWinner(fk1);
            Check($"CONTROL: a fresh read now answers from the NEW build (winner(W1) = {masterName})",
                  fresh is { } fw && Eq(fw.WinnerPlugin, masterName) && fw.OverrideDepth == 1);
            Check("CONTROL: old view vs fresh read DISAGREE — the torn pair a multi-capture operation could emit",
                  viewOld.ResolveWinner(fk1) is { } ow && !Eq(ow.WinnerPlugin, fresh!.Value.WinnerPlugin));

            // ---- PINNED: the old view, read AFTER the rebuild, answers ALL-OLD — internally consistent. ----
            Check("PINNED: viewOld.ResolveWinner(W1) still = override, depth 2",
                  viewOld.ResolveWinner(fk1) is { } pw && Eq(pw.WinnerPlugin, ovrName) && pw.OverrideDepth == 2);
            Check("PINNED: viewOld.TouchingPlugins(W1) still = [master, override]",
                  viewOld.TouchingPlugins(fk1) is { Count: 2 } pt && Eq(pt[0], masterName) && Eq(pt[1], ovrName));
            Check("PINNED: viewOld counters still = 2 records / 1 conflict / maxDepth 2",
                  viewOld.RecordCount == 2 && viewOld.ConflictCount == 1 && viewOld.MaxDepth == 2);
            Check("PINNED: viewOld.ConflictKeys() still yields exactly [W1]",
                  viewOld.ConflictKeys().ToList() is { Count: 1 } ck && ck[0] == fk1);

            // ---- PINNED SCANS (review #1, teeth proven missing): the view's SCAN STREAMS must ride the pinned
            //      build too, not re-deref the live one. Master's file is unchanged on disk, so enumerating it
            //      under the OLD snapshot is staleness-safe; the discriminators are pure index facts. ----
            var pinnedIn = viewOld.RecordsIn(new[] { masterName }, null).Where(x => x.fk == fk1).ToList();
            Check("PINNED SCAN: viewOld.RecordsIn(master) yields W1 at the OLD depth 2 (a live re-deref would say 1)",
                  pinnedIn.Count == 1 && pinnedIn[0].depth == 2);
            var pinnedWin = viewOld.WinnerRecordsOfType(new[] { typeof(Mutagen.Bethesda.Skyrim.IWeaponGetter) }).ToList();
            Check("PINNED SCAN: viewOld.WinnerRecordsOfType yields ONLY W2 (old build: W1's winner is the override; " +
                  "a live re-deref would also yield W1 from master and W3 from the rewritten override)",
                  pinnedWin.Count == 1 && pinnedWin[0].fk != fk1);

            // ---- FRESH: a new capture answers ALL-NEW — the view pins, it doesn't freeze the resolver. ----
            var viewNew = resolver.Capture();
            Check($"FRESH: viewNew.ResolveWinner(W1) = {masterName}, depth 1",
                  viewNew.ResolveWinner(fk1) is { } nw && Eq(nw.WinnerPlugin, masterName) && nw.OverrideDepth == 1);
            Check("FRESH: viewNew.TouchingPlugins(W1) = [master] only",
                  viewNew.TouchingPlugins(fk1) is { Count: 1 } nt && Eq(nt[0], masterName));
            Check("FRESH: viewNew counters = 3 records / 0 conflicts / maxDepth 0",
                  viewNew.RecordCount == 3 && viewNew.ConflictCount == 0 && viewNew.MaxDepth == 0);
            Console.WriteLine();

            // ---- SERVICE: the REAL service methods, each answering off one capture, on the (stable) new build. ----
            var svc = LoadOrderService.ForGuard(resolver, new UserConfigStore(Path.Combine(dir, "houseCARL.user.json")));

            var stats = svc.Stats();
            Check("SERVICE Stats(): one consistent counter set (2 plugins / 3 records / 0 conflicts / maxDepth 0 / 0 failures)",
                  stats.plugins == 2 && stats.records == 3 && stats.conflicts == 0 && stats.maxDepth == 0 && stats.loadFailures.Count == 0);

            var read = svc.ResolveRead(fk1, null, null, conflictTree: true);
            Check("SERVICE ResolveRead(W1): no error, winner = master, depth 1",
                  read.Error is null && Eq(read.WinnerPlugin ?? "", masterName) && read.OverrideDepth == 1);
            Check("SERVICE ResolveRead(W1): winner agrees with its OWN touching list (winner == touching[^1])",
                  read.TouchingPlugins is { Count: 1 } rt && Eq(rt[^1], read.WinnerPlugin ?? ""));

            var q = svc.CrossQuery(type: null, references: null, editoridContains: null, conflictsOnly: false,
                                   plugins: new[] { masterName, ovrName }, where: null, limit: 500);
            Check("SERVICE CrossQuery(plugins=[master,ovr]): no error, 3 matches (W1, W2, W3)",
                  q.Error is null && q.Total == 3 && q.Keys.Count == 3 && q.Prefilled is { Count: 3 });
            Check("SERVICE CrossQuery: every per-match winner/depth agrees with the build that scanned",
                  q.Error is null && q.Prefilled is not null && Enumerable.Range(0, q.Keys.Count).All(i =>
                      resolver.ResolveWinner(q.Keys[i]) is { } cw
                      && Eq(q.Prefilled[i].Winner, cw.WinnerPlugin) && q.Prefilled[i].OverrideDepth == cw.OverrideDepth));
            Check($"SERVICE CrossQuery: W1's row reads from the NEW build (winner {masterName}, depth 1 — not the old override/2)",
                  q.Error is null && q.Prefilled is not null && Enumerable.Range(0, q.Keys.Count).Any(i =>
                      q.Keys[i] == fk1 && Eq(q.Prefilled[i].Winner, masterName) && q.Prefilled[i].OverrideDepth == 1));

            var qc = svc.CrossQuery(type: null, references: null, editoridContains: null, conflictsOnly: true,
                                    plugins: null, where: null, limit: 500);
            Check("SERVICE CrossQuery(conflicts_only): 0 matches on the new build (the old build had 1)",
                  qc.Error is null && qc.Total == 0);

            Console.WriteLine();
            Console.WriteLine($"=== snapshot-view-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }
}
