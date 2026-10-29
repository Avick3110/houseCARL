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
        var root = kind switch
        {
            AssetLinkKind.Texture => SkyrimTextureAssetType.Instance.BaseFolder,
            AssetLinkKind.Model => SkyrimModelAssetType.Instance.BaseFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        try
        {
            // Only a rooted string holds the game's Data folder somewhere inside it; Mutagen's link finds it there.
            var mapped = Path.IsPathRooted(raw)
                ? kind == AssetLinkKind.Texture
                    ? new AssetLink<SkyrimTextureAssetType>(raw).DataRelativePath.Path
                    : new AssetLink<SkyrimModelAssetType>(raw).DataRelativePath.Path
                : Relative(raw.Replace('/', '\\'), root);
            // Mutagen's mapping is not the Data-path gate: a rooted or '..' result is refused here.
            return AssetResolver.ValidateRelPath(mapped);
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            throw new ArgumentException($"'{raw}' is not a path a {kind.ToString().ToLowerInvariant()} link can name — {ex.Message}", ex);
        }
    }

    /// <summary>A relative string's Data path: a leading <c>data\</c> is the Data folder, a folder named data deeper in is part of the path.</summary>
    static string Relative(string path, string root)
    {
        var lead = path.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase);
        var rest = lead ? path[@"data\".Length..] : path;
        // Mutagen's own root test (no separator boundary) and its spelling of an added root.
        if (rest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return rest;
        if (lead) throw new ArgumentException($"after its leading data\\ it is not under {root}\\");
        return Path.Combine(root, rest);
    }

    /// <summary>As <see cref="For"/>, or null when there is no legal Data path to offer.</summary>
    public static string? TryFor(string raw, AssetLinkKind kind)
    {
        try { return For(raw, kind); }
        catch (ArgumentException) { return null; }
    }
}
