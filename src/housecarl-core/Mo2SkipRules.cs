namespace HousecarlCore;

/// <summary>MO2's two USVFS skip lists: a loose file whose name ends with a skip suffix, or that sits under a folder
/// named as a skip directory, is not in the game's Data view; nor, whatever the lists say, is a root meta.ini,
/// readme.txt or fomod folder, which MO2 leaves out of its own Data view (docs/architecture/assets.md, Visibility).</summary>
public sealed record Mo2SkipRules(IReadOnlyList<string> FileSuffixes, IReadOnlyList<string> Directories)
{
    public const string SuffixesKey = "skip_file_suffixes";
    public const string DirectoriesKey = "skip_directories";

    /// <summary>MO2's defaults when ModOrganizer.ini sets neither key.</summary>
    public static readonly Mo2SkipRules Default = new(new[] { ".mohidden" }, new[] { ".git" });

    /// <summary>Is this Data-relative loose path in the game's Data view?</summary>
    public bool Shows(string relPath)
    {
        // Span segments, not Split: the folder walk asks this once per file.
        var rest = relPath.AsSpan();
        if (HiddenAtRoot(rest)) return false;
        for (int sep; (sep = rest.IndexOf('\\')) >= 0; rest = rest[(sep + 1)..])
            foreach (var d in Directories)
                if (rest[..sep].Equals(d, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var s in FileSuffixes)
            if (rest.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Does this normalized path name a root meta.ini, readme.txt or fomod folder, or something in that folder?</summary>
    public static bool NamesHiddenRoot(string path) =>
        HiddenAtRoot(path) || path.Equals("fomod", StringComparison.OrdinalIgnoreCase);

    /// <summary>Does this test over paths match a root file MO2 hides by name?</summary>
    public static bool MatchesHiddenRootFile(Func<string, bool> matches) => matches(MetaIni) || matches(ReadmeTxt);

    // MO2 constants, not read from the ini.
    const string MetaIni = "meta.ini", ReadmeTxt = "readme.txt";

    static bool HiddenAtRoot(ReadOnlySpan<char> path)
    {
        var sep = path.IndexOf('\\');
        return sep < 0
            ? path.Equals(MetaIni, StringComparison.OrdinalIgnoreCase) || path.Equals(ReadmeTxt, StringComparison.OrdinalIgnoreCase)
            : path[..sep].Equals("fomod", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Read one skip list off its raw ini value, the way QSettings writes a string list (<c>a, b</c>): unset
    /// is null, so the caller keeps the default; <c>@Invalid()</c> or a blank value is the empty list; any other
    /// <c>@</c> form throws, because it is not a list. An item Qt had to quote or escape is read as written.</summary>
    internal static IReadOnlyList<string>? ParseList(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        if (v[0] == '@') throw new FormatException($"'{v}' is not a list");
        // An empty item would hide every file, and MO2 cannot have meant that.
        return v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
