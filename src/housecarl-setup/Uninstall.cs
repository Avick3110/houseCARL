using System.Text;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>
/// Taking back exactly what the install wrote, host by host. It is its own file because it is its own job -
/// <see cref="Program"/> stays the install - and every path it deletes comes from <see cref="Program"/>'s path
/// helpers, so a removal cannot go somewhere the install never wrote.
///
/// What it removes, per host:
///   Claude Code - under the tree at ClaudeSkillsDest: the skill folders the record names (or, with no record,
///                 the skills folder the package layout defines), the server dir with the saved MO2 instance in
///                 the houseCARL.user.json beside it, and the rest of the entries the package ships
///                 (<see cref="ClaudePackageEntries"/>) - then the tree itself only when nothing else is left
///                 in it. Anything else somebody put there stays, and is named. Plus the mcpServers.housecarl
///                 entry in ~/.claude.json.
///   Codex       - the server dir (the saved MO2 instance goes with it), the skill folders the Codex record
///                 names and no other, the record itself, and the [mcp_servers.housecarl] table in
///                 ~/.codex/config.toml.
///
/// Both config edits are removal splices beside the insert splices on <see cref="Program"/>: the file is copied
/// to a ".houseCARL.uninstall.bak" first (a name of its own, so the copy the INSTALL took is still there), the
/// entry is taken out, and every other byte in the file - its BOM, each line's own newline, its trailing state -
/// is left as it was. An entry that is not in the file is reported as not found; nothing is written and nothing
/// claims a removal that did not happen.
///
/// ~/.agents/skills is shared with every other agent's skills, so a folder the record does not list is never
/// touched - an install too old to have left a record removes nothing there and says so.
/// </summary>
public static class Uninstall
{
    /// <summary>What a non-interactive <see cref="TryUninstall"/> did.</summary>
    public enum Outcome
    {
        /// <summary>The run finished. What it actually took off, per host, is in <see cref="Result.Hosts"/>.</summary>
        Removed,
        /// <summary>A houseCARL server file is in use (a live Claude/Codex session is running it), so it could
        /// not be deleted. Nothing was deleted when the refusal was at pre-flight
        /// (<see cref="Result.RefusedBeforeAnyDelete"/>).</summary>
        ServerInUse,
    }

    /// <summary>One destination the run touched, and what it did to it - past tense, decided after the fact.</summary>
    /// <param name="Path">The destination.</param>
    /// <param name="Did">What happened to it, including "nothing" when there was nothing there.</param>
    public sealed record Step(string Path, string Did);

    /// <summary>One host's share of what the run did.</summary>
    /// <param name="Host">The host's name, as the plan and the menu say it.</param>
    /// <param name="Steps">Every destination this host's removal touched, in removal order.</param>
    /// <param name="RemovedSomething">True when at least one of the steps above took something off.</param>
    public sealed record HostResult(string Host, IReadOnlyList<Step> Steps, bool RemovedSomething);

    /// <summary>Result of a non-interactive uninstall - the probeable seam under the interactive prompt.</summary>
    /// <param name="What">What happened.</param>
    /// <param name="Message">Caller-facing detail for a non-<see cref="Outcome.Removed"/> outcome (else null).</param>
    public sealed record Result(Outcome What, string? Message)
    {
        /// <summary>True only when a <see cref="Outcome.ServerInUse"/> refusal happened at PRE-FLIGHT, before
        /// anything was deleted.</summary>
        public bool RefusedBeforeAnyDelete { get; init; }

        /// <summary>What the run did, host by host. Empty on a refusal, because it did nothing.</summary>
        public IReadOnlyList<HostResult> Hosts { get; init; } = Array.Empty<HostResult>();
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
        List<HostResult> hosts = new();

        try
        {
            if (target is Program.Target.Claude or Program.Target.Both) hosts.Add(RemoveForClaude(home, keptBack));
            if (target is Program.Target.Codex  or Program.Target.Both) hosts.Add(RemoveForCodex(home, homeOverride, keptBack));
        }
        catch (IOException ex) when (Program.IsSharingViolation(ex))
        {
            return new Result(Outcome.ServerInUse, "The file was the server, or a config file setup writes.");
        }

        ReportKeptBack(keptBack);
        return new Result(Outcome.Removed, null) { Hosts = hosts };
    }

    // ---- Claude Code -------------------------------------------------------

    /// <summary>
    /// The entries the package puts at the root of the Claude tree, which are the only ones a removal deletes
    /// there. The list is scripts/build-plugin.ps1's step 7 (the plugin tree it assembles) plus the record the
    /// install writes beside them. Anything else under the tree came from somewhere else and stays.
    /// </summary>
    private static readonly string[] ClaudePackageEntries =
    {
        ".claude-plugin", "server", "skills", ".mcp.json",
        "LICENSE", "THIRD-PARTY-NOTICES.txt", "README.md", "CHANGELOG.md",
        "installed-skills.txt",
    };

    private static HostResult RemoveForClaude(string home, List<string> keptBack)
    {
        string dest       = Program.ClaudeSkillsDest(home);
        string claudeJson = Program.ClaudeJson(home);
        List<Step> steps  = new();
        bool removedSomething = false;

        if (!Directory.Exists(dest))
        {
            steps.Add(new Step(dest, "nothing was installed here"));
        }
        else
        {
            List<string> recorded = Program.ReadSkillRecord(Program.ClaudeSkillRecord(home));
            Ui.Step("Claude Code", "removing the skills, the server and the saved MO2 instance", dest);

            // With a record, the skills come off BY NAME, so a folder somebody else put under the installed
            // skills root survives. Without one there is no list to go by, and the fallback is the package
            // layout: the skills folder is one of the entries the install writes, so it goes whole.
            string skillsDir = Path.Combine(dest, "skills");
            if (recorded.Count > 0 && Directory.Exists(skillsDir))
            {
                foreach (string name in Program.RemoveSkillDirs(skillsDir, recorded, keptBack).Removed) Ui.Bullet(name);
                if (!Directory.EnumerateFileSystemEntries(skillsDir).Any()) DeleteEntry(skillsDir, keptBack);
            }

            foreach (string entry in ClaudePackageEntries)
            {
                if (entry == "skills" && recorded.Count > 0) continue; // taken by name above
                DeleteEntry(Path.Combine(dest, entry), keptBack);
            }

            List<string> left = Directory.Exists(dest)
                ? Directory.EnumerateFileSystemEntries(dest).Select(p => Path.GetFileName(p)!).ToList()
                : new List<string>();

            if (left.Count == 0)
            {
                DeleteEntry(dest, keptBack);
                steps.Add(new Step(dest, "removed - the skills, the server and the saved MO2 instance"));
            }
            else
            {
                steps.Add(new Step(dest, "the files houseCARL installed removed; "
                                       + Count(left.Count, "item") + " left in the folder"));
                Ui.Note(
                    "The houseCARL folder still holds these, so the folder itself was left in place — delete "
                    + "them by hand if you want it gone:",
                    left.Select(n => "- " + n).ToArray());
            }
            removedSomething = true;

            // An install from before setup started recording what it put here leaves no record, so the skills
            // could not be taken by name; which route this run took is said rather than left to be inferred
            // from a missing list of names.
            if (recorded.Count == 0)
                Ui.Note(
                    "This houseCARL was installed before setup began recording which skills it wrote, so the "
                    + "removal above went by the files a houseCARL package ships instead of by that record.",
                    dest);
        }

        ConfigEdit json = ConfigEdit.FileMissing;
        if (File.Exists(claudeJson))
        {
            Ui.Step("Claude Code", "removing the MCP server entry", claudeJson);
            json = UnregisterClaudeMcpServer(claudeJson, McpServerName);
        }
        steps.Add(new Step(claudeJson, ConfigDid(json)));
        // Said only when there WAS a houseCARL here: on a host that never had one, a config with no entry is
        // the expected state, not something to point at.
        if (json == ConfigEdit.NotFound && removedSomething) ReportEntryNotFound(claudeJson);
        removedSomething |= json == ConfigEdit.Removed;

        Console.WriteLine();
        return new HostResult("Claude Code", steps, removedSomething);
    }

    // ---- Codex -------------------------------------------------------------

    private static HostResult RemoveForCodex(string home, string? homeOverride, List<string> keptBack)
    {
        string serverDir  = Program.CodexServerDir(home, homeOverride);
        string skillsRoot = Program.CodexSkillsRoot(home);
        string record     = Program.CodexSkillRecord(home, homeOverride);
        string configToml = Program.CodexConfigToml(home);
        List<Step> steps  = new();
        bool removedSomething = false;

        if (Directory.Exists(serverDir))
        {
            Ui.Step("Codex", "removing the server and the saved MO2 instance", serverDir);
            DeleteEntry(serverDir, keptBack);
            steps.Add(new Step(serverDir, Directory.Exists(serverDir)
                ? "could not be removed - see the note above"
                : "removed - the server and the saved MO2 instance"));
            removedSomething = true;
        }
        else steps.Add(new Step(serverDir, "nothing was installed here"));

        List<string> recorded = Program.ReadSkillRecord(record);
        if (recorded.Count > 0)
        {
            Ui.Step("Codex", "removing the skills it installed", skillsRoot);
            List<string> went = Program.RemoveSkillDirs(skillsRoot, recorded, keptBack).Removed;
            foreach (string name in went) Ui.Bullet(name);
            steps.Add(new Step(skillsRoot, went.Count == 0
                ? "the skills the record named were already gone"
                : "removed the " + Count(went.Count, "skill folder") + " the record named"));
            removedSomething |= went.Count > 0;
        }
        else if (Directory.Exists(skillsRoot) && removedSomething)
        {
            // ~/.agents/skills holds every agent's skills. Without the record there is no way to tell which
            // folders there are houseCARL's, and a directory diff would take somebody else's, so nothing goes.
            // Said only when there WAS a houseCARL here: the shared folder is somebody else's the rest of the
            // time, and pointing at it would be pointing at a folder this run had no business in.
            Ui.Note(
                "Setup has no record of which skills it put in the shared skills folder, so it removed none of "
                + "them — delete the houseCARL ones by hand if you want them gone.",
                skillsRoot);
            steps.Add(new Step(skillsRoot, "nothing removed - setup has no record of what it put there"));
        }
        else steps.Add(new Step(skillsRoot, "nothing was installed here"));

        if (File.Exists(record))
        {
            File.Delete(record);
            steps.Add(new Step(record, "removed"));
            removedSomething = true;
        }
        else steps.Add(new Step(record, "was not there"));

        // The data dir exists only to hold the server dir and the record. Empty, it is a leftover; holding
        // anything else, it is not ours to take.
        string dataDir = Path.GetDirectoryName(serverDir)!;
        if (Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).Any())
            DeleteEntry(dataDir, keptBack);

        ConfigEdit toml = ConfigEdit.FileMissing;
        if (File.Exists(configToml))
        {
            Ui.Step("Codex", "removing the MCP server entry", configToml);
            toml = UnregisterCodexMcpServer(configToml, McpServerName);
        }
        steps.Add(new Step(configToml, ConfigDid(toml)));
        if (toml == ConfigEdit.NotFound && removedSomething) ReportEntryNotFound(configToml);
        removedSomething |= toml == ConfigEdit.Removed;

        Console.WriteLine();
        return new HostResult("Codex", steps, removedSomething);
    }

    private const string McpServerName = "housecarl";

    /// <summary>The suffix of the copy a REMOVAL takes before it edits a config. It is not the install's
    /// ".houseCARL.bak", so an uninstall never writes over the copy the install took of the file as it was
    /// before houseCARL was ever in it.</summary>
    internal const string BackupSuffix = ".houseCARL.uninstall.bak";

    /// <summary>What a config splice found and did.</summary>
    internal enum ConfigEdit
    {
        /// <summary>The config file is not on this machine, so there was nothing to edit.</summary>
        FileMissing,
        /// <summary>The file is there but carries no houseCARL entry; it was not written to.</summary>
        NotFound,
        /// <summary>The entry was taken out and the file rewritten.</summary>
        Removed,
    }

    /// <summary>"1 item" / "3 items" — a count said as a sentence rather than as "item(s)".</summary>
    private static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    private static string ConfigDid(ConfigEdit edit) => edit switch
    {
        ConfigEdit.Removed     => "the server entry removed  (a " + BackupSuffix + " copy was made first)",
        ConfigEdit.NotFound    => "no houseCARL entry was in it, so it was not written to",
        _                      => "the file is not on this machine",
    };

    /// <summary>Said when a config file is there but carries no entry of ours: the file is named, so somebody
    /// who registered houseCARL by hand under another spelling knows where to look, rather than being told the
    /// entry was removed when it is still there.</summary>
    private static void ReportEntryNotFound(string path)
        => Ui.Note(
            "This config file has no houseCARL entry setup recognises, so it was left alone — if houseCARL is "
            + "still registered in it, the entry was written by hand and has to come out by hand.",
            path);

    /// <summary>Delete one entry houseCARL owns - a file, or a directory and everything in it. A reparse point
    /// (a junction or a symlink) is removed as the LINK, never walked into, so whatever it points at is not
    /// touched. An entry that will not go (a read-only file inside it, a handle held on one) is collected for
    /// the note at the end rather than thrown: the host-config entry still comes out, and the run says what is
    /// still on disk.</summary>
    private static void DeleteEntry(string path, List<string> keptBack)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(path, recursive: false); // the link goes; its target does not
                else
                    Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keptBack.Add(path); }
    }

    /// <summary>Said at the end of an otherwise finished uninstall: what is still on disk.</summary>
    private static void ReportKeptBack(List<string> keptBack)
    {
        if (keptBack.Count == 0) return;
        List<string> detail = new(keptBack.Select(dir => "- " + dir)) { "" };
        detail.Add("A file in each is read-only or held open. Everything else is removed.");
        Ui.Note(
            "houseCARL is removed, but these could not be deleted — delete them by hand so nothing of "
            + "houseCARL is left:",
            detail.ToArray());
    }

    // ---- reading and writing a config without changing what is not ours ----

    /// <summary>Read a config as text, saying whether it carried a UTF-8 BOM. <c>File.ReadAllText</c> eats a BOM
    /// and <c>File.WriteAllText</c> does not put one back, so a file that had one would lose it on the round
    /// trip; this pair carries it across.</summary>
    private static (string Text, bool Bom) ReadTextKeepingBom(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    /// <summary>Write a config back, with the BOM it had and no other.</summary>
    private static void WriteTextKeepingBom(string path, string text, bool bom)
        => File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));

    /// <summary>Cut text into lines that each KEEP their own terminator, so joining them back gives the original
    /// bytes: a file mixing CRLF and LF keeps both, and one that ends without a newline still does.</summary>
    internal static List<string> SplitKeepingNewlines(string text)
    {
        List<string> lines = new();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>One line without its terminator.</summary>
    private static string LineBody(string line) => line.TrimEnd('\n').TrimEnd('\r');

    // ---- ~/.claude.json removal (JSON splice) ------------------------------

    /// <summary>
    /// Take mcpServers.<paramref name="name"/> out WITHOUT reparsing the whole file, the mirror of
    /// <c>RegisterClaudeMcpServer</c>: only the small mcpServers object is parsed and spliced back, so every
    /// other byte - including the case-differing keys a whole-file parser rejects - is left as it was. Backs the
    /// file up first, and writes nothing at all when there is no entry of ours to take.
    ///
    /// When ours was the only entry, the whole mcpServers member goes with it: the install CREATES that member
    /// in a file that never had one, and an empty object left behind is a key the user never wrote. An empty
    /// mcpServers and no mcpServers mean the same thing to the host, so nothing of theirs is lost either way.
    /// </summary>
    internal static ConfigEdit UnregisterClaudeMcpServer(string claudeJsonPath, string name)
    {
        if (!File.Exists(claudeJsonPath)) return ConfigEdit.FileMissing;

        var (text, bom) = ReadTextKeepingBom(claudeJsonPath);
        if (Program.FindRootMemberObject(text, "mcpServers") is not { } b) return ConfigEdit.NotFound;

        string objText = text.Substring(b.start, b.end - b.start + 1);
        JsonObject servers = JsonNode.Parse(objText) as JsonObject
            ?? throw new InvalidDataException("mcpServers is not a JSON object.");
        if (!servers.Remove(name)) return ConfigEdit.NotFound; // nothing of ours registered: the file is not touched

        File.Copy(claudeJsonPath, claudeJsonPath + BackupSuffix, overwrite: true);
        string updated;
        if (servers.Count == 0)
        {
            updated = RemoveRootMember(text, "mcpServers", b);
        }
        else
        {
            string newObj = Program.Reindent(servers.ToJsonString(Program.Indented),
                                             Program.LeadingIndentOfLineAt(text, b.start));
            updated = string.Concat(text.AsSpan(0, b.start), newObj, text.AsSpan(b.end + 1));
        }
        WriteTextKeepingBom(claudeJsonPath, updated, bom);
        return ConfigEdit.Removed;
    }

    /// <summary>
    /// Cut a root-level <c>"key": { ... }</c> member out of a JSON document, with the line it sits on and the
    /// one comma that separated it from its neighbour - the exact inverse of the block
    /// <c>RegisterClaudeMcpServer</c> splices in when it creates the member. Nothing else in the file moves.
    /// </summary>
    internal static string RemoveRootMember(string text, string key, (int start, int end) value)
    {
        int keyStart = text.LastIndexOf("\"" + key + "\"", value.start, StringComparison.Ordinal);
        if (keyStart < 0) return text; // the caller found it; without the key there is nothing safe to cut

        int cutStart = keyStart;
        int nl = text.LastIndexOf('\n', keyStart);
        if (nl >= 0 && text.AsSpan(nl + 1, keyStart - nl - 1).IsWhiteSpace()) cutStart = nl;

        int cutEnd = value.end + 1;
        int after = cutEnd;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (after < text.Length && text[after] == ',')
        {
            cutEnd = after + 1;
        }
        else
        {
            // Last member of the object: the comma above it is what would be left dangling, so it comes too.
            int before = cutStart - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            if (before >= 0 && text[before] == ',') cutStart = before;
        }

        return text.Remove(cutStart, cutEnd - cutStart);
    }

    // ---- ~/.codex/config.toml removal (TOML splice) ------------------------

    /// <summary>Take the [mcp_servers.&lt;name&gt;] table out of a TOML config, backing the file up first and
    /// writing nothing when the table is not there.</summary>
    internal static ConfigEdit UnregisterCodexMcpServer(string configTomlPath, string name)
    {
        if (!File.Exists(configTomlPath)) return ConfigEdit.FileMissing;
        var (text, bom) = ReadTextKeepingBom(configTomlPath);
        if (RemoveTomlTable(text, name) is not { } updated) return ConfigEdit.NotFound;
        File.Copy(configTomlPath, configTomlPath + BackupSuffix, overwrite: true);
        WriteTextKeepingBom(configTomlPath, updated, bom);
        return ConfigEdit.Removed;
    }

    /// <summary>
    /// Drop the [mcp_servers.&lt;name&gt;] table and any of its subtables, the mirror of
    /// <c>SpliceTomlTable</c>, or null when the file carries no such table. Line-based, and each line keeps its
    /// own terminator, so the file's newline style, mixed or not, and its trailing state are what they were.
    ///
    /// The table body ends at the LAST line in it that is neither blank nor a comment. TOML attaches a comment
    /// to the table under it, so blank lines and comments between our last key and the next header are the next
    /// table's, not ours, and they stay.
    ///
    /// A table the insert APPENDED to the end of the file was written after a blank line, and one it wrote into
    /// a file it created was written under a comment line. Those go with the table when the table is last in the
    /// file, which is where the insert put them; a table with something after it leaves what is above it alone,
    /// because there the blank line is the user's own separator.
    /// </summary>
    internal static string? RemoveTomlTable(string text, string name)
    {
        List<string> lines = SplitKeepingNewlines(text);

        int head = -1;
        for (int i = 0; i < lines.Count && head < 0; i++)
            if (HeaderOf(lines[i], name) == HeaderKind.Ours) head = i;
        if (head < 0) return null;

        int last = head;
        for (int j = head + 1; j < lines.Count; j++)
        {
            string body = LineBody(lines[j]).Trim();
            if (body.StartsWith("[") && HeaderOf(lines[j], name) is not HeaderKind.Ours and not HeaderKind.OurSub) break;
            if (body.Length > 0 && !body.StartsWith("#")) last = j;
        }

        int first = head;
        bool lastInFile = !lines.Skip(last + 1).Any(l => LineBody(l).Trim().Length > 0);
        if (lastInFile)
        {
            while (first > 0 && LineBody(lines[first - 1]).Trim().Length == 0) first--;
            if (first > 0 && LineBody(lines[first - 1]).Trim() == Program.TomlComment) first--;
        }

        lines.RemoveRange(first, last - first + 1);
        return string.Concat(lines);
    }

    /// <summary>What a line is to the table being removed.</summary>
    private enum HeaderKind
    {
        /// <summary>Not a table header at all.</summary>
        None,
        /// <summary>[mcp_servers.&lt;name&gt;], however it is spelled.</summary>
        Ours,
        /// <summary>A subtable of ours, which the install's table can carry.</summary>
        OurSub,
        /// <summary>Somebody else's table, which ends our body.</summary>
        Other,
    }

    /// <summary>
    /// Read a line as a table header. TOML spells one key several ways - <c>[mcp_servers.housecarl]</c>,
    /// <c>[mcp_servers."housecarl"]</c>, <c>[mcp_servers . housecarl]</c> - and Codex accepts a config written
    /// any of them, so a match on the exact string the install writes would leave a hand-written entry in place
    /// while the run said it was removed.
    /// </summary>
    private static HeaderKind HeaderOf(string line, string name)
    {
        string t = StripComment(LineBody(line)).Trim();
        if (t.Length < 2 || t[0] != '[' || t[^1] != ']') return HeaderKind.None;
        if (t.StartsWith("[[")) return HeaderKind.Other; // an array of tables is never ours

        List<string>? parts = SplitTomlKey(t[1..^1]);
        if (parts is null || parts.Count < 2) return HeaderKind.Other;
        if (parts[0] != "mcp_servers" || parts[1] != name) return HeaderKind.Other;
        return parts.Count == 2 ? HeaderKind.Ours : HeaderKind.OurSub;
    }

    /// <summary>Cut a trailing <c># comment</c> off a line, leaving a '#' inside a quoted key where it is.</summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') quote = c;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    /// <summary>Split a dotted TOML key into its parts, bare or quoted, or null when it does not parse as one.</summary>
    private static List<string>? SplitTomlKey(string inner)
    {
        List<string> parts = new();
        int i = 0;
        while (true)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            string part;
            if (i < inner.Length && (inner[i] == '"' || inner[i] == '\''))
            {
                char q = inner[i++];
                int s = i;
                while (i < inner.Length && inner[i] != q) i++;
                if (i >= inner.Length) return null; // unterminated quote
                part = inner[s..i];
                i++;
            }
            else
            {
                int s = i;
                while (i < inner.Length && inner[i] != '.') i++;
                part = inner[s..i].TrimEnd();
            }
            if (part.Length == 0) return null;
            parts.Add(part);
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length) return parts;
            if (inner[i] != '.') return null;
            i++;
        }
    }
}
