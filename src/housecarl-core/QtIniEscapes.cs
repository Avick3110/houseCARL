using System.Text;

namespace HousecarlCore;

// ======================================================================
//  QtIniEscapes — read one value out of a Qt/QSettings INI the way Qt
//  wrote it, for the two MO2 files we read (ModOrganizer.ini and a mod's
//  meta.ini).
//
//  Qt writes any byte outside printable ASCII as \xHH, so a profile named
//  大肥鱼整合 lands on disk as
//      selected_profile=@ByteArray(\xe5\xa4\xa7\xe8\x82\xa5\xe9\xb1\xbc...)
//  and reading that literally gives a folder name that does not exist.
//
//  One value is read in four steps, in Qt's own order (Clean does all four):
//    1. the raw line is trimmed;
//    2. a surrounding pair of double quotes comes off FIRST — Qt quotes the
//       whole serialized string, @ByteArray( prefix included, when the value
//       holds ';' ',' '=' or a leading/trailing space:
//           selected_profile="@ByteArray(Requiem, AE)";
//    3. the @ByteArray(...) wrapper comes off (a plain QString value has none),
//       and @Invalid() means unset;
//    4. the escaping is undone.
//
//  The escape grammar, as QSettingsPrivate::iniEscapedString writes it:
//    • \xHH... — hex, read GREEDILY (Qt emits no digit count). That is safe
//      because Qt escapes a hex digit that FOLLOWS a \xHH too, so a run never
//      runs into ordinary text. A run of byte-sized values is UTF-8 bytes and
//      decodes as one string; a value above 0xFF is a single UTF-16 code unit
//      (the old Qt5 form).
//    • the named escapes \\ \" \; \, \= \0 \a \b \f \n \r \t \v.
//
//  Qt doubles EVERY backslash, so a Qt-written value never carries a lone one.
//  A value that does carry one was not written by Qt (hand-edited, or an
//  installer that writes the file itself), and its backslashes are literal
//  path separators: the whole value is then left exactly as it stands rather
//  than half-decoded. That is decided once per value, on whether every
//  backslash in it begins a known escape — never per escape, so 'D:\newgame'
//  cannot lose its 'n' to a value that is plainly not Qt's.
// ======================================================================

/// <summary>Read one Qt/QSettings INI value: quotes, the <c>@ByteArray(...)</c> wrapper, and Qt's escaping.</summary>
internal static class QtIniEscapes
{
    /// <summary>The whole read for one raw <c>key=</c> value: trim the line, drop a surrounding quote pair, unwrap
    /// <c>@ByteArray(...)</c>, treat <c>@Invalid()</c> as unset, and unescape. Null when the value is unset or empty.
    /// The trim is on the RAW line only — a leading space Qt preserved inside quotes survives.</summary>
    public static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0) return null;
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        if (v.Length == 0 || v.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase)) return null;
        const string wrap = "@ByteArray(";
        if (v.StartsWith(wrap, StringComparison.Ordinal) && v.EndsWith(")", StringComparison.Ordinal))
            v = v[wrap.Length..^1];
        v = Unescape(v);
        return v.Length == 0 ? null : v;
    }

    /// <summary>Undo Qt's escaping on an already-unwrapped value: <c>\xHH</c> runs (UTF-8 bytes; a value above 0xFF is
    /// a UTF-16 code unit) and the named escapes <c>\\ \" \; \, \= \0 \a \b \f \n \r \t \v</c>. A value holding a
    /// backslash that does not begin one of those was not written by Qt, and comes back unchanged.</summary>
    public static string Unescape(string value)
    {
        if (!IsQtEscaped(value)) return value;

        var sb = new StringBuilder(value.Length);
        List<byte>? bytes = null;
        int i = 0;
        while (i < value.Length)
        {
            char c = value[i];
            if (c != '\\') { Flush(); sb.Append(c); i++; continue; }

            char e = value[i + 1];
            if (e is 'x' or 'X')
            {
                int j = i + 2;
                while (j < value.Length && Uri.IsHexDigit(value[j])) j++;
                TryHex(value.AsSpan(i + 2, j - i - 2), out uint code);
                if (code <= 0xFF) (bytes ??= new List<byte>()).Add((byte)code);
                else { Flush(); sb.Append((char)code); }
                i = j;
                continue;
            }

            Flush();
            sb.Append(Named(e)!.Value);
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

    /// <summary>True when every backslash in the value begins an escape Qt writes — i.e. the value could have come out
    /// of Qt's writer. False for a lone backslash (a literal path separator someone typed).</summary>
    static bool IsQtEscaped(string value)
    {
        int i = value.IndexOf('\\');
        if (i < 0) return false;                                     // nothing to undo
        for (; i < value.Length; i++)
        {
            if (value[i] != '\\') continue;
            if (i + 1 >= value.Length) return false;                 // trailing lone backslash
            char e = value[i + 1];
            if (e is 'x' or 'X')
            {
                int j = i + 2;
                while (j < value.Length && Uri.IsHexDigit(value[j])) j++;
                if (j == i + 2 || !TryHex(value.AsSpan(i + 2, j - i - 2), out _)) return false;
                i = j - 1;
                continue;
            }
            if (Named(e) is null) return false;
            i++;
        }
        return true;
    }

    /// <summary>The character a named escape stands for, or null when Qt writes no such escape.</summary>
    static char? Named(char e) => e switch
    {
        '\\' => '\\', '"' => '"', ';' => ';', ',' => ',', '=' => '=',
        '0' => '\0', 'a' => '\a', 'b' => '\b', 'f' => '\f',
        'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v',
        _ => null,
    };

    /// <summary>Parse a greedy hex run into a code point, or false when it is wider than a UTF-16 code unit (then the
    /// value is not Qt's and is left alone rather than silently decoding to something else).</summary>
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
