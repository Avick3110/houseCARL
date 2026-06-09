using System.Text;

namespace HousecarlCore;

/// <summary>
/// The winner-relative CONTENT comparison behind the conflict-tree diff (HCBR-2026-06-09-01).
///
/// The old diff compared depth-1 rendered lines, where a list collapses to a "[List: N item(s)]" count
/// token — so two lists with EQUAL counts but DIFFERENT contents compared identical, and a whole override
/// could be reported "(identical to winner)" while carrying the very edit that motivated it (the report's
/// masked USSEP PlayerFaction regression). This module compares DEEP reads (every modeled leaf):
///
///   • scalar / substruct / dict leaves — exact-path token comparison (the old behavior, at full depth).
///     Dict brackets are semantic KEYS — non-numeric (Skills[OneHanded]) by spelling, numeric-keyed dicts
///     (Package.Data) by the read engine's in-band "N pair(s)" container marker — so a key rebinding is a
///     real delta, never absorbed by positional handling;
///   • positional LIST contents — order-INSENSITIVE multiset comparison of whole elements keyed on their
///     content (the report's case: USSEP and the winner store the same relations in different orders, so
///     an index-wise comparison would over-report; element identity is content-based). Elements present
///     on only one side are reported with identifying leaf values. Nested list reordering INSIDE an
///     element is not canonicalised (v1) — it can over-report as a content delta, never under-report;
///   • honesty (Q3) — if either side's deep read hit the expansion cap, <see cref="Result.Complete"/> is
///     false: list comparison and one-sided-presence deltas are SUPPRESSED (where the two caps fell would
///     otherwise fabricate differences), only value mismatches observed on both sides are reported, and
///     the caller must not claim identity beyond what was actually compared.
/// </summary>
public static class FieldsDiff
{
    /// <summary>Field-level deltas, preformatted for the conflict-tree render. <see cref="Complete"/> false ⇒
    /// at least one side's read was truncated at the expansion cap, so an empty <see cref="Deltas"/> must NOT
    /// be rendered as "identical to winner" (Q3 — never claim knowledge the comparison doesn't have).</summary>
    public sealed record Result(IReadOnlyList<string> Deltas, bool Complete);

    /// <summary>Compare one plugin's deep-read fields against the winner's. Both sides should be read by the
    /// same <see cref="ReadEngine.ReadFields"/> call shape (same paths, same depth) so line sets correspond.</summary>
    public static Result Compare(RecordFields theirs, RecordFields winner)
    {
        var (tLines, tComplete) = CleanLines(theirs);
        var (wLines, wComplete) = CleanLines(winner);
        bool complete = tComplete && wComplete;

        // Numeric-bracket roots seen on EITHER side (the union, so a 0-item-vs-N-item list is still compared
        // as elements), then split DICT-vs-LIST by the read engine's in-band container marker: a numeric-KEYED
        // dict (Package.Data) renders "N pair(s)" and is compared by EXACT PATH — its bracket content is a
        // semantic KEY, so the same values rebound to different keys is a real delta — while a positional
        // list is compared by element content (PR #28 review finding 2).
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (p, _) in tLines) if (ListRoot(p) is { } r) candidates.Add(r);
        foreach (var (p, _) in wLines) if (ListRoot(p) is { } r) candidates.Add(r);
        var listRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in candidates)
        {
            var tSum = RootSummary(tLines, root);
            var wSum = RootSummary(wLines, root);
            // No root-summary line on EITHER side ⇒ a fields=-bracketed read (e.g. fields=["Data[3].Name"]):
            // the dict marker is structurally absent there, so positional handling could absorb a dict key
            // rebinding — and the user named specific indices/keys anyway, so EXACT-PATH is the natural
            // semantics. The failure direction flips to over-report (a reordered list under bracketed
            // fields= shows index-wise deltas), never under-report (PR #28 review #2).
            bool seen = tSum is not null || wSum is not null;
            bool dict = (tSum?.Contains(" pair(s)]", StringComparison.Ordinal) ?? false)
                     || (wSum?.Contains(" pair(s)]", StringComparison.Ordinal) ?? false);
            if (seen && !dict) listRoots.Add(root);
        }

        var deltas = new List<string>();

        // ---- exact-path comparison: scalars, substructs, dict children (bracket = a semantic key) --------
        // On a TRUNCATED comparison the list roots' own summary lines join the exact-path set: a root count
        // token read on BOTH sides is a real, cap-independent read, so a count delta still surfaces even
        // though element comparison is suppressed (PR #28 review #2, non-blocking note).
        var tScalar = ExactPathLines(tLines, listRoots, includeListRootSummaries: !complete);
        var wScalar = ExactPathLines(wLines, listRoots, includeListRootSummaries: !complete);
        foreach (var (path, val) in tScalar)
        {
            if (wScalar.TryGetValue(path, out var wv))
            {
                if (!string.Equals(NormalizeForCompare(val), NormalizeForCompare(wv), StringComparison.Ordinal))
                    deltas.Add($"{path}={val} (winner {wv})");
            }
            // One-sided presence is only a delta when BOTH sides were fully read: on a truncated side a
            // missing line is an artifact of WHERE its cap fell, not of content — reporting it would
            // FABRICATE a difference (PR #28 review finding 1).
            else if (complete) deltas.Add($"{path}={val} (winner has no {path})");   // shape difference (e.g. another ConditionData arm)
        }
        if (complete)
            foreach (var (path, wv) in wScalar)
                if (!tScalar.ContainsKey(path)) deltas.Add($"{path} only in winner: {wv}");

        // ---- positional lists: order-insensitive whole-element multiset comparison. SKIPPED entirely on a
        //      truncated comparison — a cap landing mid-list fabricates one-sided elements and wrong counts;
        //      the renderer surfaces the truncation instead (PR #28 review finding 1). --------------------
        if (complete)
        {
            foreach (var root in listRoots.OrderBy(r => r, StringComparer.Ordinal))
            {
                var tElems = ElementsOf(tLines, root);
                var wElems = ElementsOf(wLines, root);
                var (onlyT, onlyW) = MultisetDiff(tElems, wElems);
                if (onlyT.Count == 0 && onlyW.Count == 0) continue;    // same contents (possibly reordered) — no delta
                deltas.Add(DescribeListDelta(root, tElems.Count, wElems.Count, onlyT, onlyW));
            }
        }

        return new Result(deltas, complete);
    }

    /// <summary>The root's own container-summary line ("[Type: N item(s)/pair(s)]"), or null when the read
    /// never emitted one (a fields=-bracketed read names element paths directly, skipping the root).</summary>
    static string? RootSummary(List<(string path, string val)> lines, string root)
    {
        foreach (var (path, val) in lines)
            if (path == root) return val;
        return null;
    }

    /// <summary>The read's lines minus the expansion-cap sentinel; each value is the round-trippable token or,
    /// for a non-leaf/absent line, its note. Complete=false iff the sentinel was present.</summary>
    static (List<(string path, string val)> lines, bool complete) CleanLines(RecordFields rf)
    {
        var lines = new List<(string, string)>(rf.Fields.Count);
        bool complete = true;
        foreach (var f in rf.Fields)
        {
            if (f.Path == "…") { complete = false; continue; }         // ReadEngine's expansion-cap sentinel (Q3)
            lines.Add((f.Path, f.HasValue ? f.Token ?? "" : f.Note ?? ""));
        }
        return (lines, complete);
    }

    /// <summary>The path's OUTERMOST positional-list root — the prefix before its first NUMERIC bracket — or
    /// null when it has none (scalars, substructs, and dict keys like Skills[OneHanded], which are semantic
    /// identities and belong in exact-path comparison).</summary>
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

    /// <summary>Exact-path map of every line OUTSIDE positional-list content: scalars, substructs, dict
    /// children and dict-root summaries (a dict bracket is a semantic key — numeric or not — so exact-path is
    /// the correct comparison), excluding only positional-list content and the list roots' own summary lines
    /// (subsumed by the element comparison — including the 0-item side, whose only trace IS its summary).</summary>
    static Dictionary<string, string> ExactPathLines(List<(string path, string val)> lines,
        HashSet<string> listRoots, bool includeListRootSummaries = false)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, val) in lines)
        {
            var root = ListRoot(path);
            bool positionalContent = root is not null && listRoots.Contains(root);
            if (!positionalContent && (includeListRootSummaries || !listRoots.Contains(path)))
                map[path] = val;
        }
        return map;
    }

    sealed record Element(int Index, List<(string rel, string val)> Content, string Fingerprint);

    /// <summary>Group a root's bracketed lines into whole elements: positional index + (relative path, value)
    /// content + an order-insensitive content fingerprint (nested content included verbatim).</summary>
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

    /// <summary>Case-normalise a value for COMPARISON when it is a FormKey token ("XXXXXX:Plugin.esp").
    /// ModKeys are case-insensitive and each plugin stores a master's filename as written in ITS OWN master
    /// list, so the same link can render "17DDC4:ccBGSSSE001-Fish.esm" in one version and
    /// "17ddc4:ccbgssse001-fish.esm" in another (seen live on the report's PlayerFaction record) — an ordinal
    /// comparison would report a false content delta. Display keeps each side's original token; only equality
    /// checks and element fingerprints use this form. Non-FormKey values pass through untouched (string
    /// content stays case-SENSITIVE).</summary>
    static string NormalizeForCompare(string val)
    {
        if (val.Length < 8 || val[6] != ':') return val;
        for (int i = 0; i < 6; i++) if (!Uri.IsHexDigit(val[i])) return val;
        return string.Concat(val[..6].ToUpperInvariant(), ":", val[7..].ToLowerInvariant());
    }

    /// <summary>Content-keyed multiset difference: elements (with multiplicity) present on one side only.
    /// Equal multisets ⇒ the lists hold the same contents, merely (possibly) reordered ⇒ no delta.</summary>
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

    static string DescribeListDelta(string root, int tCount, int wCount, List<Element> onlyT, List<Element> onlyW)
    {
        var sb = new StringBuilder();
        sb.Append(root).Append(": ").Append(tCount).Append(" vs winner ").Append(wCount).Append(" item(s)");
        if (tCount == wCount) sb.Append(", contents differ");
        if (onlyT.Count > 0) sb.Append(" — only here: ").Append(DescribeElements(onlyT));
        if (onlyW.Count > 0) sb.Append(onlyT.Count > 0 ? "; " : " — ").Append("only in winner: ").Append(DescribeElements(onlyW));
        return sb.ToString();
    }

    /// <summary>Up to 2 elements, each as its index plus up to 3 identifying leaf values (emit order — the
    /// model's field order — with the bare element-summary line used only when no real leaves exist).</summary>
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
