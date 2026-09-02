namespace HousecarlMcpTests;

/// <summary>
/// Where the repo is, and which build configuration this test run belongs to.
///
/// Both are DERIVED from the running assembly's own location, never passed in or guessed: a helper that
/// silently falls back to a default is how a harness test passes while measuring the wrong tree.
/// Every failure here throws rather than returning a fallback.
/// </summary>
static class HarnessPaths
{
    /// <summary>The repo root — the directory holding housecarl.sln, found by walking up from this assembly.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Release or Debug — read off this assembly's own bin/&lt;config&gt;/net9.0 path.</summary>
    public static string Configuration { get; } = FindConfiguration();

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "housecarl.sln"))) return d.FullName;

        throw new InvalidOperationException(
            $"No housecarl.sln above '{AppContext.BaseDirectory}'. The harness tests read the SOURCE tree " +
            "(probe files, CiAll.cs, the built generator), so a test run that cannot find the repo root " +
            "must fail loud rather than measure nothing.");
    }

    static string FindConfiguration()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d?.Parent != null; d = d.Parent)
            if (string.Equals(d.Parent.Name, "bin", StringComparison.OrdinalIgnoreCase)) return d.Name;

        throw new InvalidOperationException(
            $"Could not read a build configuration out of '{AppContext.BaseDirectory}' (expected " +
            "…/bin/<Configuration>/net9.0/). The bridge needs it to find the generator built alongside " +
            "these tests — guessing 'Release' would run a stale or absent binary.");
    }
}
