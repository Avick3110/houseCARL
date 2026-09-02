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
}
