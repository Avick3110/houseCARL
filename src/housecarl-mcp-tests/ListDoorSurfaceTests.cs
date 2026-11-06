using System.Reflection;
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Server;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The list door on the served surface: every list parameter is published as an array of strings, as
/// before, and its text says what file its <c>["@&lt;absolute path&gt;"]</c> takes. The rows are derived from the
/// tool methods' <see cref="ListArg"/> parameters, never named by hand.</summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ListDoorSurfaceTests
{
    readonly ServerFixture _s;
    public ListDoorSurfaceTests(ServerFixture s) => _s = s;

    public static IEnumerable<object[]> ListParameters() =>
        ToolMethods().SelectMany(m => m.GetParameters().Where(p => p.ParameterType == typeof(ListArg))
                              .Select(p => new object[] { m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, p.Name! }));

    [Fact]
    public void EveryListParameterOfTheIssueIsAListArg()
    {
        var rows = ListParameters().Select(r => $"{r[0]}.{r[1]}").ToHashSet();
        foreach (var expected in new[] { ToolNames.AssetStatus + ".records", ToolNames.AssetStatus + ".under",
                                         ToolNames.NifInspect + ".mesh_paths", ToolNames.Check + ".formids",
                                         ToolNames.BsaList + ".under", ToolNames.MergePlugins + ".plugins" })
            Assert.Contains(expected, rows);
    }

    [Theory]
    [MemberData(nameof(ListParameters))]
    public void AListParameterIsPublishedAsAnArrayOfStringsWithItsFileSentence(string tool, string parameter)
    {
        var prop = _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("properties").GetProperty(parameter);
        static bool Is(JsonElement type, string kind) => type.ValueKind == JsonValueKind.String
            ? type.GetString() == kind
            : type.EnumerateArray().Any(t => t.GetString() == kind);
        Assert.True(Is(prop.GetProperty("type"), "array"), prop.ToString());
        Assert.True(Is(prop.GetProperty("items").GetProperty("type"), "string"), prop.ToString());
        Assert.Contains("[\"@<absolute path>\"]", prop.GetProperty("description").GetString());
    }

    /// <summary>An optional list stays optional on the wire: its published type admits null, as a string[]? did.</summary>
    [Fact]
    public void AnOptionalListParameterIsPublishedNullable()
    {
        var nullability = new NullabilityInfoContext();
        var optional = ToolMethods().SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType == typeof(ListArg) && nullability.Create(p).WriteState == NullabilityState.Nullable)
                .Select(p => (Tool: m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, Param: p.Name!)))
            .ToList();
        Assert.NotEmpty(optional);
        foreach (var (tool, param) in optional)
        {
            var type = _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("properties").GetProperty(param).GetProperty("type");
            Assert.True(type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.GetString() == "null"),
                        $"{tool}.{param}: {type}");
        }
    }

    /// <summary>A spec object's own list member (<c>plugins.names</c>, <c>walk.seed_paths</c>, …) publishes one
    /// <see cref="ListText"/> sentence verbatim, as a <see cref="ListArg"/> parameter does.</summary>
    [Fact]
    public void ASpecObjectListMemberCarriesItsListTextSentence()
    {
        var sentences = new[] { ListText.FormIds, ListText.Paths, ListText.Plugins, ListText.Names };
        var members = ToolMethods().SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType.IsClass && p.ParameterType != typeof(string) && p.ParameterType != typeof(ListArg))
                .SelectMany(p => p.ParameterType.GetProperties()
                    .Where(q => q.PropertyType == typeof(string[]))
                    .Select(q => (Tool: m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, Param: p.Name!, Member: q.Name))))
            .ToList();
        Assert.Contains(members, x => x is { Param: "plugins", Member: "names" });
        foreach (var (tool, param, member) in members)
        {
            var text = _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("properties").GetProperty(param)
                         .GetProperty("properties").GetProperty(member).GetProperty("description").GetString();
            Assert.True(sentences.Any(s => text!.Contains(s)), $"{tool}.{param}.{member}: {text}");
        }
    }

    /// <summary>No tool takes a list of strings except as a <see cref="ListArg"/>, so none skips the door.</summary>
    [Fact]
    public void NoToolParameterIsAStringListOutsideTheDoor()
    {
        var stray = ToolMethods().SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType != typeof(ListArg) && typeof(IEnumerable<string>).IsAssignableFrom(p.ParameterType))
                .Select(p => $"{m.Name}.{p.Name}"))
            .ToList();
        Assert.Empty(stray);
    }

    static IEnumerable<MethodInfo> ToolMethods() =>
        ToolSurface.Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
