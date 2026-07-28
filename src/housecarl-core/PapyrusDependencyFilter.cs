using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>What <see cref="PapyrusDependencyFilter.Relevant"/> concluded: the candidate folders this script actually
/// reaches (in the order they were offered), plus the numbers the render discloses — how many scripts were indexed,
/// how many source files were read chasing transitive references, and whether a bound cut the walk short.</summary>
public sealed record PapyrusDependencyScan(
    IReadOnlyList<string> Folders,
    int Indexed,
    int FilesRead,
    bool BudgetExhausted);

/// <summary>
/// Narrows a modlist's Papyrus source folders to the ones a specific script can actually reach (issue #200).
///
/// WHY THIS EXISTS, measured rather than assumed: on a real 3617-mod order (ARR 2.0, 2026-07-28) the modlist scan
/// finds <b>501</b> source folders — about one mod in seven — whose joined <c>-i=</c> value is <b>~40,200 characters</b>.
/// Windows caps a process command line near 32,767, so handing the compiler every folder does not merely waste effort,
/// it cannot be executed at all. And the size is the symptom, not the disease: nearly all of those folders belong to
/// quest and follower mods shipping <i>their own</i> scripts, which no other script ever references. A folder that
/// provides only names this script never mentions contributes nothing to this compile — so filtering to the reachable
/// set is the CORRECT semantics, not a size workaround that happens to fit.
///
/// The walk mirrors what the compiler itself does. The compiler resolves a referenced script by NAME against the
/// import path and takes the FIRST match, so a name is indexed to exactly one folder — the highest-precedence provider
/// — and that is the only folder that name can ever justify. From the target's own text, every identifier-shaped token
/// is looked up; each that resolves pulls in its folder AND is followed into that script's text, until nothing new
/// appears. Following matters: type-checking loads a dependency's own dependencies, so the closure is transitive, not
/// one level.
///
/// DELIBERATELY OVER-INCLUSIVE. Every <c>[A-Za-z_][A-Za-z0-9_]*</c> token is treated as a possible script name —
/// keywords, locals, comment and string text included. A token that names nothing costs one dictionary miss; the
/// failure that matters is the other direction, a folder wrongly dropped, which costs a compile that used to work.
/// The vanilla sources are deliberately NOT indexed by the caller: they are always on the import path anyway, so
/// excluding them both keeps the closure from walking the whole base game and cannot lose a folder.
/// </summary>
public static class PapyrusDependencyFilter
{
    /// <summary>Ceiling on source files READ while chasing transitive references. A bound this generous is a runaway
    /// stop, not a policy — the real closure of a framework-heavy script is orders of magnitude smaller (the whole
    /// index on the order measured above is 13,235 scripts). Hitting it is REPORTED, never absorbed (Q3).</summary>
    public const int MaxFilesRead = 5000;

    static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>The subset of <paramref name="candidateFolders"/> that <paramref name="targetScript"/> reaches.
    /// <para><paramref name="seedFolders"/> — the script's own folder and the caller's explicit import_dirs= — are
    /// INDEXED but never returned: the caller adds them unconditionally (an explicitly passed folder is not something
    /// to second-guess), and indexing them is what lets the walk hop THROUGH a local script into the framework it
    /// uses. Precedence runs seeds first, then candidates in the given order, which is the same order the compiler
    /// will search, so each name indexes to the provider that would actually win.</para>
    /// <para>Returns candidates in their GIVEN order (MO2 precedence). An unreadable script is skipped rather than
    /// thrown on — one missed hop degrades to a folder the user can pass by hand, whereas a throw would cost the
    /// compile outright.</para></summary>
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
        try { foreach (var id in Names(File.ReadAllText(targetScript))) queue.Enqueue(id); filesRead++; }
        catch { return new PapyrusDependencyScan(Array.Empty<string>(), folderOf.Count, 0, false); }

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            if (!folderOf.TryGetValue(name, out var folder)) continue;   // names nothing on the path — a free miss
            if (candidateSet.Contains(folder)) reached.Add(folder);

            // Past the budget, keep RESOLVING (a dictionary hit still earns its folder) but stop READING, so a runaway
            // closure degrades to a shorter path that is reported rather than to a hung compile.
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

    /// <summary>Every identifier-shaped token in a Papyrus source. No attempt is made to tell a type reference from a
    /// local, a keyword, or a word inside a comment — see the class summary: an extra token is a dictionary miss, a
    /// missing one is a failed compile.</summary>
    static IEnumerable<string> Names(string text)
    {
        foreach (Match m in Identifier.Matches(text)) yield return m.Value;
    }
}
