using System.Text;

namespace HousecarlCore;

/// <summary>The winner-relative CONTENT comparison behind the conflict-tree diff, over DEEP reads rather than depth-1 rendered lines; contracts in docs/architecture/read-engine.md.</summary>
public static class FieldsDiff
{
    /// <summary>Field-level deltas, preformatted for the conflict-tree render; <see cref="Complete"/> false means an empty <see cref="Deltas"/> must NOT be rendered as "identical to winner".</summary>
    /// <param name="NoVerdictCount">How many of <paramref name="Deltas"/> are UNREADABLE no-verdict lines rather than value differences.</param>
    public sealed record Result(IReadOnlyList<string> Deltas, bool Complete,
        int AgreedCount, IReadOnlyList<string> AgreedSample, int NoVerdictCount);

    /// <summary>True when a value is a read-engine "no value here" sentinel; <see cref="ReadEngine.PresentNullLinkNote"/> is deliberately not one.</summary>
    static bool IsAbsentSentinel(string val) =>
        val == ReadEngine.AbsentNote || val == ReadEngine.NullLinkNote || val == ReadEngine.UnresolvedStringNote;

    /// <summary>Compare one plugin's deep-read fields against the winner's; both sides must be read by the same <see cref="ReadEngine.ReadFields"/> call shape.</summary>
    public static Result Compare(RecordFields theirs, RecordFields winner, string referenceLabel = "winner")
    {
        var tValueLeaves = new HashSet<string>(StringComparer.Ordinal);
        var wValueLeaves = new HashSet<string>(StringComparer.Ordinal);
        var tUnreadable = new Dictionary<string, string>(StringComparer.Ordinal);
        var wUnreadable = new Dictionary<string, string>(StringComparer.Ordinal);
        var (tLines, tCapped) = CleanLines(theirs, tValueLeaves, tUnreadable);
        var (wLines, wCapped) = CleanLines(winner, wValueLeaves, wUnreadable);
        bool capped = tCapped || wCapped;
        var noVerdict = tUnreadable.Keys.Union(wUnreadable.Keys).OrderBy(p => p, StringComparer.Ordinal).ToList();
        bool complete = !capped && noVerdict.Count == 0;

        // A path is OUT of the comparison when it, or a path it hangs under, could not be read on either side.
        bool Suppressed(string p) => noVerdict.Any(s => p.StartsWith(s, StringComparison.Ordinal)
                                                     && (p.Length == s.Length || p[s.Length] is '.' or '['));

        // Numeric-bracket roots seen on either side, split dict-vs-list by the read engine's in-band container marker.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, _) in tLines) if (ListRoot(p) is { } r) candidates.Add(r);
        foreach (var (p, _) in wLines) if (ListRoot(p) is { } r) candidates.Add(r);
        var listRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in candidates)
        {
            var tSum = RootSummary(tLines, root);
            var wSum = RootSummary(wLines, root);
            // No root summary on either side means a bracketed fields= read, where exact-path is the natural semantics.
            bool seen = tSum is not null || wSum is not null;
            bool dict = (tSum?.Contains(" pair(s)]", StringComparison.Ordinal) ?? false)
                     || (wSum?.Contains(" pair(s)]", StringComparison.Ordinal) ?? false);
            if (seen && !dict) listRoots.Add(root);
        }

        // The roots whose ELEMENTS are still compared: none on a cap, and not one holding an unreadable leaf.
        var comparedRoots = new HashSet<string>(StringComparer.Ordinal);
        if (!capped)
            foreach (var root in listRoots)
                if (!Suppressed(root) && !noVerdict.Any(s => ListRoot(s) == root)) comparedRoots.Add(root);

        var deltas = new List<string>();

        // Unreadable leaves: a no-verdict, named, saying which field and on which side.
        foreach (var path in noVerdict)
        {
            bool t = tUnreadable.TryGetValue(path, out var tn), w = wUnreadable.TryGetValue(path, out var wn);
            var side = t && w ? "on both sides" : t ? "here" : $"in {referenceLabel}";
            deltas.Add($"{path}: UNREADABLE {side} — not compared{Why(t, tn, w, wn)}");
        }

        // The read's own reason travels with the line, because one of those reasons is itself the remedy.
        string Why(bool t, string? tn, bool w, string? wn)
        {
            if (t && w && !string.Equals(tn, wn, StringComparison.Ordinal)) return $" (here {tn}, {referenceLabel} {wn})";
            var note = t ? tn : wn;
            return string.IsNullOrEmpty(note) ? "" : " " + note;
        }

        // Exact-path comparison: scalars, substructs, dict children, and the summary line of a root whose elements are not compared.
        var tScalar = ExactPathLines(tLines, listRoots, comparedRoots, Suppressed);
        var wScalar = ExactPathLines(wLines, listRoots, comparedRoots, Suppressed);
        int agreedCount = 0;
        var agreedSample = new List<string>();
        foreach (var (path, val) in tScalar)
        {
            if (wScalar.TryGetValue(path, out var wv))
            {
                bool tAbsent = IsAbsentSentinel(val), wAbsent = IsAbsentSentinel(wv);
                if (tAbsent && wAbsent)
                {
                    // Both carry nothing here — same state, no delta and not an agreement.
                }
                else if (tAbsent)
                {
                    // First-class ABSENT state, never a "=(absent)" phantom value delta; only nullable fields reach here.
                    deltas.Add($"{path}: ABSENT here ({referenceLabel} has {wv})");
                }
                else if (wAbsent)
                {
                    // The contributor carries a value the WINNER doesn't — the field is absent on the winner.
                    deltas.Add($"{path}={val} ({referenceLabel} has {path} ABSENT)");
                }
                else if (!string.Equals(NormalizeForCompare(val), NormalizeForCompare(wv), StringComparison.Ordinal))
                {
                    deltas.Add($"{path}={val} ({referenceLabel} {wv})");
                }
                else if (tValueLeaves.Contains(path) && wValueLeaves.Contains(path))
                {
                    // present-==-winner: a VALUE leaf the contributor restates identically; counted, not a delta.
                    agreedCount++;
                    if (agreedSample.Count < AgreedSampleCap) agreedSample.Add(path);
                }
            }
            // One-sided presence is a delta only when neither side was CAPPED; a capped side's missing line is an artifact.
            else if (!capped) deltas.Add($"{path}={val} ({referenceLabel} has no {path})");   // shape difference (e.g. another ConditionData arm)
        }
        if (!capped)
            foreach (var (path, wv) in wScalar)
                if (!tScalar.ContainsKey(path)) deltas.Add($"{path} only in {referenceLabel}: {wv}");

        // Positional lists: order-insensitive whole-element multiset comparison, over the roots still being compared.
        foreach (var root in comparedRoots.OrderBy(r => r, StringComparer.Ordinal))
        {
            var tElems = ElementsOf(tLines, root);
            var wElems = ElementsOf(wLines, root);
            var (onlyT, onlyW) = MultisetDiff(tElems, wElems);
            if (onlyT.Count == 0 && onlyW.Count == 0)
            {
                // Equal multisets mean the same contents; an ordered fingerprint mismatch is a pure reorder, which for some types IS the semantics.
                if (!tElems.Select(e => e.Fingerprint).SequenceEqual(wElems.Select(e => e.Fingerprint)))
                    deltas.Add($"{root}: same {tElems.Count} item(s), ORDER DIFFERS from {referenceLabel}");
                continue;
            }
            deltas.Add(DescribeListDelta(root, tElems.Count, wElems.Count, onlyT, onlyW, referenceLabel));
        }

        // On a CAPPED comparison the agreed set would be a where-the-cap-fell artifact; an unreadable leaf does not touch it.
        if (capped) { agreedCount = 0; agreedSample.Clear(); }
        return new Result(deltas, complete, agreedCount, agreedSample, noVerdict.Count);
    }

    /// <summary>How many agreed-field paths to keep for the render — a small sample, not the full set.</summary>
    const int AgreedSampleCap = 3;

    /// <summary>The root's own container-summary line, or null when the read never emitted one.</summary>
    static string? RootSummary(List<(string path, string val)> lines, string root)
    {
        foreach (var (path, val) in lines)
            if (path == root) return val;
        return null;
    }

    /// <summary>The read's lines minus the expansion-cap sentinel and the UNREADABLE ones; keyed on <c>Readable</c>, with a no-such-field note carved out as a comparable shape difference.</summary>
    static (List<(string path, string val)> lines, bool capped) CleanLines(RecordFields rf,
        HashSet<string> valueLeaves, Dictionary<string, string> unreadable)
    {
        var lines = new List<(string, string)>(rf.Fields.Count);
        bool capped = false;
        foreach (var f in rf.Fields)
        {
            if (f.Path == "…") { capped = true; continue; }            // ReadEngine's expansion-cap sentinel
            if (!f.Readable && !ReadEngine.IsNoSuchFieldNote(f.Note)) { unreadable[f.Path] = f.Note ?? ""; continue; }
            if (f.HasValue) valueLeaves.Add(f.Path);
            lines.Add((f.Path, f.HasValue ? f.Token ?? "" : f.Note ?? ""));
        }
        return (lines, capped);
    }

    /// <summary>The path's OUTERMOST positional-list root — the prefix before its first NUMERIC bracket — or null when it has none.</summary>
    internal static string? ListRoot(string path)
    {
        int from = 0;
        while (true)
        {
            int lb = path.IndexOf('[', from);
            if (lb < 0) return null;
            int rb = path.IndexOf(']', lb + 1);
            if (rb < 0) return null;
            bool numeric = rb > lb + 1;
            for (int i = lb + 1; i < rb && numeric; i++) numeric = char.IsAsciiDigit(path[i]);
            if (numeric) return path[..lb];
            from = rb + 1;                                              // a dict key — keep scanning for a later list
        }
    }

    /// <summary>Exact-path map of every line outside positional-list content, minus the summary lines of the roots whose elements ARE compared and any suppressed path.</summary>
    static Dictionary<string, string> ExactPathLines(List<(string path, string val)> lines,
        HashSet<string> listRoots, HashSet<string> comparedRoots, Func<string, bool> suppressed)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, val) in lines)
        {
            var root = ListRoot(path);
            bool positionalContent = root is not null && listRoots.Contains(root);
            if (!positionalContent && !comparedRoots.Contains(path) && !suppressed(path))
                map[path] = val;
        }
        return map;
    }

    sealed record Element(int Index, List<(string rel, string val)> Content, string Fingerprint);

    /// <summary>Group a root's bracketed lines into whole elements: positional index, relative content, and an order-insensitive content fingerprint.</summary>
    static List<Element> ElementsOf(List<(string path, string val)> lines, string root)
    {
        var byIndex = new SortedDictionary<int, List<(string rel, string val)>>();
        var prefix = root + "[";
        foreach (var (path, val) in lines)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int rb = path.IndexOf(']', prefix.Length);
            if (rb < 0 || !int.TryParse(path.AsSpan(prefix.Length, rb - prefix.Length), out var idx)) continue;
            var rel = rb + 1 < path.Length && path[rb + 1] == '.' ? path[(rb + 2)..] : path[(rb + 1)..];
            if (!byIndex.TryGetValue(idx, out var content)) byIndex[idx] = content = new();
            content.Add((rel, val));
        }
        return byIndex.Select(kv => new Element(kv.Key, kv.Value,
            string.Join("\u0001", kv.Value.Select(c => c.rel + "=" + NormalizeForCompare(c.val)).OrderBy(s => s, StringComparer.Ordinal)))).ToList();
    }

    /// <summary>Case-normalise a FormKey token for COMPARISON only; display keeps each side's original token, and non-FormKey values pass through.</summary>
    static string NormalizeForCompare(string val)
    {
        if (val.Length < 8 || val[6] != ':') return val;
        for (int i = 0; i < 6; i++) if (!Uri.IsHexDigit(val[i])) return val;
        return string.Concat(val[..6].ToUpperInvariant(), ":", val[7..].ToLowerInvariant());
    }

    /// <summary>Content-keyed multiset difference: elements (with multiplicity) present on one side only.</summary>
    static (List<Element> onlyT, List<Element> onlyW) MultisetDiff(List<Element> t, List<Element> w)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in w) counts[e.Fingerprint] = counts.GetValueOrDefault(e.Fingerprint) + 1;
        var onlyT = new List<Element>();
        foreach (var e in t)
        {
            if (counts.TryGetValue(e.Fingerprint, out var n) && n > 0) counts[e.Fingerprint] = n - 1;
            else onlyT.Add(e);
        }
        var onlyW = new List<Element>();
        foreach (var e in w)
            if (counts.TryGetValue(e.Fingerprint, out var n) && n > 0) { onlyW.Add(e); counts[e.Fingerprint] = n - 1; }
        return (onlyT, onlyW);
    }

    static string DescribeListDelta(string root, int tCount, int wCount, List<Element> onlyT, List<Element> onlyW, string referenceLabel = "winner")
    {
        var sb = new StringBuilder();
        sb.Append(root).Append(": ").Append(tCount).Append(" vs ").Append(referenceLabel).Append(' ').Append(wCount).Append(" item(s)");
        if (tCount == wCount) sb.Append(", contents differ");
        if (onlyT.Count > 0) sb.Append(" — only here: ").Append(DescribeElements(onlyT));
        if (onlyW.Count > 0) sb.Append(onlyT.Count > 0 ? "; " : " — ").Append("only in ").Append(referenceLabel).Append(": ").Append(DescribeElements(onlyW));
        return sb.ToString();
    }

    /// <summary>Up to 2 elements, each as its index plus up to 3 identifying leaf values.</summary>
    static string DescribeElements(List<Element> elems)
    {
        var parts = elems.Take(2).Select(e =>
        {
            var fields = e.Content.Where(c => c.rel.Length > 0).Take(3).Select(c => $"{c.rel}={c.val}").ToList();
            if (fields.Count == 0) fields = e.Content.Take(1).Select(c => c.val).ToList();
            int more = Math.Max(0, e.Content.Count(c => c.rel.Length > 0) - 3);
            return $"[{e.Index}] {string.Join(", ", fields)}{(more > 0 ? $" (+{more} more field(s))" : "")}";
        });
        return string.Join("; ", parts) + (elems.Count > 2 ? $" (+{elems.Count - 2} more element(s))" : "");
    }
}
