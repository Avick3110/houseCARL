using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>A closed value set the server validates a nested member against; one case per table, never per member.</summary>
internal enum SchemaVocabulary
{
    /// <summary>Every write verb the apply surface accepts (<see cref="WriteVerbs.All"/>).</summary>
    WriteVerbs,
    /// <summary>The create surface's verbs (<see cref="HousecarlCore.WriteVerbs.OnCreate"/>).</summary>
    CreateVerbs,
    /// <summary>The verbs a compose's nested sets accept (<see cref="HousecarlCore.WriteVerbs.InCompose"/>).</summary>
    ComposeVerbs,
}

/// <summary>The member is one the server refuses a call without, published as a JSON Schema <c>required</c> entry.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SchemaRequiredAttribute : Attribute { }

/// <summary>The member's legal values are the named closed set — published as a JSON Schema <c>enum</c>.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SchemaValuesAttribute(SchemaVocabulary vocabulary) : Attribute
{
    public SchemaVocabulary Vocabulary { get; } = vocabulary;
}

/// <summary>The third publication pass: <c>required</c> and <c>enum</c> inside a parameter, derived from the marked
/// members and the verb tables; contract in <c>docs/architecture/tool-schema-publication.md</c>.</summary>
internal static class NestedSchemaConstraints
{
    /// <summary>The values behind a vocabulary — the table the gate reads, never a list written here.</summary>
    internal static IReadOnlyList<string> Values(SchemaVocabulary vocabulary) => vocabulary switch
    {
        SchemaVocabulary.WriteVerbs => HousecarlCore.WriteVerbs.All,
        SchemaVocabulary.CreateVerbs => HousecarlCore.WriteVerbs.OnCreate,
        SchemaVocabulary.ComposeVerbs => HousecarlCore.WriteVerbs.InCompose,
        _ => throw new ArgumentOutOfRangeException(nameof(vocabulary), vocabulary, "No table backs this vocabulary."),
    };

    /// <summary>Every tool parameter with the CLR type its published schema was generated from, reflected off the tool
    /// surface; an <c>@file</c> parameter's element type comes from <see cref="ToolSchemas.FileListParams"/>.</summary>
    internal static IReadOnlyList<(string Tool, string Parameter, Type Type)> ParameterRoots()
    {
        var rows = new List<(string, string, Type)>();
        foreach (var type in ToolSurface.Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Static | BindingFlags.Instance
                                                   | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name is not { Length: > 0 } tool)
                    continue;
                foreach (var p in method.GetParameters())
                {
                    if (p.Name is not { Length: > 0 } name) continue;
                    var declared = ToolSchemas.FileListParams
                        .FirstOrDefault(f => f.Tool == tool && f.Parameter == name).ElementArrayType;
                    rows.Add((tool, name, declared ?? p.ParameterType));
                }
            }
        }
        return rows;
    }

    /// <summary>Stamp one tool's schema from the roots of its own parameters, or false and untouched.</summary>
    internal static bool Stamp(JsonObject root, IEnumerable<(string Parameter, Type Type)> parameters)
    {
        if (root["properties"] is not JsonObject props) return false;
        bool changed = false;
        foreach (var (name, type) in parameters)
            if (props[name] is JsonObject node) changed |= Walk(type, node);
        return changed;
    }

    /// <summary>Walk one CLR type against the schema node published for it, stamping as it goes; a node that no longer
    /// spells out its members ends the branch, which keeps the walk finite over the recursive write DTOs.</summary>
    static bool Walk(Type type, JsonObject node)
    {
        // A union node's arms are alternative spellings of the same parameter, so each is walked against the type.
        if (node["anyOf"] is JsonArray arms)
        {
            bool any = false;
            foreach (var arm in arms) if (arm is JsonObject o) any |= Walk(type, o);
            return any;
        }

        if (ElementType(type) is { } element)
            return node["items"] is JsonObject items && Walk(element, items);

        if (node["properties"] is not JsonObject props) return false;

        bool changed = false;
        var required = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            if (props[wire] is not JsonObject member) continue;

            if (property.GetCustomAttribute<SchemaRequiredAttribute>() is not null)
            {
                required.Add(wire);
                // Ordered before the enum stamp so both read the same type.
                changed |= DropNull(member);
            }
            if (property.GetCustomAttribute<SchemaValuesAttribute>() is { } values)
            {
                var legal = new JsonArray();
                foreach (var v in Values(values.Vocabulary)) legal.Add(v);
                // JSON Schema applies enum to every instance, null included, so a nullable member keeps null.
                if (AdmitsNull(member)) legal.Add((JsonNode?)null);
                member["enum"] = legal;
                changed = true;
            }
            changed |= Walk(property.PropertyType, member);
        }

        // Union, never assignment: the generator emits its own nested `required` for a non-nullable member.
        if (required.Count > 0)
        {
            var names = new List<string>();
            if (node["required"] is JsonArray already)
                foreach (var entry in already)
                    if (entry is JsonValue v && v.TryGetValue<string>(out var name)
                        && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            foreach (var name in required)
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);

            var merged = new JsonArray();
            foreach (var name in names) merged.Add(name);
            node["required"] = merged;
            changed = true;
        }
        return changed;
    }

    /// <summary>Drop the null arm from a member's published type, in both spellings the generator uses; a shape that is
    /// null and nothing else is left alone.</summary>
    static bool DropNull(JsonObject member)
    {
        if (member["anyOf"] is JsonArray arms)
        {
            var keptArms = arms.Where(a => a is not JsonObject o || !IsNullOnly(o)).ToList();
            bool inner = false;
            foreach (var arm in keptArms) if (arm is JsonObject o) inner |= DropNull(o);
            if (keptArms.Count == 0 || keptArms.Count == arms.Count) return inner;
            member["anyOf"] = new JsonArray(keptArms.Select(a => a?.DeepClone()).ToArray());
            return true;
        }
        if (member["type"] is not JsonArray types) return false;
        var kept = new List<JsonNode?>();
        foreach (var t in types)
            if (t is not JsonValue v || !v.TryGetValue<string>(out var s) || s != "null") kept.Add(t?.DeepClone());
        if (kept.Count == 0 || kept.Count == types.Count) return false;
        member["type"] = kept.Count == 1 ? kept[0] : new JsonArray(kept.ToArray());
        return true;
    }

    /// <summary>Is this union arm the null arm — the <c>{"type":"null"}</c> the generator pairs a value arm with?</summary>
    static bool IsNullOnly(JsonObject arm)
        => arm["type"] is JsonValue v && v.TryGetValue<string>(out var s) && s == "null";

    /// <summary>Does the published type of this member accept a JSON null? Read off the document, not the CLR type.</summary>
    static bool AdmitsNull(JsonObject member) =>
        member["anyOf"] is JsonArray arms
            ? arms.Any(a => a is JsonObject o && AdmitsNull(o))
            : member["type"] switch
            {
                JsonArray types => types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s == "null"),
                JsonValue single => single.TryGetValue<string>(out var s) && s == "null",
                _ => false,
            };

    /// <summary>The element type of a published array shape, or null when the type is not one.</summary>
    static Type? ElementType(Type type) => type.IsArray ? type.GetElementType() : null;
}
