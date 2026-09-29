using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>GameDirOrNull</c> and <c>CompilerGameDirHints</c>, the compiler auto-detect hint (migrated from the
/// <c>compile-ergonomics-guard</c> probe, part A). Null-safe by contract: a failure falls through to the prompt, never throws.
/// </summary>
[Trait("tier", "unit")]
public sealed class CompileGameDirHintTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-compile-gamedir-tests-" + Guid.NewGuid().ToString("N"));

    UserConfigStore Store() => new(Path.Combine(_root, "user.json"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    // Probe A: "explicit mode: GameDirOrNull = DataDir's parent (the game install dir)".
    [Fact]
    public void ExplicitModeGivesTheDataDirsParent()
    {
        var svc = LoadOrderService.WithExplicitPaths(@"C:\Game\Skyrim Special Edition\Data", @"C:\Mods", @"C:\Profile", 0, Store());
        Assert.Equal(@"C:\Game\Skyrim Special Edition", svc.GameDirOrNull());
    }

    // Probe A: "unconfigured: GameDirOrNull returns null (never throws)".
    [Fact]
    public void UnconfiguredGivesNull()
    {
        Assert.Null(LoadOrderService.WithInstance(null, 0, Store()).GameDirOrNull());
    }

    // Probe A: "unusable instance: GameDirOrNull returns null, does NOT throw".
    [Fact]
    public void AnUnusableInstanceGivesNullRatherThanThrowing()
    {
        var svc = LoadOrderService.WithInstance(Path.Combine(_root, "no-such-instance"), 0, Store());
        Assert.Null(svc.GameDirOrNull());
    }

    // Probe A: "CompilerGameDirHints includes the load-order game dir as the first hint, and never throws".
    [Fact]
    public void TheHintsLeadWithTheLoadOrderGameDir()
    {
        var svc = LoadOrderService.WithExplicitPaths(@"C:\Game\Skyrim Special Edition\Data", @"C:\Mods", @"C:\Profile", 0, Store());
        Assert.Equal(@"C:\Game\Skyrim Special Edition", svc.CompilerGameDirHints()[0]);
    }

    // Probe A: "CompilerGameDirHints on an unconfigured service returns a list (no load-order hint; locator-only), never throws".
    [Fact]
    public void AnUnconfiguredServiceStillReturnsAHintList()
    {
        var hints = LoadOrderService.WithInstance(null, 0, Store()).CompilerGameDirHints();
        Assert.NotNull(hints);
    }
}
