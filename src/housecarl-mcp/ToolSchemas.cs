using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// Publishes a REAL schema for the list parameters that carry SPEC §5.1's <c>@file</c> convention.
///
/// <para>The problem: a list-valued input accepts EITHER an inline array of objects OR the string
/// <c>"@&lt;absolute path&gt;"</c>. C# has no type for that union, so the parameter is declared
/// <see cref="JsonElement"/> and the SDK's schema generator — which works from the declared type — publishes
/// "anything" (<c>{}</c>). The element shape then lives only in the tool description, where a client's schema
/// rendering cannot use it.</para>
///
/// <para>The fix: after the assembly scan has built each tool, replace those property nodes with
/// <c>anyOf[&lt;the generated array schema&gt;, string]</c>. The array arm is GENERATED from the C# element type
/// by the same generator the SDK uses — adding a member to <see cref="ApplyOp"/> updates the published schema
/// automatically, so this is not a hand-maintained copy. Only the union wrapper and the string arm are written
/// here.</para>
///
/// <para>Route note: the SDK's <c>WithToolsFromAssembly</c> has no overload carrying
/// <c>McpServerToolCreateOptions.SchemaCreateOptions</c>, so the per-node <c>TransformSchemaNode</c> hook cannot
/// reach an assembly-scanned tool. <see cref="ModelContextProtocol.Protocol.Tool.InputSchema"/> is settable
/// (and validates what it is given), so the schema is rewritten once at registration instead — which keeps
/// every tool on the one registration path rather than special-casing one tool's wiring.</para>
///
/// <para><b>This changes only what is PUBLISHED, never what is ACCEPTED.</b> Binding still goes through
/// <c>ApplyTools.ReadListParam</c>, whose strict reader is deliberately stricter than the SDK binder.</para>
/// </summary>
internal static class ToolSchemas
{
    /// <summary>One list parameter whose published schema becomes the @file union.</summary>
    internal readonly record struct FileListParam(string Tool, string Parameter, Type ElementArrayType);

    /// <summary>The parameters that carry the @file convention AND are typed <see cref="JsonElement"/> because of
    /// it. A plain <c>string[]</c> list (<c>formids=</c>, <c>bundle=</c>) needs no entry: its generated schema is
    /// already honest, since <c>["@&lt;path&gt;"]</c> IS a one-element string array.
    /// <para>Internal rather than private because it is the ONLY place the link from these parameters to their
    /// element type is declared — being <see cref="JsonElement"/>, they carry no such link in their signature.
    /// <c>wire-names-guard</c> reads it to find each spec object's carrying parameter (#341).</para></summary>
    internal static readonly FileListParam[] FileListParams =
    {
        new("housecarl_apply", "ops", typeof(ApplyOp[])),
        new("housecarl_apply", "assignments", typeof(Assignment[])),
        // W3 PR 2 — the chartered carry from PR #310's decision 1: create's records= is the same JsonElement-typed
        // @file union, so it publishes the same anyOf. Its element type reaches StructInput/NestedSet through
        // CreateFieldOp, which is why the $defs hoist below is first-wins BY NAME and sound here: those are
        // literally the same C# types apply's rows generate, so a duplicate name is a duplicate schema.
        new("housecarl_create", "records", typeof(CreateRecordSpec[])),
    };

    /// <summary>Register the rewrite. It runs as a POST-configure over <c>McpServerOptions</c>, which is the one
    /// place the final tool collection exists regardless of how the tools got there: the assembly scan registers
    /// each tool as a FACTORY, so the instances do not exist yet while the service collection is being built, and
    /// both transports (stdio and http) build their host separately — a post-configure keeps this on ONE line
    /// inside the shared registration instead of a call site per transport that could drift apart.
    /// A tool or parameter not found is skipped here; <c>apply-guard</c> asserts the published shape end-to-end,
    /// so a stale row fails there loudly rather than degrading the surface quietly.</summary>
    internal static void PublishFileListUnions(IServiceCollection services) =>
        services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.ToolCollection is not { } tools) return;
            foreach (var tool in tools)
            {
                var wanted = FileListParams.Where(p => p.Tool == tool.ProtocolTool.Name).ToList();
                if (wanted.Count == 0) continue;
                if (Rewrite(tool.ProtocolTool.InputSchema, wanted) is { } rebuilt)
                    tool.ProtocolTool.InputSchema = rebuilt;
            }
        });

    /// <summary>Build the rewritten schema, or null if the shape isn't what we expect (leave it alone rather than
    /// publish something malformed).</summary>
    static JsonElement? Rewrite(JsonElement schema, IReadOnlyList<FileListParam> parameters)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root) return null;
        if (root["properties"] is not JsonObject props) return null;

        // $ref inside a generated sub-schema is written "#/$defs/X" — resolved against the ROOT document. So a
        // generated schema's own $defs must be HOISTED to the tool schema's root, or every reference in it dangles.
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
                    // First-wins on the DEFINITION NAME. Sound for today's rows and for `create`'s records= in
                    // W3 PR 2 — same generator, and the shared shapes (StructInput/NestedSet) are literally the
                    // same C# types, so a duplicate name is a duplicate schema. It would bind the WRONG
                    // definition only if two DISTINCT types ever produced the same short name here; if that day
                    // comes, key the hoist by type rather than by name.
                    if (defs.ContainsKey(name)) continue;
                    var node = genDefs[name];
                    genDefs.Remove(name);
                    defs[name] = node;
                }
            }

            // EVERY "#/..." pointer in the generated schema is relative to ITS OWN document root — the standalone
            // array schema. Nesting that document under properties/<param>/anyOf/0 silently breaks all of them.
            // This generator terminates a RECURSIVE type (StructInput → NestedSet.compose → StructInput, our
            // compose/sets chain) with exactly such a positional back-reference, so this is not a hypothetical:
            // without rebasing, the published schema carries a dangling $ref, which is worse than publishing
            // nothing at all. Rebase to where the sub-document now lives.
            RebaseRefs(generated, $"#/properties/{p.Parameter}/anyOf/0");

            // The [Description] the SDK lifted off the parameter is the caller-facing teaching — it survives the
            // rewrite verbatim, on the union node where a client will render it.
            var description = existing["description"]?.GetValue<string>();

            var union = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    generated,
                    new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = $"\"@<absolute path>\" — read the same array from a JSON file on disk instead of inlining it.",
                    }),
            };
            if (description is not null) union["description"] = description;

            props[p.Parameter] = union;
            changed = true;
        }

        if (!changed) return null;
        if (defs is not null) root["$defs"] = defs;
        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    /// <summary>Rewrite every same-document JSON pointer in a generated sub-schema so it resolves from the tool
    /// schema's root once the sub-schema has been nested under <paramref name="basePointer"/>. A bare <c>"#"</c>
    /// (the whole document) becomes the base itself; <c>"#/x/y"</c> becomes <c>"&lt;base&gt;/x/y"</c>. A pointer
    /// into <c>$defs</c> is left alone — those definitions were hoisted to the root, which is exactly where
    /// <c>#/$defs/…</c> already points. External refs (anything not starting with <c>#</c>) are untouched.</summary>
    static void RebaseRefs(JsonNode? node, string basePointer)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"]?.GetValue<string>() is { } r && r.StartsWith('#') && !r.StartsWith("#/$defs/", StringComparison.Ordinal))
                    obj["$ref"] = r.Length == 1 ? basePointer : basePointer + r[1..];
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    if (key != "$ref") RebaseRefs(obj[key], basePointer);
                break;
            case JsonArray arr:
                foreach (var item in arr) RebaseRefs(item, basePointer);
                break;
        }
    }

    /// <summary>Match the SDK's own wire conventions so the generated arm reads like every other published schema
    /// (camelCase member names come from each DTO's explicit <c>[JsonPropertyName]</c>, not from a policy).</summary>
    static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web);
}
