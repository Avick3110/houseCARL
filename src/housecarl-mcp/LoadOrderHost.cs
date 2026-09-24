// The shared door every area's host interface extends, and the asset capture it returns.
namespace HousecarlMcp;

/// <summary>One asset build with its warnings, profile and roots, all taken in one <c>_gate</c> hold.</summary>
internal readonly record struct AssetCapture(AssetResolver.AssetView View, IReadOnlyList<string> Warnings, string ProfileName,
                                             string ProfileDir, string DataDir, string ModsDir, string OverwriteDir,
                                             IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> EnabledMods)
{
    /// <summary>The captured mods root, or null when there is none.</summary>
    public string? ModsRootOrNull => string.IsNullOrWhiteSpace(ModsDir) ? null : ModsDir;
}

/// <summary>The head members areas share; contract in docs/architecture/load-order-service.md.</summary>
internal interface ILoadOrderHost
{
    /// <summary>One asset build with its warnings, profile and roots, in one <c>_gate</c> hold.</summary>
    AssetCapture CaptureAssets();

    /// <summary>The write gate; lock it before any capture, never inside one.</summary>
    object WriteGate { get; }
}
