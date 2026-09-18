namespace HousecarlMcp;

/// <summary>The running server's build version, read once from the tool assembly's informational version; <see cref="Handshake"/> is always a prefix of <see cref="Line"/> (<c>ServerBuildLineTests</c>).</summary>
public static class ServerBuild
{
    /// <summary>The informational version verbatim, metadata suffix and all; null on an unstamped build.</summary>
    public static string? Version { get; } =
        ToolSurface.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] && !string.IsNullOrWhiteSpace(a.InformationalVersion)
            ? a.InformationalVersion : null;

    /// <summary>What the MCP handshake reports: the version with any "+metadata" suffix trimmed; 0.0.0-dev unstamped.</summary>
    public static string Handshake { get; } = Trim(Version) ?? "0.0.0-dev";

    /// <summary>What the status line prints: the full version, or 0.0.0-dev plus one clause saying why.</summary>
    public static string Line { get; } = Version ?? "0.0.0-dev (no build stamp)";

    static string? Trim(string? info)
    {
        if (info is null) return null;
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
