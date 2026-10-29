namespace HousecarlCore;

/// <summary>MO2's two USVFS skip lists: a loose file whose name ends with a skip suffix, or that sits under a folder
/// named as a skip directory, is not in the game's Data view (docs/architecture/assets.md, Visibility).</summary>
public sealed record Mo2SkipRules(IReadOnlyList<string> FileSuffixes, IReadOnlyList<string> Directories)
{
    public const string SuffixesKey = "skip_file_suffixes";
    public const string DirectoriesKey = "skip_directories";

    /// <summary>MO2's defaults when ModOrganizer.ini sets neither key.</summary>
    public static readonly Mo2SkipRules Default = new(new[] { ".mohidden" }, new[] { ".git" });

    /// <summary>Is this Data-relative loose path in the game's Data view?</summary>
    public bool Shows(string relPath)
    {
        var segs = relPath.Split('\\');
        for (int i = 0; i < segs.Length - 1; i++)
            foreach (var d in Directories)
                if (segs[i].Equals(d, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var s in FileSuffixes)
            if (segs[^1].EndsWith(s, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Read one skip list off its raw ini value, the way QSettings wrote a string list: unset is null, so
    /// the caller keeps the default; <c>@Invalid()</c> or a blank value is the empty list; any other <c>@</c> form
    /// throws, because it is not a list.</summary>
    internal static IReadOnlyList<string>? ParseList(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        if (v[0] == '@') throw new FormatException($"'{v}' is not a list");
        var items = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var c in v)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { Add(); continue; }
            cur.Append(c);
        }
        if (quoted) throw new FormatException($"'{v}' has an unclosed quote");
        Add();
        return items;

        // An empty entry would hide every file, and MO2 cannot have meant that.
        void Add()
        {
            var item = QtIniEscapes.Unescape(cur.ToString().Trim());
            if (item.Length > 0) items.Add(item);
            cur.Clear();
        }
    }
}
