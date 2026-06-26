namespace HousecarlCore;

/// <summary>
/// Nearest-plugin-name suggestion for a MISSED plugin lookup — the "did you mean …?" the tool surface appends when a
/// plugins= / lookup= / FormID-plugin value matches no plugin in the load order (HCBR-2026-06-25 ergonomics note). A
/// near-miss is an easy slip — an apostrophe dropped ("Sanguines Trade…" for "Sanguine's Trade…"), the MOD FOLDER name
/// passed where the .esp FILENAME was wanted ("Sanguine's Trade - An Economy Mod" for "…Mod.esp") — and a flat "not in
/// the load order" used to cost several dead-end calls before the exact filename was found by a broad query. This points
/// at the nearest real filename(s) instead.
///
/// PURE + ALLOCATION-LIGHT and run ONLY on the rare miss path (never hot), so a plain O(query·candidate) Levenshtein over
/// the whole name list is fine. Ranking, best first:
///   • EXTENSION-only difference  (query == candidate minus its .esp/.esm/.esl) — the bare mod-folder / missing-extension case
///   • PREFIX                     (candidate starts with the query, or their extension-stripped stems do) — partial typed name
///   • SUBSTRING                  (one contains the other) — a fragment of the real name
///   • EDIT DISTANCE within a length-scaled threshold — typos / apostrophe slips
/// Anything clearing none of these is NOT offered (Q3-adjacent: a wrong "did you mean" is worse than none), and matches
/// are de-duplicated + capped so the message stays short.
/// </summary>
public static class PluginNameSuggest
{
    static readonly string[] PluginExts = { ".esp", ".esm", ".esl" };

    /// <summary>Up to <paramref name="max"/> nearest candidate names for a missed <paramref name="query"/>, best first.
    /// Empty when nothing clears the relevance bar (no spurious suggestion). Case-insensitive throughout.</summary>
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

    /// <summary>The ready-to-append clause — e.g. " Did you mean 'Sanguine's Trade - An Economy Mod.esp'?" — or "" when
    /// there is no near match. Leads with a space so callers can append it straight onto an existing message.</summary>
    public static string DidYouMean(string query, IEnumerable<string> candidates, int max = 3)
    {
        var hits = Nearest(query, candidates, max);
        if (hits.Count == 0) return "";
        var quoted = string.Join(", ", hits.Select(h => "'" + h + "'"));
        return hits.Count == 1 ? $" Did you mean {quoted}?" : $" Did you mean one of: {quoted}?";
    }

    /// <summary>Relevance score (higher = closer; 0 = not a candidate) + the edit distance used as the tiebreak. The tiers
    /// are gapped (1000/800/600/…) so a stronger match always outranks a weaker one regardless of its distance tiebreak.</summary>
    static (int score, int dist) ScoreOne(string q, string qb, string candidate)
    {
        var cb = StripExt(candidate);
        // EXTENSION-only difference: the bare folder name / missing-extension case ("…Mod" → "…Mod.esp"). Strongest signal.
        if (string.Equals(qb, cb, StringComparison.OrdinalIgnoreCase)) return (1000, 0);

        // PREFIX: a partially typed name, on the full names or their stems.
        if (candidate.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            cb.StartsWith(qb, StringComparison.OrdinalIgnoreCase))
            return (800, Math.Abs(cb.Length - qb.Length));

        // SUBSTRING either way: a fragment of the real name (or the real name inside an over-long query).
        if (candidate.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            q.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            return (600, Math.Abs(cb.Length - qb.Length));

        // EDIT DISTANCE on the stems, within a length-scaled threshold — catches typos / apostrophe slips. Skip pairs whose
        // lengths already differ by more than the threshold (they can't be within it) so most candidates cost nothing.
        int threshold = Math.Max(2, Math.Min(qb.Length, cb.Length) / 4);
        if (Math.Abs(qb.Length - cb.Length) > threshold) return (0, 0);
        int d = Levenshtein(qb, cb, threshold);
        if (d >= 0 && d <= threshold) return (500 - d * 10, d);
        return (0, 0);
    }

    static string StripExt(string name)
    {
        foreach (var ext in PluginExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - ext.Length);
        return name;
    }

    /// <summary>Case-insensitive Levenshtein with an early-out: returns -1 the moment the best possible distance on a row
    /// exceeds <paramref name="max"/>, so a far candidate is abandoned without finishing the matrix.</summary>
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
