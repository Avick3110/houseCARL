namespace HousecarlMcp;

/// <summary>The start-up refusal on a non-Windows OS, where every loose-asset answer would be silently wrong until Linux is supported (#1256).</summary>
internal static class SupportedPlatform
{
    /// <summary>The one sentence the server says before it exits on a non-Windows OS, or null on Windows. Takes the OS fact so a test can drive both sides.</summary>
    internal static string? Refusal(bool isWindows) => isWindows
        ? null
        : "houseCARL runs on Windows only and will not start on this system; run it on Windows for now, since Linux " +
          "support is planned (https://github.com/Avick3110/houseCARL/issues/1256).";
}
