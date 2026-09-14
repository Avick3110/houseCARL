using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Recognizes a raw path that reaches INTO MO2's mods tree and says how to address that copy instead. A mod
/// folder is named, never pathed: reading or writing through the folder's own path goes around the virtual file
/// system, so houseCARL refuses the path and hands back the address form — the Data-relative path plus the mod
/// folder's name — that every source pole already takes.</summary>
static class ModsPathAddress
{
    /// <summary>The mod folder and the Data-relative remainder a raw path under <paramref name="modsRoot"/> splits
    /// into, or null when the path is not under the mods tree (or there is no mods tree to compare against). The
    /// remainder is null when the path names the mod folder itself and nothing inside it.</summary>
    internal static (string ModFolder, string? RelPath)? Split(string? rawPath, string? modsRoot)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(modsRoot)) return null;
        string full, root;
        try
        {
            full = Path.GetFullPath(rawPath.Trim().Trim('"'));
            root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }                                  // an unparseable path is somebody else's refusal
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = full.Substring(root.Length + 1);
        int cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return cut < 0 ? (rest, null) : (rest.Substring(0, cut), rest.Substring(cut + 1));
    }

    /// <summary>The one-sentence refusal for a raw mods path on a tool that reads a copy: what is wrong (the path
    /// goes around the VFS) and what to try (the same copy, addressed by mod folder name). <paramref name="pathParam"/>
    /// and <paramref name="providerParam"/> are the tool's own parameter names, so the sentence hands back a call the
    /// caller can make.</summary>
    internal static string Refusal(string where, string rawPath, string modFolder, string? relPath,
                                   string pathParam, string providerParam)
        => $"{where}'{rawPath}' is a raw path into MO2's mods folder, which reads past the virtual file system — a mod "
         + $"folder is named, never pathed. Address that copy instead with {providerParam}='{modFolder}' and "
         + $"{pathParam}=" + (relPath is null
                ? "the Data-relative path inside it (e.g. 'meshes\\armor\\iron\\cuirass_1.nif')."
                : $"'{DataRelative(relPath)}'.");

    /// <summary>The remainder as a Data-relative asset path: a mod folder's tree IS the Data tree, so the part after
    /// the folder name is already what the VFS calls the file. Normalized the way every other asset path is, and
    /// returned as it came when it will not normalize.</summary>
    static string DataRelative(string rest)
    {
        try { return AssetResolver.ValidateRelPath(rest); }
        catch (ArgumentException) { return rest; }
    }
}
