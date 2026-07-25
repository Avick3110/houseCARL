namespace HousecarlCore;

/// <summary>
/// The "you probably dropped the root folder" suggestion for an asset path that resolved to nothing (#273).
///
/// THE PAPERCUT: a model path read straight off a record — <c>Model.File</c> on an NPC / ARMA / STAT — is stored
/// relative to <c>meshes\</c>, but every asset tool wants it Data-relative. Passing the record's own value verbatim
/// is therefore the NORMAL way one arrives at a mesh, and it returns a flat ABSENT with nothing saying why. The
/// answer is true for the string as given, so this is not a wrong answer — but the caller has to already know the
/// convention to get past it, and spends a round trip discovering it.
///
/// VERIFIED, NEVER GUESSED — the house posture for suggestions (<see cref="PluginNameSuggest"/>: a wrong "did you
/// mean" is worse than none). This does not pattern-match the path or reason about what it looks like; it RE-RESOLVES
/// the prefixed candidate through the same asset view and returns it only if a real active mod or BSA provides it.
/// A suggestion that comes back therefore always names a file that exists. If nothing hits, nothing is suggested,
/// and the caller is free to say the weaker, honest thing instead ("that field is stored relative to meshes\")
/// without claiming any file exists.
///
/// Cost: one extra snapshot lookup per already-failed path, on a path that is by definition not in the hot loop.
/// </summary>
public static class AssetPathHint
{
    /// <summary>The root a record's model path is relative to — the whole reason this helper exists.</summary>
    public static readonly string[] MeshRoot = { @"meshes\" };

    /// <summary>Both asset roots a bare record-relative path could belong under, for the generic asset lane where the
    /// path's kind isn't known from the tool (a mesh path and a texture path arrive through the same door).</summary>
    public static readonly string[] AssetRoots = { @"meshes\", @"textures\" };

    /// <summary>Every <paramref name="prefixes"/> candidate that a real provider supplies for <paramref name="rel"/>
    /// — i.e. the paths the caller probably meant. EMPTY when there is nothing honest to suggest, which is the common
    /// case and the safe default:
    ///   • <paramref name="rel"/> already starts with one of the roots → the missing prefix is not the problem here.
    ///   • no prefixed candidate resolves → the file genuinely isn't there under any of them.
    ///   • the path is empty, or so malformed that even the prefixed form is rejected (drive-rooted, '..'-escaping).
    /// Never throws: a rejected candidate is simply not a suggestion.</summary>
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

    /// <summary>The sentence to append to an ABSENT message for a MESH tool, or null when there is nothing to add.
    /// Two strengths, and the difference between them is load-bearing (Q3): a VERIFIED hit names the file and says
    /// "did you mean"; a miss names only the CONVENTION and the form the path would take, explicitly stating that
    /// form isn't provided either — so the weaker note can never be read as "the file is over there".</summary>
    public static string? MeshHint(AssetResolver.AssetView view, string rel)
    {
        var norm = (rel ?? "").Trim().Replace('/', '\\').TrimStart('\\');
        if (norm.Length == 0) return null;
        if (norm.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) return null;   // already Data-relative — the prefix isn't what's wrong

        // BACKTICK-delimited, not single-quoted — the same reason PluginNameSuggest.DidYouMean moved to backticks
        // (commit 7c5dfe1): the thing being quoted is author-controlled text that routinely carries an apostrophe
        // (a mod's "Sanguine's Trade" folder, a "Dragon's Reach" mesh subtree), which collides with a wrapping '
        // and reads as a broken quote. Backticks never collide with the path's own characters.
        var hits = VerifiedPrefixes(view, norm, MeshRoot);
        if (hits.Count > 0)
            return $"Did you mean `{hits[0]}`? A record's Model.File is stored relative to meshes\\, so it needs the meshes\\ prefix to be Data-relative.";
        return $"This path is not under meshes\\ — if it came from a record's Model.File, that field is stored relative to meshes\\, "
             + $"so the Data-relative form would be `meshes\\{norm}` (not provided by any active mod or BSA either).";
    }
}
