namespace HousecarlCore;

/// <summary>Which of MO2's rules hides a loose path: a root name MO2 leaves out of its own Data view, a skip-list
/// folder, or a skip-list suffix.</summary>
public enum HiddenKind { RootName, SkipDirectory, SkipSuffix }

/// <summary>One rule that hides a loose path, with the name it matched (the root name, the folder, or the suffix).</summary>
public sealed record HiddenRule(HiddenKind Kind, string Token)
{
    /// <summary>What MO2 does to a file this rule matches, in MO2's own words.</summary>
    public string Why => Kind switch
    {
        HiddenKind.RootName => $"left out of MO2's Data view as a root {Token}; the game still maps it",
        HiddenKind.SkipDirectory => $"skipped by MO2's VFS, under a '{Token}' folder",
        HiddenKind.SkipSuffix => $"skipped by MO2's VFS, by the '{Token}' suffix",
    };
}

/// <summary>The loose copies of one path MO2 hides: the rule, the hidden name on disk (the asked path, or the asked
/// path plus a skip suffix when a mod holds only its hidden twin), and the roots that hold it, precedence order.</summary>
public sealed record HiddenCopies(HiddenRule Rule, string HiddenPath, IReadOnlyList<string> Holders)
{
    /// <summary>"hidden by MO2 (why) in 'Mod'", naming the first holder and counting the rest, and the hidden name when it is not the asked one.</summary>
    public string Sentence(string askedPath) =>
        $"hidden by MO2 ({Rule.Why}) in '{Holders[0]}'"
        + (Holders.Count > 1 ? $" and {Holders.Count - 1:N0} more" : "")
        // Compared as Windows opens the asked path, so a slash or a trailing dot is not read as another name.
        + (string.Equals(HiddenPath, AssetResolver.AsWindowsOpens(AssetResolver.Normalize(askedPath)), StringComparison.OrdinalIgnoreCase)
            ? "" : $", as '{HiddenPath}'");
}

/// <summary>What one walk dropped as hidden: how many distinct paths, by which rules, with one example path.</summary>
public sealed class HiddenTally
{
    readonly List<HiddenRule> _rules = new();
    readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _paths.Count;
    public string? Example { get; private set; }

    public void Add(HiddenRule rule, string path)
    {
        _paths.Add(path);
        Example ??= path;
        if (!_rules.Contains(rule)) _rules.Add(rule);
    }

    /// <summary>"MO2 hides N matching paths (why; why), e.g. 'path'", or null when nothing was dropped.</summary>
    public string? Sentence(string one = "matching path", string many = "matching paths") =>
        Count == 0 ? null
        : $"MO2 hides {Count:N0} {(Count == 1 ? one : many)} ({string.Join("; ", _rules.Select(r => r.Why))}), e.g. '{Example}'";
}

/// <summary>MO2's two USVFS skip lists: a loose file whose name ends with a skip suffix, or that sits under a folder
/// named as a skip directory, is not in the game's Data view; nor, whatever the lists say, is a root meta.ini,
/// readme.txt or fomod folder, which MO2 leaves out of its own Data view (docs/architecture/assets.md, Visibility).</summary>
public sealed record Mo2SkipRules(IReadOnlyList<string> FileSuffixes, IReadOnlyList<string> Directories)
{
    public const string SuffixesKey = "skip_file_suffixes";
    public const string DirectoriesKey = "skip_directories";

    /// <summary>MO2's defaults when ModOrganizer.ini sets neither key.</summary>
    public static readonly Mo2SkipRules Default = new(new[] { ".mohidden" }, new[] { ".git" });

    /// <summary>Is this Data-relative loose path, in a linked root (a mod or overwrite), in the game's Data view?</summary>
    public bool Shows(string relPath) => Hides(relPath, linked: true) is null;

    /// <summary>The rule that hides this Data-relative loose path, or null when it shows. Root name first, then skip
    /// folder, then skip suffix. The skip lists apply only to a <paramref name="linked"/> root, a mod or overwrite:
    /// the game's own Data folder is never linked, so only the root names hide there.</summary>
    public HiddenRule? Hides(string relPath, bool linked)
    {
        // Span segments, not Split: the folder walk asks this once per file.
        var rest = relPath.AsSpan();
        if (HiddenAtRoot(rest) is { } root) return new HiddenRule(HiddenKind.RootName, root);
        if (!linked) return null;
        for (int sep; (sep = rest.IndexOf('\\')) >= 0; rest = rest[(sep + 1)..])
            foreach (var d in Directories)
                if (rest[..sep].Equals(d, StringComparison.OrdinalIgnoreCase)) return new HiddenRule(HiddenKind.SkipDirectory, d);
        foreach (var s in FileSuffixes)
            if (rest.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return new HiddenRule(HiddenKind.SkipSuffix, s);
        return null;
    }

    // MO2 constants, not read from the ini.
    const string MetaIni = "meta.ini", ReadmeTxt = "readme.txt";

    static string? HiddenAtRoot(ReadOnlySpan<char> path)
    {
        var sep = path.IndexOf('\\');
        if (sep >= 0) return path[..sep].Equals("fomod", StringComparison.OrdinalIgnoreCase) ? "fomod folder" : null;
        return path.Equals(MetaIni, StringComparison.OrdinalIgnoreCase) ? MetaIni
             : path.Equals(ReadmeTxt, StringComparison.OrdinalIgnoreCase) ? ReadmeTxt
             : null;
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
