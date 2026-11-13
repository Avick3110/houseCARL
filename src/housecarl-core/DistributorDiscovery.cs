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
            var listed = folder.Descends ? view.EnumerateUnder(folder.Root) : view.EnumerateTopLevel(folder.Root);
            foreach (var rel in listed.Where(r => DistributorFormat.Reads(Path.GetFileName(r), folder))
                                      .OrderBy(r => r, StringComparer.Ordinal))
                files.Add(Resolve(view, rel, folder.Sorted ? null : UnsortedFolderNote));
        }
        return files;
    }

    /// <summary>One file's winner off the view: the loose copies in precedence order, and the BSA-only reason where no loose copy wins.</summary>
    static ConfigFile Resolve(AssetResolver.AssetView view, string rel, string? orderNote)
    {
        var place = view.ResolveForPlacement(rel);
        var winner = place.Sources.Count > 0 ? place.Sources[0] : null;
        var shadowed = place.Sources.Where(s => s.Kind == AssetKind.Loose).Skip(1).Select(s => s.ProviderName).ToList();
        return new ConfigFile(rel, winner?.ProviderName,
                              winner?.Kind == AssetKind.Loose ? winner.LooseFilePath : null, shadowed,
                              winner is null || winner.Kind != AssetKind.Loose
                                  ? "present only inside a BSA — the DLL reads loose INIs off the filesystem only"
                                  : null,
                              orderNote);
    }
}
