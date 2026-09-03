using System.Reflection;
using System.Text.RegularExpressions;
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

    // ---- ToolSurface.Assembly is the ONE home for "which assembly is the tool surface" ------------------
    //
    // The discarded spelling is `typeof(SomeToolType).Assembly` — a sentence about where a type happens to
    // live, which agrees with the registration only by coincidence of layout. ToolSurface.cs says it is the
    // one home, and that sentence has to be true of the tree it ships on.
    //
    // BOTH sides of this population are derived: the type names come off the tool-surface assembly by
    // reflection, the files off the repo's own project list. A hand-list of "the sites we know about" is
    // exactly what went short here twice — the first pass repointed the two a reviewer named and left two
    // live ci-all guards on the old spelling, and a second hand pass over those left three more.

    static readonly Regex TypeofAssembly =
        new(@"typeof\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\)\s*\.\s*Assembly", RegexOptions.Compiled);

    /// <summary>
    /// The names a `typeof(…).Assembly` expression could use to mean "the tool surface": every type the
    /// surface assembly declares by FULL name, and — unqualified — only the types that could plausibly be
    /// meant by it.
    ///
    /// <para>A bare name is matched against the tool types the server discovers, plus <c>ToolSurface</c>
    /// itself. The expression is a sentence about which assembly the tools live in, and an internal helper's
    /// name is not that sentence. The full ~180-name set made every simple name in this assembly a match, so
    /// the first type in housecarl-core or housecarl-generator to share a name with any of them would have
    /// made every <c>typeof(ThatType).Assembly</c> in that project an offender, with a failure telling the
    /// author to read <c>ToolSurface.Assembly</c> instead — which would change what their expression means.
    /// A qualified name needs no such narrowing: it is already held against a real full name.</para>
    /// </summary>
    static (HashSet<string> Simple, string[] Full) SurfaceTypeNames()
    {
        var types = HousecarlMcp.ToolSurface.Assembly.GetTypes()
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal)
                     && !t.Name.Contains('`', StringComparison.Ordinal))
            .ToArray();

        return (types.Where(CouldBeMeantByABareName).Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
                types.Select(t => t.FullName ?? t.Name).ToArray());
    }

    /// <summary>A type whose bare name a reader would take to mean the tool surface itself.</summary>
    static bool CouldBeMeantByABareName(Type t) =>
        t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is not null
        || t == typeof(HousecarlMcp.ToolSurface);

    /// <summary>
    /// Whether a `typeof(<paramref name="named"/>)` expression names a type on the tool surface.
    ///
    /// <para>A bare name matches on the simple name. A qualified one has to be consistent with a real surface
    /// type's full name, because a simple name is not unique across assemblies —
    /// <c>typeof(HousecarlSetup.Program).Assembly</c> names a different project's <c>Program</c>, and matching
    /// on the last segment alone reported it as an offender.</para>
    /// </summary>
    static bool NamesASurfaceType(string named, (HashSet<string> Simple, string[] Full) surface) =>
        named.Contains('.', StringComparison.Ordinal)
            ? surface.Full.Any(f => f == named || f.EndsWith("." + named, StringComparison.Ordinal))
            : surface.Simple.Contains(named);

    /// <summary>The type names in <paramref name="source"/> that a `typeof(…).Assembly` expression uses to
    /// mean the tool surface. One home for the reading, so an arm measures what the scan measures.</summary>
    static string[] SurfaceNamingExpressions(string source, (HashSet<string> Simple, string[] Full) surface) =>
        TypeofAssembly.Matches(source)
            .Select(m => m.Groups[1].Value)
            .Where(named => NamesASurfaceType(named, surface))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    [Trait("tier", "unit")]
    public void NothingNamesTheToolSurfaceAssemblyByPickingAType_ToolSurfaceIsTheOneHome()
    {
        var surfaceTypes = SurfaceTypeNames();

        // Vacuity canary: an empty name set would make every file below clean by arithmetic.
        Assert.True(surfaceTypes.Simple.Count > 0,
            "Reflection over ToolSurface.Assembly found no types, so nothing below can match and this arm " +
            "passes over any spelling. The reflection is broken, not the source.");

        var files = RepoProjects.All
            .SelectMany(p => Directory.EnumerateFiles(p.Directory, "*.cs", SearchOption.AllDirectories))
            .Where(NotBuildOutput)
            // The one home itself: ToolSurface.cs is where this fact is allowed to be spelled.
            .Where(f => !string.Equals(Path.GetFileName(f), "ToolSurface.cs", StringComparison.Ordinal))
            .ToArray();

        Assert.True(files.Length > 0,
            "No .cs files were found under the repo's projects, so this scan is vacuous.");

        var offenders = OffendersAmong(files, surfaceTypes);

        Assert.True(offenders.Length == 0,
            $"These name the tool surface by picking a type that happens to live in it ({files.Length} files " +
            $"scanned, {surfaceTypes.Simple.Count} surface type names):\n  " + string.Join("\n  ", offenders) +
            "\nRead HousecarlMcp.ToolSurface.Assembly instead. It is the property Program.cs passes to " +
            "WithToolsFromAssembly, so it is the assembly the server actually registers from; a typeof() " +
            "expression agrees with that only while the type stays put.");
    }

    /// <summary>Every site in <paramref name="files"/> naming the tool surface by picking a type, as
    /// repo-relative "file: expression" rows.</summary>
    static string[] OffendersAmong(IEnumerable<string> files, (HashSet<string> Simple, string[] Full) surface) =>
        files
            .SelectMany(f => SurfaceNamingExpressions(File.ReadAllText(f), surface)
                                 .Select(named => $"{Rel(f)}: typeof({named}).Assembly"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    static bool NotBuildOutput(string p) =>
        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    static string Rel(string full) =>
        Path.GetRelativePath(HarnessPaths.RepoRoot, full).Replace('\\', '/');

    /// <summary>
    /// The bare-name branch reads the names that could mean the surface, and no others.
    ///
    /// <para>Both cells are derived off the surface assembly rather than named here: the helper is a type the
    /// server does not discover, the tool type is one it does. Each goes through the same reading the file
    /// scan uses, as the text a file would carry.</para>
    /// </summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ABareNameIsReadAsTheSurfaceOnlyWhenItCouldMeanIt_AnInternalHelpersNameIsNotThatSentence()
    {
        var surface = SurfaceTypeNames();
        var declared = HousecarlMcp.ToolSurface.Assembly.GetTypes()
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal)
                     && !t.Name.Contains('`', StringComparison.Ordinal))
            .ToArray();

        var helper = declared.First(t => !surface.Simple.Contains(t.Name));
        var toolType = declared.First(t => t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is not null);

        Assert.Empty(SurfaceNamingExpressions($"var a = typeof({helper.Name}).Assembly;", surface));

        Assert.Equal(new[] { toolType.Name },
                     SurfaceNamingExpressions($"var a = typeof({toolType.Name}).Assembly;", surface));
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
        // The literal is deliberate: every other assertion over this name reads ToolNames.Check and so
        // follows a change to the constant's VALUE, while a literal does not. Deliberately redundant now
        // — data/tools-list-2.0.json spells all 46 published names as literals and PublishedNameAnchorTests
        // is the rename oracle for every one of them, in both directions. This cell is the belt beside
        // those braces for the one name #470 was about, and it costs a line.
        Assert.Contains("housecarl_check", _s.PublishedNames);
    }
}
