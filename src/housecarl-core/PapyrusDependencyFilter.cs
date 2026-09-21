using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>What <see cref="PapyrusDependencyFilter.Relevant"/> concluded: the folders reached, the numbers the render discloses, and the two degraded outcomes it flags; contracts in docs/architecture/papyrus.md.</summary>
public sealed record PapyrusDependencyScan(
    IReadOnlyList<string> Folders,
    int Indexed,
    int FilesRead,
    bool BudgetExhausted,
    bool TargetUnreadable = false);

/// <summary>Narrows a modlist's Papyrus source folders to the ones a script reaches; docs/architecture/papyrus.md.</summary>
public static class PapyrusDependencyFilter
{
    /// <summary>Ceiling on source files READ while chasing transitive references; hitting it is reported.</summary>
    public const int MaxFilesRead = 5000;

    static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>The subset of <paramref name="candidateFolders"/> that <paramref name="targetScript"/> reaches, in the
    /// given order; <paramref name="seedFolders"/> is indexed but never returned.</summary>
    public static PapyrusDependencyScan Relevant(
        string targetScript, IReadOnlyList<string> seedFolders, IReadOnlyList<string> candidateFolders)
    {
        // name -> the folder + file of its FIRST (highest-precedence) provider, exactly as the compiler would resolve it.
        var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in seedFolders.Concat(candidateFolders))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.psc"); }
            catch { continue; }
            try
            {
                foreach (var f in files)
                {
                    if (!f.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (folderOf.ContainsKey(name)) continue;      // first provider wins — the one the compiler would take
                    folderOf[name] = folder;
                    fileOf[name] = f;
                }
            }
            catch { /* the folder vanished mid-walk — it just contributes nothing */ }
        }

        var candidateSet = new HashSet<string>(candidateFolders, StringComparer.OrdinalIgnoreCase);
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        int filesRead = 0;
        bool exhausted = false;
        // A target that cannot be read returns empty and FLAGGED, never as a walk that ran and reached nothing.
        try { foreach (var id in Names(File.ReadAllText(targetScript))) queue.Enqueue(id); filesRead++; }
        catch { return new PapyrusDependencyScan(Array.Empty<string>(), folderOf.Count, 0, false, TargetUnreadable: true); }

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            if (!folderOf.TryGetValue(name, out var folder)) continue;   // names nothing on the path — a free miss
            if (candidateSet.Contains(folder)) reached.Add(folder);

            // Past the budget, keep RESOLVING but stop READING, and report it.
            if (filesRead >= MaxFilesRead) { exhausted = true; continue; }
            string text;
            try { text = File.ReadAllText(fileOf[name]); filesRead++; }
            catch { continue; }
            foreach (var id in Names(text))
                if (!seen.Contains(id)) queue.Enqueue(id);
        }

        var kept = candidateFolders.Where(reached.Contains).ToList();
        return new PapyrusDependencyScan(kept, folderOf.Count, filesRead, exhausted);
    }

    /// <summary>Every identifier-shaped token in a Papyrus source — a local, a keyword and a word in a comment too.</summary>
    static IEnumerable<string> Names(string text)
    {
        foreach (Match m in Identifier.Matches(text)) yield return m.Value;
    }
}
