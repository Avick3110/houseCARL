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
        var under = view.EnumerateUnder(HasWildcard(norm) ? LiteralPrefix(norm) : norm);
        var hits = HasWildcard(norm)
            ? under.Where(p => IsMatch(norm, p))
            : under;
        return hits.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The glob compiled to an anchored, case-insensitive regex. Every character outside the three wildcard
    /// spellings is escaped, so a mod author's own regex-flavoured filename cannot change what the pattern means.</summary>
    static Regex ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { sb.Append(".*"); i++; }
            else if (c == '*') sb.Append("[^\\\\]*");
            else if (c == '?') sb.Append("[^\\\\]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
