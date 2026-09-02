using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// The published-schema layer: rewrites each tool's <c>inputSchema</c> once at registration, after the assembly
/// scan has built it. Two passes — the SPEC §5.1 <c>@file</c> union on the parameters listed in
/// <see cref="FileListParams"/>, then <see cref="FlattenRefs"/> over every tool.
///
/// <para>Changes only what is PUBLISHED, never what is ACCEPTED. Two readers see a call's arguments and neither
/// is moved by this: <see cref="ToolCallShim"/> coerces and refuses off the published schema, but reads only its
/// TOP-LEVEL <c>properties</c> and never descends into <c>items</c>/<c>anyOf</c> — which is the nested part this
/// pass rewrites; and the composed payloads are then read by <c>ListParams.Read&lt;T&gt;</c> (reached through
/// <c>ApplyTools.ReadOps</c>/<c>ReadAssignments</c>, and from <c>CreateTools</c> for <c>records=</c>), which is
/// deliberately stricter than the SDK binder and consults no schema at all.</para>
///
/// <para>Why it is shaped this way: <c>docs/architecture/tool-schema-publication.md</c>. What it guarantees is
/// pinned by <c>binding-shim-guard</c>'s SCHEMA arms (the real served surface) and <c>schema-flatten-guard</c>
/// (the mechanism).</para>
///
/// <para><b>Scope of <see cref="FlattenRefs"/>: it normalizes what THIS SDK's schema generator emits — it is not
/// a general JSON Schema <c>$ref</c> implementation, and must not be reused as one.</b> It reads a <c>$ref</c> as
/// a JSON pointer wherever one appears, which is true of generator output and not of JSON Schema at large: plain-name
/// <c>$anchor</c> fragments, percent-encoded and empty reference tokens, boolean schemas as a pointer target, a
/// <c>$ref</c>-shaped value sitting under <c>default</c>/<c>enum</c>, and 2020-12's rule that <c>$ref</c> siblings
/// apply IN ADDITION to the target (this merge lets them override) are all outside what it handles. None is
/// reachable from today's DTOs, and that is PINNED rather than assumed: <c>schema-flatten-guard</c>'s ARM 7
/// asserts the emission grammar this depends on — no <c>$defs</c>, pointer-form refs with literal tokens, ref
/// nodes carrying only a description and an empty <c>items</c> placeholder, every target an object schema, and no
/// <c>$ref</c> in a value position. A generator that drifts on an SDK bump reddens there, not at a user's server
/// start. Widen the handling before widening the input, not after.</para>
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
        new(ToolNames.Apply, "ops", typeof(ApplyOp[])),
        new(ToolNames.Apply, "assignments", typeof(Assignment[])),
        new(ToolNames.Create, "records", typeof(CreateRecordSpec[])),
    };

    /// <summary>How many times one pointer may be inlined along a single nesting chain before
    /// <see cref="Terminator"/> closes it. Raising it deepens every recursive branch of every published schema.</summary>
    const int MaxSelfExpansions = 1;

    /// <summary>Register both passes. Runs as a POST-configure over <c>McpServerOptions</c> — the one place the
    /// final tool collection exists whichever transport built the host, since the assembly scan registers each tool
    /// as a factory. A tool or parameter not found is skipped; <c>binding-shim-guard</c>'s SCHEMA arms name every
    /// union row and assert the published shape, so a stale <see cref="FileListParams"/> row fails there rather
    /// than degrading quietly. (Not <c>apply-guard</c>, which never reads a published schema.)</summary>
    internal static void PublishSchemas(IServiceCollection services) =>
        services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.ToolCollection is not { } tools) return;
            foreach (var tool in tools)
            {
                if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject root) continue;
                var wanted = FileListParams.Where(p => p.Tool == tool.ProtocolTool.Name).ToList();
                var changed = wanted.Count > 0 && RewriteFileListUnions(root, wanted);
                changed |= FlattenRefs(root);
                if (changed) tool.ProtocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
            }
        });

    /// <summary>Republish each listed parameter as <c>anyOf[&lt;the generated element-array schema&gt;, string]</c>.
    /// The array arm is generated from the C# element type by the same generator the SDK uses, so adding a member to
    /// <see cref="ApplyOp"/> updates the published schema automatically. Returns false — leaving the schema
    /// untouched — when the document is not the shape this expects.</summary>
    internal static bool RewriteFileListUnions(JsonObject root, IReadOnlyList<FileListParam> parameters)
    {
        if (root["properties"] is not JsonObject props) return false;

        // A generated sub-schema's "#/$defs/X" resolves against the ROOT document, so its $defs must be hoisted to
        // the tool schema's root or every such reference dangles.
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
                    // First-wins BY NAME. Sound only while distinct types cannot produce the same short name here;
                    // today's rows share literal C# types (StructInput/NestedSet), so a duplicate name is a
                    // duplicate schema. Key the hoist by type if that stops holding.
                    if (defs.ContainsKey(name)) continue;
                    var node = genDefs[name];
                    genDefs.Remove(name);
                    defs[name] = node;
                }
            }

            // Every "#/..." pointer in the generated schema is relative to ITS OWN document root. Nesting that
            // document under properties/<param>/anyOf/0 breaks all of them unless they are rebased first.
            RebaseRefs(generated, $"#/properties/{p.Parameter}/anyOf/0");

            // The [Description] the SDK lifted off the parameter is the caller-facing teaching — keep it verbatim,
            // on the union node where a client will render it.
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

    /// <summary>The <c>$ref</c> pointer on a node, or null when the member is absent or does not hold a JSON
    /// string. The one place either pass reads a <c>$ref</c>, and it reads it safely: a non-string <c>$ref</c> is
    /// one of the spellings the class summary lists as outside this pass (a boolean target, a value-position ref),
    /// so it is LEFT ALONE for the invariant arm to report, exactly as an unresolvable pointer is.
    /// <c>GetValue&lt;string&gt;()</c> would instead throw <c>InvalidOperationException</c> out of the
    /// <c>PostConfigure</c> these passes run in, while the host is being built — failing the whole server's start
    /// on both transports and naming neither houseCARL nor a tool, which is the very shape #451 was filed
    /// against.</summary>
    static string? RefPointer(JsonObject node) =>
        node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Rewrite every same-document JSON pointer in a generated sub-schema so it resolves from the tool
    /// schema's root once the sub-schema has been nested under <paramref name="basePointer"/>. A bare <c>"#"</c>
    /// (the whole document) becomes the base itself; <c>"#/x/y"</c> becomes <c>"&lt;base&gt;/x/y"</c>. A pointer
    /// into <c>$defs</c> is left alone — those definitions were hoisted to the root, which is exactly where
    /// <c>#/$defs/…</c> already points. External refs (anything not starting with <c>#</c>) are untouched, and so
    /// is a <c>$ref</c> that is not a JSON string — see <see cref="RefPointer"/>.</summary>
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

    /// <summary>Inline every same-document <c>$ref</c> that resolves, bounding each recursive chain at
    /// <see cref="MaxSelfExpansions"/> expansions of the same pointer. One that does not resolve — or is not a
    /// same-document pointer, or is not a string at all — is left in place to fail the invariant
    /// <c>binding-shim-guard</c> asserts: no published tool schema carries a <c>$ref</c> MEMBER, in any spelling.
    /// That predicate is wider than this pass's gate on purpose, and it is what makes leaving a form this pass
    /// does not understand a report rather than a silence. Internal so <c>schema-flatten-guard</c> can drive it
    /// over synthetic documents — the published surface exercises only the shapes today's DTOs happen to
    /// generate.</summary>
    internal static bool FlattenRefs(JsonObject root)
    {
        // Inlined copies carry the pointers of the document they were copied FROM, so every pointer resolves
        // against an immutable snapshot rather than the tree being rewritten under it.
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

        // A pointer that does not resolve is left exactly as it is: publishing an open node in its place would
        // hide a broken rebase behind a schema that looks finished. It surfaces as the one $ref the guard forbids.
        if (Resolve(snapshot, pointer) is not JsonObject target) return null;
        spent.TryGetValue(pointer, out var used);
        if (used >= MaxSelfExpansions) return Terminator(refNode, target);

        var expanded = (JsonObject)target.DeepClone();
        // The ref node's own members are this parameter's statement about the target, so they win — except an
        // empty placeholder the generator leaves beside a $ref, which says nothing the target does not say better.
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

    /// <summary>Close a recursive chain at the bound: keep the node's own description and the target's <c>type</c>,
    /// and constrain nothing further. Says exactly what is true — nesting deeper is still accepted, and this
    /// document stops spelling it out.</summary>
    static JsonObject Terminator(JsonObject refNode, JsonObject target)
    {
        const string continues = "Nesting continues below this level with the same shape shown above; it is accepted but not spelled out again here.";
        var open = new JsonObject();
        if (target["type"] is { } type) open["type"] = type.DeepClone();
        // The clause goes on unconditionally. A parameter carrying no description of its own would otherwise
        // close silently — an open node saying nothing about why it stopped constraining.
        open["description"] = refNode["description"]?.GetValue<string>() is { } description
            ? description + " (" + continues + ")"
            : continues;
        return open;
    }

    /// <summary>Walk a same-document JSON pointer ("#", "#/a/b/0") against <paramref name="root"/>, or null if it
    /// does not resolve.</summary>
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

    /// <summary>Match the SDK's own wire conventions so the generated arm reads like every other published schema
    /// (camelCase member names come from each DTO's explicit <c>[JsonPropertyName]</c>, not from a policy).</summary>
    static readonly JsonSerializerOptions SchemaJson = new(JsonSerializerDefaults.Web);
}
