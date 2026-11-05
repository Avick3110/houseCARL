using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

// ArchiveReader — one asset view's archive reads: each archive's table is read at most once for the reader's life,
// which is its view's, and nothing outlives the view. Contract in docs/architecture/assets.md, "One build per call".

/// <summary>The archive reads of ONE asset view, keyed by full path. An archive opens at its first read, its table is
/// read once and never stat'd again, and each entry's bytes are read off the file as it is at the read (Mutagen
/// reopens the file per <c>GetBytes</c>, so no handle is held). An archive that will not open fails the same way on
/// every read. Safe to share across threads.</summary>
internal sealed class ArchiveReader(Action? opened = null)
{
    readonly ConcurrentDictionary<string, Lazy<Held>> _archives = new(StringComparer.OrdinalIgnoreCase);
    int _folderListsRead;

    /// <summary>How many folder name tables this reader has walked; a test seam.</summary>
    internal int FolderListsRead => Volatile.Read(ref _folderListsRead);

    /// <summary>One opened archive: its Mutagen reader and the name tables walked so far, guarded by the instance.</summary>
    sealed class Held(IArchiveReader reader)
    {
        internal readonly IArchiveReader Reader = reader;
        internal readonly Dictionary<string, Dictionary<string, IArchiveFile>> Folders = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, IArchiveFile>? WholeArchive;
        internal bool NoFolderIndex;                             // Mutagen's folder index would not build for this archive
    }

    /// <summary>One entry's bytes; null when the archive does not hold it; THROWS when the archive cannot be read.</summary>
    internal byte[]? ReadEntry(string archivePath, string entryPath) => Find(archivePath, entryPath)?.GetBytes();

    /// <summary>Does the archive hold the entry? THROWS when the archive cannot be read.</summary>
    internal bool HasEntry(string archivePath, string entryPath) => Find(archivePath, entryPath) is not null;

    /// <summary>The entry's table record, through its folder's name table, or the whole archive's when the archive has
    /// no such folder (no folder names, a root-level entry) or no folder index; the first spelling of a name wins.</summary>
    IArchiveFile? Find(string archivePath, string entryPath)
    {
        var held = Open(archivePath);
        var want = AssetResolver.Normalize(entryPath);
        var dir = Path.GetDirectoryName(want) ?? "";
        Dictionary<string, IArchiveFile> names;
        lock (held)
        {
            if (!held.Folders.TryGetValue(dir, out names!))
            {
                names = FolderTable(held, dir) ?? (held.WholeArchive ??= NameTable(held.Reader.Files));
                held.Folders[dir] = names;
            }
        }
        return names.TryGetValue(want, out var entry) ? entry : null;
    }

    /// <summary>The folder's name table through Mutagen's folder index; null when the archive has no such folder, or
    /// on any ArgumentException inside the try, which then marks the whole archive as having no folder index: the
    /// index build throws one on two folder records equal ignoring case or slash, and this folder's file records and
    /// names throw one on a name with a directory part or a short file-record block.</summary>
    Dictionary<string, IArchiveFile>? FolderTable(Held held, string dir)
    {
        if (held.NoFolderIndex) return null;
        try { return held.Reader.TryGetFolder(dir, out var folder) ? NameTable(folder.Files) : null; }
        catch (ArgumentException) { held.NoFolderIndex = true; return null; }
    }

    /// <summary>The archive's held reader, opened and its table read on first use; a failure is kept and rethrown.</summary>
    Held Open(string archivePath)
        => _archives.GetOrAdd(Path.GetFullPath(archivePath), p => new Lazy<Held>(() =>
        {
            opened?.Invoke();
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, p);
            // Loads every folder record and the first non-empty folder's file records, with or without folder names;
            // the other folders' file records and every name load at Find.
            using (var files = reader.Files.GetEnumerator()) files.MoveNext();
            return new Held(reader);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Normalized name to entry over one walk of <paramref name="files"/>; the first spelling wins.</summary>
    Dictionary<string, IArchiveFile> NameTable(IEnumerable<IArchiveFile> files)
    {
        var names = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            names.TryAdd(AssetResolver.Normalize(file.Path), file);
        Interlocked.Increment(ref _folderListsRead);
        return names;
    }
}
