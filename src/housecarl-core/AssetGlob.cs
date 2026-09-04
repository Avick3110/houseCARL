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

    /// <summary>Every Data-relative path the selector names, sorted. Throws ArgumentException (naming the input) for a
    /// drive-rooted or parent-escaping selector, the same gate every other asset query passes.</summary>
    public static IReadOnlyList<string> Select(AssetResolver.AssetView view, string selector)
    {
        var norm = AssetResolver.ValidateRelPath(selector).TrimEnd('\\');
        if (!HasWildcard(norm)) return Sorted(view.EnumerateUnder(norm));

        var prefix = LiteralPrefix(norm);
        // A glob with no literal directory in front of it would enumerate the whole VFS — every loose file in every
        // enabled mod and every entry of every archive — before a single path is rendered. Refused rather than paid.
        if (prefix.Length == 0)
            throw new ArgumentException(
                "a glob has to be anchored under a directory, or it sweeps the whole load order — " +
                $"put a folder in front of it, e.g. 'meshes/actors/character/**/*.nif': '{selector}'", nameof(selector));

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
