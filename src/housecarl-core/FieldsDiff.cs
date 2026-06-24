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
    /// be rendered as "identical to winner" (Q3 — never claim knowledge the comparison doesn't have).
    ///
    /// <para><see cref="AgreedCount"/> + <see cref="AgreedSample"/> are the present-==-winner signal (PR-G, item
    /// 4.3): how many VALUE LEAVES the node carries that exactly equal the winner's — i.e. ITM-restated fields,
    /// the deltas of which are (by design) OMITTED, so without this count a contributor that restates a field
    /// identically (an ITM override) was indistinguishable from one that simply doesn't carry it. Counts only
    /// exact-path value leaves read on BOTH sides (never container summaries, never a side's <c>(absent)</c> /
    /// <c>(null link)</c> sentinel — an absent field is NOT an agreement). <see cref="AgreedSample"/> is up to a
    /// few of those paths for the render. Both are 0/empty on a truncated comparison (the agreed set, like the
    /// one-sided deltas, would be a where-the-cap-fell artifact — Q3).</para>
    ///
    /// <para><b>Honest limit (Q3):</b> per-field presence is reliable only for NULLABLE fields, whose absence the
    /// read engine surfaces as a distinct <c>(absent)</c> / <c>(null link)</c> note. Non-nullable scalars grouped
    /// in a binary subrecord (Armor <c>DATA</c> → rating/value/weight) read as <c>0</c>/default with no carried
    /// presence bit, so a leaf that EQUALS the winner is counted as agreement (it IS the same modeled value) but
    /// the render never claims the contributor "carries" it as a distinct subrecord — there is no bit to prove
    /// that. The ABSENT render fires only on the explicit sentinels, which only nullable fields produce.</para></summary>
    public sealed record Result(IReadOnlyList<string> Deltas, bool Complete,
        int AgreedCount, IReadOnlyList<string> AgreedSample);

    /// <summary>True when a CleanLines value is a read-engine "no value here" note sentinel — the field is
    /// modeled but the contributor carries nothing (absent optional, or a present-but-null link). Treated as a
    /// first-class state, never compared as if it were a real token value. References the <see cref="ReadEngine"/>
    /// constants directly (same assembly) — single source of truth, compile-time coupling, no drift.</summary>
    static bool IsAbsentSentinel(string val) =>
        val == ReadEngine.AbsentNote || val == ReadEngine.NullLinkNote || val == ReadEngine.UnresolvedStringNote;

    /// <summary>Compare one plugin's deep-read fields against the winner's. Both sides should be read by the
    /// same <see cref="ReadEngine.ReadFields"/> call shape (same paths, same depth) so line sets correspond.</summary>
    public static Result Compare(RecordFields theirs, RecordFields winner)
    {
        var tValueLeaves = new HashSet<string>(StringComparer.Ordinal);
        var wValueLeaves = new HashSet<string>(StringComparer.Ordinal);
        var (tLines, tComplete) = CleanLines(theirs, tValueLeaves);
        var (wLines, wComplete) = CleanLines(winner, wValueLeaves);
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
        int agreedCount = 0;
        var agreedSample = new List<string>();
        foreach (var (path, val) in tScalar)
        {
            if (wScalar.TryGetValue(path, out var wv))
            {
                bool tAbsent = IsAbsentSentinel(val), wAbsent = IsAbsentSentinel(wv);
                if (tAbsent && wAbsent)
                {
                    // Both carry nothing here (a nullable field neither side sets) — same state, no delta and
                    // not an agreement (there is no value to agree ON).
                }
                else if (tAbsent)
                {
                    // FIRST-CLASS absent state (item 4.3): the contributor doesn't carry this field but the
                    // winner does. Rendered as ABSENT, not as a "=(absent)" phantom value delta. Only nullable
                    // fields reach here — the read engine emits the sentinel only for them.
                    deltas.Add($"{path}: ABSENT here (winner has {wv})");
                }
                else if (wAbsent)
                {
                    // The contributor carries a value the WINNER doesn't — the field is absent on the winner.
                    deltas.Add($"{path}={val} (winner has {path} ABSENT)");
                }
                else if (!string.Equals(NormalizeForCompare(val), NormalizeForCompare(wv), StringComparison.Ordinal))
                {
                    deltas.Add($"{path}={val} (winner {wv})");
                }
                else if (tValueLeaves.Contains(path) && wValueLeaves.Contains(path))
                {
                    // present-==-winner: a VALUE leaf the contributor restates identically (an ITM override).
                    // Counted (not a delta) so the render can distinguish an ITM-restating override from a
                    // fields-narrow one. Container summary lines ("[3 item(s)]") that happen to match are NOT
                    // value leaves and so never inflate this count.
                    agreedCount++;
                    if (agreedSample.Count < AgreedSampleCap) agreedSample.Add(path);
                }
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

        // On a truncated comparison the agreed set, like the one-sided deltas, would be a where-the-cap-fell
        // artifact — suppress it (Q3): a partial read must not claim "N fields match the winner".
        if (!complete) { agreedCount = 0; agreedSample.Clear(); }
        return new Result(deltas, complete, agreedCount, agreedSample);
    }

    /// <summary>How many agreed-field paths to keep for the render — a small sample, not the full set (a deep
    /// ITM override agrees on dozens of leaves; the count carries the weight, the sample is illustrative).</summary>
    const int AgreedSampleCap = 3;

    /// <summary>The root's own container-summary line ("[Type: N item(s)/pair(s)]"), or null when the read
    /// never emitted one (a fields=-bracketed read names element paths directly, skipping the root).</summary>
    static string? RootSummary(List<(string path, string val)> lines, string root)
    {
        foreach (var (path, val) in lines)
            if (path == root) return val;
        return null;
    }

    /// <summary>The read's lines minus the expansion-cap sentinel; each value is the round-trippable token or,
    /// for a non-leaf/absent line, its note. <paramref name="valueLeaves"/> collects the paths that carry a real
    /// VALUE (<c>HasValue</c>) — the only lines an agreement count may consider, so a container summary line
    /// ("[3 item(s)]") or an absent/null-link note is never miscounted as a present field. Complete=false iff the
    /// expansion-cap sentinel was present.</summary>
    static (List<(string path, string val)> lines, bool complete) CleanLines(RecordFields rf, HashSet<string> valueLeaves)
    {
        var lines = new List<(string, string)>(rf.Fields.Count);
        bool complete = true;
        foreach (var f in rf.Fields)
        {
            if (f.Path == "…") { complete = false; continue; }         // ReadEngine's expansion-cap sentinel (Q3)
            if (f.HasValue) valueLeaves.Add(f.Path);
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
