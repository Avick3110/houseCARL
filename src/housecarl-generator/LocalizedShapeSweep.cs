using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// A3 (advisor directive, 2026-08-26): classify every localized plugin in a REAL load order into the five strings
/// shapes and count them. It prices who the localized in-place write actually serves — the ruling's own
/// "what would make this wrong" line turns on this table, so the numbers ride the branch rather than a guess.
///
/// <para>Read-only: opens each plugin's header, enumerates its Strings folder, and opens an adjacent .bsa only when one
/// is present. Nothing is written.</para>
///
/// Run: dotnet run --project src/housecarl-generator localized-shape-sweep &lt;MO2-instance-dir&gt;
/// </summary>
public static class LocalizedShapeSweep
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: localized-shape-sweep <MO2-instance-dir>");
            return 1;
        }
        var instance = args[0];
        if (!Directory.Exists(instance)) { Console.Error.WriteLine($"not a dir: {instance}"); return 1; }

        // The ordered paths straight from the MO2 profile, NOT a built index: the sweep reads headers and directory
        // listings, so paying for a whole-order index build (which opens and enumerates every plugin) would cost minutes
        // to answer a question that costs milliseconds per plugin.
        var roots = Mo2Instance.Resolve(instance);
        var order = Mo2LoadOrder.Build(roots.ProfileDir, roots.ModsDir, roots.DataDir, roots.OverwriteDir);
        var paths = order.OrderedPaths;

        // DataDir the way the resolver derives it — the folder of the resolved Skyrim.esm — so the sweep classifies
        // against the same game-Data the read path would use, not against the MO2 ini's idea of it.
        var dataDir = paths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), "Skyrim.esm", StringComparison.OrdinalIgnoreCase)) is { } sky
            ? Path.GetDirectoryName(sky)
            : null;

        var counts = new Dictionary<LocalizedShape, List<string>>();
        int scanned = 0, failed = 0;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path)) { failed++; continue; }
            scanned++;
            LocalizedAssessment a;
            try { a = LocalizedStrings.Assess(path, dataDir); }
            catch (Exception ex) { Console.WriteLine($"  ERR  {name}: {ex.GetType().Name}"); failed++; continue; }
            if (a.Shape == LocalizedShape.NotLocalized) continue;
            if (!counts.TryGetValue(a.Shape, out var l)) counts[a.Shape] = l = new List<string>();
            l.Add(name + Detail(a));
        }

        int localized = counts.Values.Sum(l => l.Count);
        Console.WriteLine($"instance : {instance}");
        Console.WriteLine($"dataDir  : {dataDir ?? "<none>"}");
        Console.WriteLine($"plugins  : {scanned} scanned, {failed} unreadable, {localized} flagged localized");
        Console.WriteLine();
        foreach (var shape in Enum.GetValues<LocalizedShape>())
        {
            if (shape == LocalizedShape.NotLocalized) continue;
            var l = counts.TryGetValue(shape, out var x) ? x : new List<string>();
            var verdict = shape == LocalizedShape.LooseComplete ? "ALLOW " : "REFUSE";
            Console.WriteLine($"  {verdict} {shape,-27} {l.Count}");
        }
        Console.WriteLine();
        // The names, so the table can be read rather than merely counted — capped per shape, because a sweep that
        // dumps a 2000-plugin order's worth of names is not a summary.
        foreach (var shape in Enum.GetValues<LocalizedShape>())
        {
            if (shape == LocalizedShape.NotLocalized || !counts.TryGetValue(shape, out var l) || l.Count == 0) continue;
            Console.WriteLine($"== {shape} ({l.Count}) ==");
            foreach (var s in l.Take(25)) Console.WriteLine("   " + s);
            if (l.Count > 25) Console.WriteLine($"   … and {l.Count - 25} more");
        }
        return 0;
    }

    static string Detail(LocalizedAssessment a) => a.Shape switch
    {
        LocalizedShape.LooseComplete => $"  [{string.Join(",", a.Languages)}]",
        LocalizedShape.LoosePartial => "  missing " + string.Join("; ", a.IncompleteLanguages.Select(kv => $"{kv.Key}:{string.Join("+", kv.Value)}")),
        LocalizedShape.LooseWithGameDataDuplicate => $"  own[{string.Join(",", a.Languages)}] gameData[{string.Join(",", a.GameDataLanguages)}]",
        LocalizedShape.BsaEmbedded => "  " + Path.GetFileName(a.BsaPath!) + (a.BsaUnreadable ? " (UNREADABLE)" : ""),
        LocalizedShape.GameDataOnly => $"  gameData[{string.Join(",", a.GameDataLanguages)}]",
        _ => "",
    };
}
