using System.Globalization;
using System.Text.Json.Nodes;

namespace HousecarlMcp;

/// <summary>
/// The optional fourth publication pass: cut every published schema at a configured nesting depth and close the
/// cut with the same terminator node the recursion bound already emits. Unset — the default — it does nothing at
/// all, and the published schemas are byte for byte what they were before this pass existed.
///
/// <para>Why it exists: a strict provider that enforces a nesting-depth cap refuses the WHOLE server at
/// <c>tools/list</c>, naming no tool (issue #730, the same user-facing shape as the <c>$ref</c> case #451).
/// <c>housecarl_create</c> and <c>housecarl_apply</c> publish at 24 and 21 because that is where the recursion
/// bound lands, and a provider capped at 10 takes neither. Setting <see cref="Variable"/> to that provider's cap
/// trades description for acceptance: the schema below the cut says only that nesting continues with the same
/// shape and is accepted, which is exactly what the recursion terminator says.</para>
///
/// <para>Changes only what is PUBLISHED. <c>tools/call</c> is untouched: <see cref="ToolCallShim"/> reads a
/// schema's top-level <c>properties</c> only, and <c>ListParams.Read&lt;T&gt;</c> reads no schema at all, so a
/// cut schema accepts every call the uncut one accepted.</para>
///
/// <para>DEPTH is raw JSON container nesting — the measure the refusing providers use, and the one #730 measured
/// with: the schema object itself is level 1, and every object or array below it is one more, whether it is a
/// schema, a dictionary of schemas, or a <c>type</c>/<c>enum</c>/<c>required</c> list.</para>
///
/// <para>The <c>properties</c>, <c>patternProperties</c> and <c>$defs</c> members are name-to-schema DICTIONARIES,
/// not schemas. The cut recurses into their VALUES and never replaces the container: a terminator in place of a
/// <c>properties</c> object turns it into a property named <c>type</c> and a property named <c>description</c>,
/// and the schema stops validating.</para>
/// </summary>
internal static class SchemaDepthCap
{
    /// <summary>The environment variable, spelled the way houseCARL's other env vars are (HOUSECARL_DATA_DIR,
    /// HOUSECARL_SETUP_HOME).</summary>
    internal const string Variable = "HOUSECARL_MAX_SCHEMA_DEPTH";

    /// <summary>Members whose value is a NAME-TO-SCHEMA DICTIONARY. Each value is a schema to cut on its own; the
    /// container is never replaced.</summary>
    static readonly HashSet<string> Dictionaries =
        new(StringComparer.Ordinal) { "properties", "patternProperties", "$defs", "definitions" };

    /// <summary>Members whose value IS a schema.</summary>
    static readonly HashSet<string> SubSchemas =
        new(StringComparer.Ordinal)
        {
            "items", "additionalProperties", "not", "contains", "propertyNames",
            "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
        };

    /// <summary>Members whose value is a LIST of schemas.</summary>
    static readonly HashSet<string> SchemaLists =
        new(StringComparer.Ordinal) { "anyOf", "oneOf", "allOf", "prefixItems" };

    /// <summary>The configured cap, or null when the variable is unset or blank.</summary>
    /// <exception cref="ArgumentException">The variable is set to something that is not a whole number of 1 or
    /// more. Refused rather than ignored: a caller who set it did so because a provider refuses the uncut schema,
    /// and silently publishing uncut would fail that provider with an error naming neither houseCARL nor this
    /// variable.</exception>
    internal static int? Configured() => Read(Environment.GetEnvironmentVariable(Variable));

    /// <summary>Parse one value of the variable. Separate from <see cref="Configured"/> so a test can drive it.</summary>
    internal static int? Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) && depth >= 1)
            return depth;
        throw new ArgumentException(
            $"{Variable} is \"{raw}\" — set it to a whole number of 1 or more (the JSON nesting depth to publish " +
            "the tool schemas at), or leave it unset to publish them in full.");
    }

    /// <summary>Cut one tool schema to <paramref name="maxDepth"/>. Returns false — leaving the document
    /// untouched — when no cap is configured or the document already fits.</summary>
    internal static bool Cut(JsonObject root, int? maxDepth)
    {
        if (maxDepth is not { } cap || Depth(root) <= cap) return false;

        var shrunk = Shrink(root, cap);
        if (!ReferenceEquals(shrunk, root))
        {
            // The root schema itself became the terminator. A document root cannot be replaced from inside, so its
            // members are swapped for the terminator's.
            foreach (var key in root.Select(kv => kv.Key).ToList()) root.Remove(key);
            foreach (var member in shrunk) root[member.Key] = member.Value?.DeepClone();
        }
        return true;
    }

    /// <summary>Cut one schema node to fit in <paramref name="budget"/> levels counted from the node itself, in
    /// place. Returns the node, or a terminator to put in its place when it cannot be spelled out that shallow.
    /// Every branch leaves the result at depth <paramref name="budget"/> or less.</summary>
    static JsonObject Shrink(JsonObject node, int budget)
    {
        if (Depth(node) <= budget) return node;
        if (budget <= 1) return Terminator(node, budget);

        foreach (var key in node.Select(kv => kv.Key).ToList())
        {
            var value = node[key];
            if (Dictionaries.Contains(key) && value is JsonObject dictionary)
            {
                // The node holds the dictionary, the dictionary holds each schema: two levels before a value.
                if (budget < 3) return Terminator(node, budget);
                foreach (var name in dictionary.Select(kv => kv.Key).ToList())
                    if (dictionary[name] is JsonObject member && Shrink(member, budget - 2) is var cut
                        && !ReferenceEquals(cut, member)) dictionary[name] = cut;
            }
            else if (SubSchemas.Contains(key) && value is JsonObject sub)
            {
                if (Shrink(sub, budget - 1) is var cut && !ReferenceEquals(cut, sub)) node[key] = cut;
            }
            else if (SchemaLists.Contains(key) && value is JsonArray arms)
            {
                if (budget < 3) return Terminator(node, budget);
                for (var i = 0; i < arms.Count; i++)
                    if (arms[i] is JsonObject arm && Shrink(arm, budget - 2) is var cut
                        && !ReferenceEquals(cut, arm)) arms[i] = cut;
            }
            // Anything else is an annotation or a scalar list (type, enum, required, default). It constrains
            // nothing structurally, so one too deep to keep is dropped rather than cut into a shape of its own.
            else if (Depth(value) > budget - 1) node.Remove(key);
        }
        return node;
    }

    /// <summary>The node the cut closes a branch with — the SAME one the recursion bound emits, trimmed to fit:
    /// a <c>type</c> spelled as a list needs a level of its own, which the shallowest budget does not have.</summary>
    static JsonObject Terminator(JsonObject node, int budget)
    {
        var open = ToolSchemas.Terminator(node, node);
        if (Depth(open) > budget) open.Remove("type");
        return open;
    }

    /// <summary>Raw JSON container nesting: an object or array is one level plus its deepest member, a scalar is
    /// none. An empty container is still a level.</summary>
    internal static int Depth(JsonNode? node) => node switch
    {
        JsonObject obj => 1 + obj.Select(kv => Depth(kv.Value)).DefaultIfEmpty(0).Max(),
        JsonArray arr => 1 + arr.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
