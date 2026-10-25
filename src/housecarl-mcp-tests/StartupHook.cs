using HousecarlMcp;

/// <summary>
/// A .NET startup hook, run inside the server process only when a test names this assembly in DOTNET_STARTUP_HOOKS:
/// it makes the start read as non-Windows. The runtime finds it by this exact name in the global namespace.
/// </summary>
internal static class StartupHook
{
    public static void Initialize() => SupportedPlatform.IsWindows = () => false;
}
