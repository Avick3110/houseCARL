namespace HousecarlCore;

/// <summary>One filesystem entry's freshness key: last-write time AND length, the one key every houseCARL cache stamps against; contract in docs/architecture/output-and-artifacts.md.</summary>
public readonly record struct FileStamp(DateTime Mtime, long Size)
{
    /// <summary>The one sentinel for missing, locked and unreadable; distinct from every real stamp, whose length is never negative.</summary>
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

    /// <summary>Stat one DIRECTORY, or <see cref="Absent"/> when it cannot be statted; the size term is pinned at 0, since a directory's last-write carries the whole signal.</summary>
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
