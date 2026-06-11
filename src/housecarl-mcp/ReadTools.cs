using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// houseCARL read tools (§8.4 Beat B). Reads ride the proven read core (<see cref="ReadEngine"/>) + the load-order
/// resolver (<see cref="LoadOrderResolver"/>) through <see cref="LoadOrderService"/>; they never mutate. The
/// compact `path = token` wire format (Q4.8 lever 1) is token-lean and the token IS a value a write can reuse.
/// conflict_tree adds the winner-relative field diff (Q4.8 lever 2). Every bulk view estimates its size and
/// stops with an explicit notice rather than truncating silently (Q3 + Q4.9).
/// </summary>
[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(Name = "housecarl_read_record", ReadOnly = true, Title = "Read a record"),
     Description(
         "Read one record's fields as the load order resolves it. Returns the TRUE load-order winner's values by " +
         "default — or a named plugin's version via `plugin` — as compact `path = token` lines whose tokens a write " +
         "can reuse verbatim. With conflict_tree=true, also lists every plugin that touches the record (load order, " +
         "winner last) AND the winner-relative field diff (each other plugin's only-the-fields-that-differ). A FormID " +
         "is 'XXXXXX:Plugin.esp'. Does NOT modify anything. For many records in one call use " +
         "housecarl_batch_record_detail; to scan the order by type/reference/conflict use housecarl_cross_plugin_query; " +
         "to edit, use housecarl_set_field.")]
    public static string ReadRecord(
        LoadOrderService svc,
        [Description("The record's FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. Example: '0F1AC1:Skyrim.esm'.")]
            string formid,
        [Description("Optional. Read THIS plugin's version of the record instead of the load-order winner (a filename, e.g. 'Requiem.esp'). Useful to inspect a specific override.")]
            string? plugin = null,
        [Description("Optional. Dotted field paths to read (e.g. 'BasicStats.Damage', 'Name', 'Keywords'). Index into a list/dict element with BRACKETS, e.g. 'Effects[0].Data.Magnitude' or 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties[5].Name' — a bare '.0' is read as a field name, not an index. Omit to dump every modeled field one level deep (pass depth= to expand list/dict contents).")]
            string[]? fields = null,
        [Description("Optional. Expansion depth for list/dict/substruct CONTENTS (default 1 = one level, a container shown as just a count like '[List: 22 item(s)]'). depth=2 enumerates each element with its index + an identity, e.g. 'VirtualMachineAdapter.Aliases[0].Scripts[0].Properties[5] = [ScriptObjectProperty] Name=DAK_HorseBuyPerk' — so you can SEE indices/contents without probing each [i]. Higher depth opens deeper. Pair with a fields= path to expand only that subtree; on a whole-record dump it expands every container (bounded, with an explicit truncation note).")]
            int depth = 1,
        [Description("When true, also return the ordered list of every plugin that touches this record (winner last) and the winner-relative field diff for each.")]
            bool conflict_tree = false,
        [Description("Optional. Max characters before the diff is cut with an explicit notice (never silent). 0 = the server default (~80k). Raise to see a very deep conflict tree in full.")]
            int max_chars = 0) => Guard.Tool("housecarl_read_record", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0F1AC1:Skyrim.esm'."; }

        var outcome = svc.ResolveRead(fk, plugin, fields, conflict_tree, depth <= 0 ? 1 : depth);
        return Wire.RenderRecord(svc, outcome, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_batch_record_detail", ReadOnly = true, Title = "Read many records"),
     Description(
         "Read many records in ONE call (saves per-record tool-call overhead). Each FormID resolves to its " +
         "load-order winner and renders like housecarl_read_record; a bad or absent FormID yields a per-item error " +
         "without failing the batch. With conflict_tree=true each record also gets its touching-plugin list + " +
         "winner-relative field diff. The combined response is size-estimated: over the cap it stops with an explicit " +
         "'rendered X of N' notice (never silent truncation) — request fewer formids, pass fields= to slim each, or " +
         "raise max_chars. Does NOT modify anything.")]
    public static string BatchRecordDetail(
        LoadOrderService svc,
        [Description("The FormIDs to read, each 'XXXXXX:Plugin.esp'. Resolved in order; results are returned in the same order.")]
            string[] formids,
        [Description("Optional. Dotted field paths to read for EVERY record (e.g. 'Name', 'BasicStats.Damage'); index a list/dict element with BRACKETS, e.g. 'Effects[0].Data.Magnitude'. Omit to dump every modeled field one level deep per record.")]
            string[]? fields = null,
        [Description("Optional. Expansion depth for list/dict/substruct CONTENTS per record (default 1). depth=2 enumerates each container's elements with index + identity (see housecarl_read_record). Bounded per record with an explicit truncation note.")]
            int depth = 1,
        [Description("When true, include each record's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("Optional. Max characters before the response stops with an explicit 'rendered X of N' notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_batch_record_detail", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        if (formids is null || formids.Length == 0) return "error: formids is empty. Pass one or more 'XXXXXX:Plugin.esp' FormIDs.";
        var outcomes = svc.ResolveBatch(formids, fields, conflict_tree, depth <= 0 ? 1 : depth);
        return Wire.RenderBatch(svc, outcomes, fields, conflict_tree, max_chars);
    });

    [McpServerTool(Name = "housecarl_cross_plugin_query", ReadOnly = true, Title = "Query records across the load order"),
     Description(
         "Find records across the whole load order matching a filter — returns matches only, each as a compact " +
         "summary line (FormID, type, editorid, winner, override depth). Filters (combine freely): type= a record " +
         "signature ('WEAP') or catalog name ('Weapon'); conflicts_only=true for records >1 plugin touches; " +
         "editorid_contains= a substring of the EditorID; references= a FormID the record points at (reverse lookup, " +
         "e.g. 'what uses this keyword'); where= filters by a field's VALUE (e.g. 'MagicSkill = Destruction', " +
         "'BasicStats.Damage >= 50' — any scalar field, ANDed); plugins= limits the scan to records those plugins " +
         "touch (a bare plugins= is 'everything this plugin touches'). At least one filter or plugins= is required. " +
         "editorid_contains/references/where " +
         "are body scans and MUST be combined with type=, plugins=, or conflicts_only= to bound the work. Pass fields= " +
         "or conflict_tree=true to expand each match from a summary line to full detail. Results cap at limit= matches " +
         "and max_chars; both overruns are reported explicitly (never silent). Does NOT modify anything.")]
    public static string CrossPluginQuery(
        LoadOrderService svc,
        [Description("Optional. A record signature ('WEAP', 'NPC_') or catalog name ('Weapon', 'Npc'). Cheap — uses typed group enumeration.")]
            string? type = null,
        [Description("Optional. A FormID 'XXXXXX:Plugin.esp'; matches records whose winner references it (deep link scan). Must be combined with type=, plugins=, or conflicts_only=.")]
            string? references = null,
        [Description("Optional. Case-insensitive substring of the EditorID. Body scan — must be combined with type=, plugins=, or conflicts_only=.")]
            string? editorid_contains = null,
        [Description("When true, restrict to records more than one plugin touches (the contested set).")]
            bool conflicts_only = false,
        [Description("Optional. Plugin filenames to scope the scan to (records those plugins touch). A name not in the load order is an error. Omit to scan the whole order.")]
            string[]? plugins = null,
        [Description("Optional. Field-VALUE predicates, each \"<path> <op> <value>\" — e.g. 'MagicSkill = Destruction', 'BasicStats.Damage >= 50', 'Archetype.ActorValue = Infamy'. Operators: = != > >= < <= (>/< numeric) and contains (case-insensitive substring); multiple are ANDed. Filters on ANY scalar field the read tools can read (any type, any depth). A body scan — MUST be combined with type= or plugins=. A wrong or container/list path is reported, never a silent '0 matches'.")]
            string[]? where = null,
        [Description("Optional. Dotted field paths to show for each match (e.g. 'BasicStats.Damage'). Omit for a one-line summary per match.")]
            string[]? fields = null,
        [Description("When true, include each match's touching-plugin list (winner last) + winner-relative field diff.")]
            bool conflict_tree = false,
        [Description("Optional. Max matches to return (default 500). The TRUE total is always reported; over the cap it says 'showing first N'.")]
            int limit = 500,
        [Description("Optional. Max characters before the response stops with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_cross_plugin_query", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey? refFk = null;
        if (!string.IsNullOrWhiteSpace(references))
        {
            try { refFk = FormKey.Factory(references.Trim()); }
            catch (Exception ex) { return $"error: bad references FormID '{references}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."; }
        }
        var outcome = svc.CrossQuery(type, refFk, editorid_contains, conflicts_only, plugins, where, limit <= 0 ? 500 : limit);
        return Wire.RenderCrossQuery(svc, outcome, fields, conflict_tree, max_chars);
    });
}

/// <summary>Compact, parseable `key = value` rendering (Q4.8 lever 1) + the winner-relative conflict diff
/// (lever 2) + response-size estimation (Q3 / Q4.9 — explicit cut, never silent). Shared by all three read tools.</summary>
static class Wire
{
    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

    // ---- housecarl_read_record ----------------------------------------------------------------------
    public static string RenderRecord(LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        if (o.Error is not null) return "error: " + o.Error;
        var sb = new StringBuilder();
        AppendRecord(sb, o, Cap(maxChars));
        if (conflictTree) AppendConflictTree(sb, svc, o, fields, Cap(maxChars));
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_batch_record_detail --------------------------------------------------------------
    public static string RenderBatch(LoadOrderService svc, IReadOnlyList<ReadOutcome> outcomes, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record\n" : " records\n");
        int rendered = 0;
        foreach (var o in outcomes)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(outcomes.Count)
                  .Append(" records before hitting max_chars=").Append(cap)
                  .Append("; request fewer formids, pass fields= to slim each, or raise max_chars]\n");
                break;
            }
            sb.Append('\n');
            if (o.Error is not null) sb.Append("error: ").Append(o.Error).Append('\n');
            else { AppendRecord(sb, o, cap); if (conflictTree) AppendConflictTree(sb, svc, o, fields, cap); }
            rendered++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- housecarl_cross_plugin_query ---------------------------------------------------------------
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, bool conflictTree, int maxChars)
    {
        if (q.Error is not null) return "error: " + q.Error;
        int cap = Cap(maxChars);
        bool detail = (fields is { Count: > 0 }) || conflictTree;          // expand matches, vs. one-line summaries
        var sb = new StringBuilder();
        sb.Append("cross_plugin_query: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.Capped) sb.Append(" (showing first ").Append(q.Keys.Count).Append("; raise limit= or narrow to see more)");
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= Q3 accounting (wrong-path/no-value surface)
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // unscannable-record Q3 accounting (Mutagen-unparseable content)

        int rendered = 0;
        for (int i = 0; i < q.Keys.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(q.Keys.Count)
                  .Append(" returned matches before hitting max_chars=").Append(cap)
                  .Append("; lower limit=, drop fields=/conflict_tree, or raise max_chars]\n");
                break;
            }
            var fk = q.Keys[i];
            if (detail)
            {
                var o = svc.ResolveRead(fk, q.Sources is { } src ? src[i] : null, fields, conflictTree);   // display the body the scan filtered: scoped plugin under plugins=, else winner
                sb.Append('\n');
                if (o.Error is not null) sb.Append(fk).Append(": error: ").Append(o.Error).Append('\n');
                else { AppendRecord(sb, o, cap); if (conflictTree) AppendConflictTree(sb, svc, o, fields, cap); }
            }
            else
            {
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummary(fk);   // lazy fill for conflicts-only
                sb.Append("  ").Append(m.FormKey);
                if (m.Error is not null) sb.Append("  error=").Append(m.Error).Append('\n');
                else sb.Append("  type=").Append(m.Type).Append("  editorid=").Append(m.EditorId ?? "<none>")
                       .Append("  winner=").Append(m.Winner).Append("  override_depth=").Append(m.OverrideDepth).Append('\n');
            }
            rendered++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- shared building blocks ---------------------------------------------------------------------

    static void AppendRecord(StringBuilder sb, ReadOutcome o, int cap)
    {
        var r = o.Record!;
        sb.Append("type=").Append(r.Type)
          .Append("  formid=").Append(r.FormKey)
          .Append("  editorid=").Append(r.EditorId ?? "<none>")
          .Append("  winner=").Append(o.WinnerPlugin)
          .Append("  override_depth=").Append(o.OverrideDepth).Append('\n');
        sb.Append("fields (from ").Append(o.SourcePlugin).Append("):\n");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            if (sb.Length >= cap)                                          // depth= can produce many lines — cap them (Q3)
            {
                sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                  .Append(" field lines at max_chars=").Append(cap)
                  .Append("; narrow with fields=, lower depth=, or raise max_chars]\n");
                break;
            }
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note).Append('\n');
        }
    }

    /// <summary>The conflict tree: the ordered touching-plugin list, then (when >1 plugin touches) the
    /// winner-relative field diff — each other plugin's only-the-fields-that-differ, as `path=theirs (winner X)`.
    /// Bodies come from <see cref="LoadOrderService.ResolveTree"/> (on-demand fetch, held by nothing). The diff
    /// is char-budget-bounded: over <paramref name="cap"/> it stops with an explicit notice (Q3).</summary>
    static void AppendConflictTree(StringBuilder sb, LoadOrderService svc, ReadOutcome o, IReadOnlyList<string>? fields, int cap)
    {
        var tp = o.TouchingPlugins;
        if (tp is null) return;
        sb.Append("conflict_tree: ").Append(tp.Count).Append(tp.Count == 1 ? " plugin touches" : " plugins touch")
          .Append(" this record (load order, winner last):\n");
        for (int i = 0; i < tp.Count; i++)
        {
            if (sb.Length >= cap && i < tp.Count - 1)                      // budget gone mid-list — name the rest + the winner, stop
            {
                sb.Append("  ... [").Append(tp.Count - i).Append(" more plugins omitted at max_chars=").Append(cap)
                  .Append("; winner (last in load order) = ").Append(tp[^1]).Append("; raise max_chars or narrow with fields=]\n");
                return;                                                    // and skip the diff (the all-bodies fetch too) — already over budget
            }
            sb.Append("  ").Append(i + 1).Append(". ").Append(tp[i]).Append(i == tp.Count - 1 ? "  (winner)" : "").Append('\n');
        }

        if (tp.Count <= 1) return;                                         // nothing to diff against
        var tree = svc.ResolveTree(o.FormKey, fields);                     // materialised (no live overlay) — Option B
        if (tree is null || tree.Nodes.Count <= 1) return;

        var winnerNode = tree.Winner;                                       // Nodes[^1] = highest priority = the winner

        // CONTENT diff (HCBR-2026-06-09-01): each node's DEEP read vs the winner's, compared by FieldsDiff —
        // scalar leaves exact-path, list contents order-insensitively by whole element. The old depth-1 token
        // comparison called equal-count lists with different contents "identical to winner" — an affirmative
        // false ITM that masked a real override regression. "Identical" is now only claimed when the FULL
        // modeled content compared clean; a truncated comparison says so instead (Q3).
        sb.Append("diff (field deltas vs winner ").Append(winnerNode.Plugin)
          .Append("; identical fields omitted; list contents compared order-insensitively):\n");
        for (int n = 0; n < tree.Nodes.Count - 1; n++)                      // every node except the winner
        {
            if (sb.Length >= cap)
            {
                sb.Append("  ... [truncated: response hit max_chars=").Append(cap)
                  .Append("; pass fields= to narrow the diff or raise max_chars]\n");
                return;
            }
            var node = tree.Nodes[n];
            var diff = FieldsDiff.Compare(node.Record, winnerNode.Record);
            string line = diff.Deltas.Count > 0
                ? string.Join("; ", diff.Deltas)
                  + (diff.Complete ? "" : " [comparison TRUNCATED at the expansion cap — only value mismatches observed on both sides are shown; list contents and one-sided fields were NOT compared; narrow with fields= to fully compare]")
                : diff.Complete
                    // The identity claim must not outrun the comparison's scope: a fields=-narrowed diff
                    // compared ONLY those paths, never the whole record (PR #28 review #2).
                    ? (fields is { Count: > 0 }
                        ? "(identical to winner across the requested fields, list order ignored — other fields NOT compared)"
                        : "(identical to winner — full modeled content compared, list order ignored)")
                    : "(no differences found, but the comparison was TRUNCATED at the expansion cap — NOT a verified ITM; narrow with fields= to fully compare)";
            // One node's joined deltas are unbounded (two divergent deep reads can carry thousands); slice
            // against the remaining char budget with the same explicit notice the other cuts use (Q3).
            int room = cap - sb.Length;
            if (line.Length > room)
                line = string.Concat(line.AsSpan(0, Math.Max(0, room)),
                    " ... [delta line truncated at max_chars; narrow with fields= or raise max_chars]");
            sb.Append("  ").Append(node.Plugin).Append(": ").Append(line).Append('\n');
        }
    }
}
