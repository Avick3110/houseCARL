namespace HousecarlCore;

/// <summary>The "you probably dropped the root folder" suggestion for a path that resolved to nothing. VERIFIED,
/// never guessed: the candidate is re-resolved through the same view (docs/architecture/assets.md).</summary>
public static class AssetPathHint
{
    /// <summary>One asset root a link of one kind lives under.</summary>
    public readonly record struct AssetRoot(string Prefix, AssetLinkKind Kind);

    public static readonly AssetRoot[] MeshRoot = { new(@"meshes\", AssetLinkKind.Model) };

    /// <summary>Both asset roots a bare record-relative path could belong under, for the lane that cannot know the path's kind.</summary>
    public static readonly AssetRoot[] AssetRoots = { new(@"meshes\", AssetLinkKind.Model), new(@"textures\", AssetLinkKind.Texture) };

    /// <summary>Every root's Data form of <paramref name="rel"/> a real provider supplies; empty when there is nothing honest to suggest.</summary>
    public static IReadOnlyList<string> VerifiedPrefixes(AssetResolver.AssetView view, string rel, IReadOnlyList<AssetRoot> roots)
    {
        var norm = Norm(rel);
        if (norm.Length == 0) return Array.Empty<string>();
        foreach (var r in roots)
            if (norm.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                return Array.Empty<string>();

        List<string>? hits = null;
        foreach (var r in roots)
            if (DataForm(norm, r) is { } candidate && view.Resolve(candidate).Exists)
                (hits ??= new List<string>()).Add(candidate);
        return (IReadOnlyList<string>?)hits ?? Array.Empty<string>();
    }

    /// <summary>True when a suggestion only adds a root folder to the path asked; false when it also dropped a leading <c>data\</c>.</summary>
    public static bool AddsRootOnly(string rel, string suggestion) => suggestion.EndsWith(Norm(rel), StringComparison.OrdinalIgnoreCase);

    /// <summary>The sentence to append to an ABSENT message for a MESH tool, or null. A verified hit names the file; a miss names only the convention.</summary>
    public static string? MeshHint(AssetResolver.AssetView view, string rel)
    {
        var norm = Norm(rel);
        if (norm.Length == 0) return null;
        if (norm.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) return null;   // already Data-relative — the prefix isn't what's wrong

        // BACKTICK-delimited: the quoted text is author-controlled and routinely carries an apostrophe.
        var hits = VerifiedPrefixes(view, norm, MeshRoot);
        if (hits.Count > 0)
            return AddsRootOnly(norm, hits[0])
                ? $"Did you mean `{hits[0]}`? A record's Model.File is stored relative to meshes\\, so it needs the meshes\\ prefix to be Data-relative."
                : $"Did you mean `{hits[0]}`? {DataPrefixSentence}";
        var form = DataForm(norm, MeshRoot[0]);
        if (form is not null && !AddsRootOnly(norm, form))
            return $"This path starts with data\\, which is not part of a Data-relative path, so its Data-relative form would be `{form}` "
                 + "(not provided by any active mod or BSA either).";
        return $"This path is not under meshes\\ — if it came from a record's Model.File, that field is stored relative to meshes\\, "
             + $"so the Data-relative form would be `meshes\\{norm}` (not provided by any active mod or BSA either).";
    }

    /// <summary>The sentence for the GENERIC asset lane, which tries both roots. VERIFIED-ONLY, with no weaker convention note.</summary>
    public static string? AssetRootHint(AssetResolver.AssetView view, string rel)
    {
        var hits = VerifiedPrefixes(view, rel, AssetRoots);
        if (hits.Count == 0) return null;
        return $"Did you mean {string.Join(" or ", hits.Select(h => "`" + h + "`"))}? "
             + (hits.All(h => AddsRootOnly(rel, h))
                 ? "A path read off a record is relative to its root folder (meshes\\ / textures\\), not to Data."
                 : DataPrefixSentence);
    }

    /// <summary>Why a suggestion that dropped <c>data\</c> is the path to ask for. It claims nothing about whether the engine reads the <c>data\</c> form.</summary>
    public const string DataPrefixSentence = "A path a file names can start with data\\, which is not part of a Data-relative path.";

    /// <summary>The Data path a root's link kind gives <paramref name="norm"/>, spelt with that root's own prefix when the door only added it; null when the door refuses the string.</summary>
    static string? DataForm(string norm, AssetRoot root)
    {
        var d = AssetDataPath.TryFor(norm, root.Kind);
        if (d is null) return null;
        // Mutagen spells a root it adds as "Textures\"; the hint has always printed the root as the prefix list spells it.
        return d.Length == root.Prefix.Length + norm.Length && d.EndsWith(norm, StringComparison.OrdinalIgnoreCase) ? root.Prefix + norm : d;
    }

    static string Norm(string? rel) => (rel ?? "").Trim().Replace('/', '\\').TrimStart('\\');
}
