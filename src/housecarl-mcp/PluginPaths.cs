namespace HousecarlMcp;

/// <summary>Path helpers for two jobs: a plugin argument that names a file path (is it a path, do two paths denote one file, which active plugin a path is), and which MO2 layer any physical path is in.</summary>
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

    /// <summary>The MO2 LAYER a physical file path belongs to, as a NAME. A caller that has to say WHICH of the three
    /// answered takes <see cref="InstallLayerOfPath"/> instead, because a mod folder may itself be called "Data".</summary>
    internal static string? LayerOfInstallPath(string archivePath, Mo2Roots roots) =>
        InstallLayerOfPath(archivePath, roots)?.Name;

    /// <summary>The MO2 layer a physical file path belongs to, as the BRANCH that answered plus the name it produced.</summary>
    internal static SourceLayer? InstallLayerOfPath(string archivePath, Mo2Roots roots)
    {
        // Full-path-normalize both sides, or a trailing separator or '..' from config makes this test disagree with the rest of the plumbing.
        static string Norm(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        archivePath = Norm(archivePath);
        static bool Under(string path, string root, out string remainder)
        {
            remainder = "";
            if (root.Length == 0) return false;
            var r = Norm(root).TrimEnd('\\', '/') + "\\";
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return false;
            remainder = path.Substring(r.Length);
            return true;
        }
        if (Under(archivePath, roots.OverwriteDir, out _))
            return new SourceLayer(SourceLayerKind.Overwrite, AssetResolver.OverwriteLayerName);
        if (Under(archivePath, roots.ModsDir, out var rest))
        {
            int slash = rest.IndexOfAny(new[] { '\\', '/' });
            // a .bsa directly in mods\ belongs to no mod — no translation
            return slash > 0 ? new SourceLayer(SourceLayerKind.ModFolder, rest[..slash]) : null;
        }
        if (Under(archivePath, roots.DataDir, out _))
            return new SourceLayer(SourceLayerKind.GameData, AssetResolver.DataLayerName);
        return null;
    }
}
