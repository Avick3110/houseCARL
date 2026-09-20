using System.Globalization;
using System.Text.Json.Nodes;

namespace HousecarlMcp;

/// <summary>The optional fourth publication pass: cut every published schema at a configured nesting depth and close the cut with the terminator node the recursion bound already emits. Unset it does nothing; the depth measure, the floor and the refusals are in docs/architecture/tool-schema-publication.md.</summary>
internal static class SchemaDepthCap
{
    /// <summary>The environment variable, spelled the way houseCARL's others are.</summary>
    internal const string Variable = "HOUSECARL_MAX_SCHEMA_DEPTH";

    /// <summary>The shallowest cap that leaves the call path exactly as it was: at 4 every parameter keeps its <c>type</c> and only the shapes below a parameter are cut.</summary>
    internal const int Minimum = 4;

    /// <summary>Members whose value is a NAME-TO-SCHEMA DICTIONARY; the container is never replaced.</summary>
    static readonly HashSet<string> Dictionaries =
        new(StringComparer.Ordinal)
            { "properties", "patternProperties", "$defs", "definitions", "dependentSchemas" };

    static readonly HashSet<string> SubSchemas =
        new(StringComparer.Ordinal)
        {
            "items", "additionalProperties", "not", "contains", "propertyNames",
            "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
            "additionalItems", "contentSchema",
        };

    /// <summary>Members whose value is a LIST of schemas; <c>items</c> joins them in its tuple spelling.</summary>
    static readonly HashSet<string> SchemaLists =
        new(StringComparer.Ordinal) { "anyOf", "oneOf", "allOf", "prefixItems" };

    /// <summary>What one cut is for: the configured cap, and the tool whose schema is being cut.</summary>
    readonly record struct Cutting(int Cap, string Tool);

    /// <summary>The configured cap, or null when the variable is unset or blank; a value below <see cref="Minimum"/> is refused rather than ignored.</summary>
    internal static int? Configured() => Read(Environment.GetEnvironmentVariable(Variable));

    /// <summary>Parse one value of the variable. Separate from <see cref="Configured"/> so a test can drive it.</summary>
    internal static int? Read(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
            && depth >= Minimum) return depth;
        throw new ArgumentException(
            $"{Variable} is \"{raw}\" — set it to a whole number of {Minimum} or more (the JSON nesting depth to " +
            $"publish the tool schemas at; below {Minimum} a schema loses the parameters and parameter types every " +
            "call is checked against), or leave it unset to publish them in full.");
    }

    /// <summary>Cut one tool schema to <paramref name="maxDepth"/>; false leaves the document untouched, and a cut that could not reach the cap throws rather than publishing.</summary>
    internal static bool Cut(JsonObject root, int? maxDepth, string tool)
    {
        if (maxDepth is not { } cap || Depth(root) <= cap) return false;

        var shrunk = Shrink(root, cap, new Cutting(cap, tool));
        if (!ReferenceEquals(shrunk, root))
        {
            // A document root cannot be replaced from inside, so its members are swapped for the terminator's.
            foreach (var key in root.Select(kv => kv.Key).ToList()) root.Remove(key);
            foreach (var member in shrunk) root[member.Key] = member.Value?.DeepClone();
        }

        // MEASURED, not reasoned: a document a branch stepped over would otherwise publish over the cap.
        if (Depth(root) is var reached && reached > cap)
            throw new InvalidOperationException(
                $"{Variable} is {cap}, but {tool}'s schema is still {reached} levels deep after the cut — houseCARL " +
                "will not publish a schema deeper than the cap it was given, so unset the variable to publish in " +
                "full and report this.");
        return true;
    }

    /// <summary>Cut one schema node to fit in <paramref name="budget"/> levels counted from the node itself, in place: the node, or a terminator to put in its place.</summary>
    static JsonObject Shrink(JsonObject node, int budget, Cutting cut)
    {
        if (Depth(node) <= budget) return node;
        if (budget <= 1) return Terminator(node, budget, cut);

        foreach (var key in node.Select(kv => kv.Key).ToList())
        {
            var value = node[key];
            // items carries either one schema or the tuple spelling, so the member set is read off the value.
            if (Dictionaries.Contains(key) && value is JsonObject dictionary)
            {
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
            // Everything else is a scalar or scalar list; one that does not fit is named rather than dropped.
            else if (Depth(value) > budget - 1) throw Unhandled(cut, key);
        }
        return node;
    }

    /// <summary>Put a cut node back in its slot, only when it is a NEW node.</summary>
    static void Replace(JsonObject parent, string key, JsonObject cut)
    {
        if (!ReferenceEquals(parent[key], cut)) parent[key] = cut;
    }

    /// <inheritdoc cref="Replace(JsonObject, string, JsonObject)"/>
    static void Replace(JsonArray parent, int index, JsonObject cut)
    {
        if (!ReferenceEquals(parent[index], cut)) parent[index] = cut;
    }

    /// <summary>The refusal for a member the cut has no rule for — a drift to report, not a case to widen.</summary>
    static InvalidOperationException Unhandled(Cutting cut, string member) =>
        new($"{Variable} is {cut.Cap}, and cutting {cut.Tool}'s schema that shallow means shortening its " +
            $"\"{member}\" member, which this pass has no rule for — unset the variable to publish the schemas in " +
            "full and report the member.");

    /// <summary>The node the cut closes a branch with — the SAME one the recursion bound emits, plus a sentence.</summary>
    static JsonObject Terminator(JsonObject node, int budget, Cutting cut)
    {
        var open = ToolSchemas.Terminator(node, node, CutContinues(cut.Cap));
        if (Depth(open) > budget) open.Remove("type");
        return open;
    }

    /// <summary>What a CUT node says about the nesting below it: the bound's claim, without its "shown above".</summary>
    internal static string CutContinues(int cap) =>
        $"Nesting continues below this level and is accepted, but is not spelled out in this document: it was cut " +
        $"at depth {cap} by {Variable}. The tool accepts the full shape, and the members are named in the " +
        "description of this parameter.";

    /// <summary>Raw JSON container nesting: an object or array is one level plus its deepest member.</summary>
    internal static int Depth(JsonNode? node) => node switch
    {
        JsonObject obj => 1 + obj.Select(kv => Depth(kv.Value)).DefaultIfEmpty(0).Max(),
        JsonArray arr => 1 + arr.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
