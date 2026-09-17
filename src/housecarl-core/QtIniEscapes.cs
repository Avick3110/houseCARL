using System.Text;

namespace HousecarlCore;

// Read one value out of a Qt/QSettings INI the way Qt wrote it; the four read steps and the escape grammar are in docs/architecture/mo2-instance.md.

/// <summary>Read one Qt/QSettings INI value: quotes, the <c>@ByteArray(...)</c> wrapper, and Qt's escaping.</summary>
internal static class QtIniEscapes
{
    /// <summary>The whole read for one raw <c>key=</c> value: trim, drop a surrounding quote pair, unwrap <c>@ByteArray(...)</c>, treat <c>@Invalid()</c> as unset, and unescape; null when the value is unset or empty.</summary>
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

    /// <summary>Undo Qt's escaping on an already-unwrapped value; a value holding a backslash that begins none of Qt's escapes was not written by Qt and comes back unchanged.</summary>
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

    /// <summary>True when every backslash in the value begins an escape Qt writes — i.e. the value could have come out of Qt's writer.</summary>
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

    /// <summary>Parse a greedy hex run into a code point, or false when it is wider than a UTF-16 code unit and the value is therefore not Qt's.</summary>
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
