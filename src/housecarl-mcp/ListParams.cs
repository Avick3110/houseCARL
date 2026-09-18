using System.Text.Json;
using System.Text.Json.Serialization;

namespace HousecarlMcp;

/// <summary>The shared reader behind every typed list input: an inline JSON array, a bare
/// <c>"@&lt;absolute path&gt;"</c>, or the one-element <c>["@&lt;path&gt;"]</c> spelling; every lane deserializes
/// with <see cref="Strict"/>, so an undeclared member is refused by name.</summary>
internal static class ListParams
{
    /// <summary>Read a list parameter: inline array, "@path" or ["@path"]; <paramref name="shape"/> is the element
    /// shape as the caller writes it and appears verbatim in every refusal.</summary>
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
            // The one-element ["@path"] spelling; an @-string mixed with inline elements is refused by name.
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
            // "byte N in the line", not "column": BytePositionInLine is a UTF-8 byte offset.
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

    /// <summary>Read a list-input's <c>@&lt;path&gt;</c> target off disk; absolute paths only.</summary>
    internal static (string? Text, string? Error) ReadAtFile(string? spelling, string param)
    {
        var raw = spelling?.Trim().Trim('"', '\'') ?? "";
        var path = raw.StartsWith('@') ? raw[1..].Trim() : raw;
        if (path.Length == 0)
            return (null, $"{param}: \"@\" names no file — give the manifest's absolute path, e.g. \"@C:\\\\jobs\\\\ops.json\".");
        if (PathArguments.NotAbsolute(path, $"{param}:", "the file to read", "C:\\jobs\\ops.json") is { } notAbsolute)
            return (null, notAbsolute);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return (null, $"{param}: could not read '{path}' — {ex.GetType().Name}: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(text))
            return (null, $"{param}: '{path}' is empty. Expected a JSON array.");
        return (text, null);
    }

    /// <summary>Shear STJ's own 0-based position block off a JsonException message, which the refusal restates 1-based.</summary>
    internal static string ShearStjPosition(string msg)
    {
        int i = msg.IndexOf(" Path: ", StringComparison.Ordinal);
        return i < 0 ? msg : msg[..i].TrimEnd();
    }

    /// <summary>Strict list-element options: case-insensitive names, comments and trailing commas tolerated, and an
    /// undeclared member refused by name.</summary>
    internal static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Append a one-hop vocabulary correction to a rejected element member's refusal, matched on the member
    /// name and the declaring type; an unmatched pair gets no hint.</summary>
    static string ElementVocabularyHint(string stjMessage)
    {
        foreach (var (old, declaringType, correction) in ElementRenames)
            if (stjMessage.Contains($"'{old}'", StringComparison.Ordinal) &&
                stjMessage.Contains($".{declaringType}'", StringComparison.Ordinal))
                return $" ('{old}' was the 1.x spelling — {correction})";
        return "";
    }

    /// <summary>(old spelling, the type that rejected it, the correction), one row per member-and-shape pair.</summary>
    static readonly (string Old, string DeclaringType, string Correction)[] ElementRenames =
    {
        // A nested set inside compose= legitimately still spells `verb`, hence the type gate.
        ("verb", "ApplyOp", "at the OP level the verb member is now op — op=\"Add\". (A nested set inside compose= is unchanged and still takes verb.)"),
        ("op", "NestedSet", "a nested set inside compose= still spells its verb `verb` — only the top-level op member was renamed to op"),
        // An assignment names records, never a verb: the zip is a copy by construction.
        ("op", "Assignment", "an assignment pairs records and carries no verb — the zip is always a copy. Per-op verbs live in ops=[{…, op: \"…\"}]"),
        ("verb", "Assignment", "an assignment pairs records and carries no verb — the zip is always a copy. Per-op verbs live in ops=[{…, op: \"…\"}]"),

        ("from_plugin", "ApplyOp", "it split in two: from_source names the PLUGIN to copy from, from names a different source RECORD"),
        ("fromplugin", "ApplyOp", "it split in two: from_source names the PLUGIN to copy from, from names a different source RECORD"),
        ("from_plugin", "Assignment", "an assignment's source pole is from_source=, and its source RECORD is from="),

        ("target_formid", "Assignment", "a copy's destination record is the assignments= zip's target="),
        ("source_formid", "Assignment", "a copy's source record is the assignments= zip's from="),
        ("source_plugin", "Assignment", "a copy's source pole is from_source="),
        // The same three typed into an op instead, where the zip's members do not exist.
        ("target_formid", "ApplyOp", "an op names its own record in formid=; a copy's DESTINATION only has a separate spelling inside the assignments= zip (target=)"),
        ("source_formid", "ApplyOp", "an op's source record is from="),
        ("source_plugin", "ApplyOp", "an op's source pole is from_source="),

        // housecarl_create's record specs and their field ops.
        ("operations", "CreateRecordSpec", "a record spec's field list is now ops — ops=[{field_path, value}] (the §5.1 name, the same word " + ToolNames.Apply + " takes)"),
        ("verb", "CreateFieldOp", "at the OP level the verb member is now op — op=\"Add\". (A nested set inside compose= is unchanged and still takes verb.)"),
        // A create op sets a field on a record that does not exist yet, so these members have no meaning here.
        ("formid", "CreateFieldOp", "a create op sets a field on the NEW record, whose FormID is auto-allocated and reported back — there is nothing to name here. The record's own identity is record_type + editorid on the spec"),
        ("from_plugin", "CreateFieldOp", "copying a field FROM another version needs an existing record — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
        ("from_source", "CreateFieldOp", "copying a field FROM another version needs an existing record — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
        ("from", "CreateFieldOp", "copying a field FROM another record needs an existing target — a create has none yet. Set the field with value= / compose=, or create first and copy with " + ToolNames.Apply + " into= the same patch"),
    };
}
