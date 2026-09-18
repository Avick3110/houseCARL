namespace HousecarlCore;

/// <summary>The external tools houseCARL drives once the user supplies a path; a new rider adds an arm here plus a catalog row in <see cref="ToolBridge"/>.</summary>
public enum ToolDependency
{
    /// <summary>The Creation Kit's PapyrusCompiler.exe, which compiles .psc to .pex.</summary>
    PapyrusCompiler,
    /// <summary>BSArch.exe — list, extract and repack .bsa archives; no canonical home, so it always prompts.</summary>
    Bsarch,
    PapyrusLogs,
    /// <summary>The SKSE crash-log directory (Crash Logger SSE or .NET Script Framework), read for crash diagnosis.</summary>
    CrashLogs,
}

/// <summary>How a tool path resolved for a status surface: saved and still validating, auto-detected, or unset.</summary>
public enum ToolPathSource { Saved, AutoDetected, Unset }

/// <summary>Per-dependency metadata: wire key, display name, directory-or-exe, expected exe stem, purpose, and source.</summary>
public sealed record ToolInfo(
    ToolDependency Dep, string Key, string Display, bool IsDirectory, string? ExeStem, string Need, string WhereToGet);

/// <summary>The external-tool catalog and the bridge's pure logic: parse a wire name, validate a candidate path, render
/// the missing-dependency prompt, auto-detect canonical homes; <see cref="ToolPathResolver"/> adds persistence.</summary>
public static class ToolBridge
{
    static readonly ToolInfo[] All =
    {
        new(ToolDependency.PapyrusCompiler, "papyrus_compiler", "the Papyrus compiler (PapyrusCompiler.exe)", false, "papyruscompiler",
            "compiling .psc scripts to .pex",
            "it ships with the Creation Kit (Bethesda's free modding tool, on Steam); it lives in your REAL Steam Skyrim SE install " +
            "at <Skyrim SE>\\Papyrus Compiler\\PapyrusCompiler.exe — NOT a Wabbajack/MO2 'Stock Game' copy (that's the data dir; the CK and the vanilla script sources are in the Steam install)"),
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

    public static ToolInfo Info(ToolDependency dep) => ByDep[dep];

    /// <summary>The comma-joined wire keys, for an error listing the valid tools.</summary>
    public static string WireKeys => string.Join(", ", All.Select(i => i.Key));

    /// <summary>Parse a wire name, case-insensitively, to a dependency.</summary>
    public static bool TryParse(string? wire, out ToolDependency dep)
    {
        if (!string.IsNullOrWhiteSpace(wire) && ByKey.TryGetValue(wire.Trim(), out dep)) return true;
        dep = default; return false;
    }

    /// <summary>Validate a candidate path: an existing folder for a directory tool, an existing .exe carrying the
    /// expected stem for an exe tool; returns (ok, error) with error naming what is wrong.</summary>
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

    /// <summary>The missing-dependency prompt, returned by a rider tool when its path is unset and never thrown; it
    /// names the candidates auto-detect already checked.</summary>
    public static string RenderMissingPrompt(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
    {
        var info = Info(dep);
        var tried = Candidates(dep, gameDirHints).ToList();
        var lookedNote = tried.Count == 0 ? "" :
            $" houseCARL already looked for it automatically at {string.Join(" and ", tried.Select(c => $"'{c}'"))} " +
            "and didn't find it there.";
        return
            $"houseCARL needs {info.Display} for {info.Need}, but no path is set yet.{lookedNote} " +
            $"Ask the user for the {(info.IsDirectory ? "folder" : "full path to the .exe")}, then call " +
            $"{ToolNames.SetToolPath}(tool='{info.Key}', path='<the path they give>'). " +
            $"If they don't have it: {info.WhereToGet}. " +
            "Do NOT guess the path, invent one, or skip the step — the operation cannot run without it, and a wrong path " +
            "is refused loud.";
    }

    /// <summary>The canonical candidate paths auto-detect tries, in priority order, shared by <see cref="Probe"/> and
    /// <see cref="RenderMissingPrompt"/>; the compiler is checked under each <paramref name="gameDirHints"/> dir in turn.</summary>
    static IEnumerable<string> Candidates(ToolDependency dep, IReadOnlyList<string>? gameDirHints)
    {
        switch (dep)
        {
            case ToolDependency.PapyrusCompiler:
                foreach (var g in gameDirHints ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(g))
                        yield return Path.Combine(g, "Papyrus Compiler", "PapyrusCompiler.exe");
                break;
            case ToolDependency.PapyrusLogs:
                yield return Path.Combine(MyGames, "Logs", "Script");
                break;
            case ToolDependency.CrashLogs:
                yield return Path.Combine(MyGames, "SKSE", "Crashlogs");          // Crash Logger SSE
                yield return Path.Combine(MyGames, "NetScriptFramework", "Crash"); // .NET Script Framework
                break;
            // Bsarch: user-downloaded, no canonical home — no candidates.
        }
    }

    /// <summary>Auto-detect a canonical home: the first existing <see cref="Candidates"/> path, or null.</summary>
    public static string? Probe(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
    {
        bool isDir = Info(dep).IsDirectory;
        foreach (var c in Candidates(dep, gameDirHints))
            if (isDir ? Directory.Exists(c) : File.Exists(c)) return c;
        return null;
    }

    /// <summary>Where a dependency resolves right now and how, without the persist side-effect of
    /// <see cref="ToolPathResolver.Resolve"/>: a saved path that still validates wins, else the probe, else unset.</summary>
    public static (string? path, ToolPathSource source) Inspect(ToolDependency dep, string? savedPath, IReadOnlyList<string>? gameDirHints = null)
    {
        if (savedPath is not null && Validate(dep, savedPath).ok) return (savedPath, ToolPathSource.Saved);
        var found = Probe(dep, gameDirHints);
        return found is not null ? (found, ToolPathSource.AutoDetected) : (null, ToolPathSource.Unset);
    }

    static string MyGames => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
}
