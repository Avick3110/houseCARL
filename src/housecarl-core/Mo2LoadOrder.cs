namespace HousecarlCore;

// ======================================================================
//  Mo2LoadOrder — the active load order, read from an MO2 portable instance's
//  profile files on disk. Never from the USVFS or live IOrganizer state: a
//  subprocess-spawned server does not inherit MO2's VFS (only MO2's own
//  Executables launch does, and that locks MO2), so the three text files are
//  the only standalone source of truth.
//    • loadorder.txt — every plugin in load order (masters first, WINNER LAST).
//    • modlist.txt   — mod priority, TOP = highest; decides which physical copy
//                      wins when several enabled mods provide the same filename.
//    • plugins.txt   — the `*` active flag; drops present-but-unchecked plugins.
//  Output is an ordered list of real paths for LoadOrderResolver.Build.
//  Freshness comes from the resolver's mtime check on these files (also on the
//  instance's ModOrganizer.ini, which is where a profile SWITCH shows up).
//  A listed plugin no folder provides goes into Warnings, never a silent drop.
// ======================================================================

/// <summary>The resolved active order plus any non-fatal problems — surfaced, not swallowed.</summary>
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
/// provides (missing), ONE folder provides (use it), or SEVERAL provide (ambiguous → surface, never guess).</summary>
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

        // The can't-resolve warning names only the places actually searched: explicit-paths mode passes overwriteDir=""
        // (no overwrite layer), so naming it there would overstate the search.
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
    /// collects missing-file notes when provided.</summary>
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
    /// and MO2 lists them in the profile files, so skipping this layer leaves them unresolvable). Then enabled mods, highest-priority FIRST,
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
    /// EVERY mod folder (enabled, disabled, AND unlisted), and the game Data folder — NOT just the active order's
    /// winner map (<see cref="Build"/>, which resolves enabled mods only). This is what lets a read of an INACTIVE
    /// plugin reach a DISABLED donor's file — the realistic standalone-copy case (you standalone-ize a follower you're
    /// REMOVING from the active order). UNLISTED = a mod folder on disk that modlist.txt does not mention at all —
    /// exactly the state of a patch houseCARL just wrote, before the MO2 refresh registers it; the write side resolves
    /// such a patch by filename, so this read side must reach it too. Priority-ordered
    /// like the active map (overwrite → enabled by modlist priority → disabled → unlisted → Data), but ALL hits are
    /// returned, not just the first: a filename several folders provide is reported so the caller can ask WHICH
    /// rather than silently pick one. One stat per LISTED candidate folder plus one directory listing of ModsDir
    /// for the unlisted sweep (no per-folder enumeration, opens no plugin). <paramref name="filename"/> is reduced to
    /// a bare name so a caller's stray path parts can't escape a folder; the direct-path case is the caller's to
    /// handle before here.</summary>
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

        foreach (var (dir, where, enabled) in CandidateFolders(comp, modsDir, dataDir, overwriteDir))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { var p = Path.Combine(dir, fn); if (File.Exists(p)) hits.Add(new PluginFileHit(p, where, enabled)); }
            catch { /* an inaccessible candidate folder is simply not a hit — never a false 'found' */ }
        }
        return hits;
    }

    /// <summary>THE layer sequence a filename is searched across, in precedence order — overwrite (top of MO2's VFS),
    /// enabled mods by modlist priority, DISABLED mods, folders on disk that modlist.txt mentions in neither list (a
    /// fresh houseCARL patch pre-refresh), then game Data (vanilla/base, lowest).
    /// <para>Written ONCE because two things ask about it: <see cref="LocatePlugin"/> stats ONE filename per folder,
    /// and <see cref="AllPluginFileNames"/> enumerates every plugin in the same folders. A "not found" answer and the
    /// "did you mean" that follows it must be drawn from the SAME set of places, or the second contradicts the first
    /// by suggesting nothing while the name sits in a folder the sweep covered.</para>
    /// <para>The label IDENTIFIES the layer and its state; it must NOT carry a remedy. Callers state the remedy in
    /// their own cause line, so a remedy here prints the same instruction twice.</para></summary>
    static IEnumerable<(string dir, string where, bool enabled)> CandidateFolders(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir)
    {
        yield return (overwriteDir, "overwrite", true);
        foreach (var mod in comp.EnabledMods) yield return (Path.Combine(modsDir, mod), $"mod '{mod}' (enabled)", true);
        foreach (var mod in comp.DisabledMods) yield return (Path.Combine(modsDir, mod), $"mod '{mod}' (DISABLED)", false);
        foreach (var dir in UnlistedModFolders(comp, modsDir))
            yield return (dir, $"mod '{Path.GetFileName(dir)}' (UNLISTED)", false);
        yield return (dataDir, "game Data", true);
    }

    /// <summary>Every plugin FILENAME the install provides, walking <see cref="CandidateFolders"/> — the same layer
    /// sequence <see cref="LocatePlugin"/> searches, which is what lets an answer drawn from this set and an answer
    /// drawn from a locate mean the same install. Two things read it: the did-you-mean pool for a name found
    /// NOWHERE, and the declared-master split's is-it-installed discriminant
    /// (<see cref="SplitUnsatisfiedMasters"/>). Deliberately NOT free: where the locate stats one name per folder,
    /// this lists each folder — so it is read ONCE per call and then asked many times, never re-derived per name.
    /// De-duplicated case-insensitively; a missing or inaccessible folder contributes nothing rather than
    /// throwing.</summary>
    public static IReadOnlyCollection<string> AllPluginFileNames(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, _, _) in CandidateFolders(comp, modsDir, dataDir, overwriteDir))
            foreach (var (fn, _) in EnumeratePlugins(dir))
                names.Add(fn);
        return names;
    }

    /// <summary>Mod folders that exist under <paramref name="modsDir"/> on disk but that modlist.txt mentions in
    /// NEITHER list — the state of a mod folder created since MO2 last rewrote the profile (houseCARL's own fresh
    /// patches live here until the refresh). One directory listing; a missing/inaccessible ModsDir yields nothing —
    /// never a false hit.</summary>
    static IEnumerable<string> UnlistedModFolders(Mo2Composition comp, string modsDir)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) yield break;
        var listed = new HashSet<string>(comp.EnabledMods, StringComparer.OrdinalIgnoreCase);
        listed.UnionWith(comp.DisabledMods);
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir); }
        catch { yield break; }
        foreach (var dir in dirs)
            if (!listed.Contains(Path.GetFileName(dir)))
                yield return dir;
    }

    /// <summary>Split declared masters that are NOT satisfied by the active order into the two cases whose REMEDIES
    /// differ: <c>NotInstalled</c> — no copy anywhere in the install, so the answer is INSTALL it; and
    /// <c>InstalledButInactive</c> — a copy is there but the order does not load it (it sits in a disabled mod, or
    /// the plugin is unticked), so the answer is ENABLE it. This is the ONE home for the split:
    /// <c>read_plugin_file</c>'s master advisory and <c>housecarl_check</c>'s missing-master remedy both call it, so
    /// the two surfaces cannot come to different conclusions about the same master.
    /// <para>Every name handed in lands in exactly one of the two lists, in the order given: the caller decides what
    /// "unsatisfied" means against its own notion of the active order, and this decides only which of the two
    /// remedies each one wants.</para>
    /// <para>The discriminant is presence in <paramref name="installedPluginFiles"/>, which is what
    /// <see cref="AllPluginFileNames"/> returns — where "installed" means is stated there, once. Taking the set
    /// rather than deriving it is what keeps the cost off the name count: a sweep splitting for many plugins over
    /// one install reads the install once and asks it per report, so a master nothing provides costs the same
    /// whether one plugin declares it or thirty. Drawing the set from anywhere else would let "installed" mean a
    /// different set of folders here than at a locate.</para>
    /// <para>The case-insensitive comparison is built HERE rather than assumed of the collection handed in: a set
    /// carrying an ordinal comparer would answer "not installed" for a name differing only in case, and a wrong
    /// remedy delivered confidently is the failure this split exists to prevent. It is one pass over names
    /// already in memory — no filesystem work, whatever the caller passes.</para></summary>
    public static (IReadOnlyList<string> NotInstalled, IReadOnlyList<string> InstalledButInactive) SplitUnsatisfiedMasters(
        IReadOnlyCollection<string> installedPluginFiles, IEnumerable<string> unsatisfied)
    {
        var installed = new HashSet<string>(installedPluginFiles, StringComparer.OrdinalIgnoreCase);
        var notInstalled = new List<string>();
        var inactive = new List<string>();
        foreach (var m in unsatisfied)
        {
            // A name is reduced to its FILENAME before it is looked up; a blank one is installed nowhere.
            if (installed.Contains(Path.GetFileName(m?.Trim() ?? ""))) inactive.Add(m);
            else notInstalled.Add(m);
        }
        return (notInstalled, inactive);
    }
}
