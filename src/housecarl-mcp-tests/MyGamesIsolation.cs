using System.Runtime.CompilerServices;

namespace HousecarlMcpTests;

/// <summary>No test reads this machine's own Documents\My Games INIs: a profile without its own INIs reads an empty folder unless the test points the service elsewhere.</summary>
static class MyGamesIsolation
{
    [ModuleInitializer]
    internal static void Init()
        => Environment.SetEnvironmentVariable("HOUSECARL_MY_GAMES", Path.Combine(Path.GetTempPath(), "hc-no-my-games-" + Guid.NewGuid().ToString("N")));
}
