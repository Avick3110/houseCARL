using System.Text;

namespace HousecarlMcp;

/// <summary>The shared batch-render skeleton behind housecarl_asset_status, housecarl_nif_inspect and housecarl_place: a header count line, the batch-level
/// alarms once and first (so a long batch cannot truncate them away), a per-item loop that lays WHOLE items inside a
/// budget, and an explicit omitted-count cut. max_chars is a ceiling on the finished response: everything written
/// after the items — the caller's trailer and this skeleton's own cut notice — is charged before the first item is
/// laid, and an item that would cross what is left is taken back out rather than left hanging past the cap. A cut is
/// always named — never a silent truncation. Callers supply the header, the alarms block, the per-item renderer, the
/// cap, and the noun the omitted-count line counts in.</summary>
static class BatchRender
{
    /// <summary>Renders one batch. <paramref name="itemNoun"/> is the plural-ish noun the cut line counts, e.g.
    /// "path(s)" or "mesh(es)". <paramref name="reserve"/> is room the caller will write AFTER this body — an
    /// accounting line, a footer — held back out of <paramref name="cap"/> so what follows fits inside max_chars
    /// rather than past it. <paramref name="shown"/> is how many items reached the page, so the caller's accounting
    /// counts what the reader can see rather than a counter the loop bumped for an item it then took back out.
    /// The alarms and the item renderer are handed the budget, not the cap, so their own inner cuts land where the
    /// response's ceiling actually is. The cut marker still names <paramref name="cap"/>: that is the number the
    /// caller passed and the number they would raise.</summary>
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
        // The notice is written INSIDE the ceiling when it fires, so its widest spelling is charged with the
        // caller's trailer before anything else is laid.
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

            // Whole items only: the one that crossed is taken back out, and what it would have needed is named.
            int itemLength = sb.Length - mark;
            sb.Length = mark;
            sb.Append('\n');
            // "Wider than the whole budget" is a claim about the item, so it is made only when the item would not
            // have fitted an empty page either. When the alarms above it are what filled the budget, the item is
            // ordinary and the cut marker is the honest line.
            if (shown == 0 && roomBefore && headerEnd + itemLength > budget.Budget)
                sb.Append(Oversize(items.Count, itemNoun, cap, Needed(mark + itemLength + reserve, items.Count, itemNoun, cap)));
            else AppendCut(sb, items.Count - shown, itemNoun, cap);
            break;
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The max_chars that clears this item in ONE step. The notice quoting it prints the cap as well as the
    /// remedy, so a wider answer widens the reserve the item then has to clear: the number is settled against its own
    /// rendered width rather than named short by the digits it grew.</summary>
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

    /// <summary>The chars held back for whichever notice this render may end on, at its widest spelling: every item
    /// omitted, and an oversize remedy whose number has grown to its full width.</summary>
    static int NoticeReserve(int count, string itemNoun, int cap) =>
        1 + Math.Max(Cut(count, itemNoun, cap).Length, Oversize(count, itemNoun, cap, int.MaxValue).Length);

    /// <summary>The widest this cut marker can be spelled at <paramref name="cap"/>, so a caller whose list may end on
    /// one charges its room before the list starts rather than appending it past the budget.</summary>
    public static int CutReserve(string itemNoun, int cap) => Cut(int.MaxValue, itemNoun, cap).Length;

    /// <summary>The one cut marker: how many items were left out, and the max_chars that left them out. An empty
    /// <paramref name="itemNoun"/> counts unnamed items ("3 more omitted").</summary>
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

    /// <summary>The other way a batch ends with nothing on the page: ONE item is wider than the whole budget, so no
    /// cut of the list can help. It is said rather than dropped, and the remedy is the number that clears it in one
    /// step — max_chars is the parameter that narrows nothing else here.</summary>
    static string Oversize(int count, string itemNoun, int cap, int needed)
    {
        var sb = new StringBuilder();
        sb.Append("  … [").Append(Math.Max(count, 0)).Append(' ');
        if (itemNoun.Length > 0) sb.Append(itemNoun).Append(' ');
        sb.Append("omitted at max_chars=").Append(cap)
          .Append(": the first alone is wider than this response's whole budget; raise max_chars to at least ")
          .Append(needed).Append("]\n");
        return sb.ToString();
    }

    /// <summary>The BSAs that could not be read this build, each named with its owning plugin and the reason. An item
    /// present only in one of these is indistinguishable from a truly absent one, so an "ABSENT" below is
    /// authoritative only when this list is empty. <paramref name="subjectPhrase"/> names the thing with its article,
    /// e.g. "an asset" or "a mesh".</summary>
    public static void AppendReadFailures(StringBuilder sb, IReadOnlyList<string> failures, string subjectPhrase, RenderCap cap)
    {
        if (failures.Count == 0) return;
        // The heading carries the count, and the count IS the alarm, so it is written whatever the budget: an answer
        // that quietly loses it reads as a clean sweep. A cap too small to hold it is named by RenderCap.Settle, which
        // is the arm for what a response must carry whatever the budget.
        sb.Append("\n[!] ").Append(failures.Count).Append(" archive(s) could NOT be read this build — ")
          .Append(subjectPhrase).Append(" present only in these may read as ABSENT below:\n");
        AppendLines(sb, failures, "archive(s)", cap);
    }

    /// <summary>Archive-discovery warnings, e.g. a Skyrim.ini whose [Archive] base-archive list could not be found, so
    /// the vanilla base BSAs are not in the scan and an "ABSENT" for a base-game asset must not be over-trusted.</summary>
    public static void AppendDiscoveryWarnings(StringBuilder sb, IReadOnlyList<string> warnings, RenderCap cap)
    {
        if (warnings.Count == 0) return;
        // Written whatever the budget, for the same reason as the read-failure heading above.
        sb.Append("\n[!] discovery (").Append(warnings.Count).Append("):\n");
        AppendLines(sb, warnings, "warning(s)", cap);
    }

    /// <summary>A capped bullet list inside an alarm block, cut with the same named marker. Whole lines only, and the
    /// marker's own room is charged before the first line, so cutting the list cannot push it past the ceiling. The
    /// heading above it is the alarm and is unconditional; only these detail lines are cut.</summary>
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
