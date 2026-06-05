namespace HousecarlCore;

/// <summary>The external tools houseCARL can drive once the user supplies a path — the bridge's dependency set. Each is an
/// .exe houseCARL shells out to, or a log DIRECTORY it reads. Extensible: a new rider adds an arm here + a catalog row in
/// <see cref="ToolBridge"/>. Verified 2026-06-05 (source, not memory): Mutagen cannot compile/decompile Papyrus and has no
/// archive surface, so these are EXTERNAL exes, not engine work.</summary>
public enum ToolDependency
{
    /// <summary>The Creation Kit's PapyrusCompiler.exe — compiles .psc → .pex (housecarl_compile_script). NOT Mutagen.</summary>
    PapyrusCompiler,
    /// <summary>BSArch.exe — list / extract / repack .bsa archives (the BSA riders). No canonical home; always prompts.</summary>
    Bsarch,
    /// <summary>The Papyrus script-log DIRECTORY (…\My Games\Skyrim Special Edition\Logs\Script) — read for Papyrus triage.</summary>
    PapyrusLogs,
    /// <summary>The SKSE crash-log DIRECTORY (Crash Logger SSE / .NET Script Framework) — read for crash diagnosis.</summary>
    CrashLogs,
}

/// <summary>Per-dependency metadata: the wire key the user/tool names it by, a human display, whether the path is a
/// DIRECTORY (a log root) or an .exe FILE, the expected exe stem (a sanity-check the path is the right tool), the one-line
/// "what it's for", and where to get it (shown in the missing-dependency prompt).</summary>
public sealed record ToolInfo(
    ToolDependency Dep, string Key, string Display, bool IsDirectory, string? ExeStem, string Need, string WhereToGet);

/// <summary>
/// The external-tool catalog + the bridge's pure logic: parse a wire name, VALIDATE a candidate path (Q3 — never silently
/// accept a wrong one), render the trained MISSING-DEPENDENCY prompt (the forcing function), and AUTO-DETECT canonical
/// homes. All pure (no DI, no file mutation beyond existence checks), so the build-time probe can exercise it directly;
/// the runtime wrapper (<see cref="ToolPathResolver"/>) adds saved-path lookup + persistence on top.
/// </summary>
public static class ToolBridge
{
    static readonly ToolInfo[] All =
    {
        new(ToolDependency.PapyrusCompiler, "papyrus_compiler", "the Papyrus compiler (PapyrusCompiler.exe)", false, "papyruscompiler",
            "compiling .psc scripts to .pex",
            "it ships with the Creation Kit (Bethesda's free modding tool, on Steam); it's typically at <Skyrim>\\Papyrus Compiler\\PapyrusCompiler.exe"),
        new(ToolDependency.Bsarch, "bsarch", "BSArch (BSArch.exe)", false, "bsarch",
            "listing, extracting, and repacking .bsa archives",
            "BSArch is a standalone tool on Nexus Mods (also bundled with 'Cathedral Assets Optimizer' / 'BSA Browser')"),
        new(ToolDependency.PapyrusLogs, "papyrus_logs", "the Papyrus script-log folder", true, null,
            "reading Papyrus script logs for triage",
            "they're under Documents\\My Games\\Skyrim Special Edition\\Logs\\Script (set bEnableLogging=1 in the ini if the folder is absent)"),
        new(ToolDependency.CrashLogs, "crash_logs", "the SKSE crash-log folder", true, null,
            "reading crash logs for diagnosis",
            "Crash Logger SSE (Nexus) writes them under Documents\\My Games\\Skyrim Special Edition\\SKSE\\Crashlogs"),
    };

    static readonly Dictionary<ToolDependency, ToolInfo> ByDep = All.ToDictionary(i => i.Dep);
    static readonly Dictionary<string, ToolDependency> ByKey =
        All.ToDictionary(i => i.Key, i => i.Dep, StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalog entry for a dependency.</summary>
    public static ToolInfo Info(ToolDependency dep) => ByDep[dep];

    /// <summary>The comma-joined wire keys, for an error listing the valid tools.</summary>
    public static string WireKeys => string.Join(", ", All.Select(i => i.Key));

    /// <summary>Parse a wire name (case-insensitive) to a dependency; false if it isn't one we know.</summary>
    public static bool TryParse(string? wire, out ToolDependency dep)
    {
        if (!string.IsNullOrWhiteSpace(wire) && ByKey.TryGetValue(wire.Trim(), out dep)) return true;
        dep = default; return false;
    }

    /// <summary>Validate a candidate path for a dependency (Q3 — never silently accept a wrong path): a directory tool
    /// needs an existing folder; an exe tool needs an existing .exe whose name carries the expected stem (so a path to the
    /// wrong program is caught). Returns (ok, error) — error names what's wrong, for the tool to surface to the user.</summary>
    public static (bool ok, string? error) Validate(ToolDependency dep, string path)
    {
        var info = Info(dep);
        if (string.IsNullOrWhiteSpace(path)) return (false, "no path given.");
        if (info.IsDirectory)
            return Directory.Exists(path) ? (true, null)
                 : (false, $"no such folder: '{path}'. Give the {info.Display} (a directory).");
        if (!File.Exists(path))
            return (false, $"no such file: '{path}'. Give the full path to {info.Display}.");
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, $"'{name}' is not an .exe — give the {info.Display}.");
        if (info.ExeStem is not null && name.IndexOf(info.ExeStem, StringComparison.OrdinalIgnoreCase) < 0)
            return (false, $"'{name}' doesn't look like {info.Display} (expected a filename containing '{info.ExeStem}'). " +
                           "Double-check you pointed at the right .exe.");
        return (true, null);
    }

    /// <summary>The trained missing-dependency prompt — RETURNED by a rider tool when its path is unset, so the AI reliably
    /// asks the user and is handed the exact resolving call (the forcing function). Mirrors the MO2 not-configured prompt
    /// idiom: a RETURNED string reaches the client, whereas a thrown one is genericized to "An error occurred invoking…"
    /// (measured 2026-06-02), so the guidance must be a return value, never an exception.</summary>
    public static string RenderMissingPrompt(ToolDependency dep)
    {
        var info = Info(dep);
        return
            $"houseCARL needs {info.Display} for {info.Need}, but no path is set yet. " +
            $"Ask the user for the {(info.IsDirectory ? "folder" : "full path to the .exe")}, then call " +
            $"housecarl_set_tool_path(tool='{info.Key}', path='<the path they give>'). " +
            $"If they don't have it: {info.WhereToGet}. " +
            "Do NOT guess the path, invent one, or skip the step — the operation cannot run without it, and a wrong path " +
            "is refused loud.";
    }

    /// <summary>Auto-detect a canonical home for a dependency, so the user is only asked when houseCARL genuinely can't find
    /// it. <paramref name="skyrimGamePath"/> is the resolved MO2 instance's game root, when known (for the compiler under
    /// &lt;game&gt;\Papyrus Compiler). Returns the first EXISTING canonical path, or null (→ the prompt). BSArch has no
    /// canonical home, so it's always null. (GOG/other game-dir variants are a later refinement — flagged in the plan.)</summary>
    public static string? Probe(ToolDependency dep, string? skyrimGamePath)
    {
        switch (dep)
        {
            case ToolDependency.PapyrusCompiler:
                if (!string.IsNullOrWhiteSpace(skyrimGamePath))
                {
                    var exe = Path.Combine(skyrimGamePath!, "Papyrus Compiler", "PapyrusCompiler.exe");
                    if (File.Exists(exe)) return exe;
                }
                return null;
            case ToolDependency.PapyrusLogs:
                return FirstExistingDir(Path.Combine(MyGames, "Logs", "Script"));
            case ToolDependency.CrashLogs:
                return FirstExistingDir(
                    Path.Combine(MyGames, "SKSE", "Crashlogs"),         // Crash Logger SSE
                    Path.Combine(MyGames, "NetScriptFramework", "Crash")); // .NET Script Framework
            default:
                return null;   // bsarch — user-downloaded, no canonical home
        }
    }

    static string MyGames => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");

    static string? FirstExistingDir(params string[] candidates)
    {
        foreach (var c in candidates) if (Directory.Exists(c)) return c;
        return null;
    }
}
