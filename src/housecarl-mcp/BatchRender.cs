using System.Text;

namespace HousecarlMcp;

/// <summary>The shared batch-render skeleton behind housecarl_asset_status, housecarl_nif_inspect and
/// housecarl_place: a header count line, the batch-level alarms once and first, a per-item loop that lays WHOLE
/// items inside a budget, and an explicit omitted-count cut. Contract in docs/architecture/render-budget.md.</summary>
static class BatchRender
{
    /// <summary>Renders one batch. <paramref name="reserve"/> is room the caller will write AFTER this body, held
    /// back out of <paramref name="cap"/>; <paramref name="shown"/> is how many items reached the page. The alarms
    /// and the item renderer are handed the budget, not the cap, while the cut marker still names the cap.</summary>
    public static string Render<T>(
        string header,
        IReadOnlyList<T> items,
        string itemNoun,
        int cap,
        Action<StringBuilder, RenderCap> appendAlarms,
        Action<StringBuilder, T, RenderCap> appendItem,
        out int shown,
        int reserve = 0)
    {
        // The notice is written INSIDE the ceiling, so its widest spelling is charged before anything is laid.
        var budget = RenderCap.For(cap, reserve + NoticeReserve(items.Count, itemNoun, cap));
        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        int headerEnd = sb.Length;
        appendAlarms(sb, budget);

        shown = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int mark = sb.Length;
            bool roomBefore = mark <= budget.Budget;
            if (roomBefore) appendItem(sb, items[i], budget);
            if (roomBefore && sb.Length <= budget.Budget) { shown++; continue; }

            // Whole items only: the one that crossed is taken back out.
            int itemLength = sb.Length - mark;
            sb.Length = mark;
            sb.Append('\n');
            // "Wider than the whole budget" is claimed only when the item would not have fitted an empty page.
            if (roomBefore && headerEnd + itemLength > budget.Budget)
                sb.Append(Oversize(items.Count - shown, itemNoun, cap,
                                   Needed(Widest(header, items, i, appendAlarms, appendItem) + reserve, items.Count, itemNoun, cap)));
            else AppendCut(sb, items.Count - shown, itemNoun, cap);
            break;
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>What this response costs with nothing cut down to <paramref name="upTo"/>, each part rendered
    /// against a budget none of them can exhaust — a render that grows to fill its room is WIDER at a wider cap,
    /// so a remedy measured against the cut form would come back short.</summary>
    static int Widest<T>(string header, IReadOnlyList<T> items, int upTo,
                         Action<StringBuilder, RenderCap> appendAlarms, Action<StringBuilder, T, RenderCap> appendItem)
    {
        // Half of int.MaxValue: a budget no render can reach, with room for the reserves each one subtracts.
        var room = new RenderCap(int.MaxValue / 2, int.MaxValue / 2);
        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        appendAlarms(sb, room);
        for (int i = 0; i <= upTo; i++) appendItem(sb, items[i], room);
        return sb.Length;
    }

    /// <summary>The max_chars that clears this item in ONE step, settled against its own rendered width.</summary>
    static int Needed(int floor, int count, string itemNoun, int cap)
    {
        int needed = floor + NoticeReserve(count, itemNoun, cap);
        for (int i = 0; i < 4; i++)
        {
            int next = floor + NoticeReserve(count, itemNoun, needed);
            if (next == needed) break;
            needed = next;
        }
        return needed;
    }

    /// <summary>The chars held back for whichever notice this render may end on, at its widest spelling.</summary>
    static int NoticeReserve(int count, string itemNoun, int cap) =>
        1 + Math.Max(Cut(count, itemNoun, cap).Length, Oversize(count, itemNoun, cap, int.MaxValue).Length);

    /// <summary>The widest this cut marker can be spelled at <paramref name="cap"/>.</summary>
    public static int CutReserve(string itemNoun, int cap) => Cut(int.MaxValue, itemNoun, cap).Length;

    /// <summary>The one cut marker: how many items were left out, and the max_chars that left them out.</summary>
    public static void AppendCut(StringBuilder sb, int remaining, string itemNoun, int cap) =>
        sb.Append(Cut(remaining, itemNoun, cap));

    static string Cut(int remaining, string itemNoun, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("  … [").Append(Math.Max(remaining, 0)).Append(" more ");
        if (itemNoun.Length > 0) sb.Append(itemNoun).Append(' ');
        sb.Append("omitted at max_chars=").Append(cap).Append("; raise max_chars to see all]\n");
        return sb.ToString();
    }

    /// <summary>The other way a batch stops: ONE item is wider than the whole budget, so no cut of the list can
    /// help. It is said rather than dropped, with the number that clears it in one step.</summary>
    static string Oversize(int count, string itemNoun, int cap, int needed)
    {
        var sb = new StringBuilder();
        sb.Append("  … [").Append(Math.Max(count, 0)).Append(' ');
        if (itemNoun.Length > 0) sb.Append(itemNoun).Append(' ');
        sb.Append("omitted at max_chars=").Append(cap)
          .Append(": the next one alone is wider than this response's whole budget; raise max_chars to at least ")
          .Append(needed).Append("]\n");
        return sb.ToString();
    }

    /// <summary>The BSAs that could not be read this build, each named with its owning plugin and the reason — an
    /// "ABSENT" below is authoritative only when this list is empty.</summary>
    public static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, string subjectPhrase, RenderCap cap)
    {
        if (failures.Count == 0) return;
        // The heading carries the count, and the count IS the alarm, so it is written whatever the budget.
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — ")
          .Append(subjectPhrase).Append(" present only in these may read as ABSENT below:\n");
        AppendLines(sb, failures, "archive(s)", cap);
    }

    /// <summary>The loose roots that could not be read this build, each named with the reason — the loose twin of
    /// <see cref="AppendReadFailures"/>, and the same rule: an "ABSENT" below is authoritative only where it is empty.</summary>
    public static void AppendRootFailures(StringBuilder sb, IReadOnlyList<string> failures, string subjectPhrase, RenderCap cap)
    {
        if (failures.Count == 0) return;
        // The heading carries the count, and the count IS the alarm, so it is written whatever the budget.
        sb.Append("\n[!] ").Append(failures.Count).Append(" loose root(s) could NOT be read this build — ")
          .Append(subjectPhrase).Append(" present only in these may read as ABSENT below:\n");
        AppendLines(sb, failures, "root(s)", cap);
    }

    /// <summary>Archive-discovery warnings, e.g. a Skyrim.ini whose [Archive] base-archive list could not be found,
    /// so the vanilla base BSAs are not in the scan.</summary>
    public static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, RenderCap cap)
    {
        if (warnings.Count == 0) return;
        // Written whatever the budget, for the same reason as the read-failure heading above.
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        AppendLines(sb, warnings, "warning(s)", cap);
    }

    /// <summary>A capped bullet list inside an alarm block, cut with the same named marker. Whole lines only, and
    /// the marker's own room is charged before the first line; the heading above it is unconditional.</summary>
    public static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines, string itemNoun, RenderCap cap)
    {
        var room = cap.Less(Cut(lines.Count, itemNoun, cap.Cap).Length);
        int shown = 0;
        for (; shown < lines.Count; shown++)
        {
            var line = "  - " + lines[shown] + "\n";
            if (!room.TryAppend(sb, line)) break;
        }
        if (shown < lines.Count) AppendCut(sb, lines.Count - shown, itemNoun, cap.Cap);
    }
}
