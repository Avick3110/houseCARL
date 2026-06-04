using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// MCP-step §8.3 beat 1 — the measure-first BODY-FETCH probe (mirrors §8.1's resolve-probe).
///
/// §8.1 measured the lean INDEX (FormKey→winner+depth, O(1), no bodies) and locked the resolver shape =
/// held lean index + on-demand body fetch + freshness. It left ONE thing unmeasured (handoff): the cost
/// of FETCHING the actual record bodies for a conflict tree on demand — resolving the overriding record
/// from each overlay that overrides a FormKey, so the tree's fields can be diffed. "Expected small";
/// this probe quantifies it before the resolver is built.
///
/// It also settles a STRUCTURAL sub-question §8.1's (winner,count) index can't answer: to enumerate a
/// tree's members you need the LIST of overriding overlays per FormKey, not just the count. This probe
/// builds that list-index and reports its RAM (vs §8.1's ~124 MB count-index) + the singleton-vs-multi
/// split — because only multi-override keys need a stored list (a multi-only list index is the lean middle).
///
/// PRIMITIVES measured (per overlay, by FormKey):
///   A. single-mod link cache resolve — overlay.ToImmutableLinkCache().TryResolve(fk, out rec)
///      (the established codebase primitive — WriteEngine.cs:294/474). Open question: its build cost +
///      retained RAM, especially for a BIG master, and whether build-and-discard returns to floor.
///   B. re-enumerate the single overlay — scan overlay.EnumerateMajorRecords() for fk (builds no cache,
///      holds nothing) — the contrast.
/// A depth-D tree touches D overlays once either way; the deep case (max depth, big-master root) is the
/// stress. The numbers decide two forks returned to Aaron: (1) index storage — (winner,count) vs
/// +overrider-lists vs multi-only; (2) body-fetch caching — hold nothing vs cache the per-overlay caches.
///
/// MEASURES, does not decide (same contract as §8.1). Placeholder order (masters first, then mods\ by
/// name) — body-fetch COST is order-independent; winner IDENTITY is the §8.5 gate, not this probe.
///
/// Run: dotnet run --project src/housecarl-generator body-fetch-probe [maxPlugins]
///      (maxPlugins caps the set for a fast smoke run; 0/absent = the full set).
/// </summary>
public static class BodyFetchProbe
{
    public static int RunBodyFetchProbe(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0; // 0 = all

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL body-fetch-probe (MCP step §8.3 beat 1 — measure-first)");
        Console.WriteLine("================================================================");

        // ---- 1. Same plugin set + open path as §8.1 (numbers stay directly comparable). ----
        var paths = ResolveProbe.GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0) { Console.Error.WriteLine("error: no plugins found"); return 1; }
        Console.WriteLine($"Plugins discovered: {paths.Count}" + (maxPlugins > 0 ? $"  (capped to {maxPlugins} — smoke run)" : ""));

        var swOpen = Stopwatch.StartNew();
        var overlays = new List<ISkyrimModGetter>();
        var failures = new List<string>();
        foreach (var p in paths)
        {
            try { overlays.Add(SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)); }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(p)}: {ex.GetType().Name} {ex.Message}"); }
        }
        swOpen.Stop();
        Console.WriteLine($"Overlays opened: {overlays.Count} in {swOpen.Elapsed.TotalSeconds:N1}s" +
                          (failures.Count > 0 ? $"  ({failures.Count} FAILED — surfaced, not skipped silently)" : ""));
        foreach (var f in failures.Take(10)) Console.WriteLine($"   !! {f}");
        if (overlays.Count == 0) { Console.Error.WriteLine("error: no overlays opened"); return 1; }

        var floor = ResolveProbe.Mem();
        Console.WriteLine();
        ResolveProbe.PrintMem("floor (all overlays open)", floor);

        // ---- 2. OVERRIDER-LIST index: FormKey → overriding overlay indices (low→high priority). ----
        // This is the structure a conflict TREE needs to enumerate its members; §8.1's (winner,count)
        // index can't. Measure its held RAM vs §8.1's ~124 MB count-index — the index-storage fork (Aaron).
        Console.WriteLine();
        Console.WriteLine("---- overrider-list index (FormKey -> overriding overlay indices) ----");
        var swIdx = Stopwatch.StartNew();
        var index = new Dictionary<FormKey, List<int>>();
        var perOverlayCount = new long[overlays.Count];
        long totalRecords = 0;
        var enumErrors = new List<string>();
        for (int i = 0; i < overlays.Count; i++)
        {
            try
            {
                foreach (var rec in overlays[i].EnumerateMajorRecords())
                {
                    var fk = rec.FormKey;
                    if (index.TryGetValue(fk, out var lst)) lst.Add(i);
                    else index[fk] = new List<int> { i };
                    perOverlayCount[i]++;
                    totalRecords++;
                }
            }
            catch (Exception ex) { enumErrors.Add($"{overlays[i].ModKey}: {ex.GetType().Name} {ex.Message}"); }
        }
        swIdx.Stop();
        var afterIdx = ResolveProbe.Mem();
        Console.WriteLine($"   built in {swIdx.Elapsed.TotalSeconds:N1}s — {totalRecords:N0} record instances -> {index.Count:N0} distinct FormKeys");
        if (enumErrors.Count > 0) { Console.WriteLine($"   !! {enumErrors.Count} overlay(s) failed to enumerate (surfaced):"); foreach (var e in enumErrors.Take(5)) Console.WriteLine($"        {e}"); }
        ResolveProbe.PrintDelta("list-index over floor", floor, afterIdx);
        int singletons = 0, multi = 0, maxDepth = 0; FormKey deepest = default;
        var firstByDepth = new Dictionary<int, FormKey>();
        foreach (var kv in index)
        {
            int d = kv.Value.Count;
            if (d == 1) singletons++; else multi++;
            if (d > maxDepth) { maxDepth = d; deepest = kv.Key; }
            if (!firstByDepth.ContainsKey(d)) firstByDepth[d] = kv.Key;
        }
        Console.WriteLine($"   singletons {singletons:N0} (sole overrider = winner, need no list) | multi {multi:N0} " +
                          $"({(index.Count > 0 ? 100.0 * multi / index.Count : 0):N1}%) | max depth {maxDepth} ({deepest})");
        Console.WriteLine($"   -> a MULTI-ONLY list index stores lists for only {multi:N0} keys; §8.1's (winner,count) index stays the O(1) fast path.");

        // ---- 3. Sample trees: a depth spread (only depths that actually exist) + the deepest. ----
        var samples = new List<(string label, FormKey fk)>();
        foreach (var d in new[] { 2, 3, 5, 10, 20, 50, 100, 200, 500 })
            if (firstByDepth.TryGetValue(d, out var fk)) samples.Add(($"depth {d}", fk));
        if (maxDepth > 1) samples.Add(($"DEEPEST {maxDepth}", deepest));

        // ---- 4. PRIMITIVE A: single-mod link cache resolve per overrider, BUILD-AND-DISCARD. ----
        // The floor-preserving variant: each per-overlay cache is dropped after its resolve.
        Console.WriteLine();
        Console.WriteLine("---- PRIMITIVE A: overlay.ToImmutableLinkCache().TryResolve(fk) per overrider, build-and-discard ----");
        foreach (var (label, fk) in samples)
        {
            var overriders = index[fk];
            var sw = Stopwatch.StartNew();
            int got = 0; string? recType = null;
            foreach (var oi in overriders)
            {
                var cache = overlays[oi].ToImmutableLinkCache();
                if (cache.TryResolve(fk, out var rec)) { got++; recType ??= RecordNaming.StripOverlay(rec.GetType().Name); }
            }
            sw.Stop();
            Console.WriteLine($"   {label,-13} {fk}  {recType,-16} {got,4}/{overriders.Count,-4} bodies in {sw.Elapsed.TotalMilliseconds,9:N1}ms" +
                              $"  ({sw.Elapsed.TotalMilliseconds / Math.Max(1, overriders.Count):N3} ms/body)");
        }
        var afterA = ResolveProbe.Mem();
        ResolveProbe.PrintDelta("primitive-A residue (vs index = transient?)", afterIdx, afterA);

        // ---- 5. DEEPEST tree HELD: cost of caching all its per-overlay caches (the caching-policy fork). ----
        if (maxDepth > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"---- DEEPEST tree HELD-cache cost (caching-policy fork): {deepest} depth {maxDepth} ----");
            var before = ResolveProbe.Mem();
            var held = new List<ILinkCache>();
            var sw = Stopwatch.StartNew();
            int got = 0;
            foreach (var oi in index[deepest])
            {
                var cache = overlays[oi].ToImmutableLinkCache();
                if (cache.TryResolve(deepest, out _)) got++;
                held.Add(cache);
            }
            sw.Stop();
            var after = ResolveProbe.Mem();
            Console.WriteLine($"   resolved {got}/{index[deepest].Count} HOLDING all {held.Count} per-overlay caches in {sw.Elapsed.TotalSeconds:N1}s");
            ResolveProbe.PrintDelta("held caches over pre-fetch", before, after);
            Console.WriteLine($"   -> if build-and-discard (step 4) is fast enough, hold NOTHING (preserve the floor); else cache, bounded. Aaron's call.");
            GC.KeepAlive(held);
        }

        // ---- 6. PRIMITIVE B: re-enumerate each overrider overlay, match FormKey (no cache built). ----
        Console.WriteLine();
        Console.WriteLine("---- PRIMITIVE B: re-enumerate each overrider overlay, match FormKey (no cache, holds nothing) ----");
        foreach (var (label, fk) in samples.Where(s => s.label.StartsWith("DEEPEST") || s.label == "depth 5" || s.label == "depth 50"))
        {
            var overriders = index[fk];
            var sw = Stopwatch.StartNew();
            int got = 0;
            foreach (var oi in overriders)
                foreach (var rec in overlays[oi].EnumerateMajorRecords())
                    if (rec.FormKey == fk) { got++; break; }
            sw.Stop();
            Console.WriteLine($"   {label,-13} {fk}  found {got,4}/{overriders.Count,-4} by re-enum in {sw.Elapsed.TotalMilliseconds,10:N1}ms" +
                              $"  ({sw.Elapsed.TotalMilliseconds / Math.Max(1, overriders.Count):N3} ms/body)");
        }

        // ---- 7. Big-master atomic: single-mod cache build + first resolve for the LARGEST mod. ----
        // Worst per-overlay cost — cache build scales with the mod's record count.
        Console.WriteLine();
        Console.WriteLine("---- big-master atomic: single-mod cache build + first resolve (worst per-overlay cost) ----");
        int bigIdx = 0; for (int i = 1; i < perOverlayCount.Length; i++) if (perOverlayCount[i] > perOverlayCount[bigIdx]) bigIdx = i;
        var big = overlays[bigIdx];
        var bigFk = big.EnumerateMajorRecords().First().FormKey;
        var b0 = ResolveProbe.Mem();
        var swb = Stopwatch.StartNew();
        var bigCache = big.ToImmutableLinkCache();
        bool bgot = bigCache.TryResolve(bigFk, out _);
        swb.Stop();
        var b1 = ResolveProbe.Mem();
        Console.WriteLine($"   {big.ModKey} ({perOverlayCount[bigIdx]:N0} records): build cache + first resolve ({bgot}) in {swb.Elapsed.TotalMilliseconds:N1}ms");
        ResolveProbe.PrintDelta($"held single-mod cache", b0, b1);
        GC.KeepAlive(bigCache);

        // ---- RESULT (the numbers Aaron rules on). ----
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine(" §8.3 beat-1 RESULT — on-demand body-fetch cost (for Aaron)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"   list-index held managed    : +{ResolveProbe.MB(afterIdx.managed - floor.managed)} over {index.Count:N0} keys  (vs §8.1 (winner,count) index ~124 MB)");
        Console.WriteLine($"   multi-override keys         : {multi:N0} ({(index.Count > 0 ? 100.0 * multi / index.Count : 0):N1}%) — only these need a stored overrider list");
        Console.WriteLine($"   per-body fetch             : see ms/body above (primitive A vs B); deepest tree depth {maxDepth}");
        Console.WriteLine();
        Console.WriteLine("   Forks this surfaces for Aaron:");
        Console.WriteLine("    (1) index storage: (winner,count) [~124 MB, can't enumerate trees] vs +overrider-lists [measured]");
        Console.WriteLine("        vs multi-only lists (lean middle — lists for the multi keys only).");
        Console.WriteLine("    (2) body-fetch caching: build-and-discard (hold nothing, floor-preserving) vs cache per-overlay caches (bounded).");
        return 0;
    }
}
