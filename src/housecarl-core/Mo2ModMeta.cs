namespace HousecarlCore;

// Read the Nexus update-cache fields MO2 writes into each mod's meta.ini; no network. The fields, MO2's own update rule and the [installedFiles] join key are in docs/architecture/mo2-instance.md.

/// <summary>The Nexus update-cache fields from one mod's meta.ini; <see cref="ModId"/> is 0 for a mod with no Nexus id, and <see cref="InstalledFileIds"/> is empty for a FOMOD or manual install.</summary>
public sealed record ModMetaIni(
    int ModId, string? Version, string? NewestVersion, string? IgnoredVersion, string? LastNexusUpdate,
    IReadOnlyList<int> InstalledFileIds);

public static class Mo2ModMeta
{
    /// <summary>Read one mod's meta.ini update-cache fields, or null when the file cannot be read; a missing or blank field reads as null and nothing throws.</summary>
    public static ModMetaIni? Read(string metaIniPath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(metaIniPath); }
        catch { return null; }

        var modidRaw = Clean(FindValue(lines, "modid"));
        int modId = int.TryParse(modidRaw, out var id) && id > 0 ? id : 0;
        return new ModMetaIni(
            modId,
            Clean(FindValue(lines, "version")),
            Clean(FindValue(lines, "newestVersion")),
            Clean(FindValue(lines, "ignoredVersion")),
            Clean(FindValue(lines, "lastNexusUpdate")),
            ReadInstalledFileIds(lines));
    }

    /// <summary>The folder MO2 would keep a loose file's meta.ini in — the file's path with its Data-relative tail removed; null when the path does not end in that tail or what is left is a bare drive.</summary>
    public static string? ModRootForLooseFile(string looseFilePath, string dataRelativePath)
    {
        var tail = dataRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = looseFilePath.Replace('/', Path.DirectorySeparatorChar);
        if (!full.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return null;
        var root = full[..(full.Length - tail.Length)].TrimEnd(Path.DirectorySeparatorChar);
        return root.Length == 0 || root.EndsWith(':') ? null : root;
    }

    /// <summary>The <c>N\fileid</c> values from the <c>[installedFiles]</c> section, in index order; scoped to the section, tolerant of key ordering, and empty rather than null.</summary>
    static IReadOnlyList<int> ReadInstalledFileIds(string[] lines)
    {
        List<(int idx, int fileId)>? found = null;
        bool inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')   // a new section header ends [installedFiles]
            {
                inSection = line.Equals("[installedFiles]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line.AsSpan(0, eq).TrimEnd();
            int bs = key.IndexOf('\\');                                   // key is "<N>\fileid"; split on the QSettings group sep
            if (bs < 0) continue;                                         // e.g. the section's own "size=" key — no '\'
            if (!key[(bs + 1)..].Trim().Equals("fileid", StringComparison.OrdinalIgnoreCase)) continue;   // skip N\modid etc.
            if (!int.TryParse(key[..bs].Trim(), out var idx)) continue;
            var val = Clean(line[(eq + 1)..]);
            if (val is not null && int.TryParse(val, out var fid) && fid > 0)
                (found ??= new()).Add((idx, fid));
        }
        if (found is null) return Array.Empty<int>();
        return found.OrderBy(t => t.idx).Select(t => t.fileId).ToList();
    }

    /// <summary>First <c>key=</c> line's raw value, the key matched case-insensitively and EXACTLY, so "modid" never matches "1\modid"; null if the key is not present.</summary>
    static string? FindValue(string[] lines, string key)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (line.AsSpan(0, eq).TrimEnd().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..];
        }
        return null;
    }

    /// <summary>Read a QSettings value through the one shared reader, so both MO2 ini readers behave the same.</summary>
    static string? Clean(string? raw) => QtIniEscapes.Clean(raw);
}
