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
}
