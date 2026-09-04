using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// Verification harness for <see cref="LoadOrderResolver"/>: stand it up against a real, large load order and
/// measure held RAM, build-and-query correctness, on-demand body-fetch timing, and the freshness stat-sweep cost.
///
/// Winner IDENTITY is deliberately NOT asserted here — the plugin order this runs on is a placeholder, not the
/// user's real active order, so a winner it names would mean nothing.
///
/// Run: dotnet run --project src/housecarl-generator resolve [maxPlugins]
/// </summary>
public static class ResolveHarness
{
    public static int RunResolve(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0; // 0 = all

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL resolve harness (MCP step §8.3 — resolver verification)");
        Console.WriteLine("================================================================");

        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        Console.WriteLine($"Plugins (placeholder order — masters first, then mods\\ by name): {paths.Count}" +
                          (maxPlugins > 0 ? $"  (capped to {maxPlugins})" : ""));

        // ---- 1. BUILD — held RAM for the index alone + build time. ----
        var baseline = ResolveProbe.Mem();
        var sw = Stopwatch.StartNew();
        using var resolver = LoadOrderResolver.Build(paths);
        sw.Stop();
        var afterBuild = ResolveProbe.Mem();
        using var session = resolver.OpenSession();   // Option B: body fetches below open plugins on demand, disposed at exit
        Console.WriteLine();
        Console.WriteLine($"BUILD: {sw.Elapsed.TotalSeconds:N1}s  | plugins {resolver.PluginCount:N0} | records {resolver.RecordCount:N0} | " +
                          $"conflicts {resolver.ConflictCount:N0} ({(resolver.RecordCount > 0 ? 100.0 * resolver.ConflictCount / resolver.RecordCount : 0):N1}%) | max depth {resolver.MaxDepth}");
        ResolveProbe.PrintDelta("index held — Option B, no overlays at rest (over baseline)", baseline, afterBuild);
        Console.WriteLine($"   (§8.1 floor was ~28 MB managed → index alone ≈ held managed − floor; target was ~165–185 MB total)");
        if (resolver.LoadFailures.Count > 0)
        {
            Console.WriteLine($"   !! {resolver.LoadFailures.Count} plugin(s) EXCLUDED — open OR parse failure (surfaced, not skipped — Q3):");
            foreach (var f in resolver.LoadFailures.Take(10)) Console.WriteLine($"        {f}");
        }

        // ---- 2. CORRECTNESS — deepest conflict tree (cross-checks the body-fetch probe). ----
        Console.WriteLine();
        Console.WriteLine("---- conflict-tree correctness (deepest tree; cross-check vs body-fetch probe) ----");
        var deepestFk = resolver.ConflictKeys().MaxBy(fk => resolver.ResolveWinner(fk)!.Value.OverrideDepth);
        var swt = Stopwatch.StartNew();
        var tree = resolver.ResolveTree(session, deepestFk)!;
        swt.Stop();
        bool allSameType = tree.Nodes.All(n => RecordTypeOf(n) == tree.RecordType);
        bool allMatchFk = tree.Nodes.All(n => n.Record.FormKey == deepestFk);   // body-fetch returned the RIGHT record
        Console.WriteLine($"   {deepestFk}  type {tree.RecordType}  depth {tree.Nodes.Count}  fetched in {swt.Elapsed.TotalMilliseconds:N0}ms" +
                          $"  ({swt.Elapsed.TotalMilliseconds / Math.Max(1, tree.Nodes.Count):N3} ms/body)");
        Console.WriteLine($"   winner (last in order) = {tree.Winner.Plugin}  | IsConflict {tree.IsConflict}");
        Console.WriteLine($"   CHECK every node is {tree.RecordType}: {(allSameType ? "PASS" : "FAIL")}  | every fetched body == {deepestFk}: {(allMatchFk ? "PASS" : "FAIL")}");

        // ---- 3. A spread of conflict trees — body-fetch timing across depths. ----
        Console.WriteLine();
        Console.WriteLine("---- a spread of conflict trees (capabilities 4 + 5) ----");
        var byDepth = new SortedDictionary<int, FormKey>();
        foreach (var fk in resolver.ConflictKeys())
        {
            int d = resolver.ResolveWinner(fk)!.Value.OverrideDepth;
            if (new[] { 2, 3, 5, 10, 20 }.Contains(d) && !byDepth.ContainsKey(d)) byDepth[d] = fk;
            if (byDepth.Count >= 5) break;
        }
        int badTrees = 0;
        foreach (var (d, fk) in byDepth)
        {
            var t = resolver.ResolveTree(session, fk)!;
            bool ok = t.Nodes.Count == d && t.Nodes.All(n => n.Record.FormKey == fk);
            if (!ok) badTrees++;
            Console.WriteLine($"   depth {d,-3} {fk}  {t.RecordType,-16} winner={t.Winner.Plugin}  {(ok ? "PASS" : "FAIL")}");
        }

        // ---- 4. One plugin's whole-order conflict status. ----
        Console.WriteLine();
        Console.WriteLine("---- a plugin's conflict status (capabilities 1, 2, 6) ----");
        // Pick a non-master plugin that actually participates in conflicts: the one owning the deepest tree's winner.
        var probePlugin = tree.Winner.Plugin;
        int total = 0, wins = 0, overwritten = 0, unique = 0;
        foreach (var rs in resolver.PluginRecordStatus(probePlugin))
        {
            total++;
            if (rs.OverrideDepth == 1) unique++;
            else if (rs.PluginWins) wins++;
            else overwritten++;
        }
        Console.WriteLine($"   {probePlugin}: {total:N0} records | unique {unique:N0} | wins-a-conflict {wins:N0} | overwritten-by-higher {overwritten:N0}");

        // ---- 5. FRESHNESS — the no-change stat-sweep cost (the per-query freshness price). ----
        Console.WriteLine();
        Console.WriteLine("---- freshness: no-change stat sweep (the per-query cost when nothing edited) ----");
        var swf = Stopwatch.StartNew();
        bool rebuilt = resolver.RefreshIfStale();
        swf.Stop();
        Console.WriteLine($"   RefreshIfStale() over {resolver.PluginCount:N0} plugins: {swf.Elapsed.TotalMilliseconds:N0}ms | rebuilt={rebuilt} (expected false — nothing changed)");

        // ---- 5b. FRESHNESS REBUILD path — temp copy + forced mtime (NEVER touches a real mod file). ----
        // Use the smallest plugin that actually HAS records, so the rebuild proves it reconstructs a real
        // (non-empty) index — not the trivial 0->0 of a header-only esp.
        Console.WriteLine();
        Console.WriteLine("---- freshness: REBUILD path (temp copy, simulated mid-session edit) ----");
        var tmpDir = Path.Combine(Path.GetTempPath(), "housecarl-resolve-fresh");
        Directory.CreateDirectory(tmpDir);
        bool rebuildOk = false; int before = 0, after = 0; bool didRebuild = false; string chosen = "(no plugin with records found)";
        try
        {
            foreach (var p in paths.OrderBy(SizeOf))
            {
                var tp = Path.Combine(tmpDir, Path.GetFileName(p));
                File.Copy(p, tp, overwrite: true);
                var r2 = LoadOrderResolver.Build(new[] { tp });
                before = r2.RecordCount;
                if (before == 0) { r2.Dispose(); try { File.Delete(tp); } catch { } continue; }  // skip empty esps
                chosen = Path.GetFileName(p);
                File.SetLastWriteTimeUtc(tp, DateTime.UtcNow.AddSeconds(5));   // simulate a save in xEdit/CK
                didRebuild = r2.RefreshIfStale();
                after = r2.RecordCount;
                rebuildOk = didRebuild && after == before && before > 0;
                r2.Dispose();
                break;
            }
            Console.WriteLine($"   temp {chosen}: records {before:N0} -> mtime bumped -> RefreshIfStale()={didRebuild} -> records {after:N0}  {(rebuildOk ? "PASS" : "FAIL")}");
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { } }

        // ---- RESULT ----
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine(" §8.3 RESULT — resolver stands up");
        Console.WriteLine("================================================================");
        Console.WriteLine($"   held (index only)       : managed +{ResolveProbe.MB(afterBuild.managed - baseline.managed)} / ws +{ResolveProbe.MB(afterBuild.ws - baseline.ws)}  (Option B — no overlays at rest)");
        Console.WriteLine($"   tree correctness        : deepest {(allSameType && allMatchFk ? "PASS" : "FAIL")} | spread {(badTrees == 0 ? "PASS" : $"{badTrees} FAIL")}");
        Console.WriteLine($"   body fetch              : on-demand re-enum, ~{swt.Elapsed.TotalMilliseconds / Math.Max(1, tree.Nodes.Count):N2} ms/body, holds nothing");
        Console.WriteLine($"   freshness               : no-change sweep {swf.Elapsed.TotalMilliseconds:N0}ms ({resolver.PluginCount:N0} plugins) | rebuild-on-edit {(rebuildOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"   winner IDENTITY         : placeholder order — true active order + xEdit verify = §8.5 (NOT asserted here)");
        bool pass = allSameType && allMatchFk && badTrees == 0 && !rebuilt && rebuildOk;
        return pass ? 0 : 1;
    }

    static string RecordTypeOf(ConflictNode n) => RecordNaming.StripOverlay(n.Record.GetType().Name);
    static long SizeOf(string p) { try { return new FileInfo(p).Length; } catch { return long.MaxValue; } }
}
