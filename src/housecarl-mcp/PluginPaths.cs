namespace HousecarlMcp;

/// <summary>Plugin arguments that name a file path: is it a path, do two paths denote one file, and which active plugin a path is.</summary>
internal static class PluginPaths
{
    /// <summary>Does the user's `plugin` argument denote a PATH, used verbatim, rather than a bare filename located in the MO2 folders? True if rooted or carrying a directory separator.</summary>
    internal static bool LooksLikePath(string s) => Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/');

    /// <summary>Do two paths denote the same plugin file? A full-path compare, never a filename one, since a backup and the live copy share a name.</summary>
    internal static bool SamePluginFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>If <paramref name="path"/> is the EXACT file the active order loads for its filename, the plugin name
    /// the order knows it by; else null.</summary>
    internal static string? ActiveNameForPath(LoadOrderResolver.IndexView view, string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim()); } catch { return null; }
        var name = Path.GetFileName(full);
        if (name.Length == 0 || !view.ContainsPlugin(name)) return null;
        // An excluded plugin is still in the name table and the active lane can only refuse it; reading its file
        // directly is the escape hatch, so a path to one must keep taking the off-order lane.
        if (view.ExcludedPlugins.ContainsKey(name)) return null;
        var active = view.PluginPath(name);
        return !string.IsNullOrEmpty(active) && SamePluginFile(active, full) ? name : null;
    }
}
