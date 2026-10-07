using System.Globalization;

namespace HousecarlCore;

/// <summary>The catalog-FREE extractor for the SKSE config audit, pure and line-local; contract in docs/architecture/skse-layer.md.</summary>
public static class SkseConfigReferenceExtractor
{
    // Characters a plugin name never holds; the name rule is in docs/architecture/skse-layer.md.
    const string NotNameChars = "|~\"=,:{}()[]/\\\r\n";

    /// <summary>Every form-shaped reference and path-segment gate a config declares — pure, and faithful: duplicates included.</summary>
    public static IReadOnlyList<SkseConfigRef> Extract(string relPath, string text)
    {
        var refs = new List<SkseConfigRef>();

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
            int line = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                line++;
                foreach (var t in ScanLine(raw))
                    refs.Add(BuildTokenRef(raw[t.Start..t.End], raw[t.NameStart..t.NameEnd].Trim().Trim('\''), raw[t.HexStart..t.HexEnd], line));
            }
        }
        return refs;
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
}

/// <summary>One reference a config file declares, pre-verdict; <see cref="Unparseable"/> carries the reason a matched token could not be normalized.</summary>
public sealed record SkseConfigRef(
    string Raw,
    SkseRefShape Shape,
    string Plugin,
    uint? LocalId,
    string? RawHex,
    int Line,
    string? Unparseable);
