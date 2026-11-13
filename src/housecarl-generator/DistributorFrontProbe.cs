using System.Diagnostics;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// MANUAL real-data harness for the distributor front: what the root-only listing door costs against today's root glob
/// on a live MO2 instance, how many files each of the four formats discovers there, and what reading those winning
/// files through the generic INI reader costs. Read-only; needs a real instance, so it is not a CI probe.
/// Usage: dotnet run --project src/housecarl-generator -c Release -- distributor-front --mo2 "&lt;MO2 instance&gt;"
/// </summary>
internal static class DistributorFrontProbe
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--mo2");
        if (at < 0 || at + 1 >= args.Length) { Console.WriteLine("distributor-front needs --mo2 <MO2 instance folder>"); return 2; }

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-distr-front-" + Guid.NewGuid().ToString("N"), "user.json"));
        using var svc = LoadOrderService.WithInstance(args[at + 1], 0, store);
        var assets = ((ILoadOrderHost)svc).Assets;
        Console.WriteLine($"roots: {assets.LooseRoots.Count} loose");

        // The listing: the whole-tree walk one root glob pays today, against one directory per root.
        Time("walk  EnumerateUnder(\"\")", () => assets.Capture().EnumerateUnder("").Count);
        Time("walk  EnumerateUnder(\"\") warm", () => assets.Capture().EnumerateUnder("").Count);
        Time("door  EnumerateTopLevel(\"\")", () => assets.Capture().EnumerateTopLevel("").Count);
        Time("door  EnumerateTopLevel(\"\") warm", () => assets.Capture().EnumerateTopLevel("").Count);

        // Discovery and the parse, per format, off one view each.
        long bytes = 0, entries = 0;
        foreach (var format in RuntimeFormats.Distributors)
        {
            var sw = Stopwatch.StartNew();
            var files = DistributorDiscovery.Scan(assets.Capture(), format);
            sw.Stop();
            long formatBytes = 0, formatEntries = 0;
            var winners = files.Where(f => f.LooseFilePath is not null).Select(f => f.LooseFilePath!).ToList();
            foreach (var p in winners) formatBytes += new FileInfo(p).Length;
            double ParseOnce()
            {
                var parse = Stopwatch.StartNew();
                long n = 0;
                foreach (var p in winners) n += IniEntries.Read(p).Count();
                parse.Stop();
                formatEntries = n;
                return parse.Elapsed.TotalMilliseconds;
            }
            ParseOnce();                                           // the first pass pays the JIT and the file cache
            double parseMs = Enumerable.Range(0, 5).Select(_ => ParseOnce()).Min();
            bytes += formatBytes; entries += formatEntries;
            Console.WriteLine($"{format.Key,-5} {files.Count,4} file(s)  discovery {sw.ElapsedMilliseconds,5} ms" +
                              $"  parse {parseMs,7:F2} ms  {formatBytes,8} bytes  {formatEntries,6} entries" +
                              $"  not applied {files.Count(f => f.NotApplied is not null)}" +
                              $"  shadowed {files.Count(f => f.ShadowedProviders.Count > 0)}");
        }
        Console.WriteLine($"[all four] {bytes} bytes, {entries} entries read through IniEntries");
        Console.WriteLine($"[peak ws] {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        return 0;
    }

    static void Time(string what, Func<int> run)
    {
        var sw = Stopwatch.StartNew();
        int n = run();
        sw.Stop();
        Console.WriteLine($"{what,-36} {n,7} path(s)  {sw.ElapsedMilliseconds,6} ms");
    }
}
