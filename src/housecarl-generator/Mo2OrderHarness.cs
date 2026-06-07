using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

// MCP step §8.5: verify Mo2LoadOrder.Build against a real profile's files (loadorder.txt + modlist.txt +
// plugins.txt). Proves the static-file reader produces a sane true order — masters first, the user's top-priority
// patches last, the ~110 duplicate-name plugins resolved to a real winning path — then feeds it to the real
// LoadOrderResolver and spot-checks the depth-879 sanity record. This is the Phase-1 empirical gate before the
// server consumes it; Phase 2 is Aaron's xEdit winner-IDENTITY cross-check.
//
//   dotnet run --project src/housecarl-generator mo2-order [profileDir] [modsDir] [dataDir]
static class Mo2OrderHarness
{
    const string DefaultProfile = @"C:\MO2\Instance\profiles\Default";
    const string DefaultMods    = @"C:\MO2\Instance\mods";
    const string DefaultData    = @"C:\MO2\Instance\Stock Game\Data";

    // A few of the 110 duplicate-name plugins (appear in 2+ mod folders) — eyeball which mod won the priority race.
    static readonly string[] SampleDuplicates =
        { "1flutedarmor.esp", "beards.esp", "ccbgssse001-fish.esm", "RaceCompatibility.esm" };

    public static int RunMo2Order(string[] args)
    {
        var profileDir = args.Length > 0 ? args[0] : DefaultProfile;
        var modsDir    = args.Length > 1 ? args[1] : DefaultMods;
        var dataDir    = args.Length > 2 ? args[2] : DefaultData;

        Console.WriteLine($"mo2-order: reading the TRUE active order (static profile files)\n  profile: {profileDir}\n  mods:    {modsDir}\n  data:    {dataDir}\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Mo2LoadOrder.Build(profileDir, modsDir, dataDir);
        sw.Stop();

        Console.WriteLine($"built in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  active plugins in load order : {result.ActiveCount}");
        Console.WriteLine($"  resolved to a real path      : {result.ResolvedCount}");
        Console.WriteLine($"  warnings                     : {result.Warnings.Count}");

        var paths = result.OrderedPaths;
        if (paths.Count == 0) { Console.WriteLine("\nFAIL: no plugins resolved — check the profile/mods/data paths."); return 1; }

        Console.WriteLine("\nfirst 6 (lowest priority — expect the vanilla masters):");
        foreach (var p in paths.Take(6)) Console.WriteLine($"  {Tail(p)}");
        Console.WriteLine("last 6 (highest priority — expect the user's top patches, e.g. the houseCARL/Test plugins):");
        foreach (var p in paths.Skip(Math.Max(0, paths.Count - 6))) Console.WriteLine($"  {Tail(p)}");

        Console.WriteLine("\nduplicate-name resolution (which mod folder won the priority race):");
        foreach (var dup in SampleDuplicates)
        {
            var hit = paths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), dup, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(hit is null ? $"  {dup,-32} -> (not in the active order)" : $"  {dup,-32} -> {Tail(hit)}");
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine($"\nwarnings (first 10 of {result.Warnings.Count}):");
            foreach (var w in result.Warnings.Take(10)) Console.WriteLine($"  - {w}");
        }

        // Feed the true order into the REAL resolver and spot-check it stands up + the §8.5 sanity record.
        Console.WriteLine("\nbuilding LoadOrderResolver on the true order...");
        var rsw = System.Diagnostics.Stopwatch.StartNew();
        using var resolver = LoadOrderResolver.Build(paths);
        rsw.Stop();
        Console.WriteLine($"  resolver built in {rsw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  plugins={resolver.PluginCount}  records={resolver.RecordCount}  conflicts={resolver.ConflictCount}  maxDepth={resolver.MaxDepth}");
        if (resolver.LoadFailures.Count > 0)
        {
            Console.WriteLine($"  excluded plugins (open OR parse failure): {resolver.LoadFailures.Count} (first 5):");
            foreach (var f in resolver.LoadFailures.Take(5)) Console.WriteLine($"    - {f}");
        }

        // §8.5 sanity record: the deepest-known Worldspace 00003C:Skyrim.esm (depth ~879 in the placeholder order).
        var sanity = FormKey.Factory("00003C:Skyrim.esm");
        var winner = resolver.ResolveWinner(sanity);
        Console.WriteLine(winner is null
            ? $"\n00003C:Skyrim.esm  -> NOT FOUND (unexpected)"
            : $"\n00003C:Skyrim.esm  winner={winner.Value.WinnerPlugin}  override_depth={winner.Value.OverrideDepth}");

        // Sanity assertions (loud, not silent) — the cheap structural checks; xEdit identity is Phase 2.
        bool firstIsMaster = paths.Count > 0 && Path.GetFileName(paths[0]).Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase);
        bool plausibleCount = result.ResolvedCount > 3000;
        Console.WriteLine($"\nchecks: first==Skyrim.esm={firstIsMaster}  resolved>3000={plausibleCount}  resolved==active={result.ResolvedCount == result.ActiveCount}");
        if (!firstIsMaster || !plausibleCount) { Console.WriteLine("FAIL: structural sanity check failed."); return 1; }
        Console.WriteLine("mo2-order: OK");
        return 0;
    }

    static string Tail(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "?";
        return $"{dir}/{Path.GetFileName(path)}";
    }
}
