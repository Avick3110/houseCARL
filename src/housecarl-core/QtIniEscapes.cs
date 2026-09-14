using System.Text;

namespace HousecarlCore;

// ======================================================================
//  QtIniEscapes — undo the escaping Qt's QSettings applies when it writes
//  an INI value, for the two MO2 files we read (ModOrganizer.ini and a
//  mod's meta.ini).
//
//  Qt writes any byte outside printable ASCII as \xHH, so a profile named
//  大肥鱼整合 lands on disk as
//      selected_profile=@ByteArray(\xe5\xa4\xa7\xe8\x82\xa5\xe9\xb1\xbc...)
//  and reading that literally gives a folder name that does not exist.
//
//  The grammar we undo:
//    • \xHH...  — hex, read GREEDILY (Qt emits no digit count). A run of
//      byte-sized values is UTF-8 bytes and decodes as one string; a value
//      above 0xFF is a single UTF-16 code unit (the old Qt5 form).
//    • the named escapes \\ \; \, \= \n \r \t \0.
//    • one surrounding pair of double quotes.
//  Any OTHER backslash is left exactly as it stands, so a value MO2 wrote
//  unescaped (a plain Windows path) is never mangled.
// ======================================================================

/// <summary>Undo Qt/QSettings INI escaping on one value.</summary>
internal static class QtIniEscapes
{
    /// <summary>Decode a QSettings-escaped value: <c>\xHH</c> runs (UTF-8 bytes; a value above 0xFF is a UTF-16 code
    /// unit), the named escapes <c>\\ \; \, \= \n \r \t \0</c>, and a surrounding pair of double quotes. An escape we
    /// don't know is left as written, so an unescaped backslash survives unchanged.</summary>
    public static string Unescape(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.IndexOf('\\') < 0) return value;

        var sb = new StringBuilder(value.Length);
        List<byte>? bytes = null;
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length) { Flush(); sb.Append(c); i++; continue; }

            char e = value[i + 1];
            if (e is 'x' or 'X')
            {
                int j = i + 2;
                while (j < value.Length && Uri.IsHexDigit(value[j])) j++;
                if (j > i + 2 && TryHex(value.AsSpan(i + 2, j - i - 2), out uint code))
                {
                    if (code <= 0xFF) (bytes ??= new List<byte>()).Add((byte)code);
                    else { Flush(); sb.Append((char)code); }
                    i = j;
                    continue;
                }
                Flush(); sb.Append(c).Append(e); i += 2; continue;   // "\x" with no usable hex: left as written
            }

            char? named = e switch
            {
                '\\' => '\\', ';' => ';', ',' => ',', '=' => '=',
                'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0',
                _ => null,
            };
            Flush();
            if (named is null) sb.Append(c).Append(e);               // an escape we don't know: left as written
            else sb.Append(named.Value);
            i += 2;
        }
        Flush();
        return sb.ToString();

        // Turn the pending \xHH byte run into text: UTF-8 when it decodes, byte-per-char when it doesn't.
        void Flush()
        {
            if (bytes is not { Count: > 0 }) return;
            var raw = bytes.ToArray();
            bytes.Clear();
            try { sb.Append(new UTF8Encoding(false, true).GetString(raw)); }
            catch (DecoderFallbackException) { foreach (var b in raw) sb.Append((char)b); }
        }
    }

    /// <summary>Parse a greedy hex run into a code point, or false when it is wider than a UTF-16 code unit (then the
    /// escape stays literal rather than silently decoding to something else).</summary>
    static bool TryHex(ReadOnlySpan<char> digits, out uint code)
    {
        code = 0;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) return true;                         // \x0, \x00 — a NUL byte
        if (digits.Length > 4) return false;
        foreach (var d in digits)
            code = (code << 4) + (uint)(d <= '9' ? d - '0' : (char.ToLowerInvariant(d) - 'a' + 10));
        return true;
    }
}
