namespace HousecarlMcp;

/// <summary>The start-up refusal on a non-Windows OS, where path handling (loose assets, plugins, records, Papyrus roots, write targets) is unsound and answers could be silently wrong; it lifts only with full Linux support (#1256).</summary>
internal static class SupportedPlatform
{
    /// <summary>The OS fact the start reads. Internal, not a setting: only a test swaps it, to run the real entry point as non-Windows.</summary>
    internal static Func<bool> IsWindows = OperatingSystem.IsWindows;

    /// <summary>The one sentence the server says before it exits on a non-Windows OS, or null on Windows. Takes the OS fact so a test can drive both sides.</summary>
    internal static string? Refusal(bool isWindows) => isWindows
        ? null
        : "houseCARL runs on Windows only and will not start on this system; run it on a Windows PC instead (Linux " +
          "support is planned: https://github.com/Avick3110/houseCARL/issues/1256).";
}
