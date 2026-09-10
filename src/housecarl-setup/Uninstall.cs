using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>
/// Taking back exactly what the install wrote, host by host. It is its own file because it is its own job -
/// <see cref="Program"/> stays the install - and every path it deletes comes from <see cref="Program"/>'s path
/// helpers, so a removal cannot go somewhere the install never wrote.
///
/// What it removes, per host:
///   Claude Code - the tree at ClaudeSkillsDest (skills, server, and the saved MO2 instance in the
///                 houseCARL.user.json beside the server exe), and the mcpServers.housecarl entry in
///                 ~/.claude.json.
///   Codex       - the server dir (the saved MO2 instance goes with it), the skill folders the Codex record
///                 names and no other, the record itself, and the [mcp_servers.housecarl] table in
///                 ~/.codex/config.toml.
///
/// Both config edits are removal splices beside the insert splices on <see cref="Program"/>: the file is copied
/// to a ".houseCARL.bak" first, the entry is taken out, and every other byte in the file is left as it was.
/// ~/.agents/skills is shared with every other agent's skills, so a folder the record does not list is never
/// touched - an install too old to have left a record removes nothing there and says so.
/// </summary>
public static class Uninstall
{
    /// <summary>What a non-interactive <see cref="TryUninstall"/> did.</summary>
    public enum Outcome
    {
        /// <summary>The files and the host-config entries this target owns are gone.</summary>
        Removed,
        /// <summary>A houseCARL server file is in use (a live Claude/Codex session is running it), so it could
        /// not be deleted. Nothing was deleted when the refusal was at pre-flight
        /// (<see cref="Result.RefusedBeforeAnyDelete"/>).</summary>
        ServerInUse,
    }

    /// <summary>Result of a non-interactive uninstall - the probeable seam under the interactive prompt.</summary>
    /// <param name="What">What happened.</param>
    /// <param name="Message">Caller-facing detail for a non-<see cref="Outcome.Removed"/> outcome (else null).</param>
    public sealed record Result(Outcome What, string? Message)
    {
        /// <summary>True only when a <see cref="Outcome.ServerInUse"/> refusal happened at PRE-FLIGHT, before
        /// anything was deleted.</summary>
        public bool RefusedBeforeAnyDelete { get; init; }
    }

    /// <summary>
    /// Remove houseCARL for the chosen host(s), non-interactively. The seam <see cref="Program"/> drives after
    /// the plan's confirm, and the one the CI guard drives directly.
    ///
    /// A running session holds its server exe open, so the lock is PRE-FLIGHTED at every destination this target
    /// touches before anything is deleted - the same check the install makes, for the same reason: a delete that
    /// throws partway leaves a half-removed tree. A folder that will not delete for any other reason (a
    /// read-only file inside it, a handle held on one) is collected and said at the end rather than thrown, so
    /// the host-config entry still comes out.
    /// </summary>
    public static Result TryUninstall(Program.Target target, string home, string? homeOverride)
    {
        List<string> destExes = new();
        if (target is Program.Target.Claude or Program.Target.Both) destExes.Add(Program.ClaudeDestExe(home));
        if (target is Program.Target.Codex  or Program.Target.Both) destExes.Add(Program.CodexDestExe(home, homeOverride));

        foreach (string destExe in destExes)
            if (Program.ServerExeInUse(destExe))
                return new Result(Outcome.ServerInUse, "In use, or locked / read-only:  " + destExe)
                    { RefusedBeforeAnyDelete = true };

        List<string> keptBack = new();

        try
        {
            if (target is Program.Target.Claude or Program.Target.Both) RemoveForClaude(home, keptBack);
            if (target is Program.Target.Codex  or Program.Target.Both) RemoveForCodex(home, homeOverride, keptBack);
        }
        catch (IOException ex) when (Program.IsSharingViolation(ex))
        {
            return new Result(Outcome.ServerInUse, "The file was the server, or a config file setup writes.");
        }

        ReportKeptBack(keptBack);
        return new Result(Outcome.Removed, null);
    }

    // ---- Claude Code -------------------------------------------------------

    private static void RemoveForClaude(string home, List<string> keptBack)
    {
        string dest       = Program.ClaudeSkillsDest(home);
        string claudeJson = Program.ClaudeJson(home);

        if (Directory.Exists(dest))
        {
            List<string> recorded = Program.ReadSkillRecord(Program.ClaudeSkillRecord(home));
            Ui.Step("Claude Code", "removing the skills, the server and the saved MO2 instance", dest);
            foreach (string name in recorded) Ui.Bullet(name);
            DeleteTree(dest, keptBack);

            // An install from before setup started recording what it put here leaves no record. The tree is
            // houseCARL's outright either way, so the removal is the same one - but which of the two it was is
            // said rather than left for the reader to infer from a missing list of names.
            if (recorded.Count == 0)
                Ui.Note(
                    "This houseCARL was installed before setup began recording which skills it wrote, so the "
                    + "removal above took the whole houseCARL folder it owns.",
                    dest);
        }

        if (File.Exists(claudeJson))
        {
            Ui.Step("Claude Code", "removing the MCP server entry", claudeJson);
            UnregisterClaudeMcpServer(claudeJson, McpServerName);
        }
        Console.WriteLine();
    }

    // ---- Codex -------------------------------------------------------------

    private static void RemoveForCodex(string home, string? homeOverride, List<string> keptBack)
    {
        string serverDir  = Program.CodexServerDir(home, homeOverride);
        string skillsRoot = Program.CodexSkillsRoot(home);
        string record     = Program.CodexSkillRecord(home, homeOverride);
        string configToml = Program.CodexConfigToml(home);

        if (Directory.Exists(serverDir))
        {
            Ui.Step("Codex", "removing the server and the saved MO2 instance", serverDir);
            DeleteTree(serverDir, keptBack);
        }

        List<string> recorded = Program.ReadSkillRecord(record);
        if (recorded.Count > 0)
        {
            Ui.Step("Codex", "removing the skills it installed", skillsRoot);
            foreach (string name in Program.RemoveSkillDirs(skillsRoot, recorded, keptBack).Removed)
                Ui.Bullet(name);
        }
        else if (Directory.Exists(skillsRoot))
        {
            // ~/.agents/skills holds every agent's skills. Without the record there is no way to tell which
            // folders there are houseCARL's, and a directory diff would take somebody else's, so nothing goes.
            Ui.Note(
                "Setup has no record of which skills it put in the shared skills folder, so it removed none of "
                + "them — delete the houseCARL ones by hand if you want them gone.",
                skillsRoot);
        }

        if (File.Exists(record)) File.Delete(record);

        // The data dir exists only to hold the server dir and the record. Empty, it is a leftover; holding
        // anything else, it is not ours to take.
        string dataDir = Path.GetDirectoryName(serverDir)!;
        if (Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).Any())
            DeleteTree(dataDir, keptBack);

        if (File.Exists(configToml))
        {
            Ui.Step("Codex", "removing the MCP server entry", configToml);
            UnregisterCodexMcpServer(configToml, McpServerName);
        }
        Console.WriteLine();
    }

    private const string McpServerName = "housecarl";

    /// <summary>Delete a tree houseCARL owns. A tree that will not go (a read-only file inside it, a handle held
    /// on one) is collected for the note at the end rather than thrown: the host-config entry still comes out,
    /// and the run says what is still on disk.</summary>
    private static void DeleteTree(string dir, List<string> keptBack)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keptBack.Add(dir); }
    }

    /// <summary>Said at the end of an otherwise finished uninstall: what is still on disk.</summary>
    private static void ReportKeptBack(List<string> keptBack)
    {
        if (keptBack.Count == 0) return;
        List<string> detail = new(keptBack.Select(dir => "- " + dir)) { "" };
        detail.Add("A file in each is read-only or held open. Everything else is removed.");
        Ui.Note(
            "houseCARL is removed, but these folders could not be deleted — delete them by hand so nothing of "
            + "houseCARL is left:",
            detail.ToArray());
    }

    // ---- ~/.claude.json removal (JSON splice) ------------------------------

    /// <summary>
    /// Take mcpServers.<paramref name="name"/> out WITHOUT reparsing the whole file, the mirror of
    /// <c>RegisterClaudeMcpServer</c>: only the small mcpServers object is parsed and spliced back, so every
    /// other byte - including the case-differing keys a whole-file parser rejects - is left as it was. Backs the
    /// file up first, and writes nothing at all when there is no entry of ours to take.
    /// </summary>
    internal static void UnregisterClaudeMcpServer(string claudeJsonPath, string name)
    {
        if (!File.Exists(claudeJsonPath)) return;

        string text = File.ReadAllText(claudeJsonPath);
        if (Program.FindRootMemberObject(text, "mcpServers") is not { } b) return;

        string objText = text.Substring(b.start, b.end - b.start + 1);
        JsonObject servers = JsonNode.Parse(objText) as JsonObject
            ?? throw new InvalidDataException("mcpServers is not a JSON object.");
        if (!servers.Remove(name)) return; // nothing of ours registered: the file is not touched

        File.Copy(claudeJsonPath, claudeJsonPath + ".houseCARL.bak", overwrite: true);
        string newObj = Program.Reindent(servers.ToJsonString(Program.Indented),
                                         Program.LeadingIndentOfLineAt(text, b.start));
        File.WriteAllText(claudeJsonPath, string.Concat(text.AsSpan(0, b.start), newObj, text.AsSpan(b.end + 1)));
    }

    // ---- ~/.codex/config.toml removal (TOML splice) ------------------------

    /// <summary>Take the [mcp_servers.&lt;name&gt;] table out of a TOML config, backing the file up first and
    /// writing nothing when the table is not there.</summary>
    internal static void UnregisterCodexMcpServer(string configTomlPath, string name)
    {
        if (!File.Exists(configTomlPath)) return;
        string text = File.ReadAllText(configTomlPath);
        string updated = RemoveTomlTable(text, name);
        if (updated == text) return;
        File.Copy(configTomlPath, configTomlPath + ".houseCARL.bak", overwrite: true);
        File.WriteAllText(configTomlPath, updated);
    }

    /// <summary>
    /// Drop the [mcp_servers.&lt;name&gt;] table and any of its subtables, the mirror of
    /// <c>SpliceTomlTable</c>. Line-based, so nothing else in the file is reformatted, and the file's newline
    /// style is kept.
    ///
    /// A table the insert APPENDED to the end of the file was written after a blank line, and one it wrote into
    /// a file it created was written under a comment line. Those go with the table when the table is last in the
    /// file, which is where the insert put them; a table with something after it leaves what is above it alone,
    /// because there the blank line is the user's own separator.
    /// </summary>
    internal static string RemoveTomlTable(string text, string name)
    {
        string nl   = text.Contains("\r\n") ? "\r\n" : "\n";
        string head = "[mcp_servers." + name + "]";
        string sub  = "[mcp_servers." + name + ".";

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        List<string> outLines = new();
        bool removed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!removed && lines[i].Trim() == head)
            {
                int j = i + 1;
                while (j < lines.Length)
                {
                    string t = lines[j].Trim();
                    if (t.StartsWith("[") && t != head && !t.StartsWith(sub)) break;
                    j++;
                }
                bool lastInFile = !lines.Skip(j).Any(l => l.Trim().Length > 0);
                if (lastInFile)
                {
                    while (outLines.Count > 0 && outLines[^1].Trim().Length == 0) outLines.RemoveAt(outLines.Count - 1);
                    if (outLines.Count > 0 && outLines[^1].Trim() == Program.TomlComment) outLines.RemoveAt(outLines.Count - 1);
                }
                i = j - 1;
                removed = true;
                continue;
            }
            outLines.Add(lines[i]);
        }

        if (!removed) return text;

        while (outLines.Count > 0 && outLines[^1].Trim().Length == 0) outLines.RemoveAt(outLines.Count - 1);
        return outLines.Count == 0 ? "" : string.Join(nl, outLines) + nl;
    }
}
