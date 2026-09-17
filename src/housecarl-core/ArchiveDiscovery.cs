namespace HousecarlCore;

// ArchiveDiscovery — builds the active-archive list (the BSAs the game loads, each bound to its owning plugin and
// that plugin's rank) for AssetResolver, from the same static MO2 profile read Mo2LoadOrder does. Both archive
// sources, the rank scheme and the VFS resolution of each filename are in docs/architecture/assets.md.

/// <summary>The active archives for a profile, ready for <see cref="AssetResolver.Build"/>, plus any non-fatal problems.</summary>
public sealed record ArchiveDiscoveryResult(IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> Warnings);

public static class ArchiveDiscovery
{
    /// <summary>The <see cref="ActiveArchive.OwningPlugin"/> marker for a BASE archive. Single-sourced: consumers that discriminate official base archives key on THIS const.</summary>
    public const string IniArchiveOwner = "Skyrim.ini [Archive]";

    /// <summary>Discover the active BSAs for the MO2 profile at <paramref name="profileDir"/>.
    /// <paramref name="gamePath"/> is only the game-dir Skyrim.ini fallback; the profile's own is tried first.</summary>
    public static ArchiveDiscoveryResult Discover(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string gamePath)
    {
        var warnings = new List<string>();
        var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);

        // Active plugins in load order (winner LAST) — the same filter as Mo2LoadOrder.Build.
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
        var activeOrdered = new List<string>(comp.OrderedPluginNames.Count);
        foreach (var name in comp.OrderedPluginNames)
            if (!inactive.Contains(name)) activeOrdered.Add(name);

        // archive filename → WINNING physical path (overwrite > enabled mods highest-priority-first > Data).
        var archiveMap = BuildArchiveMap(comp.EnabledMods, modsDir, dataDir, overwriteDir);

        var archives = new List<ActiveArchive>();
        int rank = 0;

        // (1) base archives — Skyrim.ini [Archive] sResourceArchiveList/2; loaded first → the LOW rank block.
        foreach (var fn in ReadBaseArchiveNames(profileDir, gamePath, warnings))
        {
            if (archiveMap.TryGetValue(fn, out var found))
                archives.Add(new ActiveArchive(found.Path, IniArchiveOwner, rank, found.OwningMod));
            rank++;   // a distinct rank per INI entry (later in the list = later loaded); absent-on-disk just isn't added
        }

        // (2) plugin-associated archives — "X.bsa" + "X - Textures.bsa", in load order (winner last → higher rank).
        foreach (var name in activeOrdered)
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            foreach (var candidate in new[] { baseName + ".bsa", baseName + " - Textures.bsa" })
                if (archiveMap.TryGetValue(candidate, out var found))
                    archives.Add(new ActiveArchive(found.Path, name, rank, found.OwningMod));
            rank++;   // both of a plugin's archives share its rank; advance once per plugin
        }

        return new ArchiveDiscoveryResult(archives, warnings);
    }

    /// <summary>Build archive-filename to the winning real path AND the MO2 layer it came from, the .bsa twin of
    /// <see cref="Mo2LoadOrder"/>'s plugin filename map.</summary>
    static Dictionary<string, (string Path, string OwningMod)> BuildArchiveMap(
        IReadOnlyList<string> enabledModsByPriority, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fn, full) in EnumerateArchives(overwriteDir))   // overwrite — beats every mod
            map[fn] = (full, "overwrite");

        foreach (var mod in enabledModsByPriority)                    // highest priority first
            foreach (var (fn, full) in EnumerateArchives(Path.Combine(modsDir, mod)))
                if (!map.ContainsKey(fn)) map[fn] = (full, mod);      // first (highest-priority) wins

        foreach (var (fn, full) in EnumerateArchives(dataDir))        // base game / vanilla — lowest priority
            if (!map.ContainsKey(fn)) map[fn] = (full, "Data");

        return map;
    }

    /// <summary>Top-level *.bsa in one folder, as (filename, full path). Silent on a missing folder; the explicit extension check guards Windows' short-name quirk.</summary>
    static IEnumerable<(string fn, string full)> EnumerateArchives(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) yield break;
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.bsa", opts); }
        catch { yield break; }
        foreach (var f in files)
            if (Path.GetExtension(f).Equals(".bsa", StringComparison.OrdinalIgnoreCase))
                yield return (Path.GetFileName(f), f);
    }

    /// <summary>The always-loaded base archive filenames from Skyrim.ini's [Archive] lists, in file order. MO2
    /// redirects the game INIs into the active profile, so the profile's copy is tried before the game dir's, and
    /// neither being found is surfaced loud.</summary>
    static IReadOnlyList<string> ReadBaseArchiveNames(string profileDir, string gamePath, List<string> warnings)
    {
        var candidates = new List<string>(2);
        if (profileDir.Length > 0) candidates.Add(Path.Combine(profileDir, "Skyrim.ini"));
        if (gamePath.Length > 0) candidates.Add(Path.Combine(gamePath, "Skyrim.ini"));

        foreach (var ini in candidates)
        {
            if (!File.Exists(ini)) continue;
            var names = ParseResourceArchiveList(ini);
            if (names.Count > 0) return names;   // first Skyrim.ini that actually lists archives wins
        }

        warnings.Add(
            "could not read the [Archive] sResourceArchiveList from a Skyrim.ini (looked in the profile folder" +
            (gamePath.Length > 0 ? " and the game dir" : "") + ") — the base-game BSAs (Skyrim - Textures*.bsa, " +
            "etc.) are NOT in the asset scan, so an asset present ONLY in a vanilla archive may read as absent. " +
            "If your MO2 uses profile-specific INIs, make sure the active profile has a Skyrim.ini.");
        return Array.Empty<string>();
    }

    /// <summary>Parse a Skyrim.ini's [Archive] section, returning the archive filenames in file order. An INI without the section returns empty, not an error.</summary>
    static IReadOnlyList<string> ParseResourceArchiveList(string iniPath)
    {
        var names = new List<string>();
        string[] lines;
        try { lines = File.ReadAllLines(iniPath); } catch { return names; }

        bool inArchive = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { inArchive = line.Equals("[Archive]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inArchive) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            if (!key.Equals("sResourceArchiveList", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("sResourceArchiveList2", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var part in line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                names.Add(part);
        }
        return names;
    }
}
