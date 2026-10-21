namespace HousecarlMcpTests;

/// <summary>Where the repo is, and which build configuration this run belongs to — both derived from the running
/// assembly's own location. Every failure throws rather than falling back, so a test cannot measure the wrong
/// tree.</summary>
static class HarnessPaths
{
    /// <summary>The repo root — the directory holding housecarl.sln, found by walking up from this assembly.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Release or Debug — read off this assembly's own bin/&lt;config&gt;/&lt;tfm&gt; path.</summary>
    public static string Configuration => BuildFolder.Configuration;

    /// <summary>The target framework folder — read off the same path; the server is built for the same one.</summary>
    public static string TargetFramework => BuildFolder.TargetFramework;

    static readonly (string Configuration, string TargetFramework) BuildFolder = FindBuildFolder();

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "housecarl.sln"))) return d.FullName;

        throw new InvalidOperationException(
            $"No housecarl.sln above '{AppContext.BaseDirectory}'. The harness tests read the SOURCE tree " +
            "(source files, the built server), so a test run that cannot find the repo root " +
            "must fail loud rather than measure nothing.");
    }

    static (string, string) FindBuildFolder()
    {
        DirectoryInfo? below = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d?.Parent != null; below = d, d = d.Parent)
            if (below != null && string.Equals(d.Parent.Name, "bin", StringComparison.OrdinalIgnoreCase))
                return (d.Name, below.Name);

        throw new InvalidOperationException(
            $"Could not read a build configuration out of '{AppContext.BaseDirectory}' (expected " +
            "…/bin/<Configuration>/<TargetFramework>/). The stdio tests need it to find the server built alongside " +
            "these tests — guessing 'Release' would run a stale or absent binary.");
    }
}
