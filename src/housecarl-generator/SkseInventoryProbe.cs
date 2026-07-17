using System.Diagnostics;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// MANUAL real-data harness for housecarl_skse_inventory (SKSE-plugin-layer visibility, gap 2026-06-08). Runs the REAL
/// service + renderer against a live MO2 instance and prints exactly what the tool would return, plus a timing line —
/// the empirical re-check Aaron drives (the CI skse-reader-guard pins the DECODE; this proves the whole inventory over
/// real DLLs). NOT in ci-all (needs a real instance + game install). Read-only; touches nothing but a temp user.json.
/// Usage: dotnet run --project src/housecarl-generator -- skse-inventory-real --mo2 "&lt;MO2 instance&gt;" [--filter &lt;substr&gt;]
/// </summary>
internal static class SkseInventoryProbe
{
    public static int RunReal(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        string? filter = ArgVal(args, "--filter");
        bool peek = Array.IndexOf(args, "--peek") >= 0;             // tier D: peek the filter-matched DLLs (needs --filter)
        if (mo2 is null) { Console.WriteLine("skse-inventory-real needs --mo2 <MO2 instance folder>"); return 2; }
        if (peek && filter is null) { Console.WriteLine("--peek needs --filter (a peek is per-DLL)"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-skse-real-" + Guid.NewGuid().ToString("N") + ".json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);
        var sw = Stopwatch.StartNew();
        var data = svc.SkseInventory(peek ? filter : null);
        sw.Stop();

        Console.WriteLine(SkseInventoryWire.Render(data, filter, 80_000));
        int peeked = data.Dlls.Count(e => e.Peek is not null);
        Console.WriteLine($"\n[timing] SkseInventory over {data.Dlls.Count} DLLs + {data.Configs.Count} configs" +
                          $"{(peek ? $" (peeked {peeked})" : "")} in {sw.ElapsedMilliseconds} ms");

        // Tier-D import-walk accounting over the REAL layer. A DLL that opened as a PE but whose import walk came back
        // UNKNOWN is either genuinely corrupt or a bug in the walk's corruption bounds — either way it silently
        // suppresses the Debug-CRT check for that DLL, so the count is worth stating rather than discovering later.
        var withInfo = data.Dlls.Where(e => e.Plugin is not null).ToList();
        var unknown = withInfo.Where(e => e.Plugin!.Imports is null).ToList();
        var empty = withInfo.Where(e => e.Plugin!.Imports is { Count: 0 }).ToList();
        Console.WriteLine($"[imports] {withInfo.Count - unknown.Count}/{withInfo.Count} PE-read DLLs walked" +
                          $" · {unknown.Count} UNKNOWN (walk failed) · {empty.Count} genuinely import-free");
        foreach (var u in unknown) Console.WriteLine($"    UNKNOWN: {u.FileName} ({u.Plugin!.Kind})");
        return 0;
    }

    static string? ArgVal(string[] a, string key)
    {
        int i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
