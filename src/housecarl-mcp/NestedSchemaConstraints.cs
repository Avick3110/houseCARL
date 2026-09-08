using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>A closed value set the server validates a nested member against. One case per TABLE, never per member:
/// the values come from the collection the gate itself reads, so a verb added there reaches the published schema.</summary>
internal enum SchemaVocabulary
{
    /// <summary>Every write verb the apply surface accepts (<see cref="WriteVerbs.All"/>).</summary>
    WriteVerbs,
    /// <summary>The create surface's verbs (<see cref="HousecarlCore.WriteVerbs.OnCreate"/>).</summary>
    CreateVerbs,
}

/// <summary>The member is one the server REFUSES a call without — published as a JSON Schema <c>required</c> entry on
/// the object that carries it. Declared on the member because a shape is reached from several parameters
/// (<c>compose</c> appears under four) and a path list would have to name each occurrence, including the ones the
/// recursion bound expands.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SchemaRequiredAttribute : Attribute { }

/// <summary>The member's legal values are the named closed set — published as a JSON Schema <c>enum</c>.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SchemaValuesAttribute(SchemaVocabulary vocabulary) : Attribute
{
    public SchemaVocabulary Vocabulary { get; } = vocabulary;
}

/// <summary>
/// The third publication pass: <c>required</c> and <c>enum</c> INSIDE a parameter, where the SDK's generator emits
/// neither. It derives both from the C# shapes the server binds and validates against — the marked members and the
/// verb tables — so a client can check a nested call before sending it, and nothing here is a second copy of a fact.
///
/// <para>Runs LAST, after the flatten, so every recursion-expanded copy of a shape is stamped as well as the first.
/// The walk is TYPE-DIRECTED and the schema bounds it: it descends only where the published document still spells a
/// shape out, so the open node that closes a recursive chain constrains nothing, exactly as before.</para>
///
/// <para>Changes only what is PUBLISHED. Neither reader of a call's arguments consults it: <see cref="ToolCallShim"/>
/// reads a schema's top-level <c>properties</c> only, and <c>ListParams.Read&lt;T&gt;</c> reads no schema at all.</para>
/// </summary>
internal static class NestedSchemaConstraints
{
    /// <summary>The values behind a vocabulary — the table the gate reads, never a list written here.</summary>
    internal static IReadOnlyList<string> Values(SchemaVocabulary vocabulary) => vocabulary switch
    {
        SchemaVocabulary.WriteVerbs => HousecarlCore.WriteVerbs.All,
        SchemaVocabulary.CreateVerbs => HousecarlCore.WriteVerbs.OnCreate,
        _ => throw new ArgumentOutOfRangeException(nameof(vocabulary), vocabulary, "No table backs this vocabulary."),
    };

    /// <summary>Every tool parameter with the CLR type its published schema was generated from. Reflected off the
    /// tool surface rather than listed, so a new parameter carrying a marked shape is stamped without an entry here.
    /// A parameter declared <see cref="System.Text.Json.JsonElement"/> for the <c>@file</c> union carries its element
    /// type nowhere but <see cref="ToolSchemas.FileListParams"/>, so that row supplies it.</summary>
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

    /// <summary>Stamp one tool's schema from the roots of its own parameters. Returns false — leaving the document
    /// untouched — when it is not the shape this expects or no member under it is marked.</summary>
    internal static bool Stamp(JsonObject root, IEnumerable<(string Parameter, Type Type)> parameters)
    {
        if (root["properties"] is not JsonObject props) return false;
        bool changed = false;
        foreach (var (name, type) in parameters)
            if (props[name] is JsonObject node) changed |= Walk(type, node);
        return changed;
    }

    /// <summary>Walk one CLR type against the schema node published for it, stamping as it goes. The SCHEMA decides
    /// how far it goes: a node that no longer spells out its members ends the branch, which is what keeps the walk
    /// finite over the recursive write DTOs.</summary>
    static bool Walk(Type type, JsonObject node)
    {
        // A union node's arms are alternative spellings of the SAME parameter, so each is walked against the same
        // type; an arm that is not this shape (the "@<path>" string) simply carries nothing to stamp.
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
        var required = new JsonArray();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            if (props[wire] is not JsonObject member) continue;

            if (property.GetCustomAttribute<SchemaRequiredAttribute>() is not null) required.Add(wire);
            if (property.GetCustomAttribute<SchemaValuesAttribute>() is { } values)
            {
                var legal = new JsonArray();
                foreach (var v in Values(values.Vocabulary)) legal.Add(v);
                member["enum"] = legal;
                changed = true;
            }
            changed |= Walk(property.PropertyType, member);
        }

        if (required.Count > 0) { node["required"] = required; changed = true; }
        return changed;
    }

    /// <summary>The element type of a published ARRAY shape, or null when the type is not one. Arrays only: every
    /// list-valued wire shape on this surface is <c>T[]</c>, and a dictionary publishes
    /// <c>additionalProperties</c> rather than <c>items</c>.</summary>
    static Type? ElementType(Type type) => type.IsArray ? type.GetElementType() : null;
}
