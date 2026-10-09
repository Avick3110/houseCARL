using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>The catalog-FREE extractor for the SKSE config audit, pure; contract in docs/architecture/skse-layer.md.</summary>
public static class SkseConfigReferenceExtractor
{
    // Characters a plugin name never holds; the name rule is in docs/architecture/skse-layer.md.
    const string NotNameChars = "|~\"=,:{}()[]/\\\r\n";

    /// <summary>Every form-shaped reference and path-segment gate a config declares — pure, and faithful: duplicates included.</summary>
    public static IReadOnlyList<SkseConfigRef> Extract(string relPath, string text) => Extract(relPath, text, default, out _);

    /// <summary>As above, walking <paramref name="utf8"/> (the file's UTF-8 bytes past any BOM) as JSON when the caller holds them;
    /// <paramref name="multiLine"/> is true when the text's non-blank content spans more than one line.</summary>
    public static IReadOnlyList<SkseConfigRef> Extract(string relPath, string text, ReadOnlyMemory<byte> utf8, out bool multiLine)
    {
        var refs = new List<SkseConfigRef>();
        multiLine = false;

        // 1) Path-segment plugin gates: a DIRECTORY component that is a plugin filename, deduped per file.
        var segs = (relPath ?? "").Split('\\', '/');
        var seenGate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < segs.Length - 1; i++)   // exclude the last segment (the file itself)
        {
            var seg = segs[i];
            if (EndsInPlugin(seg) && seenGate.Add(seg))
                refs.Add(new SkseConfigRef(seg, SkseRefShape.PathSegmentGate, seg, null, null, 0, null));
        }

        // 2) Form-shaped tokens, one scan per physical line (references are line-local).
        if (!string.IsNullOrEmpty(text))
        {
            int line = 0, firstText = 0;
            foreach (var raw in Lines(text))
            {
                line++;
                if (!string.IsNullOrWhiteSpace(raw)) { if (firstText == 0) firstText = line; else multiLine = true; }
                foreach (var t in ScanLine(raw))
                    refs.Add(BuildTokenRef(raw[t.Start..t.End], raw[t.NameStart..t.NameEnd].Trim().Trim('\''), raw[t.HexStart..t.HexEnd], line));
            }
            // 3) A JSON file's {"id": <number>, "plugin": "<name>"} form objects, each with its JSON path.
            if ((relPath ?? "").EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ExtractFormObjects(utf8.IsEmpty ? Encoding.UTF8.GetBytes(text) : utf8.Span, refs);
        }
        return refs.OrderBy(r => r.Line).ToList();   // file order across both shapes; stable, so one line keeps its scan order
    }

    // A line ends at \n, at a lone \r, and once at \r\n: the one rule every line number and MultiLine here uses.
    static bool EndsLine(int c, int next) => c == '\n' || (c == '\r' && next != '\n');

    /// <summary>The text's physical lines, split by <see cref="EndsLine"/>, terminators dropped.</summary>
    static IEnumerable<string> Lines(string s)
    {
        int start = 0;
        for (int k = 0; k < s.Length; k++)
        {
            if (!EndsLine(s[k], k + 1 < s.Length ? s[k + 1] : -1)) continue;
            yield return s[start..(s[k] == '\n' && k > start && s[k - 1] == '\r' ? k - 1 : k)];
            start = k + 1;
        }
        yield return s[start..];
    }

    /// <summary>Advance <paramref name="line"/> over the bytes from <paramref name="counted"/> up to <paramref name="to"/>, by <see cref="EndsLine"/>.</summary>
    static void CountLines(ReadOnlySpan<byte> b, long to, ref long counted, ref int line)
    {
        for (; counted < to && counted < b.Length; counted++)
            if (EndsLine(b[(int)counted], counted + 1 < b.Length ? b[(int)counted + 1] : -1)) line++;
    }

    /// <summary>Walk a JSON document for form objects, in document order; a break with a "plugin" key after it is one named UNPARSEABLE reference.</summary>
    static void ExtractFormObjects(ReadOnlySpan<byte> bytes, List<SkseConfigRef> refs)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 512 });
        var open = new List<Frame>();   // the containers enclosing the reader, outermost first
        string? name = null;            // the property name the next value belongs to
        int line = 1; long counted = 0;
        try
        {
            while (reader.Read())
            {
                var tok = reader.TokenType;
                if (tok == JsonTokenType.PropertyName) { name = Text(ref reader); continue; }
                if (tok is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    var done = open[^1];
                    if (tok == JsonTokenType.EndObject && FormObjectRef(done, open) is { } r) refs.Add(r);
                    if (done.IdOf is { } owner) owner.Id = Encoding.UTF8.GetString(bytes[(int)done.Start..(int)reader.BytesConsumed]);
                    open.RemoveAt(open.Count - 1);
                    continue;
                }
                var parent = open.Count > 0 ? open[^1] : null;
                int index = parent is { IsArray: true } ? parent.Next++ : -1;
                bool isId = parent is { IsArray: false } && name == "id";
                if (isId && tok is not (JsonTokenType.StartObject or JsonTokenType.StartArray))   // a string id keeps the file's escaped text
                    parent!.Id = tok == JsonTokenType.String ? "\"" + Encoding.UTF8.GetString(reader.ValueSpan) + "\"" : Encoding.UTF8.GetString(reader.ValueSpan);
                else if (parent is { IsArray: false } && name == "plugin" && tok == JsonTokenType.String)
                    parent.Plugin = Text(ref reader);
                if (tok is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    CountLines(bytes, reader.TokenStartIndex, ref counted, ref line);
                    open.Add(new Frame { IsArray = tok == JsonTokenType.StartArray, Name = parent is { IsArray: true } ? null : name, Index = index,
                        Line = line, Start = reader.TokenStartIndex, IdOf = isId ? parent : null });
                }
                name = null;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)   // InvalidOperationException: a string that will not decode, such as "\uD800"
        {
            if (!HasPluginKey(bytes[(int)reader.BytesConsumed..])) return;   // named only when a form object can lie past the break
            long at = e is JsonException { LineNumber: { } ln, BytePositionInLine: { } bp } ? NewlineStart(bytes, ln) + bp : reader.TokenStartIndex;
            int breakLine = 1; long from = 0;
            CountLines(bytes, at, ref from, ref breakLine);
            refs.Add(new SkseConfigRef("(json)", SkseRefShape.FormObject, "", null, null, breakLine,
                "not valid JSON — form objects past this line are not read"));
        }
    }

    /// <summary>A string token's text; an unescaped one decodes as the file's text does, so a stray byte cannot break the walk.</summary>
    static string Text(ref Utf8JsonReader r) => r.ValueIsEscaped ? r.GetString()! : Encoding.UTF8.GetString(r.ValueSpan);

    /// <summary>The offset just past the <paramref name="n"/>th '\n', the line origin System.Text.Json counts from.</summary>
    static long NewlineStart(ReadOnlySpan<byte> b, long n)
    {
        long at = 0;
        for (; n > 0; n--) { int k = b[(int)at..].IndexOf((byte)'\n'); if (k < 0) return b.Length; at += k + 1; }
        return at;
    }

    /// <summary>True when an object key <c>"plugin"</c> lies in <paramref name="b"/>, outside comments and string values.</summary>
    static bool HasPluginKey(ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] == '/' && i + 1 < b.Length && b[i + 1] == '/') { while (i < b.Length && b[i] != '\n' && b[i] != '\r') i++; continue; }
            if (b[i] == '/' && i + 1 < b.Length && b[i + 1] == '*') { int end = b[(i + 2)..].IndexOf("*/"u8); if (end < 0) return false; i += end + 3; continue; }
            if (b[i] != '"') continue;
            int s = ++i;
            while (i < b.Length && b[i] != '"') i += b[i] == '\\' ? 2 : 1;
            if (i >= b.Length) return false;
            if (!b[s..i].SequenceEqual("plugin"u8)) continue;
            int k = i + 1;
            while (k < b.Length && b[k] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') k++;
            if (k < b.Length && b[k] == ':') return true;
        }
        return false;
    }

    /// <summary>One open JSON container: where it sits in its parent, and the "id"/"plugin" members it has held so far.</summary>
    sealed class Frame
    {
        public bool IsArray; public string? Name; public int Index; public int Next; public int Line; public long Start;
        public string? Id; public string? Plugin; public Frame? IdOf;   // IdOf: the object whose "id" this container is
    }

    /// <summary>The reference a closing object declares, or null when it is not a form object or is the empty <c>{"id":0}</c>.</summary>
    static SkseConfigRef? FormObjectRef(Frame o, List<Frame> open)
    {
        if (o.IsArray || o.Id is null || o.Plugin is null || o.Id == "0") return null;
        string raw = $"{{\"id\":{o.Id},\"plugin\":\"{o.Plugin}\"}}";
        string at = Locate(open);
        if (!uint.TryParse(o.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return new SkseConfigRef(raw, SkseRefShape.FormObject, o.Plugin, null, o.Id, o.Line,
                $"id {o.Id} is not a 32-bit decimal FormID — cannot normalize", at);
        return new SkseConfigRef(raw, SkseRefShape.FormObject, o.Plugin, FormIdRange.LocalObjectId(id), o.Id, o.Line, null, at);
    }

    /// <summary>The JSON path of the innermost open container: <c>.name</c>, <c>["odd name"]</c> or <c>[index]</c> per step.</summary>
    static string Locate(List<Frame> open)
    {
        var sb = new StringBuilder("$");
        foreach (var f in open.Skip(1))
        {
            if (f.Index >= 0) sb.Append('[').Append(f.Index).Append(']');
            else if (f.Name is { Length: > 0 } n && !char.IsAsciiDigit(n[0]) && n.All(c => IsAlnum(c) || c == '_')) sb.Append('.').Append(n);
            else sb.Append("[\"").Append((f.Name ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"]");
        }
        return sb.ToString();
    }

    readonly record struct Token(int Start, int End, int NameStart, int NameEnd, int HexStart, int HexEnd);

    /// <summary>Every token on one line, left to right: each '|' or '~' joined to a hex on one side and a plugin name on the other.</summary>
    static IEnumerable<Token> ScanLine(string s)
    {
        int floor = 0;   // end of the previous token
        int lead = 0;    // first non-blank character of the line
        while (lead < s.Length && char.IsWhiteSpace(s[lead])) lead++;
        for (int d = 0; d < s.Length; d++)
        {
            if (s[d] != '|' && s[d] != '~') continue;
            var a = HexFirst(s, d, floor, lead);
            var b = PluginFirst(s, d, floor, lead);
            var t = a is null ? b : b is null || a.Value.Start <= b.Value.Start ? a : b;
            if (t is null) continue;
            yield return t.Value;
            floor = t.Value.End;
        }
    }

    /// <summary>The hex-then-plugin token around delimiter <paramref name="d"/>, or null.</summary>
    static Token? HexFirst(string s, int d, int floor, int lead)
    {
        int hexEnd = d;
        while (hexEnd > floor && char.IsWhiteSpace(s[hexEnd - 1])) hexEnd--;
        int hexStart = hexEnd;
        while (hexStart > floor && IsHex(s[hexStart - 1])) hexStart--;
        if (hexStart == hexEnd) return null;
        if (hexStart - 2 >= floor && (s[hexStart - 1] | 0x20) == 'x' && s[hexStart - 2] == '0') hexStart -= 2;
        if (hexEnd - hexStart - (IsPrefixed(s, hexStart) ? 2 : 0) > 16 || (hexStart > 0 && IsAlnum(s[hexStart - 1]))) return null;

        int nameStart = d + 1;
        while (nameStart < s.Length && char.IsWhiteSpace(s[nameStart])) nameStart++;
        int lastGroupEnd = nameStart;
        for (int i = nameStart; i < s.Length;)
        {
            if (s[i] == '[' || s[i] == '(')
            {
                int close = MatchRight(s, i, lead);
                if (close < 0) return null;
                i = lastGroupEnd = close + 1;
                continue;
            }
            if (!IsNameChar(s, i, lead)) return null;
            i++;
            if (i - 4 >= lastGroupEnd && EndsInPluginExtension(s, i) && (i == s.Length || !IsAlnum(s[i])))
                return new Token(hexStart, i, nameStart, i, hexStart, hexEnd);
        }
        return null;
    }

    /// <summary>The plugin-then-hex token around delimiter <paramref name="d"/>, or null; the name runs left to the first boundary.</summary>
    static Token? PluginFirst(string s, int d, int floor, int lead)
    {
        int hexStart = d + 1;
        while (hexStart < s.Length && char.IsWhiteSpace(s[hexStart])) hexStart++;
        int digits = IsPrefixed(s, hexStart) ? hexStart + 2 : hexStart;
        int hexEnd = digits;
        while (hexEnd < s.Length && IsHex(s[hexEnd])) hexEnd++;
        if (hexEnd == digits || hexEnd - digits > 16 || (hexEnd < s.Length && IsAlnum(s[hexEnd]))) return null;

        int nameEnd = d;
        while (nameEnd > floor && char.IsWhiteSpace(s[nameEnd - 1])) nameEnd--;
        if (nameEnd - 4 < floor || !EndsInPluginExtension(s, nameEnd)) return null;
        int i = nameEnd - 5;
        while (i >= floor)
        {
            if (s[i] == ']' || s[i] == ')')
            {
                int open = MatchLeft(s, i, floor, lead);
                if (open < 0) break;
                i = open - 1;
            }
            else if (IsNameChar(s, i, lead)) i--;
            else break;
        }
        int start = i + 1;
        if (start > 0 && IsAlnum(s[start - 1]))   // the unit glued to the previous token cannot start a name
            start = s[start] == '[' || s[start] == '(' ? MatchRight(s, start, lead) + 1 : start + 1;
        if (start > nameEnd - 4) return null;
        return new Token(start, hexEnd, start, nameEnd, hexStart, hexEnd);
    }

    /// <summary>The closer that balances the opener at <paramref name="open"/>, holding only name characters and groups, or -1.</summary>
    static int MatchRight(string s, int open, int lead)
    {
        var want = new Stack<char>();
        for (int k = open; k < s.Length; k++)
        {
            char c = s[k];
            if (c == '[' || c == '(') want.Push(c == '[' ? ']' : ')');
            else if (c == ']' || c == ')') { if (want.Pop() != c) return -1; if (want.Count == 0) return k; }
            else if (!IsNameChar(s, k, lead)) return -1;
        }
        return -1;
    }

    /// <summary>The opener that balances the closer at <paramref name="close"/>, not before <paramref name="floor"/>, or -1.</summary>
    static int MatchLeft(string s, int close, int floor, int lead)
    {
        var want = new Stack<char>();
        for (int k = close; k >= floor; k--)
        {
            char c = s[k];
            if (c == ']' || c == ')') want.Push(c == ']' ? '[' : '(');
            else if (c == '[' || c == '(') { if (want.Pop() != c) return -1; if (want.Count == 0) return k; }
            else if (!IsNameChar(s, k, lead)) return -1;
        }
        return -1;
    }

    /// <summary>A character a plugin name can hold here; a ';' or '#' that opens a comment cannot.</summary>
    static bool IsNameChar(string s, int i, int lead)
    {
        char c = s[i];
        if (NotNameChars.IndexOf(c) >= 0) return false;
        if (c != ';' && c != '#') return true;
        return !(i == lead || i + 1 == s.Length || char.IsWhiteSpace(s[i + 1]));
    }

    static bool EndsInPluginExtension(string s, int end) =>
        end >= 4 && s[end - 4] == '.' && (s[end - 3] | 0x20) == 'e' && (s[end - 2] | 0x20) == 's' && "lmp".IndexOf((char)(s[end - 1] | 0x20)) >= 0;

    static bool IsPrefixed(string s, int i) => i + 2 < s.Length && s[i] == '0' && (s[i + 1] | 0x20) == 'x' && IsHex(s[i + 2]);

    static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    static bool IsAlnum(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>Normalize one matched token — an over-wide or unparseable hex is named LOUDLY, never guessed.</summary>
    static SkseConfigRef BuildTokenRef(string rawMatch, string plugin, string rawHex, int line)
    {
        string digits = rawHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rawHex[2..] : rawHex;
        if (digits.Length == 0 || digits.Length > 8 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var runtimeId))
            return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, null, rawHex, line,
                $"'{rawHex}' is not a 32-bit FormID (needs 1–8 hex digits) — cannot normalize");
        return new SkseConfigRef(rawMatch, SkseRefShape.FormToken, plugin, FormIdRange.LocalObjectId(runtimeId), rawHex, line, null);
    }

    static bool EndsInPlugin(string s) =>
        s.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
        s.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);
}

public enum SkseRefShape
{
    /// <summary>A hex FormID paired with a plugin filename (<c>0xHEX|Plugin.esp</c> / <c>Plugin.esp|0xHEX</c> / tilde form).</summary>
    FormToken,
    /// <summary>A directory component that is a plugin filename — gates the whole file on that plugin's presence.</summary>
    PathSegmentGate,
    /// <summary>A JSON object <c>{"id": &lt;decimal&gt;, "plugin": "Plugin.esp"}</c>, the form shape IED writes; located by its JSON path.</summary>
    FormObject,
}

/// <summary>One reference a config file declares, pre-verdict; <see cref="Unparseable"/> carries the reason a matched token could not be normalized; <see cref="Locator"/> is a form object's JSON path.</summary>
public sealed record SkseConfigRef(
    string Raw,
    SkseRefShape Shape,
    string Plugin,
    uint? LocalId,
    string? RawHex,
    int Line,
    string? Unparseable,
    string? Locator = null);
