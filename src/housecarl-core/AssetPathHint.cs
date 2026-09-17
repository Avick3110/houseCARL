namespace HousecarlCore;

/// <summary>The "you probably dropped the root folder" suggestion for a path that resolved to nothing. VERIFIED,
/// never guessed: the prefixed candidate is re-resolved through the same view (docs/architecture/assets.md).</summary>
public static class AssetPathHint
{
    public static readonly string[] MeshRoot = { @"meshes\" };

    /// <summary>Both asset roots a bare record-relative path could belong under, for the lane that cannot know the path's kind.</summary>
    public static readonly string[] AssetRoots = { @"meshes\", @"textures\" };

    /// <summary>Every <paramref name="prefixes"/> candidate a real provider supplies for <paramref name="rel"/>; empty when there is nothing honest to suggest.</summary>
    public static IReadOnlyList<string> VerifiedPrefixes(AssetResolver.AssetView view, string rel, IReadOnlyList<string> prefixes)
    {
        var norm = (rel ?? "").Trim().Replace('/', '\\').TrimStart('\\');
        if (norm.Length == 0) return Array.Empty<string>();
        foreach (var p in prefixes)
            if (norm.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

        List<string>? hits = null;
        foreach (var p in prefixes)
        {
            var candidate = p + norm;
            try
            {
                if (view.Resolve(candidate).Exists) (hits ??= new List<string>()).Add(candidate);
            }
            catch (ArgumentException) { /* the prefixed form is still not a legal Data-relative path — nothing to suggest */ }
        }
        return (IReadOnlyList<string>?)hits ?? Array.Empty<string>();
    }

    /// <summary>The sentence to append to an ABSENT message for a MESH tool, or null. A verified hit names the file; a miss names only the convention.</summary>
    public static string? MeshHint(AssetResolver.AssetView view, string rel)
    {
        var norm = (rel ?? "").Trim().Replace('/', '\\').TrimStart('\\');
        if (norm.Length == 0) return null;
        if (norm.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) return null;   // already Data-relative — the prefix isn't what's wrong

        // BACKTICK-delimited: the quoted text is author-controlled and routinely carries an apostrophe.
        var hits = VerifiedPrefixes(view, norm, MeshRoot);
        if (hits.Count > 0)
            return $"Did you mean `{hits[0]}`? A record's Model.File is stored relative to meshes\\, so it needs the meshes\\ prefix to be Data-relative.";
        return $"This path is not under meshes\\ — if it came from a record's Model.File, that field is stored relative to meshes\\, "
             + $"so the Data-relative form would be `meshes\\{norm}` (not provided by any active mod or BSA either).";
    }

    /// <summary>The sentence for the GENERIC asset lane, which tries both roots. VERIFIED-ONLY, with no weaker convention note.</summary>
    public static string? AssetRootHint(AssetResolver.AssetView view, string rel)
    {
        var hits = VerifiedPrefixes(view, rel, AssetRoots);
        if (hits.Count == 0) return null;
        return $"Did you mean {string.Join(" or ", hits.Select(h => "`" + h + "`"))}? "
             + "A path read off a record is relative to its root folder (meshes\\ / textures\\), not to Data.";
    }
}
