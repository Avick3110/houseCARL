using System.Runtime.CompilerServices;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>No test reads this machine's own Documents\My Games INIs: a profile without its own INIs reads an empty folder unless a test in <see cref="Serial"/> points it elsewhere.</summary>
static class MyGamesIsolation
{
    /// <summary>The collection a test that repoints HOUSECARL_MY_GAMES runs in, alone, so no other test reads its folder.</summary>
    internal const string Serial = "my-games";

    /// <summary>Run <paramref name="read"/> with the product's one My Games seam pointed at <paramref name="folder"/>; the
    /// caller is in <see cref="Serial"/>, so no other test reads it meanwhile.</summary>
    internal static T Pointed<T>(string folder, Func<T> read)
    {
        var isolated = Environment.GetEnvironmentVariable("HOUSECARL_MY_GAMES");
        Environment.SetEnvironmentVariable("HOUSECARL_MY_GAMES", folder);
        try { return read(); }
        finally { Environment.SetEnvironmentVariable("HOUSECARL_MY_GAMES", isolated); }
    }

    [ModuleInitializer]
    internal static void Init()
        => Environment.SetEnvironmentVariable("HOUSECARL_MY_GAMES", Path.Combine(Path.GetTempPath(), "hc-no-my-games-" + Guid.NewGuid().ToString("N")));
}

[CollectionDefinition(MyGamesIsolation.Serial, DisableParallelization = true)]
public sealed class MyGamesCollection;
