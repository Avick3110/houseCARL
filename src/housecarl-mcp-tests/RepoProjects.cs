using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The repo's own projects, read off the csproj files rather than assumed from folder names: one
/// project's assembly name differs from its directory (houseCARL-Setup).</summary>
static class RepoProjects
{
    static readonly Regex AssemblyNameElement =
        new(@"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>", RegexOptions.Compiled);

    /// <summary>Every project under src/: its assembly name and the directory holding its csproj. Cached on first
    /// read rather than in a field initializer, so <see cref="Discover"/>'s refusal arrives as the assertion it is
    /// instead of a TypeInitializationException that poisons the class for the run.</summary>
    public static IReadOnlyList<(string AssemblyName, string Directory)> All => _projects ??= Discover();

    static (string AssemblyName, string Directory)[]? _projects;

    static (string, string)[] Discover()
    {
        var src = Path.Combine(HarnessPaths.RepoRoot, "src");
        var found = Directory.EnumerateDirectories(src)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj"))
            .Select(csproj =>
            {
                var text = File.ReadAllText(csproj);
                var m = AssemblyNameElement.Match(text);
                var name = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(csproj);
                return (AssemblyName: name, Directory: Path.GetDirectoryName(csproj)!);
            })
            .OrderBy(p => p.AssemblyName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(found.Length > 0,
            $"No .csproj found under '{src}'. Every guard population derived from the repo's project list is " +
            "vacuous without one, so this is a broken measurement rather than an empty repo.");
        return found;
    }

    /// <summary>The directory of the project producing <paramref name="assemblyName"/>. Loud if it is not one.</summary>
    public static string DirectoryFor(string assemblyName)
    {
        var hits = All.Where(p => string.Equals(p.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
                      .Select(p => p.Directory).ToArray();

        Assert.True(hits.Length == 1,
            $"Found {hits.Length} projects under src/ producing assembly '{assemblyName}' " +
            (hits.Length == 0
                ? "— the source of a type compiled into it cannot be located, so anything derived from that source goes quiet."
                : "— " + string.Join(", ", hits) + ". Which one declares a given type is then unanswerable."));
        return hits[0];
    }

    /// <summary>The built assembly for one project — already-loaded ones from the runtime, the rest via
    /// <see cref="LocateAssembly"/>. A project with no build output is loud rather than skipped: skipping shrinks
    /// every population derived from this list.</summary>
    public static Assembly BuiltAssembly(string assemblyName, string directory) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
        ?? Assembly.LoadFrom(LocateAssembly(assemblyName, directory));

    /// <summary>Which file this class loads <paramref name="assemblyName"/> from: the copy beside the running test
    /// assembly first, since that is the build under test, and the project's own build output second. A project
    /// tree holds every configuration it has ever built, so picking the newest under bin/ can hand a Release run a
    /// Debug dll — and <c>LoadFrom</c> loads into the DEFAULT context, so every later <c>Assembly.Load</c> for that
    /// name resolves to the same instance.</summary>
    internal static string LocateAssembly(string assemblyName, string directory)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (File.Exists(beside)) return beside;

        // Enumerated from the PROJECT directory (which exists, its csproj having been read out of it) and then
        // filtered to the bin tree: enumerating bin/ directly throws DirectoryNotFoundException on a project that
        // was never built, which is the one case the refusal below is written for.
        var binSegment = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var dll = Directory.EnumerateFiles(directory, assemblyName + ".dll", SearchOption.AllDirectories)
                           .Where(p => p.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(File.GetLastWriteTimeUtc)
                           .FirstOrDefault();

        Assert.True(dll is not null,
            $"Project '{assemblyName}' has no built {assemblyName}.dll beside the test assembly " +
            $"({AppContext.BaseDirectory}) and none under {directory}/bin, so it cannot be searched. Build the " +
            "whole solution before running these tests — a project skipped here is a population that is short " +
            "by exactly one project.");

        return dll!;
    }

    static Assembly[]? _allAssemblies;

    /// <summary>Every project's built assembly, derived from the csproj files rather than from one assembly's
    /// reference closure — a closure reaches only what its root references, which is a smaller set than the repo's
    /// own assemblies. A method rather than a property initializer because <see cref="BuiltAssembly"/> can refuse,
    /// and a refusal out of a static initializer arrives as a TypeInitializationException that poisons every other
    /// test touching this class.</summary>
    public static IReadOnlyList<Assembly> AllAssemblies() =>
        _allAssemblies ??= All.Select(p => BuiltAssembly(p.AssemblyName, p.Directory))
                              .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                              .ToArray();
}
