using System.Text.Json;

namespace HousecarlSetup;

/// <summary>
/// A config.toml read just far enough to find one [mcp_servers.&lt;name&gt;] table and the keys in it. Line-based:
/// each line keeps its own terminator, so joining <see cref="Lines"/> gives the file back byte for byte.
/// </summary>
internal sealed class TomlFile
{
    /// <summary>The file's lines, each with its terminator.</summary>
    internal List<string> Lines { get; }

    /// <summary>True when the brackets or triple quotes in the file do not close, so what counts as a key in it is a
    /// guess that can land inside a value.</summary>
    internal bool LeftOpen { get; }

    // For each line, whether a statement starts on it rather than inside a value that runs over lines.
    private readonly bool[] starts;

    internal TomlFile(string text)
    {
        Lines  = SplitKeepingNewlines(text);
        starts = StatementStarts(Lines, out bool leftOpen);
        LeftOpen = leftOpen;
    }

    /// <summary>One table and the subtables right after it, as line numbers.</summary>
    /// <param name="Head">The table's header line.</param>
    /// <param name="KeysEnd">The line after the table's own keys: the next header, or the end of the file.</param>
    /// <param name="Last">The last line of it that is neither blank nor a comment. TOML attaches a comment to the
    /// table under it, so what follows this line is the next table's.</param>
    internal readonly record struct Table(int Head, int KeysEnd, int Last);

    /// <summary>One <c>key = value</c> line: which line, its key, and where its value starts.</summary>
    internal sealed record Key(int Line, List<string> Parts, int ValueStart)
    {
        /// <summary>True when this is the undotted key <paramref name="name"/>.</summary>
        internal bool Is(string name) => Parts.Count == 1 && Parts[0] == name;
    }

    /// <summary>The [mcp_servers.<paramref name="name"/>] table, however its header is spelled, or null.</summary>
    internal Table? FindTable(string name)
    {
        int head = -1;
        for (int i = 0; i < Lines.Count && head < 0; i++)
            if (starts[i] && OurHeader(Lines[i], name) is { Array: false, Below.Count: 0 }) head = i;
        if (head < 0) return null;

        int keysEnd = Lines.Count, last = head;
        for (int j = head + 1; j < Lines.Count; j++)
        {
            string body = LineBody(Lines[j]).Trim();
            if (starts[j] && body.StartsWith('['))
            {
                keysEnd = Math.Min(keysEnd, j);
                if (OurHeader(Lines[j], name) is not { Array: false }) break;
            }
            if (body.Length > 0 && !(starts[j] && body.StartsWith('#'))) last = j;
        }
        return new Table(head, keysEnd, last);
    }

    /// <summary>The keys of a table, its subtables' not among them.</summary>
    internal List<Key> KeysOf(Table table)
    {
        List<Key> keys = new();
        for (int i = table.Head + 1; i < table.KeysEnd; i++)
            if (starts[i] && KeyOn(i) is { } key) keys.Add(key);
        return keys;
    }

    /// <summary>The first key of every subtable of [mcp_servers.<paramref name="name"/>], wherever it sits in the
    /// file, arrays of tables among them.</summary>
    internal List<string> SubtableNames(string name)
    {
        List<string> names = new();
        for (int i = 0; i < Lines.Count; i++)
            if (starts[i] && OurHeader(Lines[i], name) is { Below: [string sub, ..] })
                names.Add(sub);
        return names;
    }

    /// <summary>
    /// For each line, whether a statement starts on it. A value can run over lines inside brackets or a
    /// triple-quoted string, and a line inside one can look like a header or a key. A file that leaves a bracket
    /// or a triple quote open does not load, so every line of it is taken as a statement: one slip must not make
    /// the rest of the file part of our table.
    /// </summary>
    private static bool[] StatementStarts(List<string> lines, out bool leftOpen)
    {
        bool[] starts = new bool[lines.Count];
        int depth = 0;
        string? quote = null;
        for (int i = 0; i < lines.Count; i++)
        {
            starts[i] = depth == 0 && quote is null;
            string line = lines[i];
            for (int k = 0; k < line.Length;)
            {
                char c = line[k];
                if (quote is not null)
                {
                    if (c == '\\' && quote[0] == '"') k += 2;
                    else if (string.CompareOrdinal(line, k, quote, 0, quote.Length) == 0) { k += quote.Length; quote = null; }
                    else k++;
                    continue;
                }
                if (c == '#') break;
                if (c is '"' or '\'')
                {
                    quote = TripleQuoteAt(line, k) ? new string(c, 3) : c.ToString();
                    k += quote.Length;
                    continue;
                }
                if (c is '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
                k++;
            }
            if (quote is { Length: 1 }) quote = null; // a single-line string ends with its line
        }

        leftOpen = depth != 0 || quote is not null;
        if (leftOpen) Array.Fill(starts, true);
        return starts;
    }

    private static bool TripleQuoteAt(string line, int at)
        => string.CompareOrdinal(line, at, new string(line[at], 3), 0, 3) == 0;

    /// <summary>Cut text into lines that each KEEP their own terminator, so joining them back gives the original
    /// bytes: a file mixing CRLF and LF keeps both, and one that ends without a newline still does.</summary>
    private static List<string> SplitKeepingNewlines(string text)
    {
        List<string> lines = new();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>One line without its terminator.</summary>
    internal static string LineBody(string line) => line.TrimEnd('\n').TrimEnd('\r');

    /// <summary>
    /// Read a line as the header of [mcp_servers.<paramref name="name"/>] or of a table below it: the key parts below
    /// it, and whether it is an array of tables; null for any other line. TOML spells one key several ways -
    /// <c>[mcp_servers.housecarl]</c>, <c>[mcp_servers."housecarl"]</c>, <c>[mcp_servers . housecarl]</c> - and Codex
    /// accepts a config written any of them, so a match on the exact string the install writes would miss a
    /// hand-written entry.
    /// </summary>
    private static (List<string> Below, bool Array)? OurHeader(string line, string name)
    {
        string t = StripComment(LineBody(line)).Trim();
        bool array = t.StartsWith("[[") && t.EndsWith("]]");
        if (!array && !(t.StartsWith('[') && t.EndsWith(']'))) return null;
        return SplitKey(array ? t[2..^2] : t[1..^1]) is ["mcp_servers", var server, .. var below] && server == name
            ? (below, array)
            : null;
    }

    /// <summary>Read a line as <c>key = value</c>, or null when it is not one.</summary>
    private Key? KeyOn(int i)
    {
        string line = Lines[i];
        int eq = IndexOutsideQuotes(StripComment(line), '=');
        if (eq < 0 || SplitKey(line[..eq]) is not { } parts) return null;
        int value = eq + 1;
        while (value < line.Length && line[value] is ' ' or '\t') value++;
        return new Key(i, parts, value);
    }

    /// <summary>The single-line string that is the whole value at <paramref name="at"/>, and where it ends, or
    /// null when the value is anything else, a string holding its own quote among them.</summary>
    internal static (string Value, int End)? StringAt(string line, int at)
    {
        if (at >= line.Length || line[at] is not ('"' or '\'')) return null;
        int close = line.IndexOf(line[at], at + 1);
        if (close < 0 || StripComment(line[(close + 1)..]).Trim().Length > 0) return null;

        string inner = line[(at + 1)..close];
        if (line[at] == '\'') return (inner, close + 1);
        try
        {
            // A path's escapes (\\, \uXXXX) are spelled the same in JSON.
            return (JsonSerializer.Deserialize<string>("\"" + inner + "\"")!, close + 1);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Cut a trailing <c># comment</c> off a line, leaving a '#' inside a quoted key where it is.</summary>
    internal static string StripComment(string line)
    {
        int at = IndexOutsideQuotes(line, '#');
        return at < 0 ? line : line[..at];
    }

    private static int IndexOutsideQuotes(string line, char wanted)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == wanted) return i;
            else if (c == '"' || c == '\'') quote = c;
        }
        return -1;
    }

    /// <summary>Split a dotted TOML key into its parts, bare or quoted, or null when it does not parse as one.</summary>
    private static List<string>? SplitKey(string inner)
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
