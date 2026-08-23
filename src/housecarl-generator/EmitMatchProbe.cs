using System.Text;

namespace HousecarlGenerator;

/// <summary>
/// emit-match-guard (#351) — the COMMITTED mutagen-reference shards must equal what the generator emits today.
///
/// <c>.claude/skills/mutagen-reference/references/*.jsonl</c> is a generated artifact that is also checked in:
/// it ships in the plugin and it is what a dev-mode skill — and a reviewer checking a classification claim —
/// actually reads. Every other corpus guard regenerates into a temp dir and asserts against THAT, so a change to
/// the classifier or the emitter that lands without a regeneration leaves the shipped reference stale with CI
/// fully green. That is #335's defect class (a reference that disagrees with what the code models) reached by
/// omission rather than by a bug, and nothing was checking for it.
///
/// This closes it by comparison, not by reasoning: regenerate into a temp dir, compare byte-for-byte against the
/// committed tree, and name the file — and the line — that differs. The remedy is always the same, so the
/// failure says it outright rather than leaving the reader to infer it.
///
/// WHY A BYTE COMPARE IS SOUND HERE. The emit is deterministic (the catalog is a SortedDictionary over an
/// ordinal key and fields sort ordinal by name), and the shards are pinned <c>eol=lf</c> in .gitattributes, so
/// they are LF on disk on every platform and no autocrlf pass can move them. Measured before this guard was
/// written: two consecutive fresh emits in separate processes are byte-identical, in both the refs and the
/// generated tree. If a future change ever makes the emit order-dependent, this guard flapping is the correct
/// and loud symptom of that — it is not a reason to sort the comparison into agreement.
/// </summary>
public static class EmitMatchProbe
{
    /// <summary>CWD-relative, matching every other tree-reading probe here; CI runs from the repo root.</summary>
    static readonly string CommittedRefDir = Path.Combine(".claude", "skills", "mutagen-reference", "references");

    const string Remedy =
        "regenerate and commit the result: dotnet run --project src/housecarl-generator -c Release";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("emit-match-guard — committed mutagen-reference shards vs a fresh emit (#351)");
        Console.WriteLine();

        if (!Directory.Exists(CommittedRefDir))
        {
            Console.Error.WriteLine($"  FAIL  committed reference tree not found at '{CommittedRefDir}'");
            Console.Error.WriteLine($"        -> wrong working directory (this guard runs from the repo root), or the tree is missing");
            return 1;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "housecarl-emit-match-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // GenerateAll reuses the process-memoized corpus, so inside the ci-all runner this costs an emit,
            // not a second reflection walk over the whole Mutagen type library.
            //
            // It also prints the generator's whole report — the per-type field dump — which is the single
            // noisiest thing this guard could add to a CI log, and it says nothing a reader of THIS guard needs.
            // Capture it instead of emitting it, and replay it only if the generation actually failed, where it
            // is the diagnosis rather than the noise.
            var freshRefDir = Path.Combine(tmp, "refs");
            var captured = new StringWriter();
            var realOut = Console.Out;
            int rc;
            try
            {
                Console.SetOut(captured);
                rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), freshRefDir);
            }
            finally { Console.SetOut(realOut); }

            if (rc != 0)
            {
                Console.Error.WriteLine($"  FAIL  the generator itself failed (exit {rc}) — nothing to compare against");
                Console.Error.WriteLine(captured.ToString());
                return rc;
            }
            return Compare(CommittedRefDir, freshRefDir);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    static int Compare(string committedDir, string freshDir)
    {
        var committed = Index(committedDir);
        var fresh = Index(freshDir);
        int failures = 0;

        void Fail(string label, string detail)
        {
            Console.WriteLine($"  FAIL  {label}");
            Console.WriteLine($"        -> {detail}");
            Console.WriteLine($"        -> {Remedy}");
            failures++;
        }

        // A file the emitter no longer produces is as much a staleness signal as a changed one — it is a shard
        // that would keep shipping after the code stopped being able to generate it.
        foreach (var name in committed.Keys.Where(k => !fresh.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            Fail($"{name}: committed but no longer emitted",
                 "the generator does not produce this file any more; it is a stale committed artifact");

        foreach (var name in fresh.Keys.Where(k => !committed.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            Fail($"{name}: emitted but not committed",
                 "the generator produces this file and it is missing from the committed tree");

        foreach (var name in committed.Keys.Where(fresh.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
        {
            var a = File.ReadAllBytes(committed[name]);
            var b = File.ReadAllBytes(fresh[name]);
            if (a.AsSpan().SequenceEqual(b))
            {
                Console.WriteLine($"  PASS  {name} ({a.Length:N0} bytes)");
                continue;
            }
            Fail($"{name}: committed content differs from a fresh emit", FirstDifference(committed[name], fresh[name], a, b));
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"emit-match-guard: PASS ({committed.Count} shard(s) match a fresh emit)"
            : $"emit-match-guard: FAIL ({failures} shard(s) stale)");
        return failures == 0 ? 0 : 1;
    }

    static Dictionary<string, string> Index(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => p, StringComparer.Ordinal);

    /// <summary>
    /// Name the first differing LINE, not just the file — a shard is one JSON object per line, so the line
    /// number is the entry, and "records.jsonl differs" alone leaves a reader diffing 133 records by hand.
    /// Falls back to a byte offset when the difference is not line-shaped (a truncation, or binary content).
    /// </summary>
    static string FirstDifference(string committedPath, string freshPath, byte[] a, byte[] b)
    {
        var sb = new StringBuilder();
        sb.Append($"committed {a.Length:N0} bytes, fresh {b.Length:N0} bytes; ");

        string[] left, right;
        try
        {
            left = File.ReadAllLines(committedPath);
            right = File.ReadAllLines(freshPath);
        }
        catch (Exception ex)
        {
            sb.Append($"could not read as text to locate the line ({ex.GetType().Name})");
            return sb.ToString();
        }

        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (string.Equals(left[i], right[i], StringComparison.Ordinal)) continue;
            sb.Append($"first difference at line {i + 1} of {left.Length:N0}");
            sb.Append($"\n        -> committed: {Excerpt(left[i])}");
            sb.Append($"\n        -> fresh    : {Excerpt(right[i])}");
            return sb.ToString();
        }

        // Every shared line matches, so one file is a prefix of the other.
        var longer = left.Length > right.Length ? "committed" : "fresh";
        var extra = Math.Abs(left.Length - right.Length);
        sb.Append($"lines 1-{Math.Min(left.Length, right.Length):N0} match; {longer} has {extra:N0} extra line(s), ");
        sb.Append($"first at line {Math.Min(left.Length, right.Length) + 1}: ");
        sb.Append(Excerpt((left.Length > right.Length ? left : right)[Math.Min(left.Length, right.Length)]));
        return sb.ToString();
    }

    /// <summary>A cataloged line is a whole record's schema — thousands of characters. Bound it so one stale
    /// shard cannot bury the rest of the guard's output (kickoff/boot rule: one readable line per cell).</summary>
    static string Excerpt(string line) =>
        line.Length <= 160 ? line : line[..160] + $"… (+{line.Length - 160:N0} more chars)";
}
