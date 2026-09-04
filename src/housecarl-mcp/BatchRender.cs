using System.Text;

namespace HousecarlMcp;

/// <summary>The shared batch-render skeleton behind housecarl_asset_status and housecarl_nif_inspect: a header count line, the batch-level
/// alarms once and first (so a long batch cannot truncate them away), a cap-checked per-item loop whose FIRST item
/// always renders its core answer, and an explicit omitted-count cut. max_chars bounds the batch tail, never a
/// single-item call's own answer, and a cut is always named — never a silent truncation. Callers supply the header,
/// the alarms block, the per-item renderer, the cap, and the noun the omitted-count line counts in.</summary>
static class BatchRender
{
    /// <summary>Renders one batch. <paramref name="itemNoun"/> is the plural-ish noun the cut line counts, e.g.
    /// "path(s)" or "mesh(es)". <paramref name="reserve"/> is room the caller will write AFTER this body — an
    /// accounting line, a footer — held back out of <paramref name="cap"/> so what follows fits inside max_chars
    /// rather than past it. The cut marker still names <paramref name="cap"/>: that is the number the caller passed
    /// and the number they would raise.</summary>
    public static string Render<T>(
        string header,
        IReadOnlyList<T> items,
        string itemNoun,
        int cap,
        Action<StringBuilder> appendAlarms,
        Action<StringBuilder, T> appendItem,
        int reserve = 0)
    {
        int budget = Math.Max(cap - reserve, 1);
        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        appendAlarms(sb);

        int shown = 0;
        foreach (var item in items)
        {
            // shown > 0: the first item always renders its core answer even when the header and alarms alone
            // exhausted the cap.
            if (shown > 0 && sb.Length >= budget)
            {
                sb.Append('\n');
                AppendCut(sb, items.Count - shown, itemNoun, cap);
                break;
            }
            appendItem(sb, item);
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The one cut marker: how many items were left out, and the max_chars that left them out. An empty
    /// <paramref name="itemNoun"/> counts unnamed items ("3 more omitted").</summary>
    public static void AppendCut(StringBuilder sb, int remaining, string itemNoun, int cap)
    {
        sb.Append("  … [").Append(Math.Max(remaining, 0)).Append(" more ");
        if (itemNoun.Length > 0) sb.Append(itemNoun).Append(' ');
        sb.Append("omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
    }

    /// <summary>The BSAs that could not be read this build, each named with its owning plugin and the reason. An item
    /// present only in one of these is indistinguishable from a truly absent one, so an "ABSENT" below is
    /// authoritative only when this list is empty. <paramref name="subjectPhrase"/> names the thing with its article,
    /// e.g. "an asset" or "a mesh".</summary>
    public static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, string subjectPhrase, int cap)
    {
        if (failures.Count == 0) return;
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — ")
          .Append(subjectPhrase).Append(" present only in these may read as ABSENT below:\n");
        AppendLines(sb, failures, "archive(s)", cap);
    }

    /// <summary>Archive-discovery warnings, e.g. a Skyrim.ini whose [Archive] base-archive list could not be found, so
    /// the vanilla base BSAs are not in the scan and an "ABSENT" for a base-game asset must not be over-trusted.</summary>
    public static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, int cap)
    {
        if (warnings.Count == 0) return;
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        AppendLines(sb, warnings, "warning(s)", cap);
    }

    /// <summary>A capped bullet list inside an alarm block, cut with the same named marker.</summary>
    static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines, string itemNoun, int cap)
    {
        int shown = 0;
        foreach (var line in lines)
        {
            // shown > 0: the first line always renders even when the header and the alarm heading alone exhausted the cap.
            if (shown > 0 && sb.Length >= cap) { AppendCut(sb, lines.Count - shown, itemNoun, cap); break; }
            sb.Append("  - ").Append(line).Append('\n'); shown++;
        }
    }
}
