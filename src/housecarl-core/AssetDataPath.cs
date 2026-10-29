using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim.Assets;

namespace HousecarlCore;

/// <summary>What kind of file a link names, which decides its root folder.</summary>
public enum AssetLinkKind { Texture, Model }

/// <summary>The one door from a path as an asset names it (a NIF slot, a record field) to the Data path the game opens:
/// Mutagen's typed asset link, which drops a leading <c>data\</c> and adds the kind's root folder where it is missing
/// (docs/architecture/assets.md, From a link to a Data path).</summary>
public static class AssetDataPath
{
    /// <summary>The Data-relative path <paramref name="raw"/> names as a <paramref name="kind"/> link; throws
    /// <see cref="ArgumentException"/> naming the input when it is no legal Data path.</summary>
    public static string For(string raw, AssetLinkKind kind)
    {
        try
        {
            var mapped = kind switch
            {
                AssetLinkKind.Texture => new AssetLink<SkyrimTextureAssetType>(raw).DataRelativePath.Path,
                AssetLinkKind.Model => new AssetLink<SkyrimModelAssetType>(raw).DataRelativePath.Path,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            // Mutagen's mapping is not the Data-path gate: a rooted or '..' result is refused here.
            return AssetResolver.ValidateRelPath(mapped);
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            throw new ArgumentException($"'{raw}' is not a path a {kind.ToString().ToLowerInvariant()} link can name — {ex.Message}", ex);
        }
    }

    /// <summary>As <see cref="For"/>, or null when there is no legal Data path to offer.</summary>
    public static string? TryFor(string raw, AssetLinkKind kind)
    {
        try { return For(raw, kind); }
        catch (ArgumentException) { return null; }
    }
}
