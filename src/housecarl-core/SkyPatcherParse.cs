using System.Globalization;

namespace HousecarlCore;

/// <summary>Structural tokenizer for one SkyPatcher patch line — grammar only, no key semantics; the productions, the address boundary and the delimiter limitation are in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherParse
{
    /// <summary>Parse one physical line. Never throws — a malformed line, and each malformed segment, is still surfaced, carrying a loud Note.</summary>
    public static SkyPatcherLine ParseLine(string raw)
    {
        raw ??= "";
        // A stray U+FEFF is whitespace anywhere in the line; it is never a legal key/value character.
        var trimmed = raw.Replace('\uFEFF', ' ').Trim();

        if (trimmed.Length == 0)
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Blank, Array.Empty<SkyPatcherSegment>(), null);

        if (trimmed[0] == ';')
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Comment, Array.Empty<SkyPatcherSegment>(), null);

        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Label, Array.Empty<SkyPatcherSegment>(), null);

        var segments = new List<SkyPatcherSegment>();
        string? note = null;

        foreach (var rawSeg in trimmed.Split(':'))
        {
            var seg = rawSeg.Trim();
            if (seg.Length == 0)
            {
                note = Append(note, "empty ':'-segment (stray or doubled ':')");
                continue;
            }

            int eq = seg.IndexOf('=');
            if (eq < 0)
            {
                note = Append(note, $"segment '{seg}' has no '=' (not key=value)");
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

            segments.Add(new SkyPatcherSegment(key, rawValue, ParseValueList(rawValue, ref note)));
        }

        return new SkyPatcherLine(raw, SkyPatcherLineKind.Patch, segments, note);
    }

    /// <summary>Parse a whole INI file's text into its lines (blank/comment/patch), in order.</summary>
    public static IReadOnlyList<SkyPatcherLine> ParseFile(string text)
    {
        var lines = new List<SkyPatcherLine>();
        if (string.IsNullOrEmpty(text)) return lines;
        // A UTF-8 BOM decoded into the text is stripped at position 0 only.
        if (text[0] == '\uFEFF') text = text[1..];
        // Empties are kept so line indices track the source file.
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(ParseLine(raw));
        return lines;
    }

    /// <summary>Comma-split a segment's raw value into items; an empty item is skipped with a loud note.</summary>
    static IReadOnlyList<SkyPatcherValue> ParseValueList(string rawValue, ref string? note)
    {
        if (rawValue.Length == 0) return Array.Empty<SkyPatcherValue>();
        var items = new List<SkyPatcherValue>();
        foreach (var rawItem in rawValue.Split(','))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                note = Append(note, $"empty ','-item in '{rawValue}' (stray or doubled ',')");
                continue;
            }
            items.Add(ParseValue(item));
        }
        return items;
    }

    /// <summary>Parse one comma-item: a <c>~…~</c> rename name-literal, or a <c>~</c>-packed compound whose first sub-arg is address-parsed.</summary>
    static SkyPatcherValue ParseValue(string item)
    {
        if (item.Length >= 2 && item[0] == '~' && item[^1] == '~')
        {
            var name = item[1..^1];
            return new SkyPatcherValue(item, IsNameLiteral: true, NameText: name,
                SubArgs: new[] { item }, Address: null);
        }

        var subArgs = item.Split('~');
        var address = TryParseAddress(subArgs[0]);
        return new SkyPatcherValue(item, IsNameLiteral: false, NameText: null, SubArgs: subArgs, Address: address);
    }

    /// <summary>Resolve the unambiguous <c>Plugin.esp|FormID</c> address form; null for anything else, including a bare EditorID.</summary>
    public static FormAddress? TryParseAddress(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        int bar = token.IndexOf('|');
        if (bar <= 0 || bar == token.Length - 1) return null;

        var plugin = token[..bar].Trim();
        var hex = token[(bar + 1)..].Trim();
        if (plugin.Length == 0 || hex.Length == 0) return null;

        // FormID is hex with trimmable leading zeros; a right side that is not clean hex is not this form.
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
            return null;

        return new FormAddress(plugin, formId, EditorId: null);
    }

    static string Append(string? note, string add) => note is null ? add : note + "; " + add;
}

/// <summary>What a physical INI line is.</summary>
public enum SkyPatcherLineKind
{
    /// <summary>Whitespace-only.</summary>
    Blank,
    /// <summary>A ';'-led comment line.</summary>
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

/// <summary>A resolved form address; the tokenizer produces only the FormID form, and <see cref="EditorId"/> is the overlay engine's to fill.</summary>
public sealed record FormAddress(string? Plugin, uint? FormId, string? EditorId)
{
    /// <summary>True when this is the <c>Plugin.esp|FormID</c> form.</summary>
    public bool IsFormId => Plugin is not null && FormId is not null;
    /// <summary>True when this addresses a form by EditorID.</summary>
    public bool IsEditorId => EditorId is not null;
}
