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

    /// <summary>Discover the active BSAs for the MO2 profile at <paramref name="profileDir"/>; the base list comes from the INI the game reads (<see cref="GameIni"/>).</summary>
    public static ArchiveDiscoveryResult Discover(string profileDir, string modsDir, string dataDir, string overwriteDir)
    {
        var warnings = new List<string>();
        return Discover(Mo2LoadOrder.ReadComposition(profileDir, warnings), GameIni.For(profileDir), modsDir, dataDir, overwriteDir, warnings);
    }

    /// <summary>The same, over a composition and INI the caller already read, so an order build and its archives describe one profile state.</summary>
    public static ArchiveDiscoveryResult Discover(Mo2Composition comp, GameIni ini, string modsDir, string dataDir,
                                                  string overwriteDir, List<string>? warnings = null)
    {
        warnings ??= new List<string>();

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
        foreach (var fn in ReadBaseArchiveNames(ini, warnings))
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

    /// <summary>The always-loaded base archive filenames from the [Archive] lists of the INI the game reads, list 1 then
    /// list 2, each key SkyrimCustom.ini's over Skyrim.ini's; neither list being found is surfaced loud.</summary>
    static IReadOnlyList<string> ReadBaseArchiveNames(GameIni ini, List<string> warnings)
    {
        var names = new List<string>();
        foreach (var key in new[] { "sResourceArchiveList", "sResourceArchiveList2" })
            if (ini.Value("Archive", key).Value is { } list)
                names.AddRange(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (names.Count > 0) return names;

        warnings.Add(
            "could not read the [Archive] sResourceArchiveList from a Skyrim.ini (looked in the " +
            (ini.ProfileLocal ? "profile folder" : "My Games folder, as the profile does not use its own INIs") +
            ") — the base-game BSAs (Skyrim - Textures*.bsa, etc.) are NOT in the asset scan, so an asset present ONLY " +
            "in a vanilla archive may read as absent. If your MO2 uses profile-specific INIs, make sure the active " +
            "profile has a Skyrim.ini.");
        return Array.Empty<string>();
    }
}
