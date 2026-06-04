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
internal static class Program
{
    private const string PluginFolderName = "housecarl"; // plugin dir shipped beside this exe
    private const string McpServerName    = "housecarl"; // server key under mcpServers / [mcp_servers.*]

    private enum Target { Claude, Codex, Both }

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
            if (target is Target.Claude or Target.Both) InstallForClaude(pluginSrc, home);
            if (target is Target.Codex  or Target.Both) InstallForCodex(pluginSrc, home, homeOverride);

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
        string destExe    = Path.Combine(skillsDest, "server", "housecarl-mcp.exe");
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
        string dataBase = string.IsNullOrWhiteSpace(homeOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : home;
        string serverDest = Path.Combine(dataBase, "houseCARL", "server");
        string destExe    = Path.Combine(serverDest, "housecarl-mcp.exe");

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
