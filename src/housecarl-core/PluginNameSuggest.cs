namespace HousecarlCore;

/// <summary>Nearest-plugin-name suggestion for a MISSED plugin lookup — the "did you mean …?" the tool surface appends
/// when a plugins= / filter= / FormID-plugin value matches no plugin in the load order. Ranked best first: an
/// EXTENSION-only difference, then PREFIX, then SUBSTRING, then EDIT DISTANCE within a length-scaled threshold.
/// Anything clearing none of these is not offered, and matches are de-duplicated and capped.</summary>
public static class PluginNameSuggest
{
    static readonly string[] PluginExts = PluginFile.Extensions;   // the one shared home (HousecarlCore.PluginFile) — no divergent copy

    /// <summary>Up to <paramref name="max"/> nearest candidate names for a missed <paramref name="query"/>, best
    /// first; empty when nothing clears the relevance bar. Case-insensitive throughout.</summary>
    public static IReadOnlyList<string> Nearest(string query, IEnumerable<string> candidates, int max = 3)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Array.Empty<string>();
        var qb = StripExt(q);

        var scored = new List<(string name, int score, int dist)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in candidates)
        {
            var c = raw?.Trim();
            if (string.IsNullOrEmpty(c)) continue;
            if (!seen.Add(c)) continue;                              // a name can repeat across lists — score it once
            if (string.Equals(c, q, StringComparison.OrdinalIgnoreCase)) continue;   // an exact match isn't a miss
            var (score, dist) = ScoreOne(q, qb, c);
            if (score > 0) scored.Add((c, score, dist));
        }

        return scored
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.dist)
            .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(s => s.name)
            .ToList();
    }

    /// <summary>Case-insensitive edit distance between two names' extension-stripped stems, always answered — the
    /// total order a caller falls back to for the names <see cref="Nearest"/> vouches for none of.</summary>
    public static int StemDistance(string a, string b)
        => Levenshtein(StripExt((a ?? "").Trim()), StripExt((b ?? "").Trim()), int.MaxValue);

    /// <summary>The ready-to-append clause, or "" when there is no near match. Leads with a space.</summary>
    public static string DidYouMean(string query, IEnumerable<string> candidates, int max = 3)
    {
        var hits = Nearest(query, candidates, max);
        if (hits.Count == 0) return "";
        // Backtick-delimit, not single-quote: mod names routinely carry an apostrophe that a wrapping ' collides with.
        var quoted = string.Join(", ", hits.Select(h => "`" + h + "`"));
        return hits.Count == 1 ? $" Did you mean {quoted}?" : $" Did you mean one of: {quoted}?";
    }

    /// <summary>Relevance score (higher = closer; 0 = not a candidate) plus the edit distance used as the tiebreak.
    /// The tiers are gapped so a stronger match always outranks a weaker one.</summary>
    static (int score, int dist) ScoreOne(string q, string qb, string candidate)
    {
        var cb = StripExt(candidate);
        // EXTENSION-only difference: the bare folder name / missing-extension case. Strongest signal.
        if (string.Equals(qb, cb, StringComparison.OrdinalIgnoreCase)) return (1000, 0);

        // PREFIX: a partially typed name, on the full names or their stems.
        if (candidate.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            cb.StartsWith(qb, StringComparison.OrdinalIgnoreCase))
            return (800, Math.Abs(cb.Length - qb.Length));

        // SUBSTRING either way: a fragment of the real name, or the real name inside an over-long query.
        if (candidate.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            q.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            return (600, Math.Abs(cb.Length - qb.Length));

        // EDIT DISTANCE on the stems, within a length-scaled threshold; pairs whose lengths already differ by more
        // than the threshold are skipped.
        int threshold = Math.Max(2, Math.Min(qb.Length, cb.Length) / 4);
        if (Math.Abs(qb.Length - cb.Length) > threshold) return (0, 0);
        int d = Levenshtein(qb, cb, threshold);
        // Floor at 1, so a genuine edit-distance match stays positive and Nearest's `score > 0` never drops it.
        if (d >= 0 && d <= threshold) return (Math.Max(1, 500 - d * 10), d);
        return (0, 0);
    }

    static string StripExt(string name)
    {
        foreach (var ext in PluginExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ext.Length);
        return name;
    }

    /// <summary>Case-insensitive Levenshtein with an early-out: returns -1 the moment the best possible distance on a
    /// row exceeds <paramref name="max"/>.</summary>
    static int Levenshtein(string a, string b, int max)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        int n = a.Length, m = b.Length;
        if (n == 0) return m; if (m == 0) return n;

        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            int rowBest = curr[0];
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowBest) rowBest = curr[j];
            }
            if (rowBest > max) return -1;                            // even the best continuation can't get back under max
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}
