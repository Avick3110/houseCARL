namespace HousecarlCore;

// ======================================================================
//  Mo2ModMeta — read the Nexus update-cache fields MO2 writes into each
//  mod's meta.ini (mods\<Folder>\meta.ini). MO2 has ALREADY paid the Nexus
//  API cost to learn these; houseCARL just reads its cache — no network,
//  squarely in the MO2-static-read lane (like Mo2Instance / Mo2LoadOrder).
//
//  The [General] fields that drive "is there an update":
//      modid=12604               (the Nexus mod id; 0/absent ⇒ not a Nexus mod)
//      version=5.2SE             (the INSTALLED version)
//      newestVersion=6.11        (what MO2 last learned is newest; empty ⇒ never checked)
//      ignoredVersion=6.10       (a version the user told MO2 to stop nagging about)
//      lastNexusUpdate=1778020881(unix seconds of MO2's last update check)
//
//  MO2's own rule (what its update flag shows): an update is available when
//  newestVersion is set, non-empty, and != version (and != ignoredVersion).
//  We report the RAW fields and let the caller apply that rule, so the
//  reasoning is visible, never a hidden boolean (Q3).
//
//  Format is QSettings-ini (same quirks Mo2Instance documents): a value may
//  be wrapped @ByteArray(...), @Invalid() means unset, backslashes are
//  doubled, and string values can be surrounded by double quotes. The
//  [installedFiles] section uses prefixed keys (1\modid=...) so a plain
//  first-match on "modid" reads the [General] one, which is written first.
//
//  That same [installedFiles] section ALSO records the exact Nexus FILE id(s)
//  MO2 installed (1\fileid=..., 2\fileid=..., a size= count key in any order),
//  which is the join key for a FILE-level currency check — "is the file I have
//  still a current file, or did Nexus retire it to OLD_VERSION/ARCHIVED?" —
//  immune to the multi-file-page confusion a mod-level version compare falls
//  into (a Nexus page hosts many independently-versioned files). A FOMOD /
//  merged / hand-installed mod has size=0 with no fileid, so the check must
//  degrade LOUDLY there rather than guess (Q3).
// ======================================================================

/// <summary>The Nexus update-cache fields from one mod's meta.ini. <see cref="ModId"/> is 0 when the mod has no Nexus
/// id (a hand-installed mod / separator). <see cref="NewestVersion"/> empty ⇒ MO2 never learned a newer version.
/// <see cref="InstalledFileIds"/> are the <c>[installedFiles] N\fileid</c> values — the exact Nexus file(s) installed,
/// the FILE-level currency join key; empty for a FOMOD/manual install (<c>size=0</c>, no fileid).</summary>
public sealed record ModMetaIni(
    int ModId, string? Version, string? NewestVersion, string? IgnoredVersion, string? LastNexusUpdate,
    IReadOnlyList<int> InstalledFileIds);

public static class Mo2ModMeta
{
    /// <summary>Read one mod's meta.ini update-cache fields, or null if the file can't be read. A missing/blank field
    /// reads as null (never throws — the caller keeps going over the rest of the mods folder).</summary>
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

    /// <summary>The <c>N\fileid</c> values from the <c>[installedFiles]</c> section, in index (N) order — the exact Nexus
    /// file(s) MO2 recorded as installed for this mod. A mod can have several (<c>1\fileid</c>, <c>2\fileid</c>, …); a
    /// FOMOD/manual install has <c>size=0</c> and none (⇒ empty list). Scoped to the section (a stray <c>fileid=</c>
    /// elsewhere is ignored) and tolerant of the <c>size=</c> / <c>N\modid</c> siblings and any key ordering. Empty,
    /// never null, so the caller can treat "no fileids" as the loud no-fileid-fallback case, not a crash.</summary>
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

    /// <summary>First <c>key=</c> line's raw value, key matched case-insensitively and EXACTLY (so "modid" never matches
    /// the [installedFiles] "1\modid"). Tolerates whitespace around '='. Null if the key isn't present.</summary>
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

    /// <summary>Clean a QSettings value: strip an <c>@ByteArray(...)</c> wrapper, treat <c>@Invalid()</c> as unset,
    /// unescape doubled backslashes, and strip a surrounding pair of double quotes. Empty ⇒ null.</summary>
    static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("@Invalid()", StringComparison.OrdinalIgnoreCase)) return null;
        const string wrap = "@ByteArray(";
        if (v.StartsWith(wrap, StringComparison.Ordinal) && v.EndsWith(")", StringComparison.Ordinal))
            v = v[wrap.Length..^1];
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        v = v.Replace(@"\\", @"\").Trim();
        return v.Length == 0 ? null : v;
    }
}
