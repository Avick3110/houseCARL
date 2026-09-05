namespace HousecarlMcp;

/// <summary>The running server's build version, read once from the informational version the tool assembly embeds
/// (build-plugin.ps1 stamps it from plugin.json, so it carries the release version, '+', and the full commit sha:
/// '1.9.5-dev+e942910...'). The tool assembly, not the entry assembly: they are the same binary for the shipped
/// server, and under a test host the entry assembly is the host rather than houseCARL. This is the one reader of the
/// attribute — the MCP handshake and the status line both come from here, so they can never disagree: <see
/// cref="Handshake"/> is always a prefix of <see cref="Line"/>, on a stamped build and an unstamped one alike.</summary>
public static class ServerBuild
{
    /// <summary>The informational version verbatim, metadata suffix and all; null on an unstamped build.</summary>
    public static string? Version { get; } =
        ToolSurface.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] && !string.IsNullOrWhiteSpace(a.InformationalVersion)
            ? a.InformationalVersion : null;

    /// <summary>What the MCP handshake reports: the version with any "+metadata" suffix trimmed; 0.0.0-dev unstamped.</summary>
    public static string Handshake { get; } = Trim(Version) ?? "0.0.0-dev";

    /// <summary>What the status line prints: the full version, or the same 0.0.0-dev the handshake reports plus one
    /// short clause saying why.</summary>
    public static string Line { get; } = Version ?? "0.0.0-dev (no build stamp)";

    static string? Trim(string? info)
    {
        if (info is null) return null;
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
