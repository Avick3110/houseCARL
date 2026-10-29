using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim.Assets;

namespace HousecarlCore;

/// <summary>What kind of file a link names, which decides its root folder.</summary>
public enum AssetLinkKind { Texture, Model }

/// <summary>The one door from a path as a file names it (a NIF slot, or an ABSENT path the root hint maps) to the Data path the game opens:
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
        var dir = root.ToLowerInvariant() + @"\";
        // A leading separator is dropped as asset_status drops it, so only a drive-rooted string is left rooted.
        var path = AssetResolver.Normalize(raw);
        string? mapped, why;
        if (Path.IsPathRooted(path))
        {
            mapped = Rooted(path, kind);
            why = $"a drive-rooted path names a Data path only through a Data folder with {dir} after it";
        }
        else
        {
            mapped = Relative(path, root);
            why = $"after its leading data\\ it is not under {dir}";
        }
        if (mapped is not null)
        {
            // Mutagen's mapping is not the Data-path gate: a '..' or no-name segment is refused here.
            try { return AssetResolver.ValidateRelPath(mapped); }
            catch (ArgumentException) { why = "it has a '..' segment, or one of only dots or spaces"; }
        }
        throw new ArgumentException($"'{raw}' names no {kind.ToString().ToLowerInvariant()} Data path: {why}. Give the path from {dir} on, as it sits under Data.");
    }

    /// <summary>A drive-rooted string's Data path through Mutagen's link, which finds the game's Data folder inside it; null when it refuses.</summary>
    static string? Rooted(string path, AssetLinkKind kind)
    {
        try
        {
            return kind == AssetLinkKind.Texture
                ? new AssetLink<SkyrimTextureAssetType>(path).DataRelativePath.Path
                : new AssetLink<SkyrimModelAssetType>(path).DataRelativePath.Path;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A relative string's Data path: a leading <c>data\</c> is the Data folder, a folder named data deeper in is part of the path; null when nothing after a leading <c>data\</c> is under the root.</summary>
    static string? Relative(string path, string root)
    {
        var lead = path.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase);
        var rest = lead ? path[@"data\".Length..] : path;
        // Mutagen's own root test (no separator boundary) and its spelling of an added root.
        if (rest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return rest;
        return lead ? null : Path.Combine(root, rest);
    }

    /// <summary>As <see cref="For"/>, or null when there is no legal Data path to offer.</summary>
    public static string? TryFor(string raw, AssetLinkKind kind)
    {
        try { return For(raw, kind); }
        catch (ArgumentException) { return null; }
    }
}
