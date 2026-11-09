using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>What houseCARL serves off Windows: the record reads over the active plugins, and nothing that reaches the
/// loose-file and archive layers, whose path handling is unsound there (#1256); everything else is refused per call.</summary>
internal static class SupportedPlatform
{
    /// <summary>The OS fact the gate reads. Internal, not a setting: only a test swaps it, to run the real server as non-Windows.</summary>
    internal static Func<bool> IsWindows = OperatingSystem.IsWindows;

    /// <summary>The issue that tracks full Linux support, named in every refusal.</summary>
    internal const string Issue = "https://github.com/Avick3110/houseCARL/issues/1256";

    /// <summary>The tools served off Windows: they read plugin records through the active order and nothing below it.</summary>
    internal static readonly IReadOnlySet<string> ServedOffWindows = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolNames.Records, ToolNames.LoadOrderStatus, ToolNames.SetMo2Instance,
        ToolNames.NexusSearch, ToolNames.NexusMod, ToolNames.NexusGraphql, ToolNames.NexusCheckUpdates,
    };

    /// <summary>The refusal for a call to a registered tool, or null when it is served. Takes the OS fact so a test can drive both sides.</summary>
    internal static string? Refusal(string tool, IDictionary<string, JsonElement>? arguments, bool isWindows)
    {
        if (isWindows) return null;
        if (!ServedOffWindows.Contains(tool))
            return $"error: {tool} is not served on Linux yet: off Windows houseCARL serves only the record reads " +
                   $"({ToolNames.Records}, {ToolNames.LoadOrderStatus}, {ToolNames.SetMo2Instance} and the Nexus " +
                   $"lookups), so run this one on a Windows PC (Linux support: {Issue}).";
        if (tool == ToolNames.Records && arguments is not null)
            foreach (var (name, value) in arguments)
                if (Pole(name) is { } pole && !ServedPole(value))
                    return $"error: {pole}= naming a plugin, a file or the SkyPatcher overlay is not served on Linux " +
                           $"yet, because it can read files outside the active order; read the winner (drop {pole}=" +
                           (pole == "versus" ? ", or pass \"previous_provider\"" : "") +
                           $") or run it on a Windows PC (Linux support: {Issue}).";
        return null;
    }

    /// <summary>The call-tool filter that applies <see cref="Refusal"/>; registered after the argument shim in Program.cs.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> OffWindowsGate => next => async (request, cancellationToken) =>
    {
        if (request.MatchedPrimitive is McpServerTool && request.Params is { } p
            && Refusal(p.Name, p.Arguments, IsWindows()) is { } refusal)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = refusal }] };
        return await next(request, cancellationToken);
    };

    /// <summary>The pole parameter an argument name spells, in any case or underscore variant; null for any other.</summary>
    static string? Pole(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "source" => "source",
        "versus" => "versus",
        _ => null,
    };

    /// <summary>A pole that reads only the active order: absent, the winner, or the provider below the subject.</summary>
    static bool ServedPole(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() is "winner" or "previous_provider" or "",
        _ => false,
    };
}
