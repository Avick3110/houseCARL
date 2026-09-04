using System.Reflection;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="RepoProjects"/> decides which BUILD every repo-wide population reflects, so a wrong
/// choice here means every derived population quietly measures a different build from the one under
/// test.</summary>
[Trait("tier", "unit")]
public sealed class RepoProjectsTests
{
    /// <summary>A real dll planted at <paramref name="path"/>, aged or freshened by <paramref name="ageDays"/>.</summary>
    static void Plant(string path, double ageDays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(typeof(RepoProjectsTests).Assembly.Location, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(ageDays));
    }

    /// <summary>The dll beside the test assembly wins over a NEWER one in the project tree — picking by
    /// timestamp would load a second configuration's build into the default context, where every later
    /// Assembly.Load for that name resolves to it too. Nothing is loaded here: the assertion is over the path
    /// chosen, and the test ends by confirming no assembly of that name entered the process.</summary>
    [Fact]
    public void TheDllBesideTheTestAssemblyWinsOverANewerOneInTheProjectTree_TheBuildUnderTestIsTheOneReflected()
    {
        var name = "hcfold-locate-" + Guid.NewGuid().ToString("N");
        var beside = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        var projectDir = Path.Combine(Path.GetTempPath(), name);
        var decoy = Path.Combine(projectDir, "bin", "Debug", "net9.0", name + ".dll");

        try
        {
            Plant(beside, ageDays: -7);     // the build under test, deliberately the OLDER file
            Plant(decoy, ageDays: +7);      // the other configuration, deliberately the NEWER one

            Assert.Equal(beside, RepoProjects.LocateAssembly(name, projectDir));

            Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
                a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(beside)) File.Delete(beside);
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
        }

        Assert.False(File.Exists(beside), $"The fixture left {beside} behind, inside the test app base.");
        Assert.False(Directory.Exists(projectDir), $"The fixture left {projectDir} behind.");
    }

    /// <summary>The project tree is still the fallback: a project whose output is not copied beside the test
    /// assembly has to be found where it was built, or the derived population goes short.</summary>
    [Fact]
    public void WithNothingBesideTheTestAssemblyTheProjectsOwnBuildOutputIsUsed_TheFallbackIsNotDead()
    {
        var name = "hcfold-fallback-" + Guid.NewGuid().ToString("N");
        var projectDir = Path.Combine(Path.GetTempPath(), name);
        var built = Path.Combine(projectDir, "bin", "Release", "net9.0", name + ".dll");

        try
        {
            Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")));
            Plant(built, ageDays: 0);

            Assert.Equal(built, RepoProjects.LocateAssembly(name, projectDir));
        }
        finally
        {
            if (Directory.Exists(projectDir)) Directory.Delete(projectDir, recursive: true);
        }

        Assert.False(Directory.Exists(projectDir), $"The fixture left {projectDir} behind.");
    }
}
