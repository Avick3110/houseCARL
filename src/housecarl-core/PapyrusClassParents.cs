using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

/// <summary>Builds the decompiler's child→parent class map from three layered sources, as a SOFT dependency;
/// contracts in docs/architecture/papyrus.md.</summary>
public static class PapyrusClassParents
{
    static readonly Regex ExtendsRx = new(@"^\s*ScriptName\s+(\S+)\s+extends\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Load the committed baseline; a missing or corrupt file gives an empty map plus the reason the caller
    /// surfaces, which says what happened and not what it costs.</summary>
    public static (Dictionary<string, string> Edges, string? Note) LoadBaseline(string jsonPath)
    {
        var edges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(jsonPath))
            return (edges, $"the baseline class hierarchy is not beside the server ({Path.GetFileName(jsonPath)})");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            foreach (var p in doc.RootElement.GetProperty("edges").EnumerateObject())
                if (p.Value.GetString() is { Length: > 0 } parent) edges.TryAdd(p.Name, parent);
            return (edges, null);
        }
        catch (Exception ex)
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    $"the baseline class hierarchy is unreadable ({ex.GetType().Name})");
        }
    }

    /// <summary>What one mods-tree walk managed to read, its own holes included: edges added, .psc files seen, files
    /// that could not be read, roots whose listing failed.</summary>
    public readonly record struct PscHeaderScan(int Added, int FilesSeen, int FilesFailed, int RootsUnreadable);

    /// <summary>Top up from `ScriptName X extends Y` headers of loose .psc files under <paramref name="roots"/>; first
    /// edge per child wins, and an unreadable root or file is counted rather than thrown on.</summary>
    public static PscHeaderScan AddFromPscHeaders(Dictionary<string, string> edges, IEnumerable<string> roots)
    {
        int added = 0, seen = 0, failed = 0, rootsUnreadable = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { rootsUnreadable++; continue; }
            try
            {
                // The enumeration throws lazily, so the listing's own failure is caught here with the reads.
                foreach (var f in Directory.EnumerateFiles(root, "*.psc",
                             new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    seen++;
                    try
                    {
                        foreach (var line in File.ReadLines(f).Take(20))
                        {
                            var m = ExtendsRx.Match(line);
                            if (!m.Success) continue;
                            if (edges.TryAdd(m.Groups[1].Value, m.Groups[2].Value)) added++;
                            break;
                        }
                    }
                    catch { failed++; }   // unreadable psc — fewer edges, never fatal, counted
                }
            }
            catch { rootsUnreadable++; }  // the tree could not be listed — nothing was read from it
        }
        return new PscHeaderScan(added, seen, failed, rootsUnreadable);
    }

    /// <summary>Top up from a parsed pex's own object→parent declarations.</summary>
    public static int AddFromPex(Dictionary<string, string> edges, PexFile pex)
    {
        int added = 0;
        foreach (var obj in pex.Objects)
            if (obj.Name is { Length: > 0 } name && obj.ParentClassName is { Length: > 0 } parent
                && edges.TryAdd(name, parent)) added++;
        return added;
    }

    /// <summary>What one sibling-.pex walk managed to read, its own holes included: edges added, .pex files seen,
    /// files that could not be read, and whether the folder itself could not be listed.</summary>
    public readonly record struct PexFolderScan(int Added, int FilesSeen, int FilesFailed, bool FolderUnreadable);

    /// <summary>Top up from every readable .pex in <paramref name="folder"/>, non-recursively; an unreadable file or
    /// folder is counted rather than thrown on, so the caller can name it.</summary>
    public static PexFolderScan AddFromPexFolder(Dictionary<string, string> edges, string folder)
    {
        int added = 0, seen = 0, failed = 0;
        if (!Directory.Exists(folder)) return new PexFolderScan(0, 0, 0, true);
        try
        {
            // The enumeration throws lazily, so the listing's own failure is caught here with the reads.
            foreach (var f in Directory.EnumerateFiles(folder, "*.pex"))
            {
                seen++;
                try { added += AddFromPex(edges, PexFile.CreateFromFile(f, GameCategory.Skyrim)); }
                catch { failed++; }   // unreadable pex — fewer edges, never fatal, counted
            }
        }
        catch { return new PexFolderScan(added, seen, failed, true); }
        return new PexFolderScan(added, seen, failed, false);
    }
}
