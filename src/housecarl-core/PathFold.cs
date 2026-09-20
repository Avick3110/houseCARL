namespace HousecarlCore;

/// <summary>The quantifier a path step declares, shared by <c>where=</c> and <c>project.fields</c>.</summary>
public enum PathFold { None, Set, Any, All, NoneOf, Count }

/// <summary>The quantified step's tokenizer — the word list, once, for every surface that reads a quantifier.</summary>
public static class PathFoldGrammar
{
    /// <summary>Split one path segment into its bare field name, the fold its bracket key spells, and that key.
    /// <see cref="PathFold.None"/> with a non-null key = a bracket key beginning '*' that is not a quantifier word.</summary>
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
