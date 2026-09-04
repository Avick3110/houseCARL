using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>What the source declares and what the wire publishes must be the same set. Neither side is a
/// hand list: the declared side reflects over <c>ToolSurface.Assembly</c>, the property Program.cs passes to
/// <c>WithToolsFromAssembly</c>, and the published side is `tools/list` off the running server.</summary>
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

    /// <summary>A type that declares a tool must be discoverable as a tool type. One named case per
    /// declaring type, so a failure says which file to open.</summary>
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
    // The server registers from exactly one assembly, so a tool declared in a second one is unreachable, and
    // nothing in the build stops that — it takes only a project reference to the MCP SDK, which
    // housecarl-generator already has. The check is a function over a population so the fixture test below can
    // hand it a population that contains an offender.

    /// <summary>The repo's shipped assemblies: every project under src/ except this test project, which is
    /// not shipped and declares an offender fixture below. The population is the repo's own project list, not
    /// the tool surface's reference closure — a closure reaches only what its root references, and
    /// housecarl-generator references housecarl-mcp rather than the reverse.</summary>
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

        // A population of one is a claim about nothing: with no second assembly the check below cannot fail.
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
    // The discarded spelling is `typeof(SomeToolType).Assembly`, which agrees with the registration only while
    // the type stays put. Both sides of the population are derived — the type names off the tool-surface
    // assembly by reflection, the files off the repo's own project list — so no hand-list can go short.

    static readonly Regex TypeofAssembly =
        new(@"typeof\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\)\s*\.\s*Assembly", RegexOptions.Compiled);

    /// <summary>Every type the tool-surface assembly declares, by simple name and by full name — the names a
    /// `typeof(…).Assembly` expression could use to mean "the tool surface".</summary>
    static (HashSet<string> Simple, string[] Full) SurfaceTypeNames()
    {
        var types = HousecarlMcp.ToolSurface.Assembly.GetTypes()
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal)
                     && !t.Name.Contains('`', StringComparison.Ordinal))
            .ToArray();

        return (types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
                types.Select(t => t.FullName ?? t.Name).ToArray());
    }

    /// <summary>Every simple type name a project declares, keyed by the project's directory. Off each
    /// project's BUILT assembly, so it is what the compiler produced — no regex over source, no list.</summary>
    static Dictionary<string, HashSet<string>> DeclaredNamesByProject() =>
        _declaredNamesByProject ??= RepoProjects.All.ToDictionary(
            p => p.Directory,
            p => RepoProjects.BuiltAssembly(p.AssemblyName, p.Directory)
                             .GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, HashSet<string>>? _declaredNamesByProject;

    static readonly HashSet<string> NoLocalNames = new(StringComparer.Ordinal);

    /// <summary>The names declared by the project the scanned file lives in — what a bare `typeof(X)` in that
    /// file could mean other than the tool surface. Empty for a file in the tool-surface project itself, where
    /// a surface type's name is the surface, and for a file under no project.</summary>
    static HashSet<string> NamesDeclaredWhereTheFileLives(string file)
    {
        var owner = RepoProjects.All
            .Where(p => file.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Directory.Length)     // nested projects: the innermost owns the file
            .Cast<(string AssemblyName, string Directory)?>()
            .FirstOrDefault();

        if (owner is not { } project) return NoLocalNames;

        return string.Equals(project.AssemblyName, HousecarlMcp.ToolSurface.Assembly.GetName().Name,
                             StringComparison.OrdinalIgnoreCase)
            ? NoLocalNames
            : DeclaredNamesByProject()[project.Directory];
    }

    /// <summary>
    /// Whether a `typeof(<paramref name="named"/>)` expression names a type on the tool surface.
    ///
    /// <para>A qualified name must be consistent with a real surface type's full name, because a simple name
    /// is not unique across assemblies — <c>typeof(HousecarlSetup.Program).Assembly</c> names a different
    /// project's <c>Program</c>, so matching on the last segment alone reports it wrongly.</para>
    ///
    /// <para>A bare name matches every surface type's simple name minus the names the file's own project
    /// declares (<paramref name="declaredWhereItLives"/>), so a name shared between projects is not reported
    /// where the local type is what the expression means. The surface set stays whole rather than narrowing to
    /// the tool types, which would stop catching spellings like <c>typeof(ApplyOp)</c> and
    /// <c>typeof(ToolSchemas)</c>.</para>
    /// </summary>
    static bool NamesASurfaceType(string named, (HashSet<string> Simple, string[] Full) surface,
                                  HashSet<string> declaredWhereItLives) =>
        named.Contains('.', StringComparison.Ordinal)
            ? surface.Full.Any(f => f == named || f.EndsWith("." + named, StringComparison.Ordinal))
            : surface.Simple.Contains(named) && !declaredWhereItLives.Contains(named);

    /// <summary>The type names in <paramref name="source"/> that a `typeof(…).Assembly` expression uses to
    /// mean the tool surface, read as if the source were the file at <paramref name="file"/>. One home for the
    /// reading, so a test measures what the scan measures.</summary>
    static string[] SurfaceNamingExpressionsIn(string file, string source,
                                               (HashSet<string> Simple, string[] Full) surface)
    {
        var local = NamesDeclaredWhereTheFileLives(file);

        return TypeofAssembly.Matches(source)
            .Select(m => m.Groups[1].Value)
            .Where(named => NamesASurfaceType(named, surface, local))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    [Trait("tier", "unit")]
    public void NothingNamesTheToolSurfaceAssemblyByPickingAType_ToolSurfaceIsTheOneHome()
    {
        var surfaceTypes = SurfaceTypeNames();

        // An empty name set would make every file below clean by arithmetic.
        Assert.True(surfaceTypes.Simple.Count > 0,
            "Reflection over ToolSurface.Assembly found no types, so nothing below can match and this arm " +
            "passes over any spelling. The reflection is broken, not the source.");

        var files = ScannedFiles();

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

    /// <summary>Every .cs file under the repo's projects, except the one file this fact is allowed to live in.
    /// The exemption is a full path, not a filename: filtering on the bare name would excuse a second
    /// ToolSurface.cs anywhere under src/, defeating a check whose subject is that the fact lives in one
    /// place. The home is derived from the project producing the tool-surface assembly plus the
    /// filename.</summary>
    static string[] ScannedFiles() =>
        RepoProjects.All
            .SelectMany(p => Directory.EnumerateFiles(p.Directory, "*.cs", SearchOption.AllDirectories))
            .Where(NotBuildOutput)
            .Where(f => !string.Equals(
                f,
                Path.Combine(RepoProjects.DirectoryFor(HousecarlMcp.ToolSurface.Assembly.GetName().Name!),
                             "ToolSurface.cs"),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Every site in <paramref name="files"/> naming the tool surface by picking a type, as
    /// repo-relative "file: expression" rows.</summary>
    static string[] OffendersAmong(IEnumerable<string> files, (HashSet<string> Simple, string[] Full) surface) =>
        files
            .SelectMany(f => SurfaceNamingExpressionsIn(f, File.ReadAllText(f), surface)
                                 .Select(named => $"{Rel(f)}: typeof({named}).Assembly"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    static bool NotBuildOutput(string p) =>
        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    static string Rel(string full) =>
        Path.GetRelativePath(HarnessPaths.RepoRoot, full).Replace('\\', '/');

    /// <summary>Real offender sites, with the spelling each carried — a fixture to hold any change to the
    /// reading against. Two rows are the same file and name and report as one, so the assertion counts sites
    /// rather than rows. Type names are stored, not whole expressions: this file is scanned like any other, so
    /// a literal <c>typeof(X).Assembly</c> here would be an offender in it.</summary>
    static readonly (string File, string TypeName)[] TheRepointedSites =
    {
        ("src/housecarl-generator/RegisteredTools.cs",                 "WriteTools"),
        ("src/housecarl-mcp-tests/ToolNameRegistryTests.cs",           "HousecarlMcp.CheckTools"),
        ("src/housecarl-generator/DescriptionVocabularyGuardProbe.cs", "ApplyOp"),
        ("src/housecarl-generator/WireNamesProbe.cs",                  "ApplyOp"),
        ("src/housecarl-generator/WireNamesProbe.cs",                  "ApplyOp"),
        ("src/housecarl-generator/PreFlattenSurface.cs",               "ToolSchemas"),
    };

    /// <summary>Every spelling above is still read as naming the tool surface, so a fix to the false-positive
    /// side cannot be paid for in teeth — narrowing the bare-name set to the tool types the server discovers
    /// would pass everywhere while losing four of these.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void EverySpellingThisBranchRepointedIsStillCaught_TheCollisionFixCostsNoTeeth()
    {
        var surface = SurfaceTypeNames();

        var caught = TheRepointedSites
            .Where(s => SurfaceNamingExpressionsIn(
                            Path.Combine(HarnessPaths.RepoRoot, s.File.Replace('/', Path.DirectorySeparatorChar)),
                            $"var a = typeof({s.TypeName}).Assembly;", surface).Length == 1)
            .ToArray();

        var missed = TheRepointedSites.Except(caught).Select(s => $"{s.File}: {s.TypeName}")
                                      .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.True(caught.Length == TheRepointedSites.Length,
            $"The reading catches {caught.Length} of the {TheRepointedSites.Length} spellings this branch " +
            "repointed. These are no longer reported:\n  " + string.Join("\n  ", missed) +
            "\nEach one occurred in this tree and was a real offender, so a reading that passes over it has " +
            "lost teeth, whatever else it fixed.");
    }

    /// <summary>The bare-name branch skips a name only where the file's own project declares it, and reports
    /// the same spelling in a project that does not. Both cases are derived off the built assemblies, and
    /// neither touches disk — the reading only takes the path a file would have.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ABareNameIsSkippedOnlyWhereTheFilesOwnProjectDeclaresIt_NoNuisanceRedNoLostTeeth()
    {
        var surface = SurfaceTypeNames();
        var surfaceAssembly = HousecarlMcp.ToolSurface.Assembly.GetName().Name!;

        var others = RepoProjects.All
            .Where(p => !string.Equals(p.AssemblyName, surfaceAssembly, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.AssemblyName, StringComparer.Ordinal)
            .ToArray();

        // A name the tool surface shares with another project, AND a third project that does not declare it —
        // so the same spelling can be read in both places and only one of them skipped.
        var pick = others
            .SelectMany(p => DeclaredNamesByProject()[p.Directory]
                                 .Where(n => surface.Simple.Contains(n))
                                 .OrderBy(n => n, StringComparer.Ordinal)
                                 .Select(n => (Colliding: p, Name: n)))
            .Select(x => (x.Colliding, x.Name,
                          Clean: others.FirstOrDefault(p => !DeclaredNamesByProject()[p.Directory].Contains(x.Name))))
            .FirstOrDefault(x => x.Clean.Directory is not null);

        Assert.True(pick.Name is not null,
            "No simple name is declared both by the tool surface and by another project that a third project " +
            "leaves undeclared, so neither cell below would prove anything. The derivation is broken, or the " +
            "repo has changed shape — either way this arm is not measuring what it claims.");

        var expression = $"var a = typeof({pick.Name}).Assembly;";

        Assert.Empty(SurfaceNamingExpressionsIn(Path.Combine(pick.Colliding.Directory, "SomeFile.cs"),
                                                expression, surface));

        Assert.Equal(new[] { pick.Name },
                     SurfaceNamingExpressionsIn(Path.Combine(pick.Clean.Directory, "SomeFile.cs"),
                                                expression, surface));
    }

    /// <summary>A second file called ToolSurface.cs is still scanned — the exemption is the one home's path,
    /// not the name. The fixture is planted in another project's directory inside the tree the scan walks and
    /// removed in a finally; its content is a comment, because the scan reads source as text and a comment
    /// cannot break a build that runs while the file exists.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ASecondFileCalledToolSurfaceCsIsStillScanned_TheExemptionIsAPathNotAFilename()
    {
        var surface = SurfaceTypeNames();
        var toolType = HousecarlMcp.ToolSurface.Assembly.GetTypes()
            .First(t => t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is not null);

        var home = Path.Combine(RepoProjects.DirectoryFor(HousecarlMcp.ToolSurface.Assembly.GetName().Name!),
                                "ToolSurface.cs");
        var elsewhere = RepoProjects.All
            .First(p => !string.Equals(p.Directory, Path.GetDirectoryName(home), StringComparison.OrdinalIgnoreCase))
            .Directory;
        var planted = Path.Combine(elsewhere, "ToolSurface.cs");

        Assert.False(File.Exists(planted),
            $"{planted} already exists — the fixture would overwrite a real file, and a second home is exactly " +
            "what this arm claims the scan reports.");

        try
        {
            File.WriteAllText(planted, $"// typeof({toolType.Name}).Assembly{Environment.NewLine}");

            Assert.Contains(OffendersAmong(ScannedFiles(), surface),
                            o => o.StartsWith(Rel(planted) + ":", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(planted)) File.Delete(planted);
        }

        Assert.False(File.Exists(planted), $"The fixture left {planted} behind in the source tree.");
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
        // The literal is deliberate: every other assertion over this name reads ToolNames.Check and so follows
        // a change to the constant's value, while a literal does not.
        Assert.Contains("housecarl_check", _s.PublishedNames);
    }
}
