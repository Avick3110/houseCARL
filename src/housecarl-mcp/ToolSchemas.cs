using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Rewrites each tool's published <c>inputSchema</c> at registration, never what is accepted; contracts in
/// <c>docs/architecture/tool-schema-publication.md</c>.</summary>
internal static class ToolSchemas
{
    /// <summary>One list parameter whose published schema becomes the @file union.</summary>
    internal readonly record struct FileListParam(string Tool, string Parameter, Type ElementArrayType);

    /// <summary>The @file parameters typed <see cref="JsonElement"/>, and the only declared link from each to its element type; read by <c>wire-names-guard</c>.</summary>
    internal static readonly FileListParam[] FileListParams =
    {
        new(ToolNames.Apply, "ops", typeof(ApplyOp[])),
        new(ToolNames.Apply, "assignments", typeof(Assignment[])),
        new(ToolNames.Create, "records", typeof(CreateRecordSpec[])),
        new(ToolNames.Place, "assets", typeof(PlaceTarget[])),
    };

    /// <summary>One parameter binding more than one wire shape, with the JSON types it accepts; "null" is added by the rewrite.</summary>
    internal readonly record struct ShapeUnionParam(string Tool, string Parameter, string[] Types);

    /// <summary>The parameters whose published type is a union of JSON kinds, so the tool refuses a shape in its own words.</summary>
    internal static readonly ShapeUnionParam[] ShapeUnionParams =
    {
        // housecarl_skse takes one family; the array shape must bind and be answered by the tool, not intercepted.
        new(ToolNames.Skse, "findings", new[] { "string", "array" }),
    };

    /// <summary>How many times one pointer may be inlined along a nesting chain before <see cref="Terminator"/> closes it.</summary>
    const int MaxSelfExpansions = 1;

    /// <summary>Register the passes as a post-configure over <c>McpServerOptions</c>. A tool or parameter not found is
    /// skipped; <c>PublishedSchemaShapeTests</c> names every <see cref="FileListParams"/> row and asserts the published
    /// shape, so a stale one fails there rather than degrading quietly.</summary>
    /// <param name="maxSchemaDepth">The published nesting depth, or null to publish uncut; judged at startup, never here.</param>
    internal static void PublishSchemas(IServiceCollection services, int? maxSchemaDepth = null) =>
        services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.ToolCollection is not { } tools) return;
            var roots = NestedSchemaConstraints.ParameterRoots();
            foreach (var tool in tools)
            {
                if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject root) continue;
                var wanted = FileListParams.Where(p => p.Tool == tool.ProtocolTool.Name).ToList();
                var changed = wanted.Count > 0 && RewriteFileListUnions(root, wanted);
                var shapes = ShapeUnionParams.Where(p => p.Tool == tool.ProtocolTool.Name).ToList();
                changed |= shapes.Count > 0 && RewriteShapeUnions(root, shapes);
                changed |= FlattenRefs(root);
                // Last, so a recursion-expanded copy of a shape carries the same stamps its first occurrence does.
                changed |= NestedSchemaConstraints.Stamp(
                    root, roots.Where(r => r.Tool == tool.ProtocolTool.Name).Select(r => (r.Parameter, r.Type)));
                // Last of all, over the finished document: the cut is measured on what is actually published.
                try
                {
                    changed |= SchemaDepthCap.Cut(root, maxSchemaDepth, tool.ProtocolTool.Name);
                }
                catch (InvalidOperationException bad)
                {
                    // A cut that cannot deliver the depth it was asked for stops the server here, in its own sentence.
                    Console.Error.WriteLine(bad.Message);
                    Environment.Exit(1);
                }
                if (changed) tool.ProtocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
            }
        });

    /// <summary>Republish each listed parameter as <c>anyOf[&lt;the generated element-array schema&gt;, string]</c>, or false and untouched.</summary>
    internal static bool RewriteFileListUnions(JsonObject root, IReadOnlyList<FileListParam> parameters)
    {
        if (root["properties"] is not JsonObject props) return false;

        // A generated sub-schema's "#/$defs/X" resolves against the root document, so its $defs are hoisted there.
        var defs = root["$defs"] as JsonObject;

        bool changed = false;
        foreach (var p in parameters)
        {
            if (props[p.Parameter] is not JsonObject existing) continue;

            var generated = JsonNode.Parse(
                AIJsonUtilities.CreateJsonSchema(p.ElementArrayType, serializerOptions: SchemaJson).GetRawText()) as JsonObject;
            if (generated is null) continue;

            if (generated["$defs"] is JsonObject genDefs)
            {
                generated.Remove("$defs");
                defs ??= new JsonObject();
                foreach (var name in genDefs.Select(kv => kv.Key).ToList())
                {
                    // First wins by name; sound only while distinct types cannot produce the same short name here.
                    if (defs.ContainsKey(name)) continue;
                    var node = genDefs[name];
                    genDefs.Remove(name);
                    defs[name] = node;
                }
            }

            // Every "#/..." pointer in the generated schema is relative to its own root, so rebase before nesting.
            RebaseRefs(generated, $"#/properties/{p.Parameter}/anyOf/0");

            // The parameter's [Description] moves verbatim onto the union node, where a client renders it.
            var description = existing["description"]?.GetValue<string>();

            var union = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    generated,
                    new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "\"@<absolute path>\" — read the same array from a JSON file on disk instead of inlining it.",
                    }),
            };
            if (description is not null) union["description"] = description;

            props[p.Parameter] = union;
            changed = true;
        }

        if (changed && defs is not null) root["$defs"] = defs;
        return changed;
    }

    /// <summary>Stamp each listed parameter's published <c>type</c> with the JSON kinds it accepts, plus "null".</summary>
    internal static bool RewriteShapeUnions(JsonObject root, IReadOnlyList<ShapeUnionParam> parameters)
    {
        if (root["properties"] is not JsonObject props) return false;

        bool changed = false;
        foreach (var p in parameters)
        {
            if (props[p.Parameter] is not JsonObject existing) continue;
            var types = new JsonArray();
            foreach (var t in p.Types) types.Add(t);
            types.Add("null");
            existing["type"] = types;
            changed = true;
        }
        return changed;
    }

    /// <summary>The <c>$ref</c> pointer on a node, or null when it is absent or not a JSON string.</summary>
    static string? RefPointer(JsonObject node) =>
        node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Rebase a generated sub-schema's same-document pointers to resolve from the root once nested under <paramref name="basePointer"/>.</summary>
    static void RebaseRefs(JsonNode? node, string basePointer)
    {
        switch (node)
        {
            case JsonObject obj:
                if (RefPointer(obj) is { } r && r.StartsWith('#') && !r.StartsWith("#/$defs/", StringComparison.Ordinal))
                    obj["$ref"] = r.Length == 1 ? basePointer : basePointer + r[1..];
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    if (key != "$ref") RebaseRefs(obj[key], basePointer);
                break;
            case JsonArray arr:
                foreach (var item in arr) RebaseRefs(item, basePointer);
                break;
        }
    }

    /// <summary>Inline every same-document <c>$ref</c> that resolves, bounded at <see cref="MaxSelfExpansions"/>; one this
    /// pass does not handle stays put and fails the no-<c>$ref</c> invariant in <c>PublishedSchemaShapeTests</c>.
    /// Internal so <c>schema-flatten-guard</c> can drive it over synthetic documents.</summary>
    internal static bool FlattenRefs(JsonObject root)
    {
        // Every pointer resolves against an immutable snapshot, not the tree being rewritten under it.
        if (root.DeepClone() is not JsonObject snapshot) return false;
        if (!Inline(root, snapshot, new Dictionary<string, int>(StringComparer.Ordinal))) return false;
        // Definitions are unreachable once nothing refers to them.
        root.Remove("$defs");
        return true;
    }

    static bool Inline(JsonNode? node, JsonObject snapshot, Dictionary<string, int> spent)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (Expand(obj[key], snapshot, spent) is { } replacement) { obj[key] = replacement; changed = true; }
                    else changed |= Inline(obj[key], snapshot, spent);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (Expand(arr[i], snapshot, spent) is { } replacement) { arr[i] = replacement; changed = true; }
                    else changed |= Inline(arr[i], snapshot, spent);
                }
                break;
        }
        return changed;
    }

    /// <summary>The replacement for one <c>$ref</c> node, or null if the node carries no same-document ref.</summary>
    static JsonNode? Expand(JsonNode? node, JsonObject snapshot, Dictionary<string, int> spent)
    {
        if (node is not JsonObject refNode) return null;
        if (RefPointer(refNode) is not { } pointer || !pointer.StartsWith('#')) return null;

        // A pointer that does not resolve is left as it is, surfacing as the one $ref the guard forbids.
        if (Resolve(snapshot, pointer) is not JsonObject target) return null;
        spent.TryGetValue(pointer, out var used);
        if (used >= MaxSelfExpansions) return Terminator(refNode, target);

        var expanded = (JsonObject)target.DeepClone();
        // The ref node's own members win over the target's, except an empty placeholder beside the $ref.
        foreach (var member in refNode)
        {
            if (member.Key == "$ref") continue;
            if (member.Value is JsonObject { Count: 0 } && expanded.ContainsKey(member.Key)) continue;
            expanded[member.Key] = member.Value?.DeepClone();
        }

        spent[pointer] = used + 1;
        Inline(expanded, snapshot, spent);
        spent[pointer] = used;
        return expanded;
    }

    /// <summary>Close a recursive chain at the bound with the node's description and the target's <c>type</c>, constraining nothing further.</summary>
    internal static JsonObject Terminator(JsonObject refNode, JsonObject target) =>
        Terminator(refNode, target, RecursionContinues);

    /// <summary>The recursion bound's clause: the shape was spelled out earlier in this document.</summary>
    internal const string RecursionContinues =
        "Nesting continues below this level with the same shape shown above; it is accepted but not spelled out again here.";

    /// <inheritdoc cref="Terminator(JsonObject, JsonObject)"/><param name="continues">The clause for why this branch closes.</param>
    internal static JsonObject Terminator(JsonObject refNode, JsonObject target, string continues)
    {
        var open = new JsonObject();
        if (target["type"] is { } type) open["type"] = type.DeepClone();
        // The clause goes on unconditionally, so a node with no description of its own does not close silently.
        open["description"] = refNode["description"]?.GetValue<string>() is { } description
            ? description + " (" + continues + ")"
            : continues;
        return open;
    }

    /// <summary>Walk a same-document JSON pointer against <paramref name="root"/>, or null if it does not resolve.</summary>
    internal static JsonNode? Resolve(JsonObject root, string pointer)
    {
        JsonNode? cur = root;
        foreach (var raw in pointer[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            cur = cur switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var next) ? next : null,
                JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Match the SDK's wire conventions so the generated arm reads like every other published schema.</summary>
    static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web);
}
