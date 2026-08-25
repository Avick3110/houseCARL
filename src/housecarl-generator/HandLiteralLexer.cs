using System.Text;

namespace HousecarlGenerator;

/// <summary>
/// READER B — a second, independent statement of "what string literals does this C# file contain", written from
/// the language's lexical grammar rather than derived from <see cref="RoslynLiteralReader"/>.
///
/// <para><b>Why a second reader exists at all.</b> <c>description-vocab-guard</c> claims its net is every shipped
/// literal. Both earlier designs of that guard shipped a completeness ARM whose oracle came out of the same
/// machinery it was certifying, and both were measured green over prose they could not see (#386, two §4
/// class-stops). The rule that came out of it: a completeness claim needs a SECOND SPELLING. So this file exists
/// to disagree with Roslyn — <c>INV6-AGREE</c> holds the two literal sets against each other per file, and a
/// reader that stops early, mis-decodes an escape, or misses a literal inside an interpolation hole turns that arm
/// red with the file named.</para>
///
/// <para><b>What "independent" means here, precisely.</b> No code is shared with reader A beyond
/// <see cref="SourceLiteral"/>, which is a record — a data shape, carrying no opinion about what a literal is.
/// This lexer is written against the C# lexical grammar directly: the four regular literal flavours (plain,
/// verbatim <c>@</c>, interpolated <c>$</c>, and both together in either order), raw string literals of any quote
/// count, the <c>u8</c> suffix, backslash escape decoding including the numeric forms, and — the shape the second
/// design was class-stopped for — RECURSION INTO INTERPOLATION HOLES, so a literal in a ternary arm inside
/// <c>$"…{c ? "a" : "b"}…"</c> is read at depth 1 rather than being invisible.</para>
///
/// <para><b>Escape decoding is a correctness surface, not a detail.</b> <c>—</c> is how an em dash reaches a
/// tool description, and a reader that decoded it as the letter <c>u</c> would hold a different string from the
/// one the compiler builds. <c>\uD83D</c> is a LONE SURROGATE, which C# permits in a string literal — the second
/// design fed it to <c>char.ConvertFromUtf32</c>, which throws, and the throw collapsed all 38 arms of the guard
/// while naming no file. <c>\u</c> and <c>\x</c> therefore produce one UTF-16 code unit each, exactly as the
/// compiler does; only <c>\U</c> composes a surrogate pair.</para>
/// </summary>
public static class HandLiteralLexer
{
    /// <summary>Every string literal in <paramref name="src"/>, decoded, with hole depth and source span.</summary>
    public static List<SourceLiteral> Read(string src)
    {
        var found = new List<SourceLiteral>();
        Scan(src, 0, src.Length, 0, found);
        var newlines = NewlineOffsets(src);
        return found
            .Select(l => l with { Line = LineOf(newlines, l.Start) })
            .OrderBy(l => l.Start).ThenBy(l => l.Depth).ToList();
    }

    // ---- the scanner: skip everything that is not a literal, lex everything that is ----

    /// <summary>Walk <c>[from, to)</c> emitting every literal at <paramref name="depth"/>. Comments and character
    /// literals are skipped rather than read — a comment is not a literal, which is where the guard's declared
    /// docstring boundary is enforced, and a character literal holding a quote would otherwise open a string that
    /// never closes.</summary>
    static void Scan(string src, int from, int to, int depth, List<SourceLiteral> outp)
    {
        int i = from;
        while (i < to)
        {
            char c = src[i];
            if (c == '/' && i + 1 < to && src[i + 1] == '/') { while (i < to && src[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < to && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < to && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i = Math.Min(to, i + 2);
                continue;
            }
            if (c == '\'') { i = SkipCharLiteral(src, i, to); continue; }
            if (c == '"' || c == '$' || c == '@')
            {
                int prefixEnd = i, dollars = 0;
                bool verbatim = false;
                while (prefixEnd < to && (src[prefixEnd] == '$' || src[prefixEnd] == '@'))
                {
                    if (src[prefixEnd] == '$') dollars++; else verbatim = true;
                    prefixEnd++;
                }
                if (prefixEnd < to && src[prefixEnd] == '"')
                {
                    i = LexLiteral(src, i, prefixEnd, to, dollars, verbatim, depth, outp);
                    continue;
                }
                // An identifier escape (a keyword used as a name) is not a literal — step past the prefix so the
                // identifier's letters are not re-examined as another possible one.
                if (prefixEnd > i) { i = prefixEnd; continue; }
            }
            i++;
        }
    }

    /// <summary>A character literal, escapes included. Skipped whole.</summary>
    static int SkipCharLiteral(string src, int at, int to)
    {
        int i = at + 1;
        while (i < to && src[i] != '\'')
        {
            if (src[i] == '\\') i++;
            if (i < to && src[i] == '\n') return i;   // unterminated: do not run off the end of the file
            i++;
        }
        return Math.Min(to, i + 1);
    }

    // ---- the literal forms ----

    /// <summary>Lex one literal whose quote starts at <paramref name="quote"/>, emit it (and anything inside its
    /// holes) into <paramref name="outp"/>, and return the index just past it.</summary>
    static int LexLiteral(string src, int start, int quote, int to, int dollars, bool verbatim, int depth, List<SourceLiteral> outp)
    {
        int quoteRun = RunLength(src, quote, to, '"');
        var (text, end) = quoteRun >= 3
            ? LexRaw(src, quote, quoteRun, to, dollars, depth, outp)
            : LexRegular(src, quote, to, dollars > 0, verbatim, depth, outp);
        // A u8 suffix changes the literal's TYPE, not its text.
        if (end + 1 < to && src[end] == 'u' && src[end + 1] == '8') end += 2;
        outp.Add(new SourceLiteral(0, depth, text, start, end));
        return end;
    }

    /// <summary>Plain, verbatim, interpolated, and verbatim-interpolated in either prefix order. Returns the
    /// decoded text and the index just past the closing quote.</summary>
    static (string Text, int End) LexRegular(string src, int quote, int to, bool interpolated, bool verbatim, int depth, List<SourceLiteral> outp)
    {
        var sb = new StringBuilder();
        int j = quote + 1;
        while (j < to)
        {
            char c = src[j];
            if (verbatim)
            {
                // In a verbatim literal the ONLY escape is a doubled quote; a backslash is an ordinary character.
                if (c == '"')
                {
                    if (j + 1 < to && src[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                    j++; break;
                }
            }
            else
            {
                if (c == '\\') { j = Unescape(src, j, to, sb); continue; }
                if (c == '"') { j++; break; }
                // An unterminated literal is a compile error the parse arm reports; stop at the line end rather
                // than consuming the rest of the file, which is how the previous design lost whole files.
                if (c == '\n') { j++; break; }
            }
            if (interpolated && c == '{')
            {
                if (j + 1 < to && src[j + 1] == '{') { sb.Append('{'); j += 2; continue; }
                int holeEnd = FindHoleEnd(src, j + 1, to);
                Scan(src, j + 1, holeEnd, depth + 1, outp);
                sb.Append(SourceLiteral.HoleMarker);
                j = Math.Min(to, holeEnd + 1);
                continue;
            }
            if (interpolated && c == '}' && j + 1 < to && src[j + 1] == '}') { sb.Append('}'); j += 2; continue; }
            sb.Append(c); j++;
        }
        return (sb.ToString(), j);
    }

    /// <summary>A raw string literal — three quotes or more. No escapes at all: the content is exactly what is
    /// written, minus the indentation rule. With <paramref name="dollars"/> greater than zero, a hole is opened by
    /// that many braces in a row and closed by the same count, so fewer braces are plain text.
    /// <para>A run LONGER than the opener count is a brace of content followed by the opener, not a wider opener:
    /// the extra braces are emitted as text and only the last <paramref name="dollars"/> of the run start the
    /// hole. Taking the whole run as the opener drops those characters, and because a brace is not a letter the
    /// loss is invisible to the phrase check — it surfaces only as an INV6-AGREE disagreement, which is exactly
    /// how it was found.</para></summary>
    static (string Text, int End) LexRaw(string src, int quote, int quoteRun, int to, int dollars, int depth, List<SourceLiteral> outp)
    {
        int j = quote + quoteRun, close = -1;
        var sb = new StringBuilder();
        while (j < to)
        {
            if (dollars > 0 && src[j] == '{' && RunLength(src, j, to, '{') >= dollars)
            {
                int open = RunLength(src, j, to, '{');
                sb.Append('{', open - dollars);          // the surplus leading braces are content, not opener
                int holeStart = j + open;
                int holeEnd = FindHoleEnd(src, holeStart, to);
                Scan(src, holeStart, holeEnd, depth + 1, outp);
                sb.Append(SourceLiteral.HoleMarker);
                j = Math.Min(to, holeEnd + dollars);
                continue;
            }
            if (src[j] == '"')
            {
                int run = RunLength(src, j, to, '"');
                // The terminator is a run of EXACTLY the opening count; a longer run means quotes are content.
                if (run == quoteRun) { close = j; break; }
                sb.Append(src, j, run); j += run; continue;
            }
            sb.Append(src[j]); j++;
        }
        if (close < 0) return (sb.ToString(), to);
        return (StripRawIndent(sb.ToString()), close + quoteRun);
    }

    static int RunLength(string src, int at, int to, char ch)
    {
        int n = 0;
        while (at + n < to && src[at + n] == ch) n++;
        return n;
    }

    /// <summary>The multi-line raw-string rule: the opening quotes are followed by nothing but a line break, the
    /// closing quotes sit on their own line, and THAT line's whitespace is removed from every content line — which
    /// is why an indented raw fixture holds the string its author meant rather than the source's indentation. A
    /// single-line raw literal has no such rule and is taken as written.
    /// <para>The line TERMINATORS the source used are kept, not normalized — the compiler keeps them, so a reader
    /// that normalized would hold a different string from the one the program prints, and the two readers would
    /// disagree about every multi-line raw literal in a CRLF checkout. That matters here specifically: this repo
    /// is developed on Windows with git's CRLF conversion on, so the same committed file is LF in one working tree
    /// and CRLF in another. Which is why the terminators are captured and replayed rather than assumed to be
    /// <c>\n</c>.</para></summary>
    static string StripRawIndent(string content)
    {
        // Odd indices are the captured terminators, even indices the lines between them.
        var parts = System.Text.RegularExpressions.Regex.Split(content, "(\r\n|\n|\r)");
        if (parts.Length < 5) return content;                           // fewer than two terminators: not the multi-line form
        string indent = parts[^1];
        if (indent.Trim().Length != 0) return content;                  // closing quotes are not alone on their line
        if (parts[0].Trim().Length != 0) return content;                // opening quotes are not alone on theirs
        var sb = new StringBuilder();
        for (int i = 2; i <= parts.Length - 3; i += 2)
        {
            var line = parts[i];
            sb.Append(line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line.TrimStart());
            // The terminator BEFORE the closing-quote line belongs to the delimiter, not to the content.
            if (i + 1 <= parts.Length - 4) sb.Append(parts[i + 1]);
        }
        return sb.ToString();
    }

    /// <summary>The index of the brace that closes a hole opened just before <paramref name="from"/>. Nested
    /// braces, strings, character literals and comments inside the expression are stepped over, so a closing brace
    /// inside a nested literal does not end the hole early.</summary>
    static int FindHoleEnd(string src, int from, int to)
    {
        int i = from, nest = 0;
        var sink = new List<SourceLiteral>();
        while (i < to)
        {
            char c = src[i];
            if (c == '/' && i + 1 < to && src[i + 1] == '/') { while (i < to && src[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < to && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < to && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i = Math.Min(to, i + 2);
                continue;
            }
            if (c == '\'') { i = SkipCharLiteral(src, i, to); continue; }
            if (c == '"' || c == '$' || c == '@')
            {
                int p = i, dollars = 0;
                bool verbatim = false;
                while (p < to && (src[p] == '$' || src[p] == '@')) { if (src[p] == '$') dollars++; else verbatim = true; p++; }
                if (p < to && src[p] == '"')
                {
                    sink.Clear();
                    i = LexLiteral(src, i, p, to, dollars, verbatim, 0, sink);
                    continue;
                }
                if (p > i) { i = p; continue; }
            }
            if (c == '{') { nest++; i++; continue; }
            if (c == '}')
            {
                if (nest == 0) return i;
                nest--; i++; continue;
            }
            i++;
        }
        return to;
    }

    /// <summary>Decode one backslash escape at <paramref name="at"/>; return the index just past it. The numeric
    /// forms yield ONE UTF-16 code unit each — which is what makes a lone surrogate decode rather than throw — and
    /// only the eight-digit form composes a pair.</summary>
    static int Unescape(string src, int at, int to, StringBuilder sb)
    {
        char next = at + 1 < to ? src[at + 1] : '\0';
        if (next is 'u' or 'x' or 'U')
        {
            int want = next == 'U' ? 8 : 4;                      // the 'x' form takes 1..4 hex digits
            int k = at + 2, taken = 0;
            long value = 0;
            while (k < to && taken < want && Uri.IsHexDigit(src[k])) { value = value * 16 + Convert.ToInt32(src[k].ToString(), 16); k++; taken++; }
            bool complete = next == 'x' ? taken > 0 : taken == want;
            if (complete)
            {
                if (next == 'U')
                {
                    // A value outside the Unicode range is a compile error the parse arm reports; append nothing
                    // rather than throw, so one malformed escape cannot take the whole run down with it.
                    if (value <= 0x10FFFF && !(value >= 0xD800 && value <= 0xDFFF)) sb.Append(char.ConvertFromUtf32((int)value));
                }
                else sb.Append((char)value);
                return k;
            }
        }
        sb.Append(next switch
        {
            'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0',
            'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v',
            _ => next,
        });
        return at + 2;
    }

    // ---- offsets to lines ----

    static int[] NewlineOffsets(string src)
    {
        var outp = new List<int>();
        for (int i = 0; i < src.Length; i++) if (src[i] == '\n') outp.Add(i);
        return outp.ToArray();
    }

    /// <summary>1-based line for a source offset. Derived from the newline index rather than counted during the
    /// scan, because a scan that recurses into holes visits offsets out of order and a running counter would
    /// silently report the wrong line.</summary>
    static int LineOf(int[] newlines, int offset)
    {
        int lo = Array.BinarySearch(newlines, offset);
        return (lo >= 0 ? lo : ~lo) + 1;
    }
}
