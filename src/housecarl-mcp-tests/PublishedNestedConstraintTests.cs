using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HousecarlMcp;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The <c>required</c> and <c>enum</c> the server publishes INSIDE a parameter — the constraints the SDK's generator
/// emits only at the top level, stamped by <see cref="NestedSchemaConstraints"/> from the marked members and the
/// verb tables.
///
/// <para>Nothing here is hand-listed. The subjects are found by walking the SERVED document and identifying each
/// object node by its own member-name set; the expectation is then read off the C# shape that set names — its
/// <c>[SchemaRequired]</c> members, and <c>NestedSchemaConstraints.Values</c> for a <c>[SchemaValues]</c> one. So a
/// verb added to <c>WriteVerbs.All</c> moves the published enum and this expectation in the same commit, and a verb
/// dropped from one and not the other reddens here rather than shipping.</para>
///
/// <para>The walk is written independently of the publication pass's: this one is type-blind and reads only the JSON,
/// which is what keeps it from agreeing with a stamping bug by construction.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedNestedConstraintTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public PublishedNestedConstraintTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    // ---- the C# shapes, and what each says it constrains --------------------------------------------------

    /// <summary>One marked wire shape: the member names it publishes, the ones it declares required, and the closed
    /// value set of each member that names one.</summary>
    sealed record Shape(Type Type, string[] Members, string[] Required, Dictionary<string, string[]> Enums);

    static IReadOnlyList<Shape>? _shapes;

    /// <summary>Every type on the tool surface that marks at least one member. Found by reflection, so a newly
    /// marked shape becomes a subject without an entry here.</summary>
    static IReadOnlyList<Shape> MarkedShapes()
    {
        if (_shapes is not null) return _shapes;

        var shapes = new List<Shape>();
        foreach (var type in ToolSurface.Assembly.GetTypes())
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                 .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                                 .ToArray();
            if (properties.Length == 0) continue;

            string Wire(PropertyInfo p) => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

            var required = properties.Where(p => p.GetCustomAttribute<SchemaRequiredAttribute>() is not null)
                                     .Select(Wire).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var enums = properties
                .Select(p => (Wire: Wire(p), Values: p.GetCustomAttribute<SchemaValuesAttribute>()))
                .Where(x => x.Values is not null)
                .ToDictionary(x => x.Wire,
                              x => NestedSchemaConstraints.Values(x.Values!.Vocabulary).ToArray(),
                              StringComparer.Ordinal);
            if (required.Length == 0 && enums.Count == 0) continue;

            shapes.Add(new Shape(type, properties.Select(Wire).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                                 required, enums));
        }
        return _shapes = shapes;
    }

    static string Key(IEnumerable<string> members) => string.Join(",", members.OrderBy(n => n, StringComparer.Ordinal));

    // ---- the served document, walked type-blind ----------------------------------------------------------

    /// <summary>One published object node: where it sits, the member names it declares, its <c>required</c> list, and
    /// the <c>enum</c> each member carries.</summary>
    sealed record Node(string Tool, string Path, string[] Members, string[] Required, Dictionary<string, string[]> Enums);

    List<Node> PublishedObjects()
    {
        var found = new List<Node>();
        foreach (var tool in _s.PublishedNames)
        {
            if (!_s.PublishedTools[tool].TryGetProperty("inputSchema", out var schema)) continue;
            if (!schema.TryGetProperty("properties", out var props)) continue;
            // The tool's own root is skipped: its required/enum come off the C# signature, not this pass.
            foreach (var parameter in props.EnumerateObject()) Walk(tool, parameter.Name, parameter.Value, found);
        }
        return found;
    }

    static void Walk(string tool, string path, JsonElement node, List<Node> found)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        if (node.TryGetProperty("anyOf", out var arms) && arms.ValueKind == JsonValueKind.Array)
            foreach (var arm in arms.EnumerateArray()) Walk(tool, path, arm, found);
        if (node.TryGetProperty("items", out var items)) Walk(tool, path + "[]", items, found);
        if (!node.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return;

        var members = new List<string>();
        var enums = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var member in props.EnumerateObject())
        {
            members.Add(member.Name);
            if (member.Value.ValueKind == JsonValueKind.Object
                && member.Value.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
                enums[member.Name] = values.EnumerateArray().Select(v => v.GetString() ?? "<non-string>").ToArray();
            Walk(tool, path + "." + member.Name, member.Value, found);
        }

        var required = node.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(v => v.GetString() ?? "<non-string>")
                 .OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        found.Add(new Node(tool, path, members.OrderBy(n => n, StringComparer.Ordinal).ToArray(), required, enums));
    }

    // ---- the assertions ----------------------------------------------------------------------------------

    /// <summary>Identifying a node by its member set is only sound while no two marked shapes share one.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void NoTwoMarkedShapesPublishTheSameMemberSet()
    {
        var clashes = MarkedShapes().GroupBy(s => Key(s.Members))
                                    .Where(g => g.Count() > 1)
                                    .Select(g => string.Join(" = ", g.Select(s => s.Type.Name)))
                                    .ToArray();

        Assert.Equal(Array.Empty<string>(), clashes);
    }

    [Fact]
    public void EveryPublishedOccurrenceOfAMarkedShapeCarriesItsRequiredAndEnum()
    {
        var shapes = MarkedShapes().ToDictionary(s => Key(s.Members), s => s);
        var problems = new List<string>();
        int occurrences = 0;

        foreach (var node in PublishedObjects())
        {
            if (!shapes.TryGetValue(Key(node.Members), out var shape)) continue;
            occurrences++;

            if (!node.Required.SequenceEqual(shape.Required))
                problems.Add($"{node.Tool} {node.Path}: required=[{string.Join(",", node.Required)}], " +
                             $"{shape.Type.Name} marks [{string.Join(",", shape.Required)}]");

            foreach (var (member, expected) in shape.Enums)
            {
                if (!node.Enums.TryGetValue(member, out var published))
                { problems.Add($"{node.Tool} {node.Path}.{member}: no enum published; {shape.Type.Name} names a closed set"); continue; }
                if (!published.SequenceEqual(expected))
                    problems.Add($"{node.Tool} {node.Path}.{member}: enum=[{string.Join(",", published)}], " +
                                 $"the table holds [{string.Join(",", expected)}]");
            }
            foreach (var member in node.Enums.Keys.Where(m => !shape.Enums.ContainsKey(m)))
                problems.Add($"{node.Tool} {node.Path}.{member}: publishes an enum {shape.Type.Name} does not name");
        }

        _out.WriteLine($"{occurrences} published occurrence(s) of {MarkedShapes().Count} marked shape(s)");
        Assert.Equal(Array.Empty<string>(), problems.ToArray());
    }

    /// <summary>The reverse direction: a constraint on the served surface that no marked shape asked for is a
    /// stamping bug, and the test above would pass over it.</summary>
    [Fact]
    public void NoPublishedNestedObjectCarriesAConstraintNoMarkedShapeAsksFor()
    {
        var shapes = MarkedShapes().Select(s => Key(s.Members)).ToHashSet(StringComparer.Ordinal);
        var strays = PublishedObjects()
            .Where(n => (n.Required.Length > 0 || n.Enums.Count > 0) && !shapes.Contains(Key(n.Members)))
            .Select(n => $"{n.Tool} {n.Path}: required=[{string.Join(",", n.Required)}] " +
                         $"enum on [{string.Join(",", n.Enums.Keys)}]")
            .ToArray();

        Assert.Equal(Array.Empty<string>(), strays);
    }

    /// <summary>Without this, every assertion above passes vacuously the moment the shapes stop being found or the
    /// stamped nodes stop being reached.</summary>
    [Fact]
    public void EveryMarkedShapeIsReachedOnTheServedSurface()
    {
        var seen = PublishedObjects().Select(n => Key(n.Members)).ToHashSet(StringComparer.Ordinal);
        var missing = MarkedShapes().Where(s => !seen.Contains(Key(s.Members))).Select(s => s.Type.Name).ToArray();

        Assert.NotEmpty(MarkedShapes());
        Assert.Equal(Array.Empty<string>(), missing);
        Assert.All(MarkedShapes().SelectMany(s => s.Enums), e => Assert.NotEmpty(e.Value));
    }
}
