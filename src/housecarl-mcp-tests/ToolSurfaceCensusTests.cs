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

    /// <summary>
    /// The names declared by the project the scanned file lives in — the names a bare `typeof(X)` in that file
    /// could be about other than the tool surface.
    ///
    /// <para>Empty for a file in the tool-surface project itself: a surface type's name there is not an
    /// ambiguity to resolve, it IS the surface, and the discarded spelling is exactly as discarded in that
    /// project as in any other. Empty too for a file under no project, which the scan does not produce.</para>
    /// </summary>
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
    /// <para>A qualified name has to be consistent with a real surface type's full name, because a simple name
    /// is not unique across assemblies — <c>typeof(HousecarlSetup.Program).Assembly</c> names a different
    /// project's <c>Program</c>, and matching on the last segment alone reported it as an offender.</para>
    ///
    /// <para>A bare name matches every surface type's simple name, MINUS the names the file's own project
    /// declares (<paramref name="declaredWhereItLives"/>). That subtraction is the whole collision fix: the
    /// day housecarl-core or housecarl-generator gains a type sharing a name with one in housecarl-mcp, every
    /// <c>typeof(ThatType).Assembly</c> in that project would otherwise be reported, with a remedy that would
    /// change what the expression means. Where the name is NOT declared locally there is no other type it
    /// could be, which is why the set stays whole — narrowing it to the tool types instead would have stopped
    /// catching four of the eight spellings this branch repointed, <c>typeof(ApplyOp)</c> and
    /// <c>typeof(ToolSchemas)</c> among them.</para>
    /// </summary>
    static bool NamesASurfaceType(string named, (HashSet<string> Simple, string[] Full) surface,
                                  HashSet<string> declaredWhereItLives) =>
        named.Contains('.', StringComparison.Ordinal)
            ? surface.Full.Any(f => f == named || f.EndsWith("." + named, StringComparison.Ordinal))
            : surface.Simple.Contains(named) && !declaredWhereItLives.Contains(named);

    /// <summary>The type names in <paramref name="source"/> that a `typeof(…).Assembly` expression uses to
    /// mean the tool surface, read as if the source were the file at <paramref name="file"/>. One home for the
    /// reading, so an arm measures what the scan measures.</summary>
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

        // Vacuity canary: an empty name set would make every file below clean by arithmetic.
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

    /// <summary>
    /// Every .cs file under the repo's projects, except the one file this fact is allowed to live in.
    ///
    /// <para>The exemption is a full path, not a filename. Filtering on the bare name excused any file called
    /// ToolSurface.cs anywhere under src/, so a second home — src/housecarl-generator/ToolSurface.cs, say —
    /// would never have entered the scan at all, and a guard whose whole subject is "this fact lives in
    /// exactly one place" would have been defeated by giving the second place the same name. The home is
    /// derived: the project producing the tool-surface assembly, plus the filename.</para>
    /// </summary>
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

    /// <summary>
    /// The eight sites this branch repointed, with the spelling each carried before it was.
    ///
    /// <para>A historical fixture, not a population: it is the record of what actually occurred in this tree,
    /// and the only way to hold a change to the READING against the offenders the reading is for. Two of the
    /// eight are one row when reported — same file, same name — which is why the assertion is over the eight
    /// sites rather than over a row count. The names are stored, not the expressions: this file is scanned
    /// like any other, and a literal <c>typeof(X).Assembly</c> here would be an offender in it.</para>
    /// </summary>
    static readonly (string File, string TypeName)[] TheRepointedSites =
    {
        ("src/housecarl-generator/RegisteredTools.cs",                 "WriteTools"),
        ("src/housecarl-mcp-tests/ToolNameRegistryTests.cs",           "HousecarlMcp.CheckTools"),
        ("src/housecarl-generator/CodexUmbrellaCoverageProbe.cs",      "ReadTools"),
        ("src/housecarl-generator/BulkPrimitivesWave3Probe.cs",        "HousecarlMcp.ReadTools"),
        ("src/housecarl-generator/DescriptionVocabularyGuardProbe.cs", "ApplyOp"),
        ("src/housecarl-generator/WireNamesProbe.cs",                  "ApplyOp"),
        ("src/housecarl-generator/WireNamesProbe.cs",                  "ApplyOp"),
        ("src/housecarl-generator/PreFlattenSurface.cs",               "ToolSchemas"),
    };

    /// <summary>
    /// Every spelling the repointing removed is still read as naming the tool surface.
    ///
    /// <para>This is what stops a fix to the false-positive side from being paid for in teeth. Narrowing the
    /// bare-name set to the tool types the server discovers would have gone green everywhere while quietly
    /// losing four of these — the three <c>typeof(ApplyOp)</c> sites and PreFlattenSurface's
    /// <c>typeof(ToolSchemas)</c>, which was a second WithToolsFromAssembly call site.</para>
    /// </summary>
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

    /// <summary>
    /// The bare-name branch skips a name only where the file's own project declares it — and reports the same
    /// spelling in a project that does not.
    ///
    /// <para>Both cells are derived: the colliding name is one the tool surface and some other project both
    /// declare, off their built assemblies, and the second cell reuses that same name in a project that does
    /// not declare it. Neither touches disk — the reading takes the path a file would have.</para>
    /// </summary>
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

    /// <summary>
    /// A SECOND file called ToolSurface.cs is still scanned. The exemption is the one home's path; the name
    /// is not a licence.
    ///
    /// <para>The fixture is planted in another project's directory, in the tree the scan walks, and removed in
    /// a finally. Its content is a comment: the scan reads source as text, so a comment carries the same
    /// expression a statement would, and it cannot break a build if anything compiles this tree while it
    /// exists.</para>
    /// </summary>
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
        // The literal is deliberate: every other assertion over this name reads ToolNames.Check and so
        // follows a change to the constant's VALUE, while a literal does not. Deliberately redundant now
        // — data/tools-list-2.0.json spells all 46 published names as literals and PublishedNameAnchorTests
        // is the rename oracle for every one of them, in both directions. This cell is the belt beside
        // those braces for the one name #470 was about, and it costs a line.
        Assert.Contains("housecarl_check", _s.PublishedNames);
    }
}
