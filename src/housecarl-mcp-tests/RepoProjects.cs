using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The repo's own projects, read off the csproj files rather than assumed from folder names. One project's
/// assembly name differs from its directory (houseCARL-Setup), so "src/&lt;assembly name&gt;" is a convention
/// that is already false once.
/// </summary>
static class RepoProjects
{
    static readonly Regex AssemblyNameElement =
        new(@"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>", RegexOptions.Compiled);

    /// <summary>Every project under src/: its assembly name and the directory holding its csproj.</summary>
    public static IReadOnlyList<(string AssemblyName, string Directory)> All { get; } = Discover();

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

    /// <summary>
    /// The built assembly for one project. Already-loaded assemblies come back from the runtime; the rest are
    /// loaded off the project's own build output. A project that has no build output at all is loud: skipping
    /// it would shrink every population derived from this list, which is the failure those populations exist
    /// to catch. One home, because two walks over the repo's projects were about to spell this twice.
    /// </summary>
    public static Assembly BuiltAssembly(string assemblyName, string directory)
    {
        var already = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (already is not null) return already;

        // Enumerated from the PROJECT directory — which exists by construction, its csproj having been read
        // out of it — and then filtered to the bin tree. Enumerating bin/ directly threw
        // DirectoryNotFoundException on a project that had never been built, which is the one case the
        // refusal below is written for: the written sentence was unreachable and the reader got a bare path
        // exception instead.
        var binSegment = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var dll = Directory.EnumerateFiles(directory, assemblyName + ".dll", SearchOption.AllDirectories)
                           .Where(p => p.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(File.GetLastWriteTimeUtc)
                           .FirstOrDefault();

        Assert.True(dll is not null,
            $"Project '{assemblyName}' has no built {assemblyName}.dll under {directory}/bin, so it cannot be " +
            "searched. Build the whole solution before running these tests — a project skipped here is a " +
            "population that is short by exactly one project.");

        return Assembly.LoadFrom(dll!);
    }

    static Assembly[]? _allAssemblies;

    /// <summary>
    /// Every project's built assembly: the repo-wide population, derived from the csproj files rather than
    /// from any one assembly's reference closure. A closure only reaches what its root REFERENCES, which is a
    /// different set from "the repo's own assemblies" and is short in a direction nothing announces.
    ///
    /// <para>A method rather than a property initializer on purpose: <see cref="BuiltAssembly"/> can refuse,
    /// and a refusal thrown out of a static initializer arrives as a TypeInitializationException that then
    /// poisons every other test touching this class for the rest of the run. From here it arrives as the
    /// assertion it is, in the test that asked.</para>
    /// </summary>
    public static IReadOnlyList<Assembly> AllAssemblies() =>
        _allAssemblies ??= All.Select(p => BuiltAssembly(p.AssemblyName, p.Directory))
                              .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
                              .ToArray();
}
