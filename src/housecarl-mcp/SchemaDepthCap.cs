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

    /// <summary>The shallowest cap that still publishes a tool's own parameters. A schema's root is level 1, its
    /// <c>properties</c> dictionary level 2 and each parameter level 3, so below 3 the root itself is the node
    /// that gets closed — and <see cref="ToolCallShim"/> READS a schema's top-level <c>properties</c>, so a root
    /// without one silently drops argument coercion, the named missing-parameter refusal and the undeclared-key
    /// refusal. A cut is allowed to say less about a nested shape; it is not allowed to change what a call
    /// gets back.</summary>
    internal const int Minimum = 3;

    /// <summary>Members whose value is a NAME-TO-SCHEMA DICTIONARY. Each value is a schema to cut on its own; the
    /// container is never replaced.</summary>
    static readonly HashSet<string> Dictionaries =
        new(StringComparer.Ordinal)
            { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas" };

    /// <summary>Members whose value IS a schema.</summary>
    static readonly HashSet<string> SubSchemas =
        new(StringComparer.Ordinal)
        {
            "items", "additionalProperties", "not", "contains", "propertyNames",
            "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
            "additionalItems", "contentSchema",
        };

    /// <summary>Members whose value is a LIST of schemas. <c>items</c> joins them when it carries the tuple
    /// spelling (<c>"items": [ … ]</c>) rather than one schema.</summary>
    static readonly HashSet<string> SchemaLists =
        new(StringComparer.Ordinal) { "anyOf", "oneOf", "allOf", "prefixItems" };

    /// <summary>What one cut is for: the configured cap, and the tool whose schema is being cut — both only so a
    /// refusal can say which knob and which tool.</summary>
    readonly record struct Cutting(int Cap, string Tool);

    /// <summary>The configured cap, or null when the variable is unset or blank.</summary>
    /// <exception cref="ArgumentException">The variable is set to something that is not a whole number of
    /// <see cref="Minimum"/> or more. Refused rather than ignored: a caller who set it did so because a provider
    /// refuses the uncut schema, and silently publishing uncut would fail that provider with an error naming
    /// neither houseCARL nor this variable.</exception>
    internal static int? Configured() => Read(Environment.GetEnvironmentVariable(Variable));

    /// <summary>Parse one value of the variable. Separate from <see cref="Configured"/> so a test can drive it.</summary>
    internal static int? Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
            && depth >= Minimum) return depth;
        throw new ArgumentException(
            $"{Variable} is \"{raw}\" — set it to a whole number of {Minimum} or more (the JSON nesting depth to " +
            $"publish the tool schemas at; below {Minimum} a schema cannot carry its own parameters, which every " +
            "call is checked against), or leave it unset to publish them in full.");
    }

    /// <summary>Cut one tool schema to <paramref name="maxDepth"/>. Returns false — leaving the document
    /// untouched — when no cap is configured or the document already fits.</summary>
    /// <exception cref="InvalidOperationException">The cut could not produce a document at the cap. Thrown rather
    /// than published: a schema still over the cap is refused by the provider the cap was set for, which takes the
    /// whole server down at <c>tools/list</c> naming neither houseCARL nor a tool.</exception>
    internal static bool Cut(JsonObject root, int? maxDepth, string tool)
    {
        if (maxDepth is not { } cap || Depth(root) <= cap) return false;

        var shrunk = Shrink(root, cap, new Cutting(cap, tool));
        if (!ReferenceEquals(shrunk, root))
        {
            // The root schema itself became the terminator. A document root cannot be replaced from inside, so its
            // members are swapped for the terminator's.
            foreach (var key in root.Select(kv => kv.Key).ToList()) root.Remove(key);
            foreach (var member in shrunk) root[member.Key] = member.Value?.DeepClone();
        }

        // MEASURED, not reasoned: every branch above is meant to leave the node inside its budget, and a document
        // shaped in a way one of them stepped over would otherwise publish over the cap in silence.
        if (Depth(root) is var reached && reached > cap)
            throw new InvalidOperationException(
                $"{Variable} is {cap}, but {tool}'s schema is still {reached} levels deep after the cut — houseCARL " +
                "will not publish a schema deeper than the cap it was given, so unset the variable to publish in " +
                "full and report this.");
        return true;
    }

    /// <summary>Cut one schema node to fit in <paramref name="budget"/> levels counted from the node itself, in
    /// place. Returns the node, or a terminator to put in its place when it cannot be spelled out that shallow.
    /// Every branch leaves the result at depth <paramref name="budget"/> or less.</summary>
    static JsonObject Shrink(JsonObject node, int budget, Cutting cut)
    {
        if (Depth(node) <= budget) return node;
        if (budget <= 1) return Terminator(node, budget, cut);

        foreach (var key in node.Select(kv => kv.Key).ToList())
        {
            var value = node[key];
            // items carries EITHER one schema or the tuple spelling, so which member set it belongs to is read
            // off the value, not off the name.
            if (Dictionaries.Contains(key) && value is JsonObject dictionary)
            {
                // The node holds the dictionary, the dictionary holds each schema: two levels before a value.
                if (budget < 3) return Terminator(node, budget, cut);
                foreach (var name in dictionary.Select(kv => kv.Key).ToList())
                    if (dictionary[name] is JsonObject member) Replace(dictionary, name, Shrink(member, budget - 2, cut));
                    else if (Depth(dictionary[name]) > budget - 2) throw Unhandled(cut, $"{key}/{name}");
            }
            else if (SubSchemas.Contains(key) && value is JsonObject sub)
            {
                Replace(node, key, Shrink(sub, budget - 1, cut));
            }
            else if ((SchemaLists.Contains(key) || key == "items") && value is JsonArray arms)
            {
                if (budget < 3) return Terminator(node, budget, cut);
                for (var i = 0; i < arms.Count; i++)
                    if (arms[i] is JsonObject arm) Replace(arms, i, Shrink(arm, budget - 2, cut));
                    else if (Depth(arms[i]) > budget - 2) throw Unhandled(cut, $"{key}/{i}");
            }
            // Everything else is a scalar or a scalar list (type, enum, required, a default), which fits whenever
            // the node has a level to spend. One that does not is a member this pass has no rule for, and it is
            // named rather than dropped: dropping it would take a constraint off the surface in silence.
            else if (Depth(value) > budget - 1) throw Unhandled(cut, key);
        }
        return node;
    }

    /// <summary>Put a cut node back in its slot — only when it is a NEW node. Assigning a node into the parent it
    /// already hangs off throws "the node already has a parent", and a node the cut left alone is exactly that.</summary>
    static void Replace(JsonObject parent, string key, JsonObject cut)
    {
        if (!ReferenceEquals(parent[key], cut)) parent[key] = cut;
    }

    /// <inheritdoc cref="Replace(JsonObject, string, JsonObject)"/>
    static void Replace(JsonArray parent, int index, JsonObject cut)
    {
        if (!ReferenceEquals(parent[index], cut)) parent[index] = cut;
    }

    /// <summary>The refusal for a member the cut has no rule for — a schema-bearing keyword outside the sets
    /// above, or one spelled in a shape they do not cover. These schemas are generated from a closed set of
    /// shapes, so a member reaching here is a drift to report, not a case to widen at a user's server start.</summary>
    static InvalidOperationException Unhandled(Cutting cut, string member) =>
        new($"{Variable} is {cut.Cap}, and cutting {cut.Tool}'s schema that shallow means shortening its " +
            $"\"{member}\" member, which this pass has no rule for — unset the variable to publish the schemas in " +
            "full and report the member.");

    /// <summary>The node the cut closes a branch with — the SAME one the recursion bound emits, plus one sentence
    /// and trimmed to fit. The sentence is needed because the bound's own reads "the same shape shown above",
    /// which is true where a cycle repeated a shape and false at a cut, where the shape is in no part of this
    /// document. What the node CLAIMS is unchanged: nesting continues below and is accepted. A <c>type</c> spelled
    /// as a list needs a level of its own, which the shallowest budget does not have.</summary>
    static JsonObject Terminator(JsonObject node, int budget, Cutting cut)
    {
        var open = ToolSchemas.Terminator(node, node);
        open["description"] = open["description"]!.GetValue<string>() +
            $" Nesting was cut here at depth {cut.Cap} by {Variable}: the tool accepts the full shape, and this " +
            "parameter's description carries its members.";
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
