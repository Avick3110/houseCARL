namespace HousecarlCore;

/// <summary>The quantifier a path step declares — the multiplicity and the fold spelled IN the step, where it
/// binds. One vocabulary, shared by <c>where=</c>'s predicates and <c>project.fields</c>' projection, so the two
/// surfaces can never drift on what a token means.</summary>
public enum PathFold { None, Set, Any, All, NoneOf, Count }

/// <summary>The quantified step's tokenizer. The word list lives here ONCE: every surface that reads a quantifier
/// reads it through this, so a token added on one side is a token on the other by construction.</summary>
public static class PathFoldGrammar
{
    /// <summary>Split one path segment into its bare field name, the fold its bracket key spells, and that key as
    /// the caller wrote it. <see cref="PathFold.None"/> with a null key = no quantifier at all;
    /// <see cref="PathFold.None"/> with a non-null key = a bracket key that begins '*' but is not a quantifier
    /// word, which each surface names in its own voice.</summary>
    public static (string Bare, PathFold Fold, string? Key) Read(string seg)
    {
        int open = seg.IndexOf('[');
        if (open < 0 || !seg.EndsWith("]", StringComparison.Ordinal)) return (seg, PathFold.None, null);
        var key = seg[(open + 1)..^1];
        if (key.Length == 0 || key[0] != '*') return (seg, PathFold.None, null);
        var word = key[1..];
        var fold = word.Length == 0 ? PathFold.Set
                 : word.Equals("any", StringComparison.OrdinalIgnoreCase) ? PathFold.Any
                 : word.Equals("all", StringComparison.OrdinalIgnoreCase) ? PathFold.All
                 : word.Equals("none", StringComparison.OrdinalIgnoreCase) ? PathFold.NoneOf
                 : word.Equals("count", StringComparison.OrdinalIgnoreCase) ? PathFold.Count
                 : PathFold.None;
        return (seg[..open], fold, key);
    }

    /// <summary>The token a fold is spelled with, for a message.</summary>
    public static string Token(PathFold f) => f switch
    {
        PathFold.Set => "[*]", PathFold.Any => "[*any]", PathFold.All => "[*all]",
        PathFold.NoneOf => "[*none]", PathFold.Count => "[*count]", _ => "",
    };
}
