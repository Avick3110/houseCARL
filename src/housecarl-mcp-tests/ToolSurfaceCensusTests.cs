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
/// Neither side is a hand list: the declared side is reflected over the assembly the server REGISTERS from
/// — <c>ToolSurface.Assembly</c>, the same property Program.cs passes to <c>WithToolsFromAssembly</c> — and
/// the published side is `tools/list` off the running server. Naming that assembly by picking a type
/// expected to live in it was the earlier shape, and it agreed with the registration by coincidence of
/// layout rather than by construction.
/// </summary>
public sealed class ToolSurfaceCensusTests
{
    /// <summary>Every method on the tool surface carrying [McpServerTool], with the type that declares it.</summary>
    static (Type Type, MethodInfo Method, McpServerToolAttribute Attr)[] DeclaredToolMethods() =>
        HousecarlMcp.ToolSurface.Assembly
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

    // ---- the tool surface is ONE assembly ---------------------------------------------------------------
    //
    // The server registers from exactly one assembly, so a tool declared in a second one is declared, guarded,
    // documented and unreachable — #470's shape, one level up. Nothing in the build stops that: it needs a
    // project reference to the MCP SDK and nothing else.
    //
    // It is NOT latent everywhere. housecarl-generator already compiles against the MCP SDK, so a marked type
    // there needs no csproj change at all; housecarl-core is the assembly that would. A latent arm still has
    // to be shown to fire, so the check is a function over a population and the fixture arm hands it a
    // population that contains an offender.

    /// <summary>
    /// The repo's shipped assemblies: every project under src/ except this test project, which is not shipped
    /// and deliberately declares an offender fixture below.
    ///
    /// <para>The population is the repo's own project list, not the tool surface's reference closure. A
    /// closure reaches only what its root REFERENCES: housecarl-generator references housecarl-mcp rather
    /// than the reverse, so walking outward from the tool surface left out the one shipped assembly that
    /// already carries the MCP SDK reference — the assembly a split would land in first, invisible to the arm
    /// written to catch it.</para>
    /// </summary>
    static Assembly[] ShippedAssemblies()
    {
        var self = typeof(ToolSurfaceCensusTests).Assembly;
        return RepoProjects.AllAssemblies()
                           .Where(a => a != self)
                           .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                           .ToArray();
    }

    /// <summary>Assemblies in <paramref name="population"/> other than the registered one that declare a tool.</summary>
    internal static string[] AssembliesDeclaringToolsOutside(IEnumerable<Assembly> population, Assembly registered) =>
        population
            .Where(a => a != registered)
            .SelectMany(a => a.GetTypes()
                              .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                          | BindingFlags.Static | BindingFlags.Instance
                                                          | BindingFlags.DeclaredOnly))
                              .Where(m => m.GetCustomAttribute<McpServerToolAttribute>(inherit: false) is not null)
                              .Select(m => $"{a.GetName().Name}: {m.DeclaringType!.FullName}.{m.Name}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    [Trait("tier", "unit")]
    public void OnlyTheAssemblyTheServerRegistersFromDeclaresTools_ASplitSurfaceIsUnreachableCode()
    {
        var population = ShippedAssemblies();
        var registered = HousecarlMcp.ToolSurface.Assembly;

        // Vacuity canary: a population of one is a claim about nothing.
        Assert.True(population.Length > 1,
            "The shipped-assembly walk found only " + string.Join(", ", population.Select(a => a.GetName().Name)) +
            ". With nothing but the tool surface in the population there is no second assembly to check, so " +
            "this arm would pass over any split. The walk is broken, not the surface.");

        var elsewhere = AssembliesDeclaringToolsOutside(population, registered);

        Assert.True(elsewhere.Length == 0,
            "These tools are declared outside the assembly the server registers from " +
            $"({registered.GetName().Name}), so no caller can reach them:\n  " + string.Join("\n  ", elsewhere) +
            "\nWithToolsFromAssembly scans one assembly. Move the tool onto the surface, or the surface has to " +
            "become more than one assembly deliberately — which is a decision, not an accident.");
    }

    /// <summary>A tool declared where the server does not look. Never registered: the server scans
    /// <c>ToolSurface.Assembly</c>, and nothing references this project.</summary>
    [McpServerToolType]
    internal static class SplitSurfaceFixture
    {
        [McpServerTool(Name = "housecarl_split_surface_fixture")]
        internal static string NotAShippedTool() => "fixture";
    }

    [Fact]
    [Trait("tier", "unit")]
    public void TheSplitSurfaceCheckFires_ProvedOnAPopulationThatContainsOne()
    {
        var registered = HousecarlMcp.ToolSurface.Assembly;
        var withOffender = ShippedAssemblies().Append(typeof(SplitSurfaceFixture).Assembly);

        var elsewhere = AssembliesDeclaringToolsOutside(withOffender, registered);

        Assert.Contains(elsewhere, e => e.Contains(nameof(SplitSurfaceFixture), StringComparison.Ordinal));
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
        HousecarlMcp.ToolSurface.Assembly
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
