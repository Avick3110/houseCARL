namespace HousecarlCore;

// The active load order, read from an MO2 portable instance's profile files on disk, never from the USVFS; the priority model and the three profile files are in docs/architecture/mo2-instance.md.

/// <summary>The resolved active order plus any non-fatal problems — surfaced, not swallowed; <see cref="OrderedPaths"/> is in resolver winner order, highest priority last.</summary>
/// <param name="Unserved">The plugins the profile lists as loading that no enabled layer serves, so the game does not load them whatever plugins.txt says.</param>
public sealed record Mo2OrderResult(
    IReadOnlyList<string> OrderedPaths, IReadOnlyList<string> Warnings, IReadOnlyList<UnservedPlugin> Unserved)
{
    public int ResolvedCount => OrderedPaths.Count;
}

/// <summary>A plugin the profile lists as loading (ticked, or an implicit master) that no enabled layer serves; <paramref name="SwitchedOffMod"/> is the switched-off mod folder holding a copy, null when none does.</summary>
public sealed record UnservedPlugin(string Name, string? SwitchedOffMod);

/// <summary>The MO2 profile's enabled/disabled composition, parsed from the three profile text files only, so it is cheap to re-read on demand; names are verbatim from the files.</summary>
public sealed record Mo2Composition(
    IReadOnlyList<string> EnabledMods,
    IReadOnlyList<string> DisabledMods,
    IReadOnlyList<string> OrderedPluginNames,
    IReadOnlySet<string> ActivePluginNames,
    IReadOnlyList<string> InactivePluginNames,
    IReadOnlyList<string> ImplicitPluginNames);

/// <summary>An MO2 profile text file is locked by another process right now — the sharing or lock violation only, which is a transient to retry; it derives from <see cref="IOException"/> so mid-write catches keep seeing it.</summary>
public sealed class ProfileUnreadableException : IOException
{
    /// <summary>The profile file that could not be read.</summary>
    public string ProfilePath { get; }

    public ProfileUnreadableException(string profilePath, Exception inner)
        : base($"the MO2 profile file '{System.IO.Path.GetFileName(profilePath)}' is held open by another process " +
               "right now, so the load order could not be read (MO2 holds these while it re-sorts). Nothing was " +
               "changed; run this again in a moment.", inner)
        => ProfilePath = profilePath;
}

/// <summary>One on-disk sighting of a plugin filename: its real path, a label for where it was found, and whether that source is enabled in the profile.</summary>
public sealed record PluginFileHit(string Path, string Where, bool Enabled);

public static class Mo2LoadOrder
{
    /// <summary>Test seam: raised with the profile folder on every <see cref="ReadComposition"/>, never for <see cref="Build"/>'s own parse; null in the product.</summary>
    internal static event Action<string>? CompositionRead;

    static readonly string[] PluginExts = PluginFile.Extensions;   // the one shared home (HousecarlCore.PluginFile) — no divergent copy

    /// <summary>Read the active order from <paramref name="profileDir"/>'s three profile files, resolving each active plugin to its winning real path; the returned paths are in load order, winner last.</summary>
    public static Mo2OrderResult Build(string profileDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var warnings = new List<string>();

        // The enabled/disabled COMPOSITION (text files only — cheap). The diagnostic re-reads this same parse fresh.
        var comp = ParseComposition(profileDir, warnings);

        // filename → WINNING real path: overwrite first, then highest-priority enabled mod (first-seen wins), data folder as base.
        var winningPath = BuildFilenameMap(comp.EnabledMods, modsDir, dataDir, overwriteDir);
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);

        // loadorder.txt order → drop unchecked plugins; resolve the rest to their winning path (winner last).
        var orderedPaths = new List<string>(comp.OrderedPluginNames.Count);
        foreach (var name in comp.OrderedPluginNames)
            if (!inactive.Contains(name) && winningPath.TryGetValue(name, out var path))   // unchecked in MO2 → not loaded
                orderedPaths.Add(path);

        var unserved = UnservedIn(comp, winningPath, modsDir);
        foreach (var u in unserved)
            warnings.Add(u.SwitchedOffMod is { } offMod
                ? $"load order lists '{u.Name}', but {ProvidedBySwitchedOffMod(offMod)}."
                : $"load order lists '{u.Name}' but {SearchedPlaces(overwriteDir)} provides it (stale loadorder.txt? " +
                  "trigger an MO2 refresh / re-sort so it re-writes the profile files).");

        return new Mo2OrderResult(orderedPaths, warnings, unserved);
    }

    /// <summary>The places a plugin file is served from, as the subject of "… provides it"; explicit-paths mode has no overwrite folder, so only the places actually searched are named.</summary>
    public static string SearchedPlaces(string overwriteDir) =>
        string.IsNullOrWhiteSpace(overwriteDir)
            ? "no enabled mod or the game Data folder"
            : "no enabled mod, the overwrite folder, or the game Data folder";

    /// <summary>The plugins <paramref name="comp"/> lists as loading that no enabled layer serves, off a fresh listing of the enabled layers — for a reader with no order build, such as another profile's inspection; it lists every enabled mod folder.</summary>
    public static IReadOnlyList<UnservedPlugin> Unserved(Mo2Composition comp, string modsDir, string dataDir, string overwriteDir) =>
        UnservedIn(comp, BuildFilenameMap(comp.EnabledMods, modsDir, dataDir, overwriteDir), modsDir);

    /// <summary>Every name listed as loading (not unchecked) that <paramref name="served"/> does not hold, with the switched-off mod folder holding a copy.</summary>
    static List<UnservedPlugin> UnservedIn(Mo2Composition comp, Dictionary<string, string> served, string modsDir)
    {
        var inactive = new HashSet<string>(comp.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
        // Served by no enabled layer: MO2's VFS does not present the file, so it is not active.
        var names = comp.OrderedPluginNames.Where(n => !inactive.Contains(n) && !served.ContainsKey(n)).ToList();
        if (names.Count == 0) return [];
        var offMod = SwitchedOffCopies(comp.DisabledMods, modsDir, names);
        return names.Select(n => new UnservedPlugin(n, offMod.GetValueOrDefault(n))).ToList();
    }

    /// <summary>For each of <paramref name="names"/>, the first switched-off mod folder in modlist.txt order holding a copy: one listing per disabled folder, stopping once every name is placed.</summary>
    static Dictionary<string, string> SwitchedOffCopies(IReadOnlyList<string> disabledMods, string modsDir, IReadOnlyCollection<string> names)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modsDir)) return found;
        var pending = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var mod in disabledMods)
        {
            if (pending.Count == 0) break;
            foreach (var (fn, _) in EnumeratePlugins(Path.Combine(modsDir, mod)))
                if (pending.Remove(fn)) found[fn] = mod;
        }
        return found;
    }

    /// <summary>The composition's active plugin names: ticked or implicit, minus <paramref name="unserved"/>.</summary>
    public static IReadOnlySet<string> ActiveNames(Mo2Composition comp, IReadOnlyList<UnservedPlugin> unserved)
    {
        var set = new HashSet<string>(comp.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(comp.ImplicitPluginNames);
        foreach (var u in unserved) set.Remove(u.Name);
        return set;
    }

    /// <summary>The one sentence for a plugin whose copy sits in a mod folder MO2 has switched off.</summary>
    public static string ProvidedBySwitchedOffMod(string modFolder) =>
        $"it is provided by mod '{modFolder}', which is switched OFF in MO2 — switch it on, then re-sort";

    /// <summary>Parse the profile's enabled/disabled composition from the three profile text files; the diagnostic re-reads this fresh each call, and <see cref="Build"/> adds the physical-path resolution on top.</summary>
    public static Mo2Composition ReadComposition(string profileDir, List<string>? warnings = null)
    {
        CompositionRead?.Invoke(profileDir);
        return ParseComposition(profileDir, warnings);
    }

    /// <summary>The parse behind <see cref="ReadComposition"/>, which <see cref="Build"/> calls directly.</summary>
    static Mo2Composition ParseComposition(string profileDir, List<string>? warnings)
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

    /// <summary>modlist.txt to enabled and disabled mod folder names in file order (top = highest priority); a <c>…_separator</c> entry is skipped from both lists.</summary>
    static void ParseModlist(string modlistPath, List<string> enabled, List<string> disabled, List<string>? warnings)
    {
        if (!File.Exists(modlistPath))
        {
            warnings?.Add($"modlist.txt not found at '{modlistPath}' — duplicate-name plugins cannot be priority-resolved.");
            return;
        }
        foreach (var raw in ReadProfileLines(modlistPath))
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

    /// <summary>Build filename to winning real path: overwrite, then enabled mods highest-priority first with the first sighting winning, then the game Data folder. The one listing of which plugin files the layers serve.</summary>
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

    /// <summary>Top-level *.esp/.esm/.esl in one folder, as (filename, full path); silent on a missing or inaccessible folder, since a modlist entry can lack a real folder.</summary>
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

    /// <summary>plugins.txt to the active set (the <c>*</c> stripped) and the inactive list; the implicit masters are not listed there at all and are classified in <see cref="ReadComposition"/>.</summary>
    static void ParsePlugins(string pluginsPath, HashSet<string> active, List<string> inactive)
    {
        if (!File.Exists(pluginsPath)) return;
        foreach (var raw in ReadProfileLines(pluginsPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '*') active.Add(line[1..].Trim());       // checked/active
            else inactive.Add(line);                                // listed without `*` → unchecked
        }
    }

    /// <summary>loadorder.txt to plugin filenames in load order, top to bottom being lowest to highest priority; a <c>#</c> header is skipped.</summary>
    static List<string> ReadLoadOrderNames(string loadOrderPath, List<string>? warnings)
    {
        var names = new List<string>();
        if (!File.Exists(loadOrderPath))
        {
            warnings?.Add($"loadorder.txt not found at '{loadOrderPath}' — cannot determine the active load order.");
            return names;
        }
        foreach (var raw in ReadProfileLines(loadOrderPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            names.Add(line);
        }
        return names;
    }

    /// <summary>Read one profile text file, naming a LOCKED file as the transient it is; only the Win32 sharing and lock violations become <see cref="ProfileUnreadableException"/>, and every other fault keeps its own error.</summary>
    static string[] ReadProfileLines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException)
                                     && (ex.HResult & 0xFFFF) is 32 or 33)   // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        {
            throw new ProfileUnreadableException(path, ex);
        }
    }

    /// <summary>Locate every on-disk copy of a plugin filename across the WHOLE install — overwrite, every mod folder enabled, disabled or unlisted, and game Data — returning ALL hits rather than the first; <paramref name="filename"/> is reduced to a bare name.</summary>
    public static IReadOnlyList<PluginFileHit> LocatePlugin(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string filename)
        => LocatePlugin(ReadComposition(profileDir), modsDir, dataDir, overwriteDir, filename);

    /// <summary>As the profileDir overload, but reusing a <see cref="Mo2Composition"/> the caller already parsed, so a scan pays the modlist parse once.</summary>
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

    /// <summary>THE layer sequence a filename is searched across, in precedence order; written once because <see cref="LocatePlugin"/> and <see cref="AllPluginFileNames"/> must draw on the same set of places. The label identifies the layer and its state, never a remedy.</summary>
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

    /// <summary>Every plugin filename the install provides, walking <see cref="CandidateFolders"/> — the did-you-mean pool and the declared-master split's is-it-installed discriminant. It lists each folder, so it is read once per call and then asked many times.</summary>
    public static IReadOnlyCollection<string> AllPluginFileNames(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, _, _) in CandidateFolders(comp, modsDir, dataDir, overwriteDir))
            foreach (var (fn, _) in EnumeratePlugins(dir))
                names.Add(fn);
        return names;
    }

    /// <summary>Mod folders that exist under <paramref name="modsDir"/> but that modlist.txt mentions in NEITHER list; one directory listing, and a missing or inaccessible ModsDir yields nothing.</summary>
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

    /// <summary>Split declared masters the active order does not satisfy into the two cases whose REMEDIES differ — <c>NotInstalled</c> (install it) and <c>InstalledButInactive</c> (enable it) — the ONE home for the split, discriminated by presence in <paramref name="installedPluginFiles"/>, which is what <see cref="AllPluginFileNames"/> returns.</summary>
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
