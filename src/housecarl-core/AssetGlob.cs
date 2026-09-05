using System.Text;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>The directory / glob SELECT form over the VFS: turn one Data-relative selector into the set of paths it
/// names, by enumerating the selector's literal directory prefix through <see cref="AssetResolver.EnumerateUnder(string)"/>
/// and, when the selector carries wildcards, keeping the paths the pattern matches. A selector with no wildcard IS a
/// directory and takes everything beneath it. Pure over the resolver's own enumeration — this class opens nothing and
/// holds nothing.
/// <para>Wildcards: <c>*</c> matches any run of characters within one path segment, <c>?</c> exactly one such
/// character, and <c>**</c> matches across separators. Matching is case-insensitive and runs against the whole
/// Data-relative path, the same spelling the enumeration returns.</para></summary>
public static class AssetGlob
{
    static readonly char[] Wildcards = { '*', '?' };

    /// <summary>Does this selector carry a wildcard — i.e. is it a pattern rather than a plain directory?</summary>
    public static bool HasWildcard(string selector) => (selector ?? "").IndexOfAny(Wildcards) >= 0;

    /// <summary>The literal directory prefix of a selector: everything before the separator that precedes the first
    /// wildcard. "meshes/actors/*/x.nif" prefixes to "meshes\actors"; a wildcard in the first segment prefixes to the
    /// Data root (""). A selector with no wildcard is its own prefix.</summary>
    public static string LiteralPrefix(string normalized)
    {
        int wild = normalized.IndexOfAny(Wildcards);
        if (wild < 0) return normalized;
        int sep = normalized.LastIndexOf('\\', Math.Max(wild - 1, 0));
        return wild == 0 || sep < 0 ? "" : normalized.Substring(0, sep);
    }

    /// <summary>Does <paramref name="path"/> match <paramref name="pattern"/>? Both are Data-relative and already
    /// normalized to backslashes.</summary>
    public static bool IsMatch(string pattern, string path) => ToRegex(pattern).IsMatch(path);

    /// <summary>Every Data-relative path the selector names, sorted. Throws ArgumentException (naming the input, never
    /// a parameter) for a drive-rooted or parent-escaping selector, the same gate every other asset query passes, and
    /// for one that names no directory at all.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector) =>
        Select(view, selector, out _);

    /// <summary>As <see cref="Select(AssetResolver.AssetView, string)"/>, and says whether the selector turned out to
    /// name one FILE rather than a folder — the caller pasted a path, and the answer is that path. The caller renders
    /// that as a note, so the selection and the wording agree about what happened.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector, out bool namedOneFile)
    {
        namedOneFile = false;
        var norm = AssetResolver.ValidateRelPath(selector).TrimEnd('\\');
        var prefix = LiteralPrefix(norm);
        // A selector with no literal directory in front of it would enumerate the whole VFS — every loose file in every
        // enabled mod and every entry of every archive — before a single path is rendered. Refused rather than paid.
        // Tested on the PREFIX, not on the presence of a wildcard: "/", "\" and "//" all normalize to the Data root and
        // sweep exactly as wide as an unanchored glob does.
        if (prefix.Length == 0)
            throw new ArgumentException(
                "under has to be anchored under a directory, or it sweeps the whole load order — " +
                $"name a folder, e.g. 'meshes/actors/character' or 'meshes/actors/character/**/*.nif': '{selector}'");

        if (!HasWildcard(norm))
        {
            var beneath = Sorted(view.EnumerateUnder(norm));
            if (beneath.Count > 0) return beneath;
            // Nothing beneath it, but the string may BE a file the load order provides — a path pasted into under=
            // instead of asset_paths=. Answer it as that one file rather than claiming no mod provides the folder,
            // which would contradict what asset_paths= says about the very same string. One resolve, only on the
            // empty branch, so the ordinary folder sweep pays nothing for it.
            if (view.Resolve(norm).Exists) { namedOneFile = true; return new[] { norm }; }
            return beneath;
        }

        var rx = ToRegex(norm);                                  // compiled ONCE, not per candidate path
        return Sorted(view.EnumerateUnder(prefix).Where(p => rx.IsMatch(p)));
    }

    static IReadOnlyList<string> Sorted(IEnumerable<string> paths) =>
        paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The glob compiled to an anchored, case-insensitive regex. Every character outside the three wildcard
    /// spellings is escaped, so a mod author's own regex-flavoured filename cannot change what the pattern means.</summary>
    static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**\" spans ZERO OR MORE whole segments, so 'a\**\x.nif' matches 'a\x.nif' as well as 'a\b\x.nif'.
                // A trailing "**" is the plain any-run-of-characters form.
                if (i + 2 < pattern.Length && pattern[i + 2] == '\\') { sb.Append("(?:[^\\\\]*\\\\)*"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append("[^\\\\]*");
            else if (c == '?') sb.Append("[^\\\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        // NonBacktracking: the '**' spelling nests quantifiers, and a linear-time engine forecloses the pathological
        // non-match rather than bounding it with a timeout.
        return new Regex(sb.Append('$').ToString(),
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
