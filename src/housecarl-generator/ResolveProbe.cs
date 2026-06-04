using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>
/// MCP-step §8.1 — the measure-first probe (the FIRST build action; gates fork §6-C).
///
/// Question it settles: at a full ~3,700-plugin scale, what does it COST to know a record's
/// true load-order winner + conflict-tree DEPTH — and can we do it WITHOUT the old-daemon's hot state
/// (rebuild failure-2)? Empirically narrowed by earlier smoke runs:
///   • Mutagen full-record load-order cache  → retains bodies → ~70 MB/plugin (≈ hundreds of GB at scale);
///   • Mutagen OnlyIdentifiers "simple" cache → cheap, but THROWS on record resolution ("simple cache");
/// so neither stock cache is the resolver primitive. Finding all overrides of a FormKey inherently needs
/// either a held index or a per-query whole-order scan — there is no free lunch; this probe quantifies
/// the leanest CAPABLE held index.
///
/// ARM 1 (the candidate, FULL scale): a custom LEAN override index — enumerate every overlay's records
/// once (low→high priority) and keep, per FormKey, only the winning plugin + the override count. No
/// record bodies retained (those are fetched on-demand in 3b). This is the floor of what "what wins +
/// how deep" can cost as held state, built from certain APIs (EnumerateMajorRecords + FormKey).
/// ARM 2 (the contrast, CAPPED): Mutagen's full-record cache on a safe subset → MB/plugin to extrapolate
/// the RAM failure (never run full — it would OOM the machine).
///
/// MEASURES, does not decide: the index-vs-on-demand SEQUENCING call returns to Aaron with these numbers
/// (handoff: "the cost number"). Order here is a deterministic placeholder (masters first, then mods\ by
/// name): override COUNTS/DEPTHS are correct, winner IDENTITY is not asserted — pinning the true active
/// order (plugins.txt) and xEdit-verifying winner identity is the §8.5 correctness gate, not this probe.
///
/// Run: dotnet run --project src/housecarl-generator resolve-probe [maxPlugins]
///      (maxPlugins caps the whole set for a fast smoke run; 0/absent = the full set).
/// </summary>
public static class ResolveProbe
{
    // Same canonical game roots the write/read proofs use (NOT the global Steam install).
    const string DefaultDataDir = @"C:\MO2\Instance\Stock Game\Data";
    const string DefaultModsDir = @"C:\MO2\Instance\mods";
    static readonly string[] VanillaMasters =
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    // The full-record arm is the strawman — never run it across the whole order (it would OOM).
    // Cap it to a subset purely to characterise its per-plugin RAM so we can extrapolate the failure.
    const int FullRecordArmCap = 200;

    public static int RunResolveProbe(string[] args)
    {
        int maxPlugins = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 0; // 0 = all

        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL resolve-probe (MCP step §8.1 — measure-first)");
        Console.WriteLine("================================================================");

        // ---- 1. Enumerate the full plugin set, in a load order (masters first). ----
        var paths = GatherPlugins();
        if (maxPlugins > 0 && paths.Count > maxPlugins) paths = paths.Take(maxPlugins).ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine($"error: no plugins found (looked in {DefaultDataDir} + {DefaultModsDir})");
            return 1;
        }
        Console.WriteLine($"Plugins discovered: {paths.Count}  (masters first, then mods\\ deduped by name)");
        if (maxPlugins > 0) Console.WriteLine($"   (capped to {maxPlugins} via arg — smoke run)");

        // ---- 2. Open all as standalone binary overlays (loud on failure — Q3). ----
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
        foreach (var f in failures.Take(20)) Console.WriteLine($"   !! {f}");
        if (failures.Count > 20) Console.WriteLine($"   ... +{failures.Count - 20} more load failures");
        if (overlays.Count == 0) { Console.Error.WriteLine("error: no overlays opened"); return 1; }

        // ---- 3. The FLOOR: RAM with every overlay open, nothing resolved. ----
        var floor = Mem();
        Console.WriteLine();
        Console.WriteLine("---- FLOOR (all overlays open, nothing resolved) ----");
        PrintMem("floor", floor);

        // ================================================================
        //  ARM 1 — custom LEAN override index (the candidate). FULL scale.
        // ================================================================
        Console.WriteLine();
        Console.WriteLine("---- ARM 1: lean override index (FormKey → winner + depth, no bodies) — FULL scale ----");
        var swIdx = Stopwatch.StartNew();
        // FormKey → packed (winnerModIdx, overrideCount). Single dict = the leanest held index that still
        // answers "what wins" AND "how deep is the tree". Processed low→high so the LAST writer = winner.
        var index = new Dictionary<FormKey, (int winner, int count)>();
        long totalRecords = 0;
        var enumErrors = new List<string>();
        for (int i = 0; i < overlays.Count; i++)
        {
            try
            {
                foreach (var rec in overlays[i].EnumerateMajorRecords())
                {
                    var fk = rec.FormKey;
                    if (index.TryGetValue(fk, out var e)) index[fk] = (i, e.count + 1);
                    else index[fk] = (i, 1);
                    totalRecords++;
                }
            }
            catch (Exception ex) { enumErrors.Add($"{overlays[i].ModKey}: {ex.GetType().Name} {ex.Message}"); }
        }
        swIdx.Stop();
        var afterIdx = Mem();
        Console.WriteLine($"   built in {swIdx.Elapsed.TotalSeconds:N1}s — {totalRecords:N0} record instances → {index.Count:N0} distinct FormKeys");
        if (enumErrors.Count > 0) { Console.WriteLine($"   !! {enumErrors.Count} overlay(s) FAILED to enumerate (surfaced):"); foreach (var e in enumErrors.Take(10)) Console.WriteLine($"        {e}"); }
        PrintMem("lean index held", afterIdx);
        PrintDelta("lean index over floor", floor, afterIdx);
        double idxBytesPerKey = index.Count > 0 ? (double)(afterIdx.managed - floor.managed) / index.Count : 0;
        Console.WriteLine($"   ≈ {idxBytesPerKey:N0} managed bytes per distinct FormKey");

        // conflict-tree DEPTH distribution straight off the index (real conflicts at scale).
        int multi = 0, maxDepth = 0; long depthSum = 0; FormKey deepest = default;
        var depthHist = new SortedDictionary<int, int>();
        foreach (var kv in index)
        {
            int d = kv.Value.count;
            depthSum += d;
            if (d > 1) multi++;
            if (d > maxDepth) { maxDepth = d; deepest = kv.Key; }
            depthHist[d] = depthHist.TryGetValue(d, out var h) ? h + 1 : 1;
        }
        Console.WriteLine($"   conflict trees: {multi:N0}/{index.Count:N0} FormKeys overridden by >1 plugin " +
                          $"({(index.Count > 0 ? 100.0 * multi / index.Count : 0):N1}%) | max depth {maxDepth} ({deepest})");
        Console.Write("   depth histogram: ");
        foreach (var h in depthHist.Where(h => h.Key <= 10)) Console.Write($"{h.Key}:{h.Value:N0}  ");
        var deep = depthHist.Where(h => h.Key > 10).Sum(h => h.Value);
        if (deep > 0) Console.Write($">10:{deep:N0}");
        Console.WriteLine();
        Console.WriteLine($"   query latency = O(1) dict lookup once built (winner + depth are instant); body fetch is on-demand in 3b.");
        GC.KeepAlive(index);

        // ================================================================
        //  ARM 2 — Mutagen FULL-RECORD held cache (the strawman). CAPPED.
        // ================================================================
        Console.WriteLine();
        var fullCap = Math.Min(overlays.Count, FullRecordArmCap);
        Console.WriteLine($"---- ARM 2: Mutagen full-record cache — CAPPED to {fullCap} plugins (extrapolate the RAM failure) ----");
        var probeFk = index.Keys.First();
        var sub = overlays.Take(fullCap).ToList();
        var fullFloor = Mem();
        var swFull = Stopwatch.StartNew();
        var fullCache = sub.ToImmutableLinkCache();          // default prefs = retain full records
        int fullFirst = -1;
        try { fullFirst = fullCache.ResolveAll<IMajorRecordGetter>(probeFk).Count(); } catch (Exception ex) { Console.WriteLine($"   !! {ex.GetType().Name} {ex.Message}"); }
        swFull.Stop();
        var afterFull = Mem();
        Console.WriteLine($"   build + first universal ResolveAll: {swFull.Elapsed.TotalSeconds:N1}s  (tree depth {fullFirst})");
        PrintDelta($"full-record cache over its floor ({fullCap} plugins)", fullFloor, afterFull);
        double mbPerPlugin = (afterFull.managed - fullFloor.managed) / 1024.0 / 1024.0 / Math.Max(1, fullCap);
        Console.WriteLine($"   ≈ {mbPerPlugin:N1} MB managed per plugin → extrapolated to {overlays.Count}: " +
                          $"~{mbPerPlugin * overlays.Count / 1024.0:N1} GB if held full-record across the whole order");
        GC.KeepAlive(fullCache);

        // ---- RESULT (the numbers Aaron rules on). ----
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine(" §8.1 RESULT — for the index-vs-on-demand sequencing call (Aaron)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"   plugins resolved across   : {overlays.Count}   (floor ws {MB(floor.ws)})");
        Console.WriteLine($"   LEAN INDEX (full)         : build {swIdx.Elapsed.TotalSeconds:N1}s | held ws +{MB(afterIdx.ws - floor.ws)} / managed +{MB(afterIdx.managed - floor.managed)}");
        Console.WriteLine($"   conflict trees            : {multi:N0}/{index.Count:N0} FormKeys (>1 plugin), max depth {maxDepth}");
        Console.WriteLine($"   FULL-record (strawman)    : ~{mbPerPlugin:N1} MB/plugin → ~{mbPerPlugin * overlays.Count / 1024.0:N1} GB across the order (RAM failure)");
        Console.WriteLine();
        Console.WriteLine("   Read it: the LEAN INDEX held-RAM at full scale is the number that decides §6-C.");
        Console.WriteLine("   If it is modest → resolver = held lean index + on-demand body fetch (fast 'what wins',");
        Console.WriteLine("   no full records held), but it is HELD state → freshness flexes §5.2 into this step.");
        Console.WriteLine("   If even the lean index is too heavy → on-demand whole-order scan per query is the");
        Console.WriteLine("   alternative (cost ≈ the build time above, paid per request). Aaron's sequencing call.");
        return 0;
    }

    /// <summary>Vanilla masters (canonical order) + the mods\ tree deduped by filename (largest copy),
    /// ordered by name as a deterministic placeholder. True active order (plugins.txt) is a §8.5 concern.
    /// <c>internal</c> so the §8.3 body-fetch probe reuses the EXACT same plugin set (comparable numbers).</summary>
    internal static List<string> GatherPlugins()
    {
        var list = new List<string>();
        foreach (var m in VanillaMasters)
        {
            var p = Path.Combine(DefaultDataDir, m);
            if (File.Exists(p)) list.Add(p);
        }
        if (Directory.Exists(DefaultModsDir))
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".esp", ".esm", ".esl" };
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
            list.AddRange(Directory.EnumerateFiles(DefaultModsDir, "*.es*", opts)
                .Where(p => exts.Contains(Path.GetExtension(p)))
                .Select(p => new FileInfo(p))
                .GroupBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(fi => fi.Length).First())
                .Where(fi => !VanillaMasters.Contains(fi.Name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                .Select(fi => fi.FullName));
        }
        return list;
    }

    // ---- measurement helpers (internal: shared with the §8.3 body-fetch probe so its GC/measurement
    //      protocol is byte-identical to §8.1's — the numbers must be directly comparable) ----
    internal static (long managed, long ws, long priv) Mem()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return (GC.GetTotalMemory(forceFullCollection: true), p.WorkingSet64, p.PrivateMemorySize64);
    }
    internal static string MB(long b) => $"{b / 1024.0 / 1024.0:N0} MB";
    internal static void PrintMem(string label, (long managed, long ws, long priv) m)
        => Console.WriteLine($"   {label,-34} ws {MB(m.ws),9} | priv {MB(m.priv),9} | managed {MB(m.managed),9}");
    internal static void PrintDelta(string label, (long managed, long ws, long priv) a, (long managed, long ws, long priv) b)
        => Console.WriteLine($"   {label,-34} ws +{MB(b.ws - a.ws),8} | priv +{MB(b.priv - a.priv),8} | managed +{MB(b.managed - a.managed),8}");
}
