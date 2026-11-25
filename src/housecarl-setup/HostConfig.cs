using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>
/// The one door to houseCARL's entry in a host's config: find the entry, set only setup's own keys on it
/// (<c>type</c> and <c>command</c> in ~/.claude.json, <c>command</c> in ~/.codex/config.toml), and write nothing
/// when that changes nothing. Every other key on the entry is the user's or the host's and stays. The removals on
/// <see cref="Uninstall"/> read, find and copy through here too.
/// </summary>
public static class HostConfig
{
    /// <summary>The suffix of the copy a registration takes before it changes a config.</summary>
    internal const string InstallCopySuffix = ".houseCARL.bak";

    /// <summary>What a registration does to houseCARL's entry.</summary>
    internal enum Did
    {
        /// <summary>The entry goes in, or setup's keys on the one that is there change.</summary>
        Registered,
        /// <summary>The file would come out the same, so it is not written to.</summary>
        Unchanged,
        /// <summary>The entry is not one setup recognises as running houseCARL's server exe, so a fresh one takes its
        /// place.</summary>
        Replaced,
    }

    /// <summary>What a registration does, decided before anything is written.</summary>
    /// <param name="Path">The config.</param>
    /// <param name="Did">What happens to the entry.</param>
    /// <param name="Text">The file's new text, or null when there is nothing to write.</param>
    /// <param name="Encoding">How the file is written.</param>
    /// <param name="Copy">Where the file as it was is kept, or null when there is no such file or no change.</param>
    /// <param name="Kept">The names of the settings on the entry that are not setup's.</param>
    /// <param name="TurnedOff">True when the kept settings turn houseCARL off in the host.</param>
    internal sealed record Registration(
        string Path, Did Did, string? Text, Encoding Encoding, string? Copy, IReadOnlyList<string> Kept, bool TurnedOff);

    /// <summary>Write a registration, copying the file first.</summary>
    internal static Registration Apply(Registration r)
    {
        if (r.Text is not null) Write(r.Path, r.Copy, r.Text, r.Encoding);
        return r;
    }

    /// <summary>The registration for a file that is there, given the text it would come out as.</summary>
    private static Registration Decide(
        string path, string before, string after, Encoding encoding, Did did,
        IReadOnlyList<string> kept, bool turnedOff = false)
    {
        List<string> names = kept.Distinct().ToList();
        return before == after
            ? new Registration(path, Did.Unchanged, null, encoding, null, names, turnedOff)
            : new Registration(path, did, after, encoding, CopyName(path, InstallCopySuffix), names, turnedOff);
    }

    /// <summary>True when a command is houseCARL's server exe, wherever it sits.</summary>
    private static bool RunsHouseCarl(string? command)
        => string.Equals(Path.GetFileName(command), Program.ServerExeName, StringComparison.OrdinalIgnoreCase);

    // ---- reading, writing and copying a config -----------------------------

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Read a config as text with the encoding its byte order mark names, so it is written back with the
    /// mark it had and no other.</summary>
    internal static (string Text, Encoding Encoding) Read(string path)
    {
        using StreamReader reader = new(path, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    /// <summary>Where the file as it is now is kept: the first name in the series that is free or already holds
    /// these bytes (".houseCARL.bak", ".houseCARL.2.bak", ...). A copy holding other bytes is never the answer.</summary>
    internal static string CopyName(string path, string suffix)
    {
        byte[] bytes = File.ReadAllBytes(path);
        for (int n = 1; ; n++)
        {
            string copy = path + (n == 1 ? suffix : Path.ChangeExtension(suffix, null) + "." + n + Path.GetExtension(suffix));
            if (!File.Exists(copy) || File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes)) return copy;
        }
    }

    /// <summary>Write a config, keeping the file as it was first at the name <see cref="CopyName"/> gave.</summary>
    internal static void Write(string path, string? copy, string text, Encoding encoding)
    {
        if (copy is not null && !File.Exists(copy)) File.Copy(path, copy);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, encoding);
    }

    // ---- ~/.claude.json (JSON splice) --------------------------------------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// The registration of mcpServers.<paramref name="name"/> WITHOUT reparsing the whole file. ~/.claude.json
    /// can hold keys that differ only by case (Windows path history), which case-insensitive parsers
    /// reject; we parse ONLY the small, duplicate-free mcpServers object and splice it back, leaving the
    /// rest of the file byte-for-byte intact.
    /// </summary>
    internal static Registration ForClaude(string path, string name, string command)
    {
        JsonObject fresh = new()
        {
            ["type"]    = "stdio",
            ["command"] = command,
            ["args"]    = new JsonArray(),
        };

        if (!File.Exists(path))
        {
            JsonObject newRoot = new() { ["mcpServers"] = new JsonObject { [name] = fresh } };
            return new Registration(path, Did.Registered, newRoot.ToJsonString(Indented), Utf8NoBom, null, [], false);
        }

        (string text, Encoding encoding) = Read(path);

        if (FindRootMemberObject(text, "mcpServers") is not { } b)
        {
            int rootBrace = text.IndexOf('{');
            if (rootBrace < 0) throw new InvalidDataException("~/.claude.json is not a JSON object.");
            JsonObject created = new() { [name] = fresh };
            string block = "\n  \"mcpServers\": " + Reindent(created.ToJsonString(Indented), "  ") + ",";
            string added = string.Concat(text.AsSpan(0, rootBrace + 1), block, text.AsSpan(rootBrace + 1));
            return Decide(path, text, added, encoding, Did.Registered, []);
        }

        JsonObject servers = ServersIn(text, b);
        Did did = servers.ContainsKey(name) ? Did.Replaced : Did.Registered;
        List<string> kept = new();
        bool same = false;
        if (EntryIn(servers, name) is { } entry && RunsHouseCarl(StringOf(entry["command"])))
        {
            did = Did.Registered;
            kept = entry.Where(p => !IsSetupsClaudeKey(p.Key, p.Value)).Select(p => p.Key).ToList();
            same = StringOf(entry["type"]) == "stdio" && StringOf(entry["command"]) == command;
            entry["type"]    = "stdio";
            entry["command"] = command;
        }
        else servers[name] = fresh;

        return Decide(path, text, same ? text : WithServers(text, b, servers), encoding, did, kept);
    }

    /// <summary>The mcpServers object at these bounds.</summary>
    internal static JsonObject ServersIn(string text, (int start, int end) b)
        => JsonNode.Parse(text.Substring(b.start, b.end - b.start + 1)) as JsonObject
            ?? throw new InvalidDataException("mcpServers is not a JSON object.");

    /// <summary>The entry as an object, or null when it is not one or holds a key twice, which JsonObject does not
    /// read.</summary>
    private static JsonObject? EntryIn(JsonObject servers, string name)
    {
        JsonObject? entry = servers[name] as JsonObject;
        try
        {
            _ = entry?.Count; // the keys are read on first use
            return entry;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The file with the mcpServers object at these bounds written as <paramref name="servers"/>.</summary>
    internal static string WithServers(string text, (int start, int end) b, JsonObject servers)
        => string.Concat(
            text.AsSpan(0, b.start),
            Reindent(servers.ToJsonString(Indented), LeadingIndentOfLineAt(text, b.start)),
            text.AsSpan(b.end + 1));

    private static string? StringOf(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The keys setup writes on a Claude entry. An <c>args</c> with something in it is the user's.</summary>
    private static bool IsSetupsClaudeKey(string key, JsonNode? value)
        => key is "type" or "command" || (key == "args" && value is JsonArray { Count: 0 });

    /// <summary>Finds the `{ ... }` value of a DEPTH-1 (root-level) member named <paramref name="key"/>. String-aware.</summary>
    internal static (int start, int end)? FindRootMemberObject(string text, string key)
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

    // ---- ~/.codex/config.toml (TOML splice) --------------------------------

    /// <summary>The line written above a [mcp_servers.housecarl] table setup created the file for. The removal
    /// splice takes it back with the table, so the two spellings are one const.</summary>
    internal const string TomlComment = "# houseCARL MCP server (added by houseCARL-Setup)";

    /// <summary>
    /// The registration of [mcp_servers.<paramref name="name"/>] in a TOML config.toml. On a table that is setup's
    /// only the value of <c>command</c> changes (docs/architecture/setup-host-config.md). The command path is written
    /// as a LITERAL TOML string (single quotes) so Windows backslashes pass through verbatim - a basic
    /// "double-quoted" string would treat them as escapes.
    /// </summary>
    internal static Registration ForCodex(string path, string name, string command)
    {
        string head  = "[mcp_servers." + name + "]";
        string value = "'" + command + "'";
        string set   = "command = " + value;

        if (!File.Exists(path))
            return new Registration(
                path, Did.Registered, TomlComment + "\n" + head + "\n" + set + "\n", Utf8NoBom, null, [], false);

        (string text, Encoding encoding) = Read(path);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        TomlFile toml = new(text);

        // In a file left open a line of a value can read as a key, so no name in it is printed.
        Registration Decided(string after, Did did, IReadOnlyList<string> kept, bool off = false)
            => Decide(path, text, after, encoding, did, toml.LeftOpen ? [] : kept, off);

        if (toml.FindTable(name) is not { } table)
        {
            string trimmed = text.TrimEnd('\r', '\n');
            string above = trimmed.Length == 0 ? TomlComment + nl : trimmed + nl + nl;
            return Decided(above + head + nl + set + nl, Did.Registered, toml.SubtableNames(name));
        }

        List<string> lines = toml.Lines;
        List<TomlFile.Key> keys = toml.KeysOf(table);
        if (keys.Find(k => k.Is("command")) is { } key
            && TomlFile.StringAt(lines[key.Line], key.ValueStart) is { } was && RunsHouseCarl(was.Value))
        {
            List<string> kept = keys.Select(k => k.Parts[0]).Where(k => k != "command")
                .Concat(toml.SubtableNames(name)).ToList();
            bool off = keys.Any(k => k.Is("enabled")
                && TomlFile.StripComment(lines[k.Line][k.ValueStart..]).Trim() == "false");

            string line = lines[key.Line];
            if (was.Value != command)
                lines[key.Line] = string.Concat(line.AsSpan(0, key.ValueStart), value, line.AsSpan(was.End));
            return Decided(string.Concat(lines), Did.Registered, kept, off);
        }

        lines.RemoveRange(table.Head, table.Last - table.Head + 1);
        lines.InsertRange(table.Head, [head + nl, set + nl]);
        string replaced = string.Concat(lines);
        // A subtable of ours that is not right after the table is not part of what was cut.
        return Decided(replaced, Did.Replaced, new TomlFile(replaced).SubtableNames(name));
    }
}
