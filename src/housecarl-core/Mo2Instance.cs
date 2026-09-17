namespace HousecarlCore;

// Derive the four load-order roots from ONE path — the MO2 instance folder — out of its ModOrganizer.ini; the derivation and the QSettings quirks are in docs/architecture/mo2-instance.md.

/// <summary>The four load-order roots derived from an MO2 instance folder, plus the active profile name and game root; feed them straight to <see cref="Mo2LoadOrder.Build"/>.</summary>
public sealed record Mo2InstancePaths(
    string InstanceDir, string ProfileName, string ProfileDir, string ModsDir, string DataDir, string GamePath, string OverwriteDir);

public static class Mo2Instance
{
    public const string IniFileName = "ModOrganizer.ini";

    /// <summary>The ModOrganizer.ini path for an instance folder (the file the freshness check stats for a profile switch).</summary>
    public static string IniPath(string instanceDir) => Path.Combine(instanceDir, IniFileName);

    /// <summary>Derive the load-order roots from an instance folder, or THROW naming what is missing; use <see cref="Validate"/> for the problems without an exception.</summary>
    public static Mo2InstancePaths Resolve(string instanceDir)
    {
        var problems = new List<string>();
        var paths = Derive(instanceDir, problems);
        if (paths is null)
            throw new InvalidOperationException(
                $"'{instanceDir}' is not a usable Mod Organizer 2 instance — {string.Join("; ", problems)}.");
        return paths;
    }

    /// <summary>Non-throwing derive for the cheap freshness re-check: never throws, so a transient read yields false and the caller keeps its last good set.</summary>
    public static bool TryResolve(string instanceDir, out Mo2InstancePaths? paths)
    {
        paths = Derive(instanceDir, null);
        return paths is not null;
    }

    /// <summary>Validate a candidate instance folder for the setup tool: whether it is usable, the derived paths, and the specific problems to show the user.</summary>
    public static (bool ok, Mo2InstancePaths? paths, IReadOnlyList<string> problems) Validate(string instanceDir)
    {
        var problems = new List<string>();
        var paths = Derive(instanceDir, problems);
        return (paths is not null, paths, problems);
    }

    /// <summary>Just the active profile name, cheaply — what the freshness check reads to learn whether the user switched profiles; null if the file or key is absent.</summary>
    public static string? ReadSelectedProfile(string instanceDir)
    {
        var ini = IniPath(instanceDir);
        if (!File.Exists(ini)) return null;
        string[] lines;
        try { lines = File.ReadAllLines(ini); }
        catch { return null; }
        var v = CleanValue(FindValue(lines, "selected_profile"));
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>The single derive worker: it populates <paramref name="problems"/> when given one, and returns paths ONLY when every required piece resolves to a real folder.</summary>
    static Mo2InstancePaths? Derive(string instanceDir, List<string>? problems)
    {
        if (string.IsNullOrWhiteSpace(instanceDir)) { problems?.Add("no path was given"); return null; }
        instanceDir = instanceDir.Trim().TrimEnd('\\', '/');
        if (!Directory.Exists(instanceDir)) { problems?.Add($"the folder does not exist: '{instanceDir}'"); return null; }

        var ini = IniPath(instanceDir);
        if (!File.Exists(ini))
        {
            problems?.Add($"no {IniFileName} here — point at the MO2 instance folder (for a Wabbajack/portable list, the list's install folder)");
            return null;
        }

        string[] lines;
        try { lines = File.ReadAllLines(ini); }
        catch (Exception ex) { problems?.Add($"cannot read {IniFileName}: {ex.Message}"); return null; }

        var profile  = CleanValue(FindValue(lines, "selected_profile"));
        var gamePath = CleanValue(FindValue(lines, "gamePath"));
        var baseDir  = CleanValue(FindValue(lines, "base_directory"));

        if (string.IsNullOrWhiteSpace(profile))  problems?.Add($"{IniFileName} has no selected_profile (open a profile in MO2 once)");
        if (string.IsNullOrWhiteSpace(gamePath)) problems?.Add($"{IniFileName} has no gamePath (MO2 doesn't know where the game is)");

        // base_directory SET but missing is its own problem; an absent one is the documented portable default and stays quiet.
        if (!string.IsNullOrWhiteSpace(baseDir) && !Directory.Exists(baseDir))
            problems?.Add($"{IniFileName} sets base_directory='{baseDir}' but that folder doesn't exist — falling back to the instance dir for mods/ + profiles/");

        // base_directory overrides where mods/ + profiles/ live; absent (the common portable case) ⇒ the instance dir.
        var basePath = (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir)) ? baseDir! : instanceDir;
        var modsDir    = Path.Combine(basePath, "mods");
        var profileDir = string.IsNullOrWhiteSpace(profile)  ? "" : Path.Combine(basePath, "profiles", profile!);
        var dataDir    = string.IsNullOrWhiteSpace(gamePath) ? "" : Path.Combine(gamePath!, "Data");

        if (profileDir.Length > 0 && !Directory.Exists(profileDir))
            problems?.Add($"the active profile's folder is missing: '{profileDir}'");
        else if (profileDir.Length > 0 && !File.Exists(Path.Combine(profileDir, "loadorder.txt")))
            problems?.Add($"profile '{profile}' has no loadorder.txt yet (open it in MO2 once so it writes its profile files)");
        if (!Directory.Exists(modsDir)) problems?.Add($"the mods folder is missing: '{modsDir}'");
        if (dataDir.Length > 0 && !Directory.Exists(dataDir))
            problems?.Add($"the game Data folder is missing: '{dataDir}' (from gamePath '{gamePath}')");

        // Validity is decided on the real outputs, NOT the problems list (so TryResolve with a null list is correct).
        bool ok = !string.IsNullOrWhiteSpace(profile) && !string.IsNullOrWhiteSpace(gamePath)
                  && Directory.Exists(profileDir) && File.Exists(Path.Combine(profileDir, "loadorder.txt"))
                  && Directory.Exists(modsDir) && Directory.Exists(dataDir);
        if (!ok) return null;

        // Overwrite is derived base-relative like mods and profiles, but never gates validity.
        return new Mo2InstancePaths(instanceDir, profile!, profileDir, modsDir, dataDir, gamePath!, Path.Combine(basePath, "overwrite"));
    }

    /// <summary>First <c>key=</c> line's raw value, the key matched case-insensitively and ignoring section headers; null if the key is not present.</summary>
    static string? FindValue(string[] lines, string key)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            // Tolerate whitespace around '=' — MO2 2.5.x writes "key = value"; older MO2 wrote "key=value".
            if (line.AsSpan(0, eq).TrimEnd().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..];
        }
        return null;
    }

    /// <summary>Read a QSettings value through the one shared reader, so both MO2 ini readers behave the same.</summary>
    static string? CleanValue(string? raw) => QtIniEscapes.Clean(raw);
}
