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

/// <summary>How a tool path resolved for a status / diagnostic surface (<see cref="ToolBridge.Inspect"/>): the user SAVED it
/// (and it still validates), houseCARL AUTO-DETECTED a canonical home, or it's UNSET — no save and no probe hit, so a rider
/// would fire the trained missing-dependency prompt.</summary>
public enum ToolPathSource { Saved, AutoDetected, Unset }

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
    /// (measured 2026-06-02), so the guidance must be a return value, never an exception. When auto-detect had canonical
    /// candidates to check (the compiler with a <paramref name="gameDirHint"/>, the log folders), the prompt NAMES them —
    /// so a miss says WHERE houseCARL already looked (6.2: "better message"), telling a Wabbajack/Stock-Game user why the
    /// game-dir anchor missed and that they must supply the real CK path. The tool-key + resolving-call contract is kept
    /// intact regardless (the AI still gets the exact housecarl_set_tool_path call).</summary>
    public static string RenderMissingPrompt(ToolDependency dep, string? gameDirHint = null)
    {
        var info = Info(dep);
        var tried = Candidates(dep, gameDirHint).ToList();
        var lookedNote = tried.Count == 0 ? "" :
            $" houseCARL already looked for it automatically at {string.Join(" and ", tried.Select(c => $"'{c}'"))} " +
            "and didn't find it there.";
        return
            $"houseCARL needs {info.Display} for {info.Need}, but no path is set yet.{lookedNote} " +
            $"Ask the user for the {(info.IsDirectory ? "folder" : "full path to the .exe")}, then call " +
            $"housecarl_set_tool_path(tool='{info.Key}', path='<the path they give>'). " +
            $"If they don't have it: {info.WhereToGet}. " +
            "Do NOT guess the path, invent one, or skip the step — the operation cannot run without it, and a wrong path " +
            "is refused loud.";
    }

    /// <summary>The canonical candidate paths houseCARL auto-detects for a dependency, in priority order — the SINGLE
    /// source of truth shared by <see cref="Probe"/> (returns the first that EXISTS) and <see cref="RenderMissingPrompt"/>
    /// (lists them, so a miss can say WHERE houseCARL looked). The Papyrus compiler is anchored on the active instance's
    /// game dir (<paramref name="gameDirHint"/> = the load order's gamePath; the CK installs its compiler at
    /// &lt;game&gt;\Papyrus Compiler\PapyrusCompiler.exe). That hits a NORMAL Steam+CK install (the game dir IS the Steam
    /// game), and MISSES a Wabbajack "Stock Game" copy (the CK lives in the separate real Steam install, on any drive) —
    /// a BEST-EFFORT anchor, so a miss falls through to the forcing prompt, never a wrong guess. A future enhancement is
    /// Steam-library discovery (registry + libraryfolders.vdf) for the Stock-Game case. The log folders live under the
    /// user's Documents; BSArch is user-downloaded with no canonical home — both yield zero candidates (always prompt).</summary>
    static IEnumerable<string> Candidates(ToolDependency dep, string? gameDirHint)
    {
        switch (dep)
        {
            case ToolDependency.PapyrusCompiler:
                if (!string.IsNullOrWhiteSpace(gameDirHint))
                    yield return Path.Combine(gameDirHint!, "Papyrus Compiler", "PapyrusCompiler.exe");
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

    /// <summary>Auto-detect a canonical home for a dependency, so the user is only asked when houseCARL genuinely can't find
    /// it. Returns the first EXISTING <see cref="Candidates"/> path, or null (→ the forcing prompt). A directory dep needs
    /// an existing folder; an exe dep (the compiler) needs an existing file — its candidate is built as the literal
    /// PapyrusCompiler.exe, so File.Exists is sufficient (the name carries the right stem by construction, and
    /// <see cref="ToolPathResolver.Resolve"/> re-Validates before persisting it anyway).</summary>
    public static string? Probe(ToolDependency dep, string? gameDirHint = null)
    {
        bool isDir = Info(dep).IsDirectory;
        foreach (var c in Candidates(dep, gameDirHint))
            if (isDir ? Directory.Exists(c) : File.Exists(c)) return c;
        return null;
    }

    /// <summary>For a status / "what's configured" surface: where a dependency RESOLVES right now, WITHOUT the persist
    /// side-effect of the runtime resolver (<see cref="ToolPathResolver.Resolve"/>, which saves an auto-detected home so it
    /// probes once). Given the user's <paramref name="savedPath"/> (or null), returns the path + HOW it resolved: a saved
    /// path that still VALIDATES wins (Saved); else an auto-detect <see cref="Probe"/> of the canonical home (AutoDetected);
    /// else none (Unset — a rider would fire the forcing prompt). PURE (no file write), so a ReadOnly status read never
    /// mutates config, and it mirrors what a rider's resolve would find — so "where the logs are" in status is the truth a
    /// Read would hit. A saved path that no longer validates falls through (tool moved / folder deleted), same as the
    /// runtime resolver. The optional <paramref name="gameDirHint"/> feeds the compiler's game-dir-anchored auto-detect
    /// (the log deps that use this surface ignore it).</summary>
    public static (string? path, ToolPathSource source) Inspect(ToolDependency dep, string? savedPath, string? gameDirHint = null)
    {
        if (savedPath is not null && Validate(dep, savedPath).ok) return (savedPath, ToolPathSource.Saved);
        var found = Probe(dep, gameDirHint);
        return found is not null ? (found, ToolPathSource.AutoDetected) : (null, ToolPathSource.Unset);
    }

    static string MyGames => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
}
