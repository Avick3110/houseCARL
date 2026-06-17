using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>
/// houseCARL desktop setup - a no-CLI, no-GUI double-click installer.
///
/// houseCARL can be hosted by TWO agents; this utility installs for either or both, behind a
/// pick-a-number prompt (or a --claude / --codex / --both flag for an unattended run):
///
///   [1] Claude Code - copies the bundled plugin into ~/.claude/skills/housecarl/ (the desktop app
///                     auto-loads its skills) and registers the MCP server in ~/.claude.json (the
///                     desktop spawns it per session). UNCHANGED from the proven desktop install.
///   [2] Codex       - installs the server under %LOCALAPPDATA%\houseCARL\server\, copies the helper
///                     skills + the houseCARL umbrella skill FLAT into ~/.agents/skills/ (the location
///                     a fresh Codex install was confirmed to scan), and registers the server as
///                     [mcp_servers.housecarl] in ~/.codex/config.toml.
///   [3] Both        - both of the above.
///
/// The MO2 folder is intentionally NOT set here; houseCARL asks for it in chat on first use and stores
/// it in user.json beside whichever server copy is running.
///
/// Codex layout note: Codex scans ~/.agents/skills/ for skill FOLDERS, so the skills go there flat (not
/// nested inside a plugin folder), and the server - which is not a skill - lives in its own neutral dir.
/// For a Both install each host runs its own server copy (so MO2 is set once per host); unifying to a
/// single shared server is a deferred clean-up that would re-touch the proven Claude path.
/// </summary>
public static class Program
{
    private const string PluginFolderName = "housecarl"; // plugin dir shipped beside this exe
    private const string McpServerName    = "housecarl"; // server key under mcpServers / [mcp_servers.*]

    // Major version of the runtimes the bundled SERVER needs (keep in sync with housecarl-mcp's
    // TargetFramework). The default roll-forward policy stays within a major, so "10.x installed"
    // does not satisfy a net9.0 framework-dependent server.
    private const string ServerRuntimeMajor = "9";

    public enum Target { Claude, Codex, Both }

    /// <summary>What a non-interactive <see cref="TryInstall"/> did.</summary>
    public enum InstallOutcome
    {
        /// <summary>Skills + server copied and the MCP server registered for the chosen host(s).</summary>
        Installed,
        /// <summary>A houseCARL server file at a destination is in use (a live Claude/Codex session is
        /// running it), so it could not be overwritten. Nothing usable was changed when the refusal was at
        /// pre-flight (<see cref="InstallResult.RefusedBeforeAnyCopy"/>).</summary>
        ServerInUse,
    }

    /// <summary>Result of a non-interactive install attempt — the probeable seam under the interactive prompt.</summary>
    /// <param name="Outcome">What happened.</param>
    /// <param name="Message">Caller-facing detail for a non-<see cref="InstallOutcome.Installed"/> outcome (else null).</param>
    public sealed record InstallResult(InstallOutcome Outcome, string? Message)
    {
        /// <summary>True only when a <see cref="InstallOutcome.ServerInUse"/> refusal happened at PRE-FLIGHT,
        /// before any file was copied (so nothing was changed). False for the mid-copy defense-in-depth catch.</summary>
        public bool RefusedBeforeAnyCopy { get; init; }
    }

    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("houseCARL setup - installs houseCARL into Claude Code and/or Codex.");
            Console.WriteLine();
            Console.WriteLine("  Just run it (double-click) and pick which host(s) to install for.");
            Console.WriteLine("  Or pass a flag to skip the prompt:");
            Console.WriteLine("    --claude   install for Claude Code only");
            Console.WriteLine("    --codex    install for Codex only");
            Console.WriteLine("    --both     install for both");
            Console.WriteLine("    --skip-runtime-check   skip the .NET runtime preflight (custom DOTNET_ROOT etc.)");
            return 0;
        }

        try
        {
            Console.WriteLine("houseCARL setup");
            Console.WriteLine("===============");
            Console.WriteLine();

            // Locate the plugin shipped beside this program.
            string pkgDir      = AppContext.BaseDirectory;
            string pluginSrc   = Path.Combine(pkgDir, PluginFolderName);
            string srcManifest = Path.Combine(pluginSrc, ".claude-plugin", "plugin.json");
            string srcExe      = Path.Combine(pluginSrc, "server", "housecarl-mcp.exe");
            if (!File.Exists(srcManifest) || !File.Exists(srcExe))
            {
                Console.Error.WriteLine("ERROR: couldn't find the houseCARL plugin next to this program.");
                Console.Error.WriteLine("  Looked in: " + pluginSrc);
                Console.Error.WriteLine("  Keep this program in the same folder as the unzipped 'housecarl' folder, then run it again.");
                return Finish(1);
            }

            // ---- server runtime preflight ------------------------------------
            // The bundled server is framework-dependent net9.0 + ASP.NET Core: it needs BOTH the
            // base .NET Runtime (Microsoft.NETCore.App) and the ASP.NET Core Runtime
            // (Microsoft.AspNetCore.App). On Windows those are TWO separate installers, and the
            // ASP.NET Core one does NOT include the base runtime -- a real-world install trap.
            // This exe ships self-contained precisely so it still runs on a machine with neither
            // and can say exactly what's missing, instead of the install "succeeding" into a
            // server that never starts.
            if (!args.Contains("--skip-runtime-check"))
            {
                List<string> missing = MissingServerRuntimes();
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine("ERROR: the houseCARL server needs .NET runtime(s) that are not installed:");
                    if (missing.Contains("Microsoft.NETCore.App"))
                        Console.Error.WriteLine("    - .NET Runtime " + ServerRuntimeMajor + ".x           (Microsoft.NETCore.App)");
                    if (missing.Contains("Microsoft.AspNetCore.App"))
                        Console.Error.WriteLine("    - ASP.NET Core Runtime " + ServerRuntimeMajor + ".x   (Microsoft.AspNetCore.App)");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Both come from the same page:");
                    Console.Error.WriteLine("    https://dotnet.microsoft.com/download/dotnet/" + ServerRuntimeMajor + ".0");
                    Console.Error.WriteLine("  NOTE: they are two separate installers, and the ASP.NET Core Runtime");
                    Console.Error.WriteLine("  installer does NOT include the base .NET Runtime -- you need both.");
                    Console.Error.WriteLine("  Or via winget:");
                    if (missing.Contains("Microsoft.NETCore.App"))
                        Console.Error.WriteLine("    winget install Microsoft.DotNet.Runtime." + ServerRuntimeMajor);
                    if (missing.Contains("Microsoft.AspNetCore.App"))
                        Console.Error.WriteLine("    winget install Microsoft.DotNet.AspNetCore." + ServerRuntimeMajor);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Install the missing runtime(s), then run this setup again. (If you're sure");
                    Console.Error.WriteLine("  your setup is fine -- e.g. a custom dotnet location -- re-run this setup");
                    Console.Error.WriteLine("  with --skip-runtime-check.)");
                    return Finish(1);
                }
                Console.WriteLine("[check] .NET Runtime " + ServerRuntimeMajor + " + ASP.NET Core Runtime " + ServerRuntimeMajor + ": found.");
                Console.WriteLine();
            }

            // HOUSECARL_SETUP_HOME overrides the home dir (testing / unusual setups).
            string? homeOverride = Environment.GetEnvironmentVariable("HOUSECARL_SETUP_HOME");
            string home = string.IsNullOrWhiteSpace(homeOverride)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeOverride;

            Target? target = ResolveTarget(args);
            if (target is null)
            {
                Console.WriteLine("Cancelled - nothing was installed.");
                return Finish(0);
            }

            Console.WriteLine();
            InstallResult result = TryInstall(target.Value, pluginSrc, home, homeOverride);
            if (result.Outcome == InstallOutcome.ServerInUse)
            {
                Console.Error.WriteLine("ERROR: houseCARL is already installed and a server file is in use, so it");
                Console.Error.WriteLine("       can't be updated right now.");
                if (result.Message is not null)
                    Console.Error.WriteLine("  " + result.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Fully quit Claude Code AND Codex -- every desktop window, every terminal");
                Console.Error.WriteLine("  session, and any background session -- then run this setup again.");
                Console.Error.WriteLine(result.RefusedBeforeAnyCopy
                    ? "  Nothing was changed."
                    : "  The update was stopped partway; re-running after you quit will finish it.");
                return Finish(1);
            }

            PrintNext(target.Value);
            return Finish(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: houseCARL setup did not complete.");
            Console.Error.WriteLine("  " + ex.Message);
            return Finish(1);
        }
    }

    // ---- non-interactive install (the probeable seam under the prompt) -----

    /// <summary>
    /// Copy the plugin + register the MCP server for the chosen host(s), non-interactively. This is the seam
    /// <see cref="Main"/> drives after the prompt, and the one the CI guard drives directly.
    ///
    /// Re-running setup over a LIVE install would have <see cref="CopyDirectory"/> overwrite the running
    /// <c>housecarl-mcp.exe</c> (File.Copy overwrite:true), throw mid-copy, and leave a half-updated tree
    /// behind a generic "did not complete". So we PRE-FLIGHT the lock at EVERY destination this target
    /// touches, before copying anything, and refuse with actionable guidance (mirrors the runtime preflight
    /// below). A clean first install (no destination exe yet) is never blocked. As defense in depth, a
    /// sharing violation that slips past the pre-flight (a held sibling DLL, or a session started between the
    /// check and the copy) is caught and surfaced with the same guidance instead of the generic failure.
    /// </summary>
    public static InstallResult TryInstall(Target target, string pluginSrc, string home, string? homeOverride)
    {
        List<string> destExes = new();
        if (target is Target.Claude or Target.Both) destExes.Add(ClaudeDestExe(home));
        if (target is Target.Codex  or Target.Both) destExes.Add(CodexDestExe(home, homeOverride));

        foreach (string destExe in destExes)
            if (ServerExeInUse(destExe))
                return new InstallResult(InstallOutcome.ServerInUse, "Looks like the server is running here: " + destExe)
                    { RefusedBeforeAnyCopy = true };

        try
        {
            if (target is Target.Claude or Target.Both) InstallForClaude(pluginSrc, home);
            if (target is Target.Codex  or Target.Both) InstallForCodex(pluginSrc, home, homeOverride);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return new InstallResult(InstallOutcome.ServerInUse,
                "A houseCARL server file was in use during the update.");
        }

        return new InstallResult(InstallOutcome.Installed, null);
    }

    /// <summary>The Claude install's server exe path. Single source of truth so pre-flight == installer.</summary>
    private static string ClaudeDestExe(string home)
        => Path.Combine(home, ".claude", "skills", PluginFolderName, "server", "housecarl-mcp.exe");

    /// <summary>The Codex install's server dir. Under a test home (HOUSECARL_SETUP_HOME) it hangs off that
    /// home so tests never touch the real LOCALAPPDATA; otherwise it lives under %LOCALAPPDATA%.</summary>
    private static string CodexServerDir(string home, string? homeOverride)
    {
        string dataBase = string.IsNullOrWhiteSpace(homeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : home;
        return Path.Combine(dataBase, "houseCARL", "server");
    }

    /// <summary>The Codex install's server exe path. Single source of truth so pre-flight == installer.</summary>
    private static string CodexDestExe(string home, string? homeOverride)
        => Path.Combine(CodexServerDir(home, homeOverride), "housecarl-mcp.exe");

    /// <summary>
    /// True if <paramref name="destExe"/> already exists AND can't be opened for writing — i.e. a live
    /// Claude/Codex session is running it (a running image denies write sharing). A missing file (a clean
    /// first install) returns false, so it's never falsely blocked. The handle is opened then immediately
    /// closed and never written, so a held server's exe stays byte-intact.
    /// </summary>
    private static bool ServerExeInUse(string destExe)
    {
        if (!File.Exists(destExe)) return false;
        try
        {
            using FileStream _ = new(destExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // got exclusive write access -> nothing holds it -> safe to overwrite
        }
        catch (IOException)                 { return true; } // in use by a running session
        catch (UnauthorizedAccessException) { return true; } // locked / not writable -> can't overwrite either
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33): the file-in-use cases. We re-stamp ONLY these
    // as "server in use" so an unrelated IOException (disk full, path too long) still surfaces as the honest
    // generic failure rather than a misleading "quit Claude" message (Q3 — no silently wrong diagnosis).
    private const int HrSharingViolation = unchecked((int)0x80070020);
    private const int HrLockViolation    = unchecked((int)0x80070021);

    private static bool IsSharingViolation(IOException ex)
        => ex.HResult == HrSharingViolation || ex.HResult == HrLockViolation;

    // ---- server runtime preflight ------------------------------------------

    /// <summary>
    /// Which of the server's required shared frameworks are missing at the required major version.
    /// Asks `dotnet --list-runtimes` first (covers custom install locations on PATH); falls back to
    /// scanning the default machine-wide install dir, which also covers a console whose PATH predates
    /// a just-finished runtime install.
    /// </summary>
    private static List<string> MissingServerRuntimes()
    {
        string[] required = { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" };
        HashSet<string> found = new(StringComparer.Ordinal);

        try
        {
            ProcessStartInfo psi = new("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using Process? p = Process.Start(psi);
            if (p is not null)
            {
                // Drain stderr asynchronously so a broken dotnet host writing errors can't fill the
                // pipe and deadlock the stdout read, and bound the whole interaction so a wedged host
                // can't hang the preflight - on timeout we kill it and fall through to the folder scan.
                p.ErrorDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
                }
                if (stdoutTask.Wait(2000))
                {
                    foreach (string line in stdoutTask.Result.Split('\n'))
                    {
                        string t = line.Trim();
                        foreach (string fx in required)
                            if (t.StartsWith(fx + " " + ServerRuntimeMajor + ".", StringComparison.Ordinal))
                                found.Add(fx);
                    }
                }
            }
        }
        catch { /* dotnet not on PATH -- the folder scan below still gets a say */ }

        string sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
        foreach (string fx in required)
        {
            if (found.Contains(fx)) continue;
            string fxDir = Path.Combine(sharedDir, fx);
            // A version folder must actually contain assemblies - an empty 9.x dir left behind by an
            // aborted install/uninstall must not count as "installed".
            if (Directory.Exists(fxDir) &&
                Directory.GetDirectories(fxDir, ServerRuntimeMajor + ".*")
                    .Any(d => Directory.EnumerateFiles(d, "*.dll").Any()))
                found.Add(fx);
        }

        return required.Where(fx => !found.Contains(fx)).ToList();
    }

    // ---- target selection (flag or interactive prompt) --------------------

    private static Target? ResolveTarget(string[] args)
    {
        if (args.Contains("--both"))   return Target.Both;
        if (args.Contains("--codex"))  return Target.Codex;
        if (args.Contains("--claude")) return Target.Claude;

        Console.WriteLine("Install houseCARL for which agent?");
        Console.WriteLine("  [1] Claude Code");
        Console.WriteLine("  [2] Codex");
        Console.WriteLine("  [3] Both");
        Console.WriteLine();
        while (true)
        {
            Console.Write("Enter 1, 2, or 3 (or q to quit): ");
            string? s = Console.ReadLine();
            if (s is null) return null;        // no interactive input (redirected) - treat as cancel
            switch (s.Trim().ToLowerInvariant())
            {
                case "1": return Target.Claude;
                case "2": return Target.Codex;
                case "3": return Target.Both;
                case "q": case "quit": return null;
                default: Console.WriteLine("  Please type 1, 2, 3, or q."); break;
            }
        }
    }

    // ---- Claude Code install (unchanged from the proven desktop install) ---

    private static void InstallForClaude(string pluginSrc, string home)
    {
        string skillsDest = Path.Combine(home, ".claude", "skills", PluginFolderName);
        string destExe    = ClaudeDestExe(home);
        string claudeJson = Path.Combine(home, ".claude.json");

        Console.WriteLine("[Claude Code] installing skills + server");
        Console.WriteLine("      -> " + skillsDest);
        CopyDirectory(pluginSrc, skillsDest);

        Console.WriteLine("[Claude Code] registering the MCP server");
        Console.WriteLine("      -> " + claudeJson);
        RegisterClaudeMcpServer(claudeJson, McpServerName, destExe);
        Console.WriteLine();
    }

    // ---- Codex install -----------------------------------------------------

    private static void InstallForCodex(string pluginSrc, string home, string? homeOverride)
    {
        // Server + corpus go to a neutral per-user dir, NOT the skills dir: Codex scans ~/.agents/skills
        // for skill FOLDERS, and the server is not a skill. Under a test home (HOUSECARL_SETUP_HOME) the
        // data dir hangs off that home so tests never touch the real LOCALAPPDATA.
        string serverDest = CodexServerDir(home, homeOverride);
        string destExe    = CodexDestExe(home, homeOverride);

        // Skills go FLAT under ~/.agents/skills/ (the cross-agent, user-scope skills dir).
        string skillsRoot = Path.Combine(home, ".agents", "skills");

        // ~/.codex/config.toml, honoring CODEX_HOME if the user set it.
        string? codexHomeEnv = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexHome = string.IsNullOrWhiteSpace(codexHomeEnv)
            ? Path.Combine(home, ".codex")
            : codexHomeEnv;
        string configToml = Path.Combine(codexHome, "config.toml");

        Console.WriteLine("[Codex] installing the server");
        Console.WriteLine("      -> " + serverDest);
        CopyDirectory(Path.Combine(pluginSrc, "server"), serverDest);

        Console.WriteLine("[Codex] installing skills");
        Console.WriteLine("      -> " + skillsRoot);
        string skillsSrc = Path.Combine(pluginSrc, "skills");
        if (Directory.Exists(skillsSrc))
            foreach (string skillDir in Directory.GetDirectories(skillsSrc))
                CopyDirectory(skillDir, Path.Combine(skillsRoot, Path.GetFileName(skillDir)));

        // Codex-only umbrella skill: the $housecarl entry point (a top-level SKILL.md routing to the
        // helpers + an agents/openai.yaml declaring the MCP-server dependency). It ships beside the plugin
        // in the package (codex/housecarl), NOT inside it, so the Claude install never sees it. Placed in
        // ~/.agents/skills/ alongside the helpers - the location a fresh Codex install was confirmed to
        // scan (the helpers there are discovered and working).
        string umbrellaSrc = Path.Combine(Path.GetDirectoryName(pluginSrc)!, "codex", "housecarl");
        if (Directory.Exists(umbrellaSrc))
        {
            string umbrellaDest = Path.Combine(skillsRoot, PluginFolderName);
            Console.WriteLine("[Codex] installing the houseCARL umbrella skill");
            Console.WriteLine("      -> " + umbrellaDest);
            CopyDirectory(umbrellaSrc, umbrellaDest);
        }

        Console.WriteLine("[Codex] registering the MCP server");
        Console.WriteLine("      -> " + configToml);
        RegisterCodexMcpServer(configToml, McpServerName, destExe);
        Console.WriteLine();
    }

    // ---- NEXT steps --------------------------------------------------------

    private static void PrintNext(Target target)
    {
        Console.WriteLine("houseCARL is installed.");
        Console.WriteLine();
        Console.WriteLine("  NEXT:");
        if (target is Target.Claude or Target.Both)
            Console.WriteLine("   - Claude Code: fully quit and reopen the Claude desktop app.");
        if (target is Target.Codex or Target.Both)
            Console.WriteLine("   - Codex: fully restart Codex (close every session), then check /mcp and /skills.");
        Console.WriteLine("   - On first use of a houseCARL tool it will ask you to point it at your");
        Console.WriteLine("     Mod Organizer 2 folder (the one containing ModOrganizer.ini).");
        if (target is Target.Both)
            Console.WriteLine("   - (Each host runs its own server copy, so you'll set the MO2 folder once per host.)");
    }

    private static int Finish(int exitCode)
    {
        Console.WriteLine();
        Console.Write("Press any key to close...");
        try { Console.ReadKey(intercept: true); } catch { /* no interactive console (redirected) */ }
        Console.WriteLine();
        return exitCode;
    }

    // ---- file copy --------------------------------------------------------

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
    }

    // ---- ~/.claude.json registration (JSON splice) ------------------------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Insert/replace mcpServers.<paramref name="name"/> WITHOUT reparsing the whole file. ~/.claude.json
    /// can hold keys that differ only by case (Windows path history), which case-insensitive parsers
    /// reject; we parse ONLY the small, duplicate-free mcpServers object and splice it back, leaving the
    /// rest of the file byte-for-byte intact. Backs the file up first.
    /// </summary>
    private static void RegisterClaudeMcpServer(string claudeJsonPath, string name, string command)
    {
        JsonObject entry = new()
        {
            ["type"]    = "stdio",
            ["command"] = command,
            ["args"]    = new JsonArray(),
        };

        if (!File.Exists(claudeJsonPath))
        {
            JsonObject newRoot = new() { ["mcpServers"] = new JsonObject { [name] = entry } };
            File.WriteAllText(claudeJsonPath, newRoot.ToJsonString(Indented));
            return;
        }

        string text = File.ReadAllText(claudeJsonPath);
        File.Copy(claudeJsonPath, claudeJsonPath + ".houseCARL.bak", overwrite: true);

        (int start, int end)? bounds = FindRootMemberObject(text, "mcpServers");
        string updated;
        if (bounds is { } b)
        {
            string objText = text.Substring(b.start, b.end - b.start + 1);
            JsonObject servers = JsonNode.Parse(objText) as JsonObject
                ?? throw new InvalidDataException("mcpServers is not a JSON object.");
            servers[name] = entry; // insert or replace (idempotent on re-run)
            string newObj = Reindent(servers.ToJsonString(Indented), LeadingIndentOfLineAt(text, b.start));
            updated = string.Concat(text.AsSpan(0, b.start), newObj, text.AsSpan(b.end + 1));
        }
        else
        {
            int rootBrace = text.IndexOf('{');
            if (rootBrace < 0) throw new InvalidDataException("~/.claude.json is not a JSON object.");
            JsonObject servers = new() { [name] = entry };
            string block = "\n  \"mcpServers\": " + Reindent(servers.ToJsonString(Indented), "  ") + ",";
            updated = string.Concat(text.AsSpan(0, rootBrace + 1), block, text.AsSpan(rootBrace + 1));
        }

        File.WriteAllText(claudeJsonPath, updated);
    }

    /// <summary>Finds the `{ ... }` value of a DEPTH-1 (root-level) member named <paramref name="key"/>. String-aware.</summary>
    private static (int start, int end)? FindRootMemberObject(string text, string key)
    {
        string token = "\"" + key + "\"";
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                if (depth == 1 && i + token.Length <= text.Length
                    && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                {
                    int j = i + token.Length;
                    while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                    if (j < text.Length && text[j] == ':')
                    {
                        j++;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && text[j] == '{')
                            return MatchBraces(text, j);
                        throw new InvalidDataException("mcpServers exists but its value is not an object.");
                    }
                }
                inString = true;
            }
            else if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return null;
    }

    private static (int start, int end)? MatchBraces(string text, int openIndex)
    {
        int depth = 0;
        bool inString = false, escape = false;
        for (int i = openIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return (openIndex, i); }
        }
        return null;
    }

    private static string LeadingIndentOfLineAt(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', index) + 1;
        int j = lineStart;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
        return text.Substring(lineStart, j - lineStart);
    }

    private static string Reindent(string json, string indent)
    {
        if (indent.Length == 0) return json;
        string[] lines = json.Split('\n');
        for (int i = 1; i < lines.Length; i++) lines[i] = indent + lines[i];
        return string.Join('\n', lines);
    }

    // ---- ~/.codex/config.toml registration (TOML splice) ------------------

    /// <summary>
    /// Insert/replace [mcp_servers.<paramref name="name"/>] in a TOML config.toml, leaving everything
    /// else intact. The command path is written as a LITERAL TOML string (single quotes) so Windows
    /// backslashes pass through verbatim - a basic "double-quoted" string would treat them as escapes.
    /// Backs the file up first; idempotent on re-run.
    /// </summary>
    private static void RegisterCodexMcpServer(string configTomlPath, string name, string command)
    {
        string? dir = Path.GetDirectoryName(configTomlPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!File.Exists(configTomlPath))
        {
            string fresh = "# houseCARL MCP server (added by houseCARL-Setup)\n"
                         + "[mcp_servers." + name + "]\n"
                         + "command = '" + command + "'\n";
            File.WriteAllText(configTomlPath, fresh);
            return;
        }

        string text = File.ReadAllText(configTomlPath);
        File.Copy(configTomlPath, configTomlPath + ".houseCARL.bak", overwrite: true);
        File.WriteAllText(configTomlPath, SpliceTomlTable(text, name, command));
    }

    /// <summary>
    /// Replace the [mcp_servers.&lt;name&gt;] table (and any of its subtables) with a fresh one, or append
    /// it if absent. Line-based so it never reformats the rest of the file; preserves the file's newline
    /// style. A TOML table body runs from its header to the next table header (or EOF).
    /// </summary>
    private static string SpliceTomlTable(string text, string name, string command)
    {
        string nl   = text.Contains("\r\n") ? "\r\n" : "\n";
        string head = "[mcp_servers." + name + "]";
        string sub  = "[mcp_servers." + name + ".";
        string[] body = { head, "command = '" + command + "'" };

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        List<string> outLines = new();
        bool replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!replaced && lines[i].Trim() == head)
            {
                // skip our existing table body + any [mcp_servers.<name>.*] subtables
                int j = i + 1;
                while (j < lines.Length)
                {
                    string t = lines[j].Trim();
                    if (t.StartsWith("[") && t != head && !t.StartsWith(sub)) break;
                    j++;
                }
                outLines.AddRange(body);
                i = j - 1;          // resume after the skipped block
                replaced = true;
                continue;
            }
            outLines.Add(lines[i]);
        }

        if (!replaced)
        {
            string trimmed = string.Join(nl, outLines).TrimEnd('\r', '\n');
            return trimmed.Length == 0
                ? "# houseCARL MCP server (added by houseCARL-Setup)" + nl + string.Join(nl, body) + nl
                : trimmed + nl + nl + string.Join(nl, body) + nl;
        }

        string result = string.Join(nl, outLines);
        return result.EndsWith(nl) ? result : result + nl;
    }
}
