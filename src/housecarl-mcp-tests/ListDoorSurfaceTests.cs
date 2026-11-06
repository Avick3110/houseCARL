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
        ToolSurface.Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters().Where(p => p.ParameterType == typeof(ListArg))
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
}
