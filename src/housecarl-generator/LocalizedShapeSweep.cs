using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Classify every localized plugin in a REAL load order into the strings shapes and count them — the population a
/// refusal message has to describe accurately. The in-place write is refused for EVERY shape, so what differs per
/// row is the refusal FAMILY, not whether the write is allowed.
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

        // Broken out, not summed: a plugin houseCARL could not OPEN is not evidence that it is localized, so it gets
        // its own number instead of padding the localized count.
        int localized = counts.Where(kv => LocalizedStrings.ConfirmedLocalized(kv.Key)).Sum(kv => kv.Value.Count);
        int unreadable = counts.Where(kv => !LocalizedStrings.ConfirmedLocalized(kv.Key)).Sum(kv => kv.Value.Count);
        Console.WriteLine($"instance : {instance}");
        Console.WriteLine($"dataDir  : {dataDir ?? "<none>"}");
        Console.WriteLine($"plugins  : {scanned} scanned, {failed} not scanned (absent or errored), "
                          + $"{localized} flagged localized, {unreadable} houseCARL could not read");
        Console.WriteLine();
        foreach (var shape in Enum.GetValues<LocalizedShape>())
        {
            if (shape == LocalizedShape.NotLocalized) continue;
            var l = counts.TryGetValue(shape, out var x) ? x : new List<string>();
            // Not a verdict column: the in-place write is refused for every shape here, so the column names which
            // refusal applies, never whether one does.
            var family = LocalizedStrings.ConfirmedLocalized(shape) ? "REFUSE localized " : "REFUSE unreadable";
            Console.WriteLine($"  {family} {shape,-27} {l.Count}");
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
        LocalizedShape.StringsFolderUnreadable => "  Strings folder present, could not be listed",
        LocalizedShape.ModFolderUnreadable => "  the mod folder itself could not be listed",
        LocalizedShape.Nowhere => a.UnmatchedTables.Total > 0 ? $"  {a.UnmatchedTables.Total} unmatched table file(s) in the folder" : "",
        LocalizedShape.Unreadable => "  the plugin itself could not be opened",
        _ => "",
    };
}
