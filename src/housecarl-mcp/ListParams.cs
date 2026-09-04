using System.Text.Json;
using System.Text.Json.Serialization;

namespace HousecarlMcp;

/// <summary>
/// The shared reader behind every typed list input: an inline JSON array, a bare <c>"@&lt;absolute path&gt;"</c>,
/// or the one-element <c>["@&lt;path&gt;"]</c> spelling that matches how <c>formids=</c> writes it. One
/// implementation so the convention reads the same on every lane, and one place for the strictness: every lane
/// deserializes with <see cref="Strict"/>, so an undeclared member is refused BY NAME, where the SDK's own binder
/// would silently drop it and surface the typo as an unrelated downstream refusal.
/// </summary>
internal static class ListParams
{
    /// <summary>Read a list parameter: inline array | "@path" | ["@path"]. Every failure (unreadable, not JSON, not
    /// an array, empty, a null element, an undeclared member) names itself and its element.
    /// <paramref name="shape"/> is the element shape as the caller writes it, e.g.
    /// <c>{formid, field_path, op?, …}</c> — it appears verbatim in the refusals.</summary>
    internal static (T[]? Items, string? Error) Read<T>(JsonElement el, string param, string shape)
    {
        string json;
        string origin;
        if (el.ValueKind is JsonValueKind.String)
        {
            var (text, err) = ReadAtFile(el.GetString(), param);
            if (err is not null) return (null, err);
            json = text!; origin = $"the file named by {param}";
        }
        else if (el.ValueKind is JsonValueKind.Array)
        {
            // The one-element ["@path"] spelling — the same shape formids= uses. An @-string anywhere else in the
            // array is a mixed inline/file list, which has no meaning: refuse it by name rather than half-honor it.
            var atIndexes = new List<int>();
            int count = 0;
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.String && item.GetString()?.TrimStart().StartsWith('@') == true)
                    atIndexes.Add(count);
                count++;
            }
            if (atIndexes.Count > 0)
            {
                if (count != 1)
                    return (null, $"{param}: \"@<path>\" reads the WHOLE list from a file, so it cannot be mixed with inline elements " +
                                  $"(found {atIndexes.Count} @-element(s) among {count}). Pass either the inline array or a single \"@<absolute path>\".");
                var (text, err) = ReadAtFile(el[0].GetString(), param);
                if (err is not null) return (null, err);
                json = text!; origin = $"the file named by {param}";
            }
            else { json = el.GetRawText(); origin = param; }
        }
        else
            return (null, $"{param} must be an ARRAY of {shape} elements, or \"@<absolute path>\" naming a JSON file holding that array (got {el.ValueKind}).");

        T[]? items;
        try { items = JsonSerializer.Deserialize<T[]>(json, Strict); }
        catch (JsonException ex)
        {
            // "byte N in the line", not "column": BytePositionInLine is a UTF-8 byte offset, which skews from the
            // visual column on a non-ASCII line. STJ appends its own 0-based position block to the message, which
            // would contradict the 1-based position stated here — shear it; the element path is surfaced separately.
            string at = ex.LineNumber is { } ln ? $" at line {ln + 1}, byte {(ex.BytePositionInLine ?? 0) + 1} in the line" : "";
            string element = ex.Path is { Length: > 2 } p ? $" (element {p})" : "";
            var msg = ShearStjPosition(Guard.Flatten(ex.Message));
            return (null, $"{origin} could not be parsed{at}{element}: {msg} " +
                          $"Expected a JSON ARRAY of {shape} elements.{ElementVocabularyHint(msg)}");
        }
        if (items is null) return (null, $"{origin} parsed to JSON null — expected a JSON array of {shape} elements.");
        if (items.Length == 0) return (null, $"{origin} is an empty array — give at least one {shape}.");
        for (int i = 0; i < items.Length; i++)
            if (items[i] is null) return (null, $"{origin}: element [{i}] is null — every element must be an object.");
        return (items, null);
    }

    /// <summary>Read a list-input's <c>@&lt;path&gt;</c> target off disk. Absolute-path-only, for the reason the
    /// message states: the server's working directory is not the caller's, so a relative path silently resolves
    /// somewhere neither of them meant.</summary>
    internal static (string? Text, string? Error) ReadAtFile(string? spelling, string param)
    {
        var raw = spelling?.Trim().Trim('"', '\'') ?? "";
        var path = raw.StartsWith('@') ? raw[1..].Trim() : raw;
        if (path.Length == 0)
            return (null, $"{param}: \"@\" names no file — give the manifest's absolute path, e.g. \"@C:\\\\jobs\\\\ops.json\".");
        if (!Path.IsPathRooted(path))
            return (null, $"{param}: '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return (null, $"{param}: could not read '{path}' — {ex.GetType().Name}: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(text))
            return (null, $"{param}: '{path}' is empty. Expected a JSON array.");
        return (text, null);
    }

    /// <summary>STJ appends its own position block (" Path: $[0].x | LineNumber: 0 | BytePositionInLine: 89.") to a
    /// JsonException message — 0-based, contradicting the 1-based position the refusal already leads with. Shear it.</summary>
    internal static string ShearStjPosition(string msg)
    {
        int i = msg.IndexOf(" Path: ", StringComparison.Ordinal);
        return i < 0 ? msg : msg[..i].TrimEnd();
    }

    /// <summary>Strict list-element options: property names case-insensitive like the SDK's inline binder, comments +
    /// trailing commas tolerated (hand-generated files), and an undeclared member REFUSED by name.</summary>
    internal static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Append a one-hop vocabulary correction to a rejected element member's refusal. The alias layer
    /// rewrites top-level arguments only, so a caller carrying 1.x habits has no other way to learn the new word.
    /// <para>Matched on the member name AND the declaring type, both of which STJ already quotes. The type half is
    /// load-bearing: the same word is a different mistake — or no mistake — per shape, so matching the name alone
    /// would answer a stray <c>op</c> inside an assignment by lecturing about a construct that caller never used.
    /// Unmatched pairs get no hint; the refusal still names the member and its type.</para></summary>
    static string ElementVocabularyHint(string stjMessage)
    {
        foreach (var (old, declaringType, correction) in ElementRenames)
            if (stjMessage.Contains($"'{old}'", StringComparison.Ordinal) &&
                stjMessage.Contains($".{declaringType}'", StringComparison.Ordinal))
                return $" ('{old}' was the 1.x spelling — {correction})";
        return "";
    }

    /// <summary>(old spelling, the type that REJECTED it, the correction). One row per (member, shape) pair,
    /// because the same word is a different mistake — or no mistake — depending on which shape it landed in.</summary>
    static readonly (string Old, string DeclaringType, string Correction)[] ElementRenames =
    {
        // AT THE OP LEVEL the rename is verb -> op. A nested set inside compose= is a NestedSet, shared verbatim
        // with the 1.x wire shape, and legitimately still spells `verb` — hence the type gate, and the two
        // opposite corrections below it.
        ("verb", "ApplyOp", "at the OP level the verb member is now op — op=\"Add\". (A nested set inside compose= is unchanged and still takes verb.)"),
        ("op", "NestedSet", "a nested set inside compose= still spells its verb `verb` — only the top-level op member was renamed to op"),
        // An assignment names RECORDS, never a verb: the zip is a copy by construction. Answering this one with
        // the NestedSet correction would lecture about compose=, which such a caller never used.
        ("op", "Assignment", "an assignment pairs records and carries no verb — the zip is always a copy. Per-op verbs live in ops=[{…, op: \"…\"}]"),
        ("verb", "Assignment", "an assignment pairs records and carries no verb — the zip is always a copy. Per-op verbs live in ops=[{…, op: \"…\"}]"),

        ("from_plugin", "ApplyOp", "it split in two: from_source names the PLUGIN to copy from, from names a different source RECORD"),
        ("fromplugin", "ApplyOp", "it split in two: from_source names the PLUGIN to copy from, from names a different source RECORD"),
        ("from_plugin", "Assignment", "an assignment's source pole is from_source=, and its source RECORD is from="),

        ("target_formid", "Assignment", "a copy's destination record is the assignments= zip's target="),
        ("source_formid", "Assignment", "a copy's source record is the assignments= zip's from="),
        ("source_plugin", "Assignment", "a copy's source pole is from_source="),
        // The same three, typed into an OP instead — the zip's members do not exist there; an op copies with
        // from=/from_source= and names its own record in formid=.
        ("target_formid", "ApplyOp", "an op names its own record in formid=; a copy's DESTINATION only has a separate spelling inside the assignments= zip (target=)"),
        ("source_formid", "ApplyOp", "an op's source record is from="),
        ("source_plugin", "ApplyOp", "an op's source pole is from_source="),

        // ---- housecarl_create's record specs + their field ops ----------------------------------------------
        // The 1.x batch element spelled its field list `operations` and each op's verb `verb`; both are the same
        // renames the op level took, so they get the same corrections in this shape's own words.
        ("operations", "CreateRecordSpec", "a record spec's field list is now ops — ops=[{field_path, value}] (the §5.1 name, the same word " + ToolNames.Apply + " takes)"),
        ("verb", "CreateFieldOp", "at the OP level the verb member is now op — op=\"Add\". (A nested set inside compose= is unchanged and still takes verb.)"),
        // A create op sets a field on a record that does not exist yet, so three op members are not merely renamed
        // — they have no meaning here at all, and the engine refuses each of them by name too. Say which is which.
        ("formid", "CreateFieldOp", "a create op sets a field on the NEW record, whose FormID is auto-allocated and reported back — there is nothing to name here. The record's own identity is record_type + editorid on the spec"),
        ("from_plugin", "CreateFieldOp", "copying a field FROM another version needs an existing record — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
        ("from_source", "CreateFieldOp", "copying a field FROM another version needs an existing record — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
        ("from", "CreateFieldOp", "copying a field FROM another record needs an existing target — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
    };
}
