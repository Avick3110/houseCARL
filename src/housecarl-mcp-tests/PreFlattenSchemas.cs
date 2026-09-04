using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace HousecarlMcpTests;

/// <summary>
/// The tool surface as the SDK's schema generator emits it, BEFORE <c>ToolSchemas.PublishSchemas</c> runs,
/// plus the pointer arithmetic the published-schema tests stand on. Test machinery, not product code; the
/// derivation rules here are themselves checked in <see cref="PublishedSchemaShapeTests"/>. Kept here rather
/// than reached across into <c>housecarl-generator</c>'s <c>PreFlattenSurface</c>, which is internal to a
/// project this test assembly is not a friend of.
///
/// <para>The registration mirrors the server's own scan line and stops there: no transport, no server
/// identity, and above all no schema publication pass, because that pass mutates <c>InputSchema</c> in place
/// and would leave nothing pre-flatten to read.</para>
/// </summary>
internal static class PreFlattenSchemas
{
    /// <summary>One tool's name and its raw generated input schema (a fresh parse — callers may mutate it).</summary>
    internal readonly record struct Tool(string Name, JsonObject Schema);

    /// <summary>Every registered tool's pre-flatten schema, in registration order. Throws rather than
    /// returning empty: an empty result would make every test built on it pass vacuously.</summary>
    internal static IReadOnlyList<Tool> Read()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(ToolSurface.Assembly);
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection
            ?? throw new InvalidOperationException(
                "PreFlattenSchemas: the MCP options carry no ToolCollection — the assembly scan did not run.");

        var injected = InjectedParameters();
        var list = new List<Tool>();
        foreach (var tool in tools)
        {
            if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject schema)
                throw new InvalidOperationException(
                    $"PreFlattenSchemas: {tool.ProtocolTool.Name}'s generated schema is not a JSON object.");
            if (injected.TryGetValue(tool.ProtocolTool.Name, out var notCallerFacing))
                DropParameters(schema, notCallerFacing);
            list.Add(new Tool(tool.ProtocolTool.Name, schema));
        }

        if (list.Count == 0)
            throw new InvalidOperationException("PreFlattenSchemas: the assembly scan registered no tools.");
        return list;
    }

    /// <summary>
    /// The parameters the real server INJECTS rather than takes from a caller, per tool — a tool method's
    /// parameters carrying no <c>[Description]</c>, the same filter the wire-path tests pin against
    /// <c>tools/list</c>.
    ///
    /// <para>This container registers no houseCARL services, so the SDK cannot tell an injected
    /// <c>LoadOrderService</c> from a caller argument and publishes it as one — required, and with whatever
    /// schema the generator makes of the class. The live server registers them and does not. Filtering them
    /// out is what makes a schema read here comparable with the one the server serves.</para>
    /// </summary>
    static Dictionary<string, HashSet<string>> InjectedParameters()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in ToolSurface.Assembly.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Static | BindingFlags.Instance
                                                 | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { Name: { Length: > 0 } name })
                    continue;
                var notCallerFacing = method.GetParameters()
                    .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is null && p.Name is not null)
                    .Select(p => p.Name!)
                    .ToHashSet(StringComparer.Ordinal);
                if (notCallerFacing.Count > 0) map[name] = notCallerFacing;
            }
        return map;
    }

    static void DropParameters(JsonObject schema, HashSet<string> names)
    {
        if (schema["properties"] is JsonObject props)
            foreach (var n in names) props.Remove(n);

        if (schema["required"] is not JsonArray required) return;
        for (var i = required.Count - 1; i >= 0; i--)
            if (required[i]?.GetValue<string>() is { } r && names.Contains(r)) required.RemoveAt(i);
        if (required.Count == 0) schema.Remove("required");
    }

    /// <summary>Every object carrying a <c>$ref</c> member, by JSON pointer. Collects the MEMBER wherever it
    /// appears and whatever it holds — no pointer-form test, no resolve test — so a spelling neither
    /// publication pass understands is still counted rather than silently skipped.</summary>
    internal static List<(string Path, JsonObject Node)> RefNodes(JsonObject root)
    {
        var found = new List<(string, JsonObject)>();
        Walk(root, "#");
        return found;

        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.ContainsKey("$ref")) found.Add((path, obj));
                    foreach (var kv in obj.ToList()) Walk(kv.Value, $"{path}/{kv.Key}");
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++) Walk(arr[i], $"{path}/{i}");
                    break;
            }
        }
    }

    /// <summary>The <c>$ref</c> pointer on a node, or null when the member is absent or does not hold a JSON
    /// string. Safe on purpose: a non-string <c>$ref</c> is a form neither pass understands, and the invariant
    /// test is what reports it — reading it as a string here would throw before that test could.</summary>
    internal static string? RefPointer(JsonObject node) =>
        node["$ref"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Every DISTINCT (step, cycle) derivable from one tool's pre-flatten <c>$ref</c> sites. A site whose
    /// pointer is a proper ancestor of its own path is a positional back-reference, and the path segment
    /// between them is one turn of that cycle.
    ///
    /// <para>Two rules, neither exercised by today's surface, so each has its own fixture test: the prefix must
    /// end ON a JSON-pointer segment boundary, or a sibling whose wire name merely EXTENDS the target's reads as
    /// a false ancestor and the derived step resolves nowhere; and every distinct pair is returned rather than
    /// the last, so a caller can refuse a tool carrying more than one cycle instead of measuring whichever the
    /// walk saw last.</para>
    /// </summary>
    internal static List<(string Step, string Cycle)> DeriveRecursions(
        IEnumerable<(string Path, JsonObject Node)> sites)
    {
        var found = new List<(string Step, string Cycle)>();
        foreach (var (path, node) in sites)
        {
            if (RefPointer(node) is not { } p || path.Length <= p.Length
                || !path.StartsWith(p, StringComparison.Ordinal) || path[p.Length] != '/') continue;
            var pair = (Step: path[p.Length..], Cycle: p);
            if (!found.Contains(pair)) found.Add(pair);
        }
        return found;
    }

    /// <summary>True when a published node states structure rather than merely closing. The terminator emits
    /// exactly a <c>type</c> and a <c>description</c>, so any member beyond those two is the node still
    /// saying what it contains — which is what amputation removes.</summary>
    internal static bool SpellsOutStructure(JsonElement node)
    {
        foreach (var p in node.EnumerateObject())
            if (p.Name is not ("type" or "description")) return true;
        return false;
    }

    /// <summary>Walk one recursive chain from <paramref name="start"/> by repeatedly appending the derived
    /// <paramref name="step"/>, counting the levels spelled out concretely and reporting whether it ends on
    /// the open node the bound writes.</summary>
    internal static (int Levels, bool ClosedOpen, string Detail) WalkRecursion(
        JsonElement schema, string start, string step)
    {
        var levels = 0;
        var cur = start;
        while (true)
        {
            if (Pointer(schema, cur) is not { ValueKind: JsonValueKind.Object } node)
                return (levels, false, $"level {levels + 1} does not resolve to an object schema");
            if (!SpellsOutStructure(node))
                return (levels,
                    node.TryGetProperty("type", out _)
                    && node.TryGetProperty("description", out var d)
                    && d.GetString()?.Contains("Nesting continues", StringComparison.Ordinal) == true,
                    $"closed at level {levels + 1}: {Trunc(node)}");
            levels++;
            cur += step;
            // The bound exists so this terminates; an unbounded chain is a finding, not a hang.
            if (levels > 8) return (levels, false, $"still concrete at level {levels} — the chain is not closing");
        }
    }

    /// <summary>Every <c>$ref</c> MEMBER anywhere in a schema document, by pointer, with what it holds.
    /// <para>No filter on the value: a non-string <c>$ref</c>, a plain-name anchor, an external URI and a
    /// same-document pointer all count the same, because the invariant is about the member being there at
    /// all. A detector that inherits the publication pass's own blind spot — gating on a leading '#' the way
    /// the pass does — measures nothing at the only moment it matters.</para></summary>
    internal static IEnumerable<(string Path, JsonElement Value)> CollectRefMembers(JsonElement node, string path)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in node.EnumerateObject())
                {
                    if (p.Name == "$ref") yield return (path, p.Value);
                    foreach (var r in CollectRefMembers(p.Value, $"{path}/{p.Name}")) yield return r;
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    foreach (var r in CollectRefMembers(item, $"{path}/{i}")) yield return r;
                    i++;
                }
                break;
        }
    }

    /// <summary>Anything not starting with '#' is external and counts as resolvable — these tests police the
    /// pointers the publication pass rebases, not the whole of JSON Schema.</summary>
    internal static bool PointerResolves(JsonElement root, string reference)
        => !reference.StartsWith('#') || Pointer(root, reference) is not null;

    /// <summary>Walk a same-document JSON pointer ("#/a/b/0"), or null where it does not resolve.</summary>
    internal static JsonElement? Pointer(JsonElement root, string reference)
    {
        var cur = root;
        foreach (var raw in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out var next)) return null;
                cur = next;
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var i) || i < 0 || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
            else return null;
        }
        return cur;
    }

    /// <summary>The tail of a JSON pointer, for theory-row labels that stay readable at 100-odd characters.</summary>
    internal static string Tail(string pointer)
    {
        var parts = pointer.Split('/');
        return parts.Length <= 4 ? pointer : ".../" + string.Join("/", parts[^4..]);
    }

    internal static string Trunc(JsonElement e)
    {
        var s = e.GetRawText();
        return s.Length > 240 ? s[..240] + "…" : s;
    }
}
