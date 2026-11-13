namespace HousecarlCore;

/// <summary>Which files a distributor format reads out of the active order, as the DLL finds them: its row's folders,
/// each listed top level only through the asset door, the names it takes, in its own order. File names only — a line is
/// never read here; that is each format's own reader.</summary>
public static class DistributorDiscovery
{
    /// <summary>The sentence a file out of a folder the format does not sort carries, because one merged listing cannot reproduce the filesystem's own order.</summary>
    public const string UnsortedFolderNote =
        "comes from a folder the DLL appends in the filesystem's own order, which one merged listing cannot reproduce, so its place among that folder's files is not the DLL's";

    /// <summary>One config file the format would read: the Data-relative path, the mod that wins it, the winning loose
    /// file, the loose copies it shadows, why the DLL does not read it (null when it does), and what the order of this
    /// file is not (null when its folder is sorted).</summary>
    public sealed record ConfigFile(
        string RelPath,
        string? WinningProvider,
        string? LooseFilePath,
        IReadOnlyList<string> ShadowedProviders,
        string? NotApplied,
        string? OrderNote);

    /// <summary>Every file <paramref name="format"/> reads, in its own read order: each row folder in turn, its files
    /// ordinally by path. A folder the row marks unsorted is appended after the sorted ones, as the DLL appends it, and
    /// every file out of it carries <see cref="UnsortedFolderNote"/>.</summary>
    public static IReadOnlyList<ConfigFile> Scan(AssetResolver.AssetView view, DistributorFormat format)
    {
        var files = new List<ConfigFile>();
        foreach (var folder in format.Folders)
        {
            bool Reads(string rel) => folder.Reads(Path.GetFileName(rel));
            // A descending folder filters inside the walk, so a whole subtree is never materialized to keep a few names.
            var listed = folder.Descends ? view.EnumerateUnder(folder.Root, Reads, 0, out _) : view.EnumerateTopLevel(folder.Root);
            var note = folder.Sorted ? null : UnsortedFolderNote;
            foreach (var rel in listed.Where(Reads).OrderBy(r => r, StringComparer.Ordinal))
            {
                var w = LayerFileWinner.Of(view, rel, format.Name);
                files.Add(new ConfigFile(rel, w.Provider, w.LooseFilePath, w.Shadowed, w.NotApplied, note));
            }
        }
        return files;
    }
}
