namespace HousecarlCore;

// ArchiveDiscovery — builds the active-archive list (the BSAs the game loads, each bound to its owning
// plugin and that plugin's load-order rank) for AssetResolver, from the same static MO2 profile read
// Mo2LoadOrder does. No game state, no VFS hooking, no live tracking.
//
// Skyrim SE loads BSAs from two sources, and both must be scanned or an asset present only in an
// un-scanned archive reads as falsely absent:
//   1. Base archives — the always-loaded set in Skyrim.ini's [Archive] sResourceArchiveList and
//      sResourceArchiveList2 (vanilla "Skyrim - Textures*.bsa" etc., where base-game facegen lives).
//      They load first, so they take the lowest ranks.
//   2. Plugin-associated archives — for each active plugin X.<esp/esm/esl> the engine auto-loads "X.bsa"
//      and "X - Textures.bsa" when present, in plugin load order, so they outrank every base archive.
//
// A .bsa is itself subject to MO2's overwrite > enabled mods (highest priority first) > Data precedence,
// so each archive FILENAME resolves through that same map — never "look beside the .esp", which would miss
// an archive overridden by a higher-priority mod. AssetResolver.DedupeArchives collapses a path bound by
// more than one plugin to its max-rank binding, so emitting a duplicate is harmless.
//
// Rank means "higher wins among BSAs" (AssetResolver.ActiveArchive's contract). Base archives take the low
// block in INI order (later entry = later loaded = higher); plugin archives rank above all of them, in load
// order. A plugin's "X.bsa" and "X - Textures.bsa" share its rank, and AssetResolver tie-breaks equal ranks
// by filename. A Skyrim.ini that cannot be found is surfaced as a warning, never dropped silently; a base
// archive named in the INI but absent on disk simply isn't loaded (the INI lists a superset across game
// variants), which is not worth a warning.

/// <summary>The active archives for a profile (feed <see cref="ArchiveDiscoveryResult.Archives"/> straight to
/// <see cref="AssetResolver.Build"/>'s activeArchives) plus any non-fatal problems — e.g. a Skyrim.ini that
/// couldn't be found, so base-game BSAs aren't in the scan.</summary>
public sealed record ArchiveDiscoveryResult(IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> Warnings);

public static class ArchiveDiscovery
{
    /// <summary>The <see cref="ActiveArchive.OwningPlugin"/> marker for a BASE archive (loaded via Skyrim.ini's
    /// [Archive] list, not bound to a plugin). Single-sourced: consumers that discriminate "official base archive"
    /// (the native-pairing audit's ENGINE carve-out) key on THIS const, never a re-typed literal.</summary>
    public const string IniArchiveOwner = "Skyrim.ini [Archive]";

    /// <summary>Discover the active BSAs for the MO2 profile at <paramref name="profileDir"/>, resolving each
    /// through the same overwrite &gt; mods(priority) &gt; Data VFS the loose/plugin layers use. The roots are the
    /// ones <see cref="Mo2LoadOrder.Build"/> already receives; <paramref name="gamePath"/> is only the game-dir
    /// Skyrim.ini fallback (the profile's Skyrim.ini — the MO2 profile-specific INI — is tried first).</summary>
    public static ArchiveDiscoveryResult Discover(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string gamePath)
    {
        var warnings = new List<string>();
        var comp = Mo2LoadOrder.ReadComposition(profileDir, warnings);

        // Active plugins in load order (winner LAST) — same filter as Mo2LoadOrder.Build: drop the unchecked
        // (inactive) ones; implicit masters/CC and explicitly-active plugins both load.
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

    /// <summary>Build archive-filename → the winning real path AND the MO2 layer it came from, the .bsa twin of
    /// <see cref="Mo2LoadOrder"/>'s plugin filename map: MO2's overwrite layer first (beats every mod), then enabled
    /// mods highest-priority-first (first sighting wins), then the game Data folder last (base game = lowest).
    /// OrdinalIgnoreCase keys. The layer rides along so a caller can address an archive by naming the mod it lives
    /// in, not only by its filename (#388).</summary>
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

    /// <summary>Top-level *.bsa in one folder (a mod root is the Data root, so archives live at its top level).
    /// Yields (filename, full path). Silent on a missing/inaccessible folder — a modlist entry can lack a real
    /// folder. The explicit extension check guards the Windows "*.bsa matches short names" quirk (as
    /// <see cref="Mo2LoadOrder"/>'s plugin enumerator does for *.es*).</summary>
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

    /// <summary>The always-loaded base archive filenames from Skyrim.ini's [Archive] sResourceArchiveList +
    /// sResourceArchiveList2, in file order. MO2 redirects the game INIs into the active PROFILE folder (the common
    /// Wabbajack/portable setup), so the profile's Skyrim.ini is tried first, then the game-dir copy. The user's
    /// Documents\My Games copy is NOT reachable from the MO2 instance, so if neither is found we surface it loud —
    /// base-game-only assets then can't be seen, which a caller acting on "absent → fine" must know.</summary>
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

    /// <summary>Parse a Skyrim.ini's [Archive] section for sResourceArchiveList + sResourceArchiveList2, returning
    /// the comma-separated archive filenames in file order (sResourceArchiveList before its "2"). Tolerant of
    /// section/comment lines; returns empty (not an error) for an INI without the section — the caller then tries
    /// the next candidate.</summary>
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
