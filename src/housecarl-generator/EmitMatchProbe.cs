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

    [CiProbe("emit-match-guard")]
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
            // Capture it instead of emitting it, and replay it only when the generation actually failed, where
            // it is the diagnosis rather than the noise.
            //
            // The failure that actually occurs is a THROW, not a non-zero return: EmitCorpus has exactly one
            // `return 0;` and no error path, so an `if (rc != 0)` replay is unreachable code promising a
            // diagnostic that can never print. Catch the throw, replay the capture there, and rethrow so the
            // guard still fails loudly through CiAll.
            var freshRefDir = Path.Combine(tmp, "refs");
            var captured = new StringWriter();
            var realOut = Console.Out;
            int rc;
            try
            {
                Console.SetOut(captured);
                rc = CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), freshRefDir);
            }
            catch
            {
                Console.SetOut(realOut);
                Console.Error.WriteLine("  FAIL  the generator threw while regenerating — nothing to compare against.");
                Console.Error.WriteLine("        Its output up to the failure follows, because it is the diagnosis:");
                Console.Error.WriteLine(captured.ToString());
                throw;
            }
            finally { Console.SetOut(realOut); }

            if (rc != 0)
            {
                // Defensive: no current path returns non-zero, but a future one should not lose the capture.
                Console.Error.WriteLine($"  FAIL  the generator reported exit {rc} — nothing to compare against");
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
        // that would keep shipping after the code stopped being able to generate it. This globs the WORKING TREE,
        // not the git index, and deliberately does not filter by extension: build-plugin.ps1 copies this whole
        // directory into the plugin, so anything sitting here ships. That means a local scratch file (a .bak
        // beside the shards) also trips it — correct as a shipping check, but the remedy line below is written
        // for the stale-shard case, so the message names both readings rather than assuming the wrong one.
        foreach (var name in committed.Keys.Where(k => !fresh.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            Fail($"{name}: present in the reference tree but not emitted",
                 "the generator does not produce this file. Either it is a stale committed artifact, or it is " +
                 "an untracked local file sitting in a directory that ships wholesale — check which, then " +
                 "delete it or regenerate");

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
            // A cataloged line is one whole record's schema — often >1,000 characters — and the difference is
            // usually nowhere near the start. Excerpting from character 0 would print two windows that look
            // IDENTICAL and name nothing, which is how a "helpful" diagnostic becomes a dead end. Window on the
            // first differing column instead.
            int col = FirstDifferingColumn(left[i], right[i]);
            sb.Append($"first difference at line {i + 1} of {left.Length:N0}, column {col + 1}");
            sb.Append($"\n        -> committed: {Excerpt(left[i], col)}");
            sb.Append($"\n        -> fresh    : {Excerpt(right[i], col)}");
            return sb.ToString();
        }

        // Every shared line matches. Either one file is a prefix of the other, or the two differ ONLY in bytes
        // that ReadAllLines does not surface — a BOM, the line terminators themselves, or a trailing newline.
        // That second case is reachable in ordinary use (PowerShell's Set-Content / Out-File write a BOM by
        // default) and must not be treated as the prefix case: with equal line counts there is no "extra" line
        // to name, and indexing one would read off the end of the array.
        int shared = Math.Min(left.Length, right.Length);
        if (left.Length == right.Length)
        {
            sb.Append($"all {shared:N0} lines match as text, so the difference is in bytes that do not survive ");
            sb.Append("line splitting — a byte-order mark, the line terminators, or a trailing newline. ");
            sb.Append($"First differing byte at offset {FirstDifferingByte(a, b):N0}. ");
            sb.Append("These shards are pinned `eol=lf` in .gitattributes and the generator writes '\\n' directly, ");
            sb.Append("so a committed shard should carry no BOM and no CRLF");
            return sb.ToString();
        }

        var longer = left.Length > right.Length ? "committed" : "fresh";
        // Lengths differ here — the equal-length case returned above — so this is the genuine prefix case.
        sb.Append($"lines 1-{shared:N0} match; {longer} has {Math.Abs(left.Length - right.Length):N0} extra line(s), ");
        sb.Append($"first at line {shared + 1}: ");
        sb.Append(Excerpt((left.Length > right.Length ? left : right)[shared], 0));
        return sb.ToString();
    }

    /// <summary>Offset of the first differing byte — the only locator available when the two files agree on
    /// every line as text and differ only in bytes line splitting discards.</summary>
    static int FirstDifferingByte(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }

    static int FirstDifferingColumn(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n; // one is a prefix of the other; the difference starts where the shorter ends
    }

    const int Window = 140;

    /// <summary>A cataloged line is a whole record's schema — thousands of characters. Show a bounded window
    /// AROUND the interesting column so one stale shard names what changed without burying the rest of the
    /// guard's output, and mark each side that was clipped so a truncated window is never read as the whole line.
    /// </summary>
    static string Excerpt(string line, int around)
    {
        if (line.Length <= Window) return line;
        int start = Math.Max(0, Math.Min(around - Window / 2, line.Length - Window));
        int end = Math.Min(line.Length, start + Window);
        var head = start > 0 ? $"…(+{start:N0} chars)" : "";
        var tail = end < line.Length ? $"…(+{line.Length - end:N0} chars)" : "";
        return head + line[start..end] + tail;
    }
}
