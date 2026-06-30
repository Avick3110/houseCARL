namespace HousecarlCore;

// ======================================================================
//  Mo2Instance — derive the four load-order roots from ONE path: the MO2
//  instance folder, read out of its ModOrganizer.ini (launch-arc item 3,
//  2026-06-02). The user configures a single "where is your MO2?" path;
//  ProfileDir / ModsDir / DataDir are derived, and the ACTIVE profile is
//  auto-detected — no hand-typed profile name, and a profile switch is
//  picked up by re-reading this file (LoadOrderService's freshness check).
//
//  ModOrganizer.ini layout we read (a portable / Wabbajack instance):
//      [General]
//      gameName=Skyrim Special Edition
//      selected_profile=@ByteArray(Default)
//      gamePath=@ByteArray(C:\\MO2\\Instance\\Stock Game)
//      [Settings]
//      base_directory=...            (OPTIONAL — absent ⇒ the instance dir IS the base)
//
//  Two Qt/QSettings quirks, both MEASURED against Aaron's real file (the
//  kind of thing the "measure, don't assume" discipline exists for):
//    • Values stored as QByteArray are wrapped @ByteArray(...) — but a plain
//      QString value (gameName) is NOT. So we unwrap WHEN PRESENT, per value,
//      never assume it. @Invalid() means "unset".
//    • Backslashes inside a value are doubled (\\). We unescape \\ → \. We do
//      NOT attempt the full Qt escape grammar — a path/profile name only ever
//      carries \\, and anything exotic just fails the existence check loud (Q3).
//
//  Derivation (base = base_directory if set+real, else the instance dir):
//      ModsDir      = base\mods
//      ProfileDir   = base\profiles\<selected_profile>
//      DataDir      = <gamePath>\Data
//      OverwriteDir = base\overwrite   (MO2's overwrite layer — tool outputs land here; NOT required to exist)
//
//  Q3: a missing/empty piece is NAMED in the problem list and the resolve
//  FAILS — never a silently-empty or half-derived path set.
// ======================================================================

/// <summary>The four load-order roots derived from an MO2 instance folder, plus the active profile name + game root.
/// Feed ProfileDir/ModsDir/DataDir/OverwriteDir straight to <see cref="Mo2LoadOrder.Build"/>.</summary>
/// <param name="InstanceDir">The MO2 instance folder (contains ModOrganizer.ini).</param>
/// <param name="ProfileName">The ACTIVE profile (ModOrganizer.ini selected_profile) — auto-detected.</param>
/// <param name="ProfileDir">base\profiles\&lt;ProfileName&gt; — holds loadorder.txt + modlist.txt + plugins.txt.</param>
/// <param name="ModsDir">base\mods — each enabled mod is a subfolder.</param>
/// <param name="DataDir">gamePath\Data — the base-game Data folder (vanilla masters).</param>
/// <param name="GamePath">The game root (ModOrganizer.ini gamePath); DataDir = this + \Data.</param>
/// <param name="OverwriteDir">base\overwrite — MO2's overwrite layer, the HIGHEST-priority file source (tool outputs:
/// Synthesis, xEdit "new file", Wrye Bash land plugins here, and MO2 lists them in the profile files). Derived but NOT
/// required to exist (a fresh instance may lack it; resolution just skips a missing folder).</param>
public sealed record Mo2InstancePaths(
    string InstanceDir, string ProfileName, string ProfileDir, string ModsDir, string DataDir, string GamePath, string OverwriteDir);

public static class Mo2Instance
{
    public const string IniFileName = "ModOrganizer.ini";

    /// <summary>The ModOrganizer.ini path for an instance folder (the file the freshness check stats for a profile switch).</summary>
    public static string IniPath(string instanceDir) => Path.Combine(instanceDir, IniFileName);

    /// <summary>Derive the load-order roots from an instance folder, or THROW (Q3, a clear actionable message naming what's
    /// missing) if it isn't a usable MO2 instance. Use <see cref="Validate"/> when you want the problems without an exception.</summary>
    public static Mo2InstancePaths Resolve(string instanceDir)
    {
        var problems = new List<string>();
        var paths = Derive(instanceDir, problems);
        if (paths is null)
            throw new InvalidOperationException(
                $"'{instanceDir}' is not a usable Mod Organizer 2 instance — {string.Join("; ", problems)}.");
        return paths;
    }

    /// <summary>Non-throwing derive for the cheap freshness re-check: true + paths on success, false on any problem (paths
    /// left null). Never throws — a transient read (MO2 mid-write) just yields false and the caller keeps its last good set.</summary>
    public static bool TryResolve(string instanceDir, out Mo2InstancePaths? paths)
    {
        paths = Derive(instanceDir, null);
        return paths is not null;
    }

    /// <summary>Validate a candidate instance folder for the setup tool: never throws; returns whether it's usable, the
    /// derived paths (null if not), and the specific problems (Q3) to show the user so they can fix the path.</summary>
    public static (bool ok, Mo2InstancePaths? paths, IReadOnlyList<string> problems) Validate(string instanceDir)
    {
        var problems = new List<string>();
        var paths = Derive(instanceDir, problems);
        return (paths is not null, paths, problems);
    }

    /// <summary>Just the active profile name (ModOrganizer.ini selected_profile), cheaply — the freshness check reads this
    /// after the ini's mtime changes to learn whether the user switched profiles. null if the file/key is absent.</summary>
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

    /// <summary>The single derive worker. Populates <paramref name="problems"/> (when provided) with every missing/invalid
    /// piece, and returns the paths ONLY when all required pieces resolve to real folders — otherwise null (the validity
    /// gate is independent of the problems list, so TryResolve with a null list still fails correctly).</summary>
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

        // base_directory SET but pointing at a missing folder is its own problem: we still fall back to the instance dir
        // (below), but silently doing so would point every downstream "mods/profiles missing" message at the WRONG root
        // (Q3 — never a silently-degraded mode). Absent base_directory (the common portable case) stays quiet: it's the
        // documented default, not a misconfiguration.
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

        // Overwrite is derived like mods/profiles (base-relative, the portable default measured on Aaron's real ini)
        // but never gates validity — a missing folder just means no overwrite-provided plugins.
        return new Mo2InstancePaths(instanceDir, profile!, profileDir, modsDir, dataDir, gamePath!, Path.Combine(basePath, "overwrite"));
    }

    /// <summary>First <c>key=</c> line's raw value (the key matched case-insensitively, ignoring section headers — the keys
    /// we read are unique across the file). Returns null if the key isn't present.</summary>
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

    /// <summary>Unwrap a QSettings value: strip an <c>@ByteArray(...)</c> wrapper if present (a plain QString value has
    /// none), treat <c>@Invalid()</c> as unset, and unescape doubled backslashes (<c>\\</c> → <c>\</c>). Trimmed.</summary>
    static string? CleanValue(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase)) return null;
        const string wrap = "@ByteArray(";
        if (v.StartsWith(wrap, StringComparison.Ordinal) && v.EndsWith(")", StringComparison.Ordinal))
            v = v[wrap.Length..^1];
        v = v.Replace(@"\\", @"\");
        return v.Length == 0 ? null : v;
    }
}
