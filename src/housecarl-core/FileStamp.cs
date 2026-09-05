namespace HousecarlCore;

/// <summary>One filesystem entry's freshness key: its last-write time AND its length. THE freshness key houseCARL
/// caches against — the plugin read cache, the MO2 profile gate, the BSA and loose-subtree tables, and the SkyPatcher
/// INI parse cache all stamp with this one type, so there is one answer to "has this changed" rather than a different
/// one per cache.
///
/// <para>Length is in the key because last-write alone is coarse: an edit landing inside the filesystem's timestamp
/// granularity, or one whose tool restores the timestamp it found, leaves the mtime where it was and serves stale
/// state with nothing saying so (#406). Two terms do not make the key exact — an edit that changes neither is still
/// invisible — they make the common same-mtime edit visible. Both terms come from ONE stat: on Windows
/// <see cref="FileSystemInfo.Exists"/> refreshes through GetFileAttributesEx and both properties read that cached
/// result, so the no-change path costs what the mtime-only path cost.</para></summary>
public readonly record struct FileStamp(DateTime Mtime, long Size)
{
    /// <summary>The one sentinel for missing, locked and unreadable — so a path that comes back is a change and one
    /// that stays gone is not. Distinct from every real stamp: a real length is never negative.</summary>
    public static readonly FileStamp Absent = new(DateTime.MinValue, -1);

    /// <summary>Stat one FILE, or <see cref="Absent"/> when it cannot be statted.</summary>
    public static FileStamp Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);                                   // ONE stat serves both terms
            return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc, fi.Length) : Absent;
        }
        catch { return Absent; }
    }

    /// <summary>Stat one DIRECTORY, or <see cref="Absent"/> when it cannot be statted. A directory has no length, so
    /// the size term is pinned at 0 and the last-write carries the whole signal — which it does for a directory:
    /// adding or removing an entry moves it. Separate from <see cref="Of"/> so a directory costs one stat too.</summary>
    public static FileStamp OfDirectory(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Exists ? new FileStamp(di.LastWriteTimeUtc, 0) : Absent;
        }
        catch { return Absent; }
    }
}
