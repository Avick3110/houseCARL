using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The papyrus-reference corpus's upstream doc errata (corpus-notes.md "Regenerating", pass 5): each listed
/// correction replaces its upstream sentence in exactly its entry's block, the StorageUtil.IntListSet block states the
/// value written (#1408), and every index row still lands on its own heading.</summary>
[Trait("tier", "unit")]
public sealed class PapyrusCorpusErrataTests
{
    static readonly string Skill = Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills", "papyrus-reference");
    static readonly string Refs = Path.Combine(Skill, "references");

    sealed record Row(string Name, string Qualified, string File, int LineStart, int LineEnd);

    static readonly List<Row> Index = File.ReadLines(Path.Combine(Refs, "index.jsonl"))
        .Where(l => l.Length > 0)
        .Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            var e = doc.RootElement;
            return new Row(e.GetProperty("name").GetString()!, e.GetProperty("qualified").GetString()!,
                e.GetProperty("file").GetString()!, e.GetProperty("line_start").GetInt32(), e.GetProperty("line_end").GetInt32());
        })
        .ToList();

    static readonly Dictionary<string, string[]> Files = new();

    static string[] Lines(string file)
    {
        if (!Files.TryGetValue(file, out var lines))
            Files[file] = lines = File.ReadAllLines(Path.Combine(Skill, file));
        return lines;
    }

    static string Block(string qualified)
    {
        var row = Assert.Single(Index, r => r.Qualified == qualified);
        var lines = Lines(row.File);
        // a file's last row ends one past its last line (the trailing newline)
        return string.Join("\n", lines[(row.LineStart - 1)..Math.Min(row.LineEnd, lines.Length)]);
    }

    /// <summary>The rows of pass 5's table: entry, upstream sentence, correction.</summary>
    static List<(string Entry, string Upstream, string Correction)> Errata()
    {
        var notes = File.ReadAllLines(Path.Combine(Refs, "corpus-notes.md"));
        var start = Array.FindIndex(notes, l => l.StartsWith("5. **Apply the upstream doc errata.**"));
        Assert.True(start >= 0, "corpus-notes.md 'Regenerating' has no pass 5 (upstream doc errata)");
        Assert.Contains(notes[start..], l => l.Contains("**stop and say so**"));
        return notes[start..]
            .SkipWhile(l => !l.TrimStart().StartsWith("|---"))
            .Skip(1)
            .TakeWhile(l => l.TrimStart().StartsWith('|'))
            .Select(l => l.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray())
            .Select(c => (c[0].Trim('`'), c[1], c[2]))
            .ToList();
    }

    [Fact]
    public void IntListSetStatesTheValueWrittenNotThePreviousOne()
    {
        var block = Block("StorageUtil.IntListSet");
        Assert.DoesNotContain("previous value", block);
        Assert.Contains("houseCARL correction", block);
        Assert.Contains("returns the value written", block);
        Assert.Contains("at an index past the end, or with an empty KeyName, nothing is written and it returns 0 (0.0, \"\", None)", block);
        Assert.Contains("index 0 on a missing or empty list appends the value and returns it", block);
        Assert.Contains("FloatListSet, StringListSet and FormListSet behave the same, on a form or globally", block);
    }

    [Fact]
    public void EveryListedErratumReplacesItsSentenceInItsBlockOnly()
    {
        var errata = Errata();
        Assert.Contains(errata, e => e.Entry == "StorageUtil.IntListSet");
        foreach (var (entry, upstream, correction) in errata)
        {
            var block = Block(entry);
            Assert.DoesNotContain(upstream, block);
            Assert.Contains("  " + correction, block.Split('\n'));
        }
        var corrected = Directory.EnumerateFiles(Refs, "*.md", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "corpus-notes.md")
            .SelectMany(f => Lines(Path.GetRelativePath(Skill, f).Replace('\\', '/')))
            .Count(l => l.Contains("houseCARL correction"));
        Assert.Equal(errata.Count, corrected);
    }

    // pins unchanged behaviour: a one-line-for-one-line erratum leaves every range resolving
    [Fact]
    public void EveryIndexRowLandsOnAHeadingNamingIt()
    {
        var bad = Index.Where(r =>
        {
            var lines = Lines(r.File);
            if (r.LineStart < 1 || r.LineStart > lines.Length) return true;
            var head = lines[r.LineStart - 1];
            return !head.StartsWith("### ") || !head.Contains(r.Name);
        }).Select(r => $"{r.Qualified} @ {r.File}:{r.LineStart}").ToList();
        Assert.True(bad.Count == 0, $"{bad.Count} of {Index.Count} rows do not resolve: {string.Join(", ", bad.Take(10))}");
    }
}
