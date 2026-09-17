using System.Text;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>The directory / glob SELECT form over the VFS: one Data-relative selector to the set of paths it names,
/// by enumerating its literal directory prefix and keeping what the pattern matches. Wildcard grammar and the two
/// bounds are in docs/architecture/assets.md. Opens nothing and holds nothing.</summary>
public static class AssetGlob
{
    static readonly char[] Wildcards = { '*', '?' };

    /// <summary>Does this selector carry a wildcard — i.e. is it a pattern rather than a plain directory?</summary>
    public static bool HasWildcard(string selector) => (selector ?? "").IndexOfAny(Wildcards) >= 0;

    /// <summary>The literal directory prefix: everything before the separator preceding the first wildcard. A selector with no wildcard is its own prefix.</summary>
    public static string LiteralPrefix(string normalized)
    {
        int wild = normalized.IndexOfAny(Wildcards);
        if (wild < 0) return normalized;
        int sep = normalized.LastIndexOf('\\', Math.Max(wild - 1, 0));
        return wild == 0 || sep < 0 ? "" : normalized.Substring(0, sep);
    }

    public static bool IsMatch(string pattern, string path) => ToRegex(pattern).IsMatch(path);

    /// <summary>Every Data-relative path the selector names, sorted. Throws ArgumentException for a drive-rooted, parent-escaping or unanchored selector.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector) =>
        Select(view, selector, out _);

    /// <summary>As above, and says whether the selector turned out to name one FILE rather than a folder.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector, out bool namedOneFile)
        => Select(view, selector, out namedOneFile, 0, out _);

    /// <summary>As above, with the enumeration BOUNDED: the walk stops at <paramref name="max"/> MATCHES and says so
    /// through <paramref name="stopped"/>, so a narrow glob under a wide prefix is never refused for the prefix.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector, out bool namedOneFile,
                                               int max, out bool stopped)
    {
        namedOneFile = false;
        stopped = false;
        var norm = AssetResolver.ValidateRelPath(selector).TrimEnd('\\');
        var prefix = LiteralPrefix(norm);
        // A selector with no literal directory would enumerate the whole VFS before a path rendered. Tested on the
        // PREFIX, not on the presence of a wildcard: "/", "\" and "//" sweep exactly as wide as an unanchored glob.
        if (prefix.Length == 0)
            throw new ArgumentException(
                "under has to be anchored under a directory, or it sweeps the whole load order — " +
                $"name a folder, e.g. 'meshes/actors/character' or 'meshes/actors/character/**/*.nif': '{selector}'");

        if (!HasWildcard(norm))
        {
            var beneath = Sorted(view.EnumerateUnder(norm, null, max, out stopped));
            if (beneath.Count > 0) return beneath;
            // Nothing beneath it, but the string may BE a file the load order provides — a path pasted into under=.
            // One resolve, only on the empty branch, so the ordinary folder sweep pays nothing for it.
            if (view.Resolve(norm).Exists) { namedOneFile = true; return new[] { norm }; }
            return beneath;
        }

        var rx = ToRegex(norm);                                  // compiled ONCE, not per candidate path
        return Sorted(view.EnumerateUnder(prefix, p => rx.IsMatch(p), max, out stopped));
    }

    static IReadOnlyList<string> Sorted(IEnumerable<string> paths) =>
        paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The glob compiled to an anchored, case-insensitive regex, every non-wildcard character escaped.</summary>
    static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // "**\" spans ZERO OR MORE whole segments; a trailing "**" is the plain any-run form.
                if (i + 2 < pattern.Length && pattern[i + 2] == '\\') { sb.Append("(?:[^\\\\]*\\\\)*"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append("[^\\\\]*");
            else if (c == '?') sb.Append("[^\\\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        // NonBacktracking: the '**' spelling nests quantifiers, so a linear-time engine forecloses the pathological non-match.
        return new Regex(sb.Append('$').ToString(),
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
