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

/// <summary>The four MO2 roots, taken in one <c>_gate</c> hold after the roots are derived.</summary>
internal readonly record struct Mo2Roots(string ModsDir, string DataDir, string OverwriteDir, string ProfileDir);

/// <summary>The head members areas share; contract in docs/architecture/load-order-service.md.</summary>
internal interface ILoadOrderHost
{
    /// <summary>The record resolver, built on first access and kept fresh on every later one.</summary>
    LoadOrderResolver Resolver { get; }

    /// <summary>The live asset resolver, for a core check that captures it itself.</summary>
    AssetResolver Assets { get; }

    /// <summary>The four roots in one <c>_gate</c> hold, derived first; throws what derivation throws.</summary>
    Mo2Roots CaptureRoots();

    /// <summary>One asset build with its warnings, profile and roots, in one <c>_gate</c> hold.</summary>
    AssetCapture CaptureAssets();

    /// <summary>A pinned index and the asset build that pairs with it, in one <c>_gate</c> hold; <paramref name="afterPin"/> runs between the two.</summary>
    (LoadOrderService.ViewPin Pin, AssetCapture Assets) CapturePinAndAssets(Action? afterPin);

    /// <summary>The write gate; lock it before any capture, never inside one.</summary>
    object WriteGate { get; }
}
