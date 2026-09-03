using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// The tool surface as the SDK's schema generator emits it, BEFORE <see cref="ToolSchemas.PublishSchemas"/> runs —
/// the real input to both published-schema passes, read the way the server gets it.
///
/// <para>Why it exists: a guard whose claim is about a POPULATION must read that population, not a hand-written
/// stand-in for it. Both callers previously named their subjects — the expansion arm walked one of the five
/// ref-carrying tools, and the emission-grammar arm re-derived standalone schemas for six hand-named DTO types
/// instead of reading the SDK emission path its claim is about. Deriving the subject set here makes the guards'
/// coverage a consequence of the surface rather than of who remembered to update a list (#451, the
/// derive-don't-enumerate ruling).</para>
///
/// <para>The registration deliberately mirrors <c>Program.AddMcp</c>'s scan line and stops there: no transport,
/// no server identity, and above all no <see cref="ToolSchemas.PublishSchemas"/>, because that pass mutates
/// <c>InputSchema</c> in place and would leave nothing pre-flatten to read. This container is its own, so the
/// server's own registration is untouched either way.</para>
/// </summary>
internal static class PreFlattenSurface
{
    /// <summary>One tool's name and its raw generated input schema (a fresh parse — callers may mutate it).</summary>
    internal readonly record struct Tool(string Name, JsonObject Schema);

    /// <summary>Every registered tool's pre-flatten schema, in registration order. Throws rather than returning
    /// empty: a derivation that finds no tools is a broken derivation, and every arm built on it would pass
    /// vacuously.</summary>
    internal static IReadOnlyList<Tool> Read()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(HousecarlMcp.ToolSurface.Assembly);
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection
            ?? throw new InvalidOperationException(
                "PreFlattenSurface: the MCP options carry no ToolCollection — the assembly scan did not run.");

        var list = new List<Tool>();
        foreach (var tool in tools)
        {
            if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject schema)
                throw new InvalidOperationException(
                    $"PreFlattenSurface: {tool.ProtocolTool.Name}'s generated schema is not a JSON object.");
            list.Add(new Tool(tool.ProtocolTool.Name, schema));
        }

        if (list.Count == 0)
            throw new InvalidOperationException(
                "PreFlattenSurface: the assembly scan registered no tools.");
        return list;
    }

    /// <summary>Every object carrying a <c>$ref</c> member, by JSON pointer, in <paramref name="root"/>.
    /// Collects the MEMBER wherever it appears and whatever it holds — no pointer-form test, no resolve test —
    /// so a spelling neither pass understands is still counted rather than silently skipped.</summary>
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
}
