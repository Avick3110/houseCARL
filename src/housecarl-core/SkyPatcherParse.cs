using System.Globalization;

namespace HousecarlCore;

/// <summary>Structural tokenizer for one SkyPatcher patch line — grammar only, no key semantics; the productions, the address boundary and the delimiter limitation are in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherParse
{
    /// <summary>The shipped delimiters, from SkyPatcher's row of the runtime-format source; a parse given another row's grammar uses that.</summary>
    static readonly SkyPatcherGrammar G = RuntimeFormats.SkyPatcher.Grammar;

    /// <summary>Parse one physical line with the shipped delimiters.</summary>
    public static SkyPatcherLine ParseLine(string raw) => ParseLine(raw, G);

    /// <summary>Parse one physical line with <paramref name="g"/>'s delimiters. Never throws — a no-'=' or empty-key segment is still surfaced carrying a loud Note, while an empty ':'-segment is noted and skipped.</summary>
    public static SkyPatcherLine ParseLine(string raw, SkyPatcherGrammar g)
    {
        raw ??= "";
        // A stray U+FEFF is whitespace anywhere in the line; it is never a legal key/value character.
        var trimmed = raw.Replace('\uFEFF', ' ').Trim();

        if (trimmed.Length == 0)
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Blank, Array.Empty<SkyPatcherSegment>(), null);

        if (trimmed[0] == g.Comment || g.CommentLeads.Contains(trimmed[0]))
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Comment, Array.Empty<SkyPatcherSegment>(), null);

        if (g.LabelLines && trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Label, Array.Empty<SkyPatcherSegment>(), null);

        // Since 8.0.1 a comment may follow a rule: the text from the first ';' outside a ~name~ literal is dropped.
        if (g.TrailingComment) trimmed = StripTrailingComment(trimmed, g);

        var segments = new List<SkyPatcherSegment>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? note = null;

        foreach (var rawSeg in trimmed.Split(g.Segment))
        {
            var seg = rawSeg.Trim();
            if (seg.Length == 0)
            {
                note = Append(note, $"empty '{g.Segment}'-segment (stray or doubled '{g.Segment}')");
                continue;
            }

            int eq = seg.IndexOf(g.Assign);
            if (eq < 0)
            {
                note = Append(note, $"segment '{seg}' has no '{g.Assign}' (not key{g.Assign}value)");
                segments.Add(new SkyPatcherSegment(seg, null, Array.Empty<SkyPatcherValue>()));
                continue;
            }

            var key = seg[..eq].Trim();
            var rawValue = seg[(eq + 1)..].Trim();   // split on the FIRST '=' only — a value may legitimately contain '=' (e.g. faction=rank)
            if (key.Length == 0)
            {
                note = Append(note, $"segment '{seg}' has an empty key");
                segments.Add(new SkyPatcherSegment(key, rawValue, Array.Empty<SkyPatcherValue>()));
                continue;
            }

            // SkyPatcher ignores a second copy of a key on one line.
            if (!keys.Add(key))
            {
                note = Append(note, $"key '{key}' appears twice; SkyPatcher ignores the second copy ('{seg}')");
                continue;
            }
            segments.Add(new SkyPatcherSegment(key, rawValue, ParseValueList(rawValue, g, ref note)));
        }

        return new SkyPatcherLine(raw, SkyPatcherLineKind.Patch, segments, note);
    }

    /// <summary>The line up to its first ';' that is not inside a <c>=~…~</c> name literal, trimmed; a compound's '~' never opens one.</summary>
    static string StripTrailingComment(string line, SkyPatcherGrammar g)
    {
        bool inName = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == g.Compound)
            {
                if (inName) inName = false;
                else if (line[..i].TrimEnd() is { Length: > 0 } before && before[^1] == g.Assign) inName = true;
            }
            else if (line[i] == g.Comment && !inName) return line[..i].TrimEnd();
        }
        return line;
    }

    /// <summary>Parse a whole INI file's text into its lines (blank/comment/patch), in order.</summary>
    public static IReadOnlyList<SkyPatcherLine> ParseFile(string text, SkyPatcherGrammar? grammar = null)
    {
        var lines = new List<SkyPatcherLine>();
        if (string.IsNullOrEmpty(text)) return lines;
        // A UTF-8 BOM decoded into the text is stripped at position 0 only.
        if (text[0] == '\uFEFF') text = text[1..];
        // Empties are kept so line indices track the source file.
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(ParseLine(raw, grammar ?? G));
        return lines;
    }

    /// <summary>Comma-split a segment's raw value into items; an empty item is skipped with a loud note.</summary>
    static IReadOnlyList<SkyPatcherValue> ParseValueList(string rawValue, SkyPatcherGrammar g, ref string? note)
    {
        if (rawValue.Length == 0) return Array.Empty<SkyPatcherValue>();
        var items = new List<SkyPatcherValue>();
        foreach (var rawItem in rawValue.Split(g.Item))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                note = Append(note, $"empty '{g.Item}'-item in '{rawValue}' (stray or doubled '{g.Item}')");
                continue;
            }
            items.Add(ParseValue(item, g));
        }
        return items;
    }

    /// <summary>Parse one comma-item: a <c>~…~</c> rename name-literal, or a <c>~</c>-packed compound whose first sub-arg is address-parsed.</summary>
    static SkyPatcherValue ParseValue(string item, SkyPatcherGrammar g)
    {
        if (item.Length >= 2 && item[0] == g.Compound && item[^1] == g.Compound)
        {
            var name = item[1..^1];
            return new SkyPatcherValue(item, IsNameLiteral: true, NameText: name,
                SubArgs: new[] { item }, Address: null);
        }

        var subArgs = item.Split(g.Compound);
        var address = TryParseAddress(subArgs[0], g);
        return new SkyPatcherValue(item, IsNameLiteral: false, NameText: null, SubArgs: subArgs, Address: address);
    }

    /// <summary>A token holding the grammar's address separator as SkyPatcher reads it: <c>Plugin|FormID</c>, never an EditorID; null for a token without one. Read through the one runtime-layer resolver, on SkyPatcher's own spelling with this grammar's address character.</summary>
    public static FormAddress? TryParseAddress(string token, SkyPatcherGrammar? grammar = null)
        => RuntimeTargets.TryParseAddress(token, RuntimeFormats.SkyPatcher.Targets with { Separator = (grammar ?? G).Address });

    static string Append(string? note, string add) => note is null ? add : note + "; " + add;
}

/// <summary>What a physical INI line is.</summary>
public enum SkyPatcherLineKind
{
    /// <summary>Whitespace-only.</summary>
    Blank,
    /// <summary>A ';'- or '#'-led comment line.</summary>
    Comment,
    /// <summary>A patch line (one or more key=value segments).</summary>
    Patch,
    /// <summary>A whole-line '[' … ']' label — inert: no segments, no note, skipped by every consumer.</summary>
    Label,
}

/// <summary>One parsed SkyPatcher line. <see cref="Note"/> carries any loud parse warning.</summary>
public sealed record SkyPatcherLine(
    string Raw,
    SkyPatcherLineKind Kind,
    IReadOnlyList<SkyPatcherSegment> Segments,
    string? Note);

/// <summary>One <c>key=value</c> segment; <see cref="RawValue"/> is null only for a malformed no-'=' segment.</summary>
public sealed record SkyPatcherSegment(
    string Key,
    string? RawValue,
    IReadOnlyList<SkyPatcherValue> Values);

/// <summary>One comma-item of a segment's value: a rename name-literal or a <c>~</c>-packed compound, with the first sub-arg's FormID address when it has one.</summary>
public sealed record SkyPatcherValue(
    string Raw,
    bool IsNameLiteral,
    string? NameText,
    IReadOnlyList<string> SubArgs,
    FormAddress? Address);

/// <summary>A resolved form address; the tokenizer produces only the '|' form, and <see cref="EditorId"/> is the overlay engine's to fill.</summary>
public sealed record FormAddress(string? Plugin, uint? FormId, string? EditorId)
{
    /// <summary>True when this is the <c>Plugin.esp|FormID</c> form.</summary>
    public bool IsFormId => Plugin is not null && FormId is not null;
    /// <summary>True for a '|' token whose FormID side SkyPatcher cannot read: it names nothing, and is never an EditorID.</summary>
    public bool IsMalformed => Plugin is not null && FormId is null;
    /// <summary>True when this addresses a form by EditorID.</summary>
    public bool IsEditorId => EditorId is not null;
}
