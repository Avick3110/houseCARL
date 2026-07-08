using System.Globalization;

namespace HousecarlCore;

/// <summary>
/// Structural parser for a SkyPatcher patch line — the catalog-FREE tokenizer spine of the
/// SkyPatcher distributor subsystem (plan: dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md,
/// Wave 0a). It turns one line of a <c>Data/SKSE/Plugins/SkyPatcher/&lt;type&gt;/&lt;file&gt;.ini</c>
/// into a typed model WITHOUT knowing what any key MEANS. That semantic layer — filter vs operation,
/// operation shape, field mapping, CLEAN/COLLECTION/HARD tractability — is the <b>catalog</b>, built
/// on top of this in Wave 0b. Keeping the two apart is deliberate: the tokenizer is pure, universal
/// grammar mechanics and can't drift when the (hand-modeled) catalog changes.
///
/// <para><b>Grammar</b> (from the skypatcher-authoring reference, grammar-core.md §3–4):</para>
/// <code>
///   line         := ';' comment | blank | patch
///   patch        := segment ( ':' segment )*
///   segment      := key '=' value
///   value        := item ( ',' item )*
///   item         := address | name-literal | scalar | compound
///   address      := plugin '|' formid            (FormID form — unambiguous grammar)
///                 | editorid                      (deferred: see "Address" note below)
///   name-literal := '~' text '~'                  (rename ops: fullName=~New Name~)
///   compound     := part ( '~' part )*            (mgefsToAdd=Form~Mag~Dur~Area)
/// </code>
///
/// <para><b>Address (0a boundary).</b> Only the unambiguous <c>Plugin.esp|FormID</c> form is resolved
/// here. A bare identifier is left un-addressed because an EditorID (<c>filterByWeapons=IronSword</c>)
/// and an enum scalar (<c>armorType=heavy</c>) are lexically identical — telling them apart needs the
/// catalog's knowledge of whether the key is form-valued. So EditorID resolution waits for Wave 0b.</para>
///
/// <para><b>Q3 — no silent failure.</b> A malformed segment (no <c>=</c>, empty key) is captured as a
/// loud <see cref="SkyPatcherLine.Note"/> and the segment is still surfaced — never dropped silently.</para>
///
/// <para><b>KNOWN LIMITATION (Wave-1 empirical item).</b> Line splitting on <c>:</c> is naive, matching
/// the documented "<c>:</c> separates every segment". A rename literal that itself contains a colon
/// (<c>fullName=~Sword: Reforged~</c>) would be over-split. SkyPatcher's real delimiter precedence must
/// be verified against the running DLL before this is hardened — do not assume it here.</para>
/// </summary>
public static class SkyPatcherParse
{
    /// <summary>Parse one physical line. Never throws — a malformed line returns a Patch line carrying a Note.</summary>
    public static SkyPatcherLine ParseLine(string raw)
    {
        raw ??= "";
        var trimmed = raw.Trim();

        if (trimmed.Length == 0)
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Blank, Array.Empty<SkyPatcherSegment>(), null);

        // Documented behavior: a comment line begins with ';'. (Trailing inline comments are NOT stripped
        // here — SkyPatcher value tokens don't contain ';', but stripping mid-line risks eating a value, so
        // that stays a Wave-1 empirical question rather than a silent guess.)
        if (trimmed[0] == ';')
            return new SkyPatcherLine(raw, SkyPatcherLineKind.Comment, Array.Empty<SkyPatcherSegment>(), null);

        var segments = new List<SkyPatcherSegment>();
        string? note = null;

        // Segments are ':'-separated. Split on the RAW (untrimmed) content; each segment is edge-trimmed below.
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

            segments.Add(new SkyPatcherSegment(key, rawValue, ParseValueList(rawValue)));
        }

        return new SkyPatcherLine(raw, SkyPatcherLineKind.Patch, segments, note);
    }

    /// <summary>Parse a whole INI file's text into its lines (blank/comment/patch), in order.</summary>
    public static IReadOnlyList<SkyPatcherLine> ParseFile(string text)
    {
        var lines = new List<SkyPatcherLine>();
        if (string.IsNullOrEmpty(text)) return lines;
        // Split on any newline flavor; keep empties so line indices track the source file.
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(ParseLine(raw));
        return lines;
    }

    /// <summary>Comma-split a segment's raw value into items, parsing each. Empty value ⇒ no items.</summary>
    static IReadOnlyList<SkyPatcherValue> ParseValueList(string rawValue)
    {
        if (rawValue.Length == 0) return Array.Empty<SkyPatcherValue>();
        var items = new List<SkyPatcherValue>();
        foreach (var rawItem in rawValue.Split(','))
        {
            var item = rawItem.Trim();
            if (item.Length == 0) continue;   // a trailing/double comma — skip the empty slot
            items.Add(ParseValue(item));
        }
        return items;
    }

    /// <summary>
    /// Parse one comma-item. A <c>~…~</c>-wrapped item is a rename name-literal; otherwise the item is
    /// treated as a <c>~</c>-packed compound (single-element when there's no <c>~</c>), and the first
    /// sub-arg is address-parsed (FormID form only — see class note).
    /// </summary>
    static SkyPatcherValue ParseValue(string item)
    {
        // Name literal: starts AND ends with '~' (e.g. fullName=~New Name~). This is lexically distinct
        // from a compound a~b~c, which does not start with '~'. Internal text is preserved verbatim.
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

    /// <summary>
    /// Resolve the unambiguous <c>Plugin.esp|FormID</c> address form. Returns null for anything else
    /// (a scalar, an enum value, or a bare EditorID — EditorID vs scalar can't be told apart without the
    /// catalog, so it's deferred to Wave 0b).
    /// </summary>
    public static FormAddress? TryParseAddress(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        int bar = token.IndexOf('|');
        if (bar <= 0 || bar == token.Length - 1) return null;

        var plugin = token[..bar].Trim();
        var hex = token[(bar + 1)..].Trim();
        if (plugin.Length == 0 || hex.Length == 0) return null;

        // FormID is hex, leading zeros trimmable (myMod.esp|08000223 ≡ myMod.esp|223). If the right side
        // isn't clean hex it isn't this address form (e.g. objectsToAdd's "form=count" packs an '=').
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
}

/// <summary>One parsed SkyPatcher line. <see cref="Note"/> carries any loud parse warning (Q3).</summary>
public sealed record SkyPatcherLine(
    string Raw,
    SkyPatcherLineKind Kind,
    IReadOnlyList<SkyPatcherSegment> Segments,
    string? Note);

/// <summary>
/// One <c>key=value</c> segment. Whether this is a filter or an operation — and, for operations, its
/// shape and field mapping — is the catalog's job (Wave 0b); the tokenizer only carries key + values.
/// <see cref="RawValue"/> is null only for a malformed no-'=' segment.
/// </summary>
public sealed record SkyPatcherSegment(
    string Key,
    string? RawValue,
    IReadOnlyList<SkyPatcherValue> Values);

/// <summary>
/// One comma-item of a segment's value. Either a rename name-literal (<see cref="IsNameLiteral"/>,
/// <see cref="NameText"/>) or a <c>~</c>-packed compound (<see cref="SubArgs"/>, single-element when
/// there's no <c>~</c>). <see cref="Address"/> is the FormID address of the first sub-arg when present.
/// </summary>
public sealed record SkyPatcherValue(
    string Raw,
    bool IsNameLiteral,
    string? NameText,
    IReadOnlyList<string> SubArgs,
    FormAddress? Address);

/// <summary>
/// A resolved form address. Wave 0a only produces the FormID form (<see cref="Plugin"/> + <see cref="FormId"/>);
/// <see cref="EditorId"/> is reserved for the Wave 0b catalog layer, which knows which keys are form-valued.
/// </summary>
public sealed record FormAddress(string? Plugin, uint? FormId, string? EditorId)
{
    /// <summary>True when this is the <c>Plugin.esp|FormID</c> form.</summary>
    public bool IsFormId => Plugin is not null && FormId is not null;
    /// <summary>True when this addresses a form by EditorID (Wave 0b).</summary>
    public bool IsEditorId => EditorId is not null;
}
