namespace HousecarlCore;

// ======================================================================
//  Mo2LoadOrder — the TRUE active load order, read STATICALLY from an MO2
//  portable instance's profile files (MCP step §8.5, decision 2026-06-01).
//
//  WHY static-file reads (not the USVFS, not live IOrganizer state):
//    The legacy build resolved the order from MO2's live IOrganizer API and
//    from the USVFS-merged Data folder. Both failed in practice — live state
//    drifts from MO2's truth without manual refreshes, the sync/monitoring
//    tooling crashed, and a subprocess-spawned server does NOT inherit the
//    USVFS (only MO2's Executables-UI launch does, and that LOCKS MO2). So the
//    safe, crash-free 1.0 path is to read the two files MO2 writes to disk:
//      • loadorder.txt — every plugin in load ORDER (masters first; WINNER LAST).
//      • modlist.txt   — the left-pane MOD priority (TOP = highest), which
//                        resolves WHICH physical copy wins when the same plugin
//                        filename is provided by more than one enabled mod
//                        (~110 such duplicate names in a large modlist).
//      • plugins.txt   — the active/inactive flag (`*` = active); used only to
//                        drop a plugin that's present-but-unchecked.
//    Output = an ordered list of REAL physical paths for LoadOrderResolver.Build
//    (which is built to take exactly that). No VFS ⇒ the server runs standalone.
//
//  FRESHNESS: the resolver's cheap mtime check re-reads these files when they change
//  (a mod/plugin toggle, a re-sort, or a profile SWITCH — the last seen via the
//  configured instance's ModOrganizer.ini), lazily on the NEXT tool call — no manual
//  restart, no live watcher. See memory project_mo2_load_order_resolution.
//
//  Q3: a plugin the load order lists but that no enabled mod (or the data folder)
//  provides is COLLECTED into Warnings and surfaced — never silently dropped.
//
//  COMPOSITION: ReadComposition() parses just these three text files (no mod-folder
//  walk) into the enabled/disabled breakdown the diagnostic (housecarl_load_order_status)
//  surfaces; Build() calls it, then adds the heavier physical-path resolution on top.
// ======================================================================

/// <summary>The resolved active order plus any non-fatal problems (Q3 — surfaced, not swallowed).</summary>
/// <param name="OrderedPaths">Real plugin paths, masters-first → highest priority LAST (resolver winner order).</param>
/// <param name="Warnings">Plugins listed in the order with no resolvable file, or missing profile files.</param>
/// <param name="ActiveCount">Active plugins in the load order (the resolution target).</param>
public sealed record Mo2OrderResult(
    IReadOnlyList<string> OrderedPaths, IReadOnlyList<string> Warnings, int ActiveCount)
{
    public int ResolvedCount => OrderedPaths.Count;
}

/// <summary>The MO2 profile's enabled/disabled COMPOSITION, parsed from the three profile text files only (no mod-folder
/// enumeration — cheap to re-read on demand). This is the picture the diagnostic surfaces; the heavier path resolution
/// (which physical file wins) lives in <see cref="Mo2LoadOrder.Build"/>. Names are verbatim from the files: mod folder
/// names for the mod lists, plugin filenames for the plugin lists.</summary>
/// <param name="EnabledMods">modlist.txt `+` entries (separators excluded), priority order (top = highest).</param>
/// <param name="DisabledMods">modlist.txt `-` entries (separators excluded) — present in MO2 but switched OFF.</param>
/// <param name="OrderedPluginNames">loadorder.txt — every plugin in load order (masters first, winner last).</param>
/// <param name="ActivePluginNames">plugins.txt `*` entries (the `*` stripped) — explicitly checked/active.</param>
/// <param name="InactivePluginNames">plugins.txt entries WITHOUT a `*` — present but unchecked (the game won't load them).</param>
/// <param name="ImplicitPluginNames">in the load order but NOT listed in plugins.txt at all — the force-loaded base/CC masters.</param>
public sealed record Mo2Composition(
    IReadOnlyList<string> EnabledMods,
    IReadOnlyList<string> DisabledMods,
    IReadOnlyList<string> OrderedPluginNames,
    IReadOnlySet<string> ActivePluginNames,
    IReadOnlyList<string> InactivePluginNames,
    IReadOnlyList<string> ImplicitPluginNames);

/// <summary>One on-disk sighting of a plugin FILENAME: its real path plus a human label for WHERE it was found
/// (the overwrite layer, a named mod folder, or the game Data folder) and whether that source is ENABLED in the
/// profile. <see cref="Mo2LoadOrder.LocatePlugin"/> returns these so a caller can distinguish a name NO folder
/// provides (missing), ONE folder provides (use it), or SEVERAL provide (ambiguous → surface, never guess — Q3).</summary>
public sealed record PluginFileHit(string Path, string Where, bool Enabled);

public static class Mo2LoadOrder
{
    static readonly string[] PluginExts = PluginFile.Extensions;   // the one shared home (HousecarlCore.PluginFile) — no divergent copy

    /// <summary>Read the active order from <paramref name="profileDir"/>'s loadorder.txt + modlist.txt + plugins.txt,
    /// resolving each active plugin to its WINNING real path: MO2's <paramref name="overwriteDir"/> first (the overwrite
    /// layer beats every mod — it's where tool outputs land), then the highest-priority enabled mod under
    /// <paramref name="modsDir"/> that provides the filename, falling back to <paramref name="dataDir"/> for vanilla/base
    /// plugins. The returned paths are in load order (winner last) — feed straight to <see cref="LoadOrderResolver.Build"/>.</summary>
    public static Mo2OrderResult Build(string profileDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var warnings = new List<string>();

        // The enabled/disabled COMPOSITION (text files only — cheap). The diagnostic re-reads this same parse fresh.
        var comp = ReadComposition(profileDir, warnings);

        // filename → WINNING real path: overwrite first, then highest-priority enabled mod (first-seen wins), data folder as base.
        var winningPath = BuildFilenameMap(comp.EnabledMods, modsDir, dataDir, overwriteDir);
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);

        // The can't-resolve warning names the places actually searched. The overwrite folder is only one of them in
        // MO2-instance mode; explicit-paths mode passes overwriteDir="" (no overwrite layer), so naming it there would
        // overstate the search (Q3 — honest about what was checked).
        var searchedPlaces = string.IsNullOrWhiteSpace(overwriteDir)
            ? "no enabled mod or the game Data folder"
            : "no enabled mod, the overwrite folder, or the game Data folder";

        // loadorder.txt order → drop unchecked plugins; resolve the rest to their winning path (winner last).
        var orderedPaths = new List<string>(comp.OrderedPluginNames.Count);
        int active = 0;
        foreach (var name in comp.OrderedPluginNames)
        {
            if (inactive.Contains(name)) continue;                  // present-but-unchecked in MO2 → not loaded
            active++;
            if (winningPath.TryGetValue(name, out var path))
                orderedPaths.Add(path);
            else
                warnings.Add(
                    $"load order lists '{name}' but {searchedPlaces} provides it (stale loadorder.txt? " +
                    "trigger an MO2 refresh / re-sort so it re-writes the profile files).");
        }

        return new Mo2OrderResult(orderedPaths, warnings, active);
    }

    /// <summary>Parse the profile's enabled/disabled COMPOSITION from loadorder.txt + modlist.txt + plugins.txt — text
    /// files ONLY, no mod-folder enumeration, so it is cheap to call on demand. The diagnostic (housecarl_load_order_status)
    /// re-reads this FRESH each call (independent of the cached resolver), so a just-toggled mod/plugin shows immediately;
    /// <see cref="Build"/> calls it too, then adds the heavier physical-path resolution on top. <paramref name="warnings"/>
    /// collects missing-file notes (Q3) when provided.</summary>
    public static Mo2Composition ReadComposition(string profileDir, List<string>? warnings = null)
    {
        var enabled = new List<string>();
        var disabled = new List<string>();
        ParseModlist(Path.Combine(profileDir, "modlist.txt"), enabled, disabled, warnings);

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new List<string>();
        ParsePlugins(Path.Combine(profileDir, "plugins.txt"), active, inactive);
        var inactiveSet = new HashSet<string>(inactive, StringComparer.OrdinalIgnoreCase);

        var ordered = ReadLoadOrderNames(Path.Combine(profileDir, "loadorder.txt"), warnings);
        var implicitNames = new List<string>();
        foreach (var name in ordered)
            if (!active.Contains(name) && !inactiveSet.Contains(name))
                implicitNames.Add(name);                            // in the order, never in plugins.txt → force-loaded master/CC

        return new Mo2Composition(enabled, disabled, ordered, active, inactive, implicitNames);
    }

    /// <summary>modlist.txt → enabled + disabled mod folder names (file order: TOP = highest priority). `+Name` = enabled,
    /// `-Name` = disabled, `#` = comment; a `…_separator` (either marker) is a UI separator, skipped from BOTH lists.</summary>
    static void ParseModlist(string modlistPath, List<string> enabled, List<string> disabled, List<string>? warnings)
    {
        if (!File.Exists(modlistPath))
        {
            warnings?.Add($"modlist.txt not found at '{modlistPath}' — duplicate-name plugins cannot be priority-resolved.");
            return;
        }
        foreach (var raw in File.ReadAllLines(modlistPath))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#') continue;
            char marker = line[0];
            if (marker != '+' && marker != '-') continue;           // only +/- lines are mods
            var name = line[1..].Trim();
            if (name.Length == 0 || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            (marker == '+' ? enabled : disabled).Add(name);
        }
    }

    /// <summary>Build filename → winning real path. MO2's OVERWRITE folder is scanned first — it is the top of MO2's
    /// VFS (a copy there beats every mod; tool outputs like Synthesis patches and xEdit "new file" plugins live there,
    /// and MO2 lists them in the profile files — 2026-06-12 hunt F9: these were unresolvable and the warning
    /// misdiagnosed them as a stale-profile problem a re-sort can't fix). Then enabled mods, highest-priority FIRST,
    /// first sighting of a filename wins (a higher-priority mod's copy beats a lower one's — MO2's own overwrite rule).
    /// The data folder is scanned LAST and only fills names no mod provided (vanilla masters / base game = lowest priority).</summary>
    static Dictionary<string, string> BuildFilenameMap(IReadOnlyList<string> enabledModsByPriority, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fn, full) in EnumeratePlugins(overwriteDir))  // MO2's overwrite layer — beats every mod
            map[fn] = full;                                         // (map is empty here; plain set keeps the rule obvious)

        foreach (var mod in enabledModsByPriority)                  // highest priority first
        {
            var modRoot = Path.Combine(modsDir, mod);
            foreach (var (fn, full) in EnumeratePlugins(modRoot))
                if (!map.ContainsKey(fn)) map[fn] = full;           // first (highest-priority) wins
        }

        foreach (var (fn, full) in EnumeratePlugins(dataDir))       // base game / vanilla masters — lowest priority
            if (!map.ContainsKey(fn)) map[fn] = full;

        return map;
    }

    /// <summary>Top-level *.esp/.esm/.esl in one folder (a mod root is the Data root, so plugins live at its top level).
    /// Yields (filename, full path). Silent on a missing/inaccessible folder — a modlist entry can lack a real folder.</summary>
    static IEnumerable<(string fn, string full)> EnumeratePlugins(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) yield break;
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.es*", opts); }
        catch { yield break; }
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            if (Array.Exists(PluginExts, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                yield return (Path.GetFileName(f), f);
        }
    }

    /// <summary>plugins.txt → the active set (`*`-prefixed, the `*` stripped) and the inactive list (present but unchecked,
    /// no `*`). The implicit masters/CC aren't listed here at all — they're force-loaded (classified in <see cref="ReadComposition"/>).</summary>
    static void ParsePlugins(string pluginsPath, HashSet<string> active, List<string> inactive)
    {
        if (!File.Exists(pluginsPath)) return;
        foreach (var raw in File.ReadAllLines(pluginsPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '*') active.Add(line[1..].Trim());       // checked/active
            else inactive.Add(line);                                // listed without `*` → unchecked
        }
    }

    /// <summary>loadorder.txt → plugin filenames in load order (top → bottom = lowest → highest priority). Plain
    /// filenames, `#` header skipped.</summary>
    static List<string> ReadLoadOrderNames(string loadOrderPath, List<string>? warnings)
    {
        var names = new List<string>();
        if (!File.Exists(loadOrderPath))
        {
            warnings?.Add($"loadorder.txt not found at '{loadOrderPath}' — cannot determine the active load order.");
            return names;
        }
        foreach (var raw in File.ReadAllLines(loadOrderPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            names.Add(line);
        }
        return names;
    }

    /// <summary>Locate every on-disk copy of a plugin FILENAME across the WHOLE MO2 install — the overwrite layer,
    /// EVERY mod folder (enabled AND disabled), and the game Data folder — NOT just the active order's winner map
    /// (<see cref="Build"/>, which resolves enabled mods only). This is what lets a read of an INACTIVE plugin reach a
    /// DISABLED donor's file — the realistic standalone-copy case (you standalone-ize a follower you're REMOVING from
    /// the active order). Priority-ordered like the active map (overwrite → enabled by modlist priority → disabled →
    /// Data), but ALL hits are returned, not just the first: a filename several folders provide is reported so the
    /// caller can ask WHICH rather than silently pick one (Q3). Pure disk existence checks (one stat per candidate
    /// folder — no folder enumeration, opens no plugin). <paramref name="filename"/> is reduced to a bare name so a
    /// caller's stray path parts can't escape a folder; the direct-path case is the caller's to handle before here.</summary>
    public static IReadOnlyList<PluginFileHit> LocatePlugin(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string filename)
        => LocatePlugin(ReadComposition(profileDir), modsDir, dataDir, overwriteDir, filename);

    /// <summary>As the profileDir overload, but reusing a <see cref="Mo2Composition"/> the caller already parsed — so a
    /// scan of a file AND its declared masters pays the modlist parse once, not once per name.</summary>
    public static IReadOnlyList<PluginFileHit> LocatePlugin(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string filename)
    {
        var hits = new List<PluginFileHit>();
        var fn = Path.GetFileName(filename?.Trim() ?? "");
        if (fn.Length == 0) return hits;

        void TryDir(string dir, string where, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try { var p = Path.Combine(dir, fn); if (File.Exists(p)) hits.Add(new PluginFileHit(p, where, enabled)); }
            catch { /* an inaccessible candidate folder is simply not a hit — never a false 'found' (Q3) */ }
        }

        TryDir(overwriteDir, "overwrite", enabled: true);             // MO2's overwrite layer (top of the VFS)
        foreach (var mod in comp.EnabledMods) TryDir(Path.Combine(modsDir, mod), $"mod '{mod}' (enabled)", enabled: true);
        foreach (var mod in comp.DisabledMods) TryDir(Path.Combine(modsDir, mod), $"mod '{mod}' (DISABLED)", enabled: false);
        TryDir(dataDir, "game Data", enabled: true);                  // vanilla / base — lowest priority
        return hits;
    }

    /// <summary>True iff SOME on-disk copy of <paramref name="filename"/> exists anywhere in the install (overwrite,
    /// any mod folder enabled or disabled, or Data) — the short-circuiting existence twin of <see cref="LocatePlugin"/>:
    /// it stops at the FIRST hit, so checking a master that IS present costs a handful of stats, not a whole-install
    /// scan. Used for the read-plugin-file "is this declared master installed?" advisory (Q3 — say when it isn't).</summary>
    public static bool PluginFileExists(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string filename)
    {
        var fn = Path.GetFileName(filename?.Trim() ?? "");
        if (fn.Length == 0) return false;
        bool Has(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try { return File.Exists(Path.Combine(dir, fn)); } catch { return false; }
        }
        if (Has(overwriteDir)) return true;
        foreach (var mod in comp.EnabledMods) if (Has(Path.Combine(modsDir, mod))) return true;
        foreach (var mod in comp.DisabledMods) if (Has(Path.Combine(modsDir, mod))) return true;
        return Has(dataDir);
    }
}
