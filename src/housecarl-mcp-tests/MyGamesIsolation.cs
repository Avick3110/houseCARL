using System.Runtime.CompilerServices;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>No test reads this machine's own Documents\My Games INIs: a profile without its own INIs reads an empty folder unless a test in <see cref="Serial"/> points it elsewhere.</summary>
static class MyGamesIsolation
{
    /// <summary>The collection a test that repoints HOUSECARL_MY_GAMES runs in, alone, so no other test reads its folder.</summary>
    internal const string Serial = "my-games";

    [ModuleInitializer]
    internal static void Init()
        => Environment.SetEnvironmentVariable("HOUSECARL_MY_GAMES", Path.Combine(Path.GetTempPath(), "hc-no-my-games-" + Guid.NewGuid().ToString("N")));
}

[CollectionDefinition(MyGamesIsolation.Serial, DisableParallelization = true)]
public sealed class MyGamesCollection;
