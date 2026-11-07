using System.Text.Json;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The walk arguments a test passes from C#: asset_status's walk= as the JSON the wire sends, and
/// exclusions written 'Type:severity' for short.</summary>
internal static class Walks
{
    /// <summary>walk=true: the asset walk at its defaults.</summary>
    public static JsonElement On => Of("true");

    /// <summary>walk= as this JSON text, e.g. <c>{"depth": 1}</c>.</summary>
    public static JsonElement Of(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>One {match, severity} per 'Type:severity' entry; null for null.</summary>
    public static RecordsTools.RecordsWalkExclusion[]? Excl(params string[]? specs)
        => specs?.Select(s => s.Split(':', 2))
                 .Select(p => new RecordsTools.RecordsWalkExclusion { match = p[0], severity = p.Length > 1 ? p[1] : null })
                 .ToArray();
}
