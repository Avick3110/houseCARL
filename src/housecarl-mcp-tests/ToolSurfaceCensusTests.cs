using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// #470's own class, guarded from both sides: what the source DECLARES and what the wire PUBLISHES must be
/// the same set.
///
/// The bug was that they were not, and could not be seen to differ: <c>CheckTools</c> carried
/// <c>[McpServerTool]</c> on its method but no <c>[McpServerToolType]</c> on the class, and
/// <c>WithToolsFromAssembly()</c> discovers only marked types. Every count anyone quoted came from the
/// attributes, so the tool read as shipped for twelve days while no client could call it.
///
/// Neither side is a hand list: the declared side is reflected over the housecarl-mcp assembly, the
/// published side is `tools/list` off the running server.
/// </summary>
public sealed class ToolSurfaceCensusTests
{
    /// <summary>Every method in housecarl-mcp carrying [McpServerTool], with the type that declares it.</summary>
    static (Type Type, MethodInfo Method, McpServerToolAttribute Attr)[] DeclaredToolMethods() =>
        typeof(HousecarlMcp.CheckTools).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                              .Select(m => (Type: t, Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()!))
                              .Where(x => x.Attr is not null))
            .ToArray();

    public static IEnumerable<object[]> DeclaringTypes() =>
        DeclaredToolMethods().Select(x => x.Type).Distinct().Select(t => new object[] { t.FullName! });

    /// <summary>
    /// The defect itself, reflected: a type that declares a tool must be discoverable as a tool type.
    /// One named cell per declaring type, so a RED says which file to open.
    /// </summary>
    [Theory]
    [Trait("tier", "unit")]
    [MemberData(nameof(DeclaringTypes))]
    public void ATypeThatDeclaresAToolCarriesTheTypeAttribute_OtherwiseWithToolsFromAssemblyNeverSeesIt(string typeName)
    {
        var type = DeclaredToolMethods().First(x => x.Type.FullName == typeName).Type;
        Assert.True(type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null,
            $"{typeName} declares [McpServerTool] methods but carries no [McpServerToolType]. " +
            "Program.cs registers with WithToolsFromAssembly(), which discovers only marked types, so every " +
            "tool in this file is unreachable by any caller. This is #470 exactly.");
    }

    /// <summary>Every declared tool must carry an explicit Name — a census cannot derive what it cannot read.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void EveryDeclaredToolNamesItselfExplicitly_SoTheCensusHasSomethingToCompare()
    {
        var nameless = DeclaredToolMethods()
            .Where(x => string.IsNullOrWhiteSpace(x.Attr.Name))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .ToArray();

        Assert.True(nameless.Length == 0,
            "These tool methods leave [McpServerTool(Name = …)] unset, so their published spelling is the " +
            "SDK's derivation and this census cannot check them: " + string.Join(", ", nameless));
    }
}

/// <summary>The wire half of the census — it needs the running server.</summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ToolSurfaceCensusWireTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public ToolSurfaceCensusWireTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    static string[] DeclaredNames() =>
        typeof(HousecarlMcp.CheckTools).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EveryToolDeclaredInSourceIsPublishedOnTheWire_AndNothingIsPublishedThatIsNotDeclared()
    {
        var declared = DeclaredNames();
        var published = _s.PublishedNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        _out.WriteLine($"declared {declared.Length}, published {published.Length}");

        // Set equality, not a count: a count can be right while the two sets differ, and the number moves
        // every wave. The invariant that does not move is that they are the same set.
        Assert.Equal(declared, published);
    }

    [Fact]
    public void ToolsListNamesHousecarlCheck_TheWholePointOf470()
    {
        // The literal is deliberate, and is the one site on the branch that keeps one. Every other
        // assertion over this name reads ToolNames.Check and so follows a change to the constant's
        // VALUE; this cell is the rename oracle, and a constant here would move with the thing it
        // exists to pin. A typo'd rename goes RED here while the reflected set-equality above,
        // which compares two sets that both moved, stays green.
        Assert.Contains("housecarl_check", _s.PublishedNames);
    }
}
