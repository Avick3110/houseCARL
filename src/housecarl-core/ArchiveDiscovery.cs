namespace HousecarlCore;

// ======================================================================
//  ArchiveDiscovery — build the ACTIVE-ARCHIVE list (the BSAs the game loads, each bound to its
//  owning plugin and that plugin's load-order rank) for AssetResolver, from the SAME static MO2
//  profile read Mo2LoadOrder already does (facegen-diagnostics Phase 2; Aaron 2026-06-15: FULL
//  discovery — both archive sources — not the co-name convention alone).
//
//  WHICH BSAs LOAD (Skyrim SE has two sources; a COMPLETE answer needs both, or an asset present
//  only in an un-scanned archive reads as falsely "absent" — a Q3 silent-wrong-answer):
//    1. BASE archives — the always-loaded set listed in Skyrim.ini's [Archive] sResourceArchiveList
//       + sResourceArchiveList2 (vanilla "Skyrim - Textures*.bsa" etc., where base-game NPC facegen
//       lives). These load FIRST → the LOWEST ranks.
//    2. PLUGIN-associated archives — for each active plugin X.<esp/esm/esl> the engine auto-loads
//       "X.bsa" and (SE) "X - Textures.bsa" when present. These load in plugin load order, so a
//       later plugin's archive outranks an earlier one's AND every base archive.
//
//  WINNING PHYSICAL PATH (MO2 VFS): a .bsa is itself subject to MO2's overwrite > enabled-mods
//  (highest priority first) > Data precedence, identical to how Mo2LoadOrder resolves a plugin
//  filename (a higher-priority mod can ship its own "X - Textures.bsa"). So we resolve each archive
//  FILENAME through that same VFS map, never by "look beside the .esp" — which would miss an
//  archive overridden by a higher-priority mod. (AssetResolver.DedupeArchives then collapses a path
//  bound by more than one plugin to its max-rank binding, so emitting a duplicate is harmless.)
//
//  RANK = "higher wins among BSAs" (AssetResolver.ActiveArchive contract). Base archives take the
//  low block (INI order: later entry = later loaded = higher); plugin archives take ranks above all
//  base archives, in load order (winner last). A plugin's "X.bsa" and "X - Textures.bsa" share the
//  plugin's rank (they load together; AssetResolver tie-breaks equal ranks by filename).
//
//  Q3: a Skyrim.ini we cannot find (so the base-archive list is unknown) is SURFACED as a warning,
//  never silently dropped — the caller acting on "no facegen → the NPC is fine" must know base-game
//  archives weren't scanned. A base archive named in the INI but absent on disk simply isn't loaded
//  (the INI can list a superset across game variants — not a problem worth a warning).
//
//  No game state, no VFS, no live tracking — pure static reads of the profile + mods/overwrite/Data
//  folders + Skyrim.ini, the same crash-free model as Mo2LoadOrder. See memory
//  project_facegen_diagnostics_resolver.
// ======================================================================

/// <summary>The active archives for a profile (feed <see cref="ArchiveDiscoveryResult.Archives"/> straight to
/// <see cref="AssetResolver.Build"/>'s activeArchives) plus any non-fatal problems (Q3 — e.g. a Skyrim.ini that
/// couldn't be found, so base-game BSAs aren't in the scan).</summary>
public sealed record ArchiveDiscoveryResult(IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> Warnings);

public static class ArchiveDiscovery
{
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
            if (archiveMap.TryGetValue(fn, out var path))
                archives.Add(new ActiveArchive(path, "Skyrim.ini [Archive]", rank));
            rank++;   // a distinct rank per INI entry (later in the list = later loaded); absent-on-disk just isn't added
        }

        // (2) plugin-associated archives — "X.bsa" + "X - Textures.bsa", in load order (winner last → higher rank).
        foreach (var name in activeOrdered)
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            foreach (var candidate in new[] { baseName + ".bsa", baseName + " - Textures.bsa" })
                if (archiveMap.TryGetValue(candidate, out var path))
                    archives.Add(new ActiveArchive(path, name, rank));
            rank++;   // both of a plugin's archives share its rank; advance once per plugin
        }

        return new ArchiveDiscoveryResult(archives, warnings);
    }

    /// <summary>Build archive-filename → winning real path, the .bsa twin of <see cref="Mo2LoadOrder"/>'s plugin
    /// filename map: MO2's overwrite layer first (beats every mod), then enabled mods highest-priority-first
    /// (first sighting wins), then the game Data folder last (base game = lowest). OrdinalIgnoreCase keys.</summary>
    static Dictionary<string, string> BuildArchiveMap(
        IReadOnlyList<string> enabledModsByPriority, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fn, full) in EnumerateArchives(overwriteDir))   // overwrite — beats every mod
            map[fn] = full;

        foreach (var mod in enabledModsByPriority)                    // highest priority first
            foreach (var (fn, full) in EnumerateArchives(Path.Combine(modsDir, mod)))
                if (!map.ContainsKey(fn)) map[fn] = full;             // first (highest-priority) wins

        foreach (var (fn, full) in EnumerateArchives(dataDir))        // base game / vanilla — lowest priority
            if (!map.ContainsKey(fn)) map[fn] = full;

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
    /// Documents\My Games copy is NOT reachable from the MO2 instance, so if neither is found we surface it LOUD
    /// (Q3) — base-game-only assets then can't be seen, which a caller acting on "absent → fine" must know.</summary>
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
