using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Recognizes a raw path that reaches INTO MO2's mods tree and says how to address that copy instead. A mod
/// folder is named, never pathed: reading or writing through the folder's own path goes around the virtual file
/// system, so houseCARL refuses the path and hands back the address form — the Data-relative path plus the mod
/// folder's name — that every source pole already takes.</summary>
static class ModsPathAddress
{
    /// <summary>The mod folder and the Data-relative remainder a raw path under <paramref name="modsRoot"/> splits
    /// into, or null when the path is not under the mods tree (or there is no mods tree to compare against). A mod
    /// folder's tree IS the Data tree, so the remainder is already what the VFS calls the file, normalized the way
    /// every other asset path is. The remainder is null when the path names the mod folder itself and nothing
    /// inside it.</summary>
    internal static (string ModFolder, string? RelPath)? Split(string? rawPath, string? modsRoot)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || string.IsNullOrWhiteSpace(modsRoot)) return null;
        var raw = rawPath.Trim().Trim('"');
        // Only a path the caller actually ROOTED can be a raw mods path. Path.GetFullPath resolves a relative one
        // against the server's own working directory, so without this a session started inside a mod folder would
        // see every ordinary Data-relative address as a raw mods path and refuse the normal form.
        if (!Path.IsPathFullyQualified(raw)) return null;
        string full, root;
        try
        {
            full = Path.GetFullPath(raw);
            root = Path.GetFullPath(modsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }                                  // an unparseable path is somebody else's refusal
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = full.Substring(root.Length + 1);
        int cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return cut < 0 ? (rest, null) : (rest.Substring(0, cut), DataRelative(rest.Substring(cut + 1)));
    }

    /// <summary>The one-sentence refusal for a raw mods path: what is wrong (the path goes around the VFS) and what
    /// to try. <paramref name="remedy"/> is the second half, which differs by which parameter carried the path and by
    /// what that parameter will actually accept on the retry — built by the caller, which is the only place that
    /// knows.</summary>
    internal static string Refusal(string where, string rawPath, string remedy)
        => $"{where}'{rawPath}' is a raw path into MO2's mods folder, which reads past the virtual file system — a mod "
         + $"folder is named, never pathed. {remedy}";

    /// <summary>The ordinary remedy: name the mod folder in the provider pole and pass the Data-relative path.</summary>
    internal static string Address(string modFolder, string? relPath, string pathParam, string providerParam)
        => $"Address that copy instead with {providerParam}='{modFolder}' and "
         + $"{pathParam}=" + (relPath is null
                ? "the Data-relative path inside it (e.g. 'meshes\\armor\\iron\\cuirass_1.nif')."
                : $"'{relPath}'.");

    /// <summary>A Data-relative asset path, folded the way the resolver folds one; returned as it came when it will
    /// not normalize, so a refusal quotes something rather than nothing.</summary>
    static string DataRelative(string rest)
    {
        try { return AssetResolver.ValidateRelPath(rest); }
        catch (ArgumentException) { return rest; }
    }
}
