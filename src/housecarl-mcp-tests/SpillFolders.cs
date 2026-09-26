using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A service's own results folder, for a test that needs to find the one file its call spilled.</summary>
static class SpillFolders
{
    /// <summary>Empty the service's results folder and return it. Each world belongs to one class or one collection,
    /// and xUnit runs those tests one at a time, so nothing else spills into it while the test runs.</summary>
    public static string Emptied(LoadOrderService svc)
    {
        var dir = svc.ResultsDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
