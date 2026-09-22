using System.Text;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The render layer the retired read tools shared; housecarl_records absorbs the tools themselves.

/// <summary>The record reads' text render: compact `key = value` output, the winner-relative conflict diff, and an always-explicit cut.</summary>
static class Wire
{
    /// <summary>The text lane's stamp on its own line: <c>epoch=</c> plus the degraded-order clause, or empty when the outcome consulted no build.</summary>
    internal static string EpochLine(OrderStamp? stamp) => stamp is null ? "" : $"\nepoch={stamp.Epoch}{stamp.Clause}";

    /// <summary>The same stamp inline in a head line, after the counts.</summary>
    internal static string EpochInline(OrderStamp? stamp) => stamp is null ? "" : $"  epoch={stamp.Epoch}{stamp.Clause}";

    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    /// <summary>Default char budget for any write-tool read-back dump, held below <see cref="DefaultMaxChars"/> by the host's per-result ceiling; pinned by <c>CompactReadbackProbe</c>'s 80k-spill guard.</summary>
    public const int ReadbackMaxChars = 24_000;

    /// <summary>How many distinct contested parent hosts a create render names before it says "and N further"; shared with the json twin, which publishes the full count beside the capped list.</summary>
    public const int ContestedHostsShown = 10;

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

    /// <summary>Parse the shared format= param: null or "text" is text, "json" is json, and anything else is a named error rather than a fall-through to text.</summary>
    public static bool WantsJson(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return true;
        error = $"error: format='{format}' is not recognized — use 'text' (the default) or 'json'.";
        return false;
    }

    /// <summary>The read surface's refusal prefix, defined once so the text and json lanes agree where the sentence starts.</summary>
    internal const string RefusalPrefix = "error: ";

    /// <summary>The one whole-call refusal render for the read surface: text unchanged, json stripped of the prefix; contract in docs/architecture/records-tool-front.md.</summary>
    internal static string Refuse(bool json, string message, OrderStamp? epoch = null)
    {
        if (!json) return message;
        var bare = message.StartsWith(RefusalPrefix, StringComparison.Ordinal)
            ? message[RefusalPrefix.Length..]
            : message;
        return JsonWire.RenderError(bare, epoch);
    }

    /// <summary>The scan lane's format vocabulary — the one lane with a third format, the columnar <c>dense</c> render.</summary>
    internal enum QueryFormat { Text, Json, Dense }

    /// <summary>Parse the scan lane's <c>format=</c>: text (default), json or dense; anything else is a named refusal listing all three.</summary>
    internal static QueryFormat CrossQueryFormat(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Text;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Json;
        if (f.Equals("dense", StringComparison.OrdinalIgnoreCase)) return QueryFormat.Dense;
        error = $"error: format='{format}' is not recognized — use 'text' (the default), 'json', or 'dense'.";
        return QueryFormat.Text;
    }

    // ---- the identity form ----
    /// <summary>Render the bulk name-resolution result: one identity line per input FormID, or a per-item <c>error=</c> for a bad or absent one.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    /// <param name="header">The caller's own header line, written INSIDE the budget.</param>
    /// <param name="bodyCost">What resolving these FormIDs cost, over the ids that RESOLVED; contract in docs/architecture/records-tool-front.md.</param>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch, SpillState? spill, out bool truncated,
                                       string? header = null, (int RowsRead, long Millis)? bodyCost = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        if (header is not null) sb.Append(header).Append('\n');
        sb.Append("resolve: ").Append(rows.Count).Append(rows.Count == 1 ? " formid" : " formids")
          .Append(Wire.EpochInline(epoch)).Append('\n');
        string Notice(int r) => "... [truncated: rendered " + r + " of " + rows.Count +
                                " at max_chars=" + cap + "; request fewer formids or raise max_chars]\n";
        var spillText = SpillText(spill);
        int budget = cap - spillText.Length - Notice(rows.Count).Length - (bodyCost is null ? 0 : RenderBudget.AccountingReserve);
        for (int i = 0; i < rows.Count && !(spill?.ManifestOnly ?? false); i++)
        {
            int mark = sb.Length;
            var r = rows[i];
            sb.Append("  ").Append(r.Token);
            if (r.Resolved)
            {
                sb.Append("  type=").Append(r.Type).Append("  editorid=").Append(r.EditorId ?? "<none>");
                if (!string.IsNullOrEmpty(r.Name)) sb.Append("  name=\"").Append(r.Name).Append('"');
                sb.Append("  winner=").Append(r.Winner);
            }
            else sb.Append("  error=").Append(r.Error ?? "not present in the active order");
            sb.Append('\n');
            if (sb.Length > budget)
            {
                sb.Length = mark;
                truncated = true;
                sb.Append(Notice(i));
                break;
            }
        }
        // What resolving these FormIDs cost — the count is the LIST's, not this window's.
        if (bodyCost is { } bc) sb.Append(RenderBudget.BodiesLine(bc.RowsRead, bc.Millis));
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>The spill block as a string, so its room can be charged before the rows are laid; empty when this call spills nothing.</summary>
    internal static string SpillText(SpillState? spill)
    {
        if (spill is null) return "";
        var sb = new StringBuilder();
        Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString();
    }

    /// <summary>The requested-but-empty types as ONE line, bounded by <paramref name="room"/>, past which the names stop and the line says how many it did not print.</summary>
    internal static string EmptyGroupsLine(IReadOnlyList<string> names, int room)
    {
        if (names.Count == 0) return "";
        const string lead = "no records: ";
        var sb = new StringBuilder(lead);
        int printed = 0;
        foreach (var n in names)
        {
            var piece = (printed == 0 ? "" : ", ") + n;
            // The tail has to fit too, so the line can always say what it left out.
            if (printed > 0 && sb.Length + piece.Length + (", and " + (names.Count - printed) + " more").Length > room) break;
            sb.Append(piece);
            printed++;
        }
        if (printed < names.Count) sb.Append(", and ").Append(names.Count - printed).Append(" more");
        return sb.Append('\n').ToString();
    }

    /// <summary>Whether this response has earned the owned-child clause, and over which fields; contract in docs/architecture/records-tool-front.md.</summary>
    internal sealed class ChildNotes
    {
        // One set per TIER: an assembled union and an index-only annotation earn different clauses.
        readonly SortedSet<string> _unioned = new(StringComparer.Ordinal);
        readonly SortedSet<string> _indexOnly = new(StringComparer.Ordinal);
        int _mayState;

        /// <summary>The clauses this response may still state, reserved from here on; <paramref name="tiers"/> is how many tiers the record about to render can annotate in.</summary>
        public void May(int tiers = 1) => _mayState = Math.Max(_mayState, tiers);

        /// <summary>An annotated field line just went into the medium: the clause is now stated, over this field, in that line's own tier.</summary>
        public void Emitted(string field, bool unioned)
        {
            (unioned ? _unioned : _indexOnly).Add(field);
            May(Tiers);
        }

        /// <summary>The chars to hold back from <c>max_chars</c> for the clauses this response may still state.</summary>
        public int Reserve => ReadSentences.ClauseReserve(_mayState);

        /// <summary>What this response had earned before a row was written, so a row taken back out takes its clause with it.</summary>
        public (int May, string[] Unioned, string[] IndexOnly) Mark() => (_mayState, _unioned.ToArray(), _indexOnly.ToArray());

        /// <inheritdoc cref="Mark"/>
        public void Restore((int May, string[] Unioned, string[] IndexOnly) mark)
        {
            _mayState = mark.May;
            _unioned.Clear(); _indexOnly.Clear();
            foreach (var f in mark.Unioned) _unioned.Add(f);
            foreach (var f in mark.IndexOnly) _indexOnly.Add(f);
        }

        internal IReadOnlyCollection<string> UnionedFields => _unioned;

        internal IReadOnlyCollection<string> IndexOnlyFields => _indexOnly;

        int Tiers => (_unioned.Count > 0 ? 1 : 0) + (_indexOnly.Count > 0 ? 1 : 0);
    }

    /// <summary>The owned-child clause, stated once per response after the body, naming the fields it was earned over and no position.</summary>
    internal static void AppendOwnedChildNotes(StringBuilder sb, ChildNotes n)
    {
        foreach (var clause in ReadSentences.OwnedChildClauses(n.UnionedFields, n.IndexOnlyFields))
            sb.Append('\n').Append(clause).Append('\n');
    }

    /// <summary>Render one record, keeping the index-only annotation the service put on the outcome; the precise tier lives on the tree form.</summary>
    static void AppendRecordBlock(StringBuilder sb, ReadOutcome o, RenderCap cap, ChildNotes notes, LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        // Reserve the clause this record could earn before its fields render, in both tiers where it can annotate in both. Only a record that can annotate pays.
        if (o.OwnedChildFields is { Count: > 0 } ann)
            notes.May((ann.Values.Any(v => v is not null) ? 1 : 0) + (ann.Values.Any(v => v is null) ? 1 : 0));
        // The reserve comes off the budget, never off the max_chars a cut notice quotes.
        AppendRecord(sb, o, cap, notes.Reserve, notes, lv);
    }

    // ---- many records ----
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    /// <summary><paramref name="levers"/> is the caller's own parameter vocabulary for the remedy sentences below; omitted means the legacy spelling.</summary>
    /// <param name="bodyCost">What reading these bodies cost, counted over the BODIES READ; contract in docs/architecture/records-tool-front.md.</param>
    /// <param name="header">The caller's own header line, written INSIDE the budget.</param>
    /// <param name="matches">Parallel to <paramref name="outcomes"/>: which multi-target references= target(s) each row hit, or null when the selection was not one.</param>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars,
                                     SpillState? spill, out bool truncated, LeverNames? levers = null,
                                     (int RowsRead, long Millis)? bodyCost = null, string? header = null,
                                     IReadOnlyList<string?>? matches = null)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        int cap = Cap(maxChars);
        var notes = new ChildNotes();   // accumulated over the rows actually rendered, not over the input list
        var sb = new StringBuilder();
        if (header is not null) sb.Append(header).Append('\n');
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record" : " records");
        // One captured build for the whole batch, so the epoch is response-level: the first non-null, since a malformed-FormID row consulted no view.
        if (outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp is { } stamp) sb.Append(Wire.EpochInline(stamp));
        sb.Append('\n');
        int rendered = 0;
        int costReserve = bodyCost is null ? 0 : RenderBudget.AccountingReserve;
        string Notice(int r) =>
            "... [truncated: rendered " + r + " of " + outcomes.Count + " records before hitting max_chars=" + cap +
            "; " + lv.BatchSelection + (lv.HasFieldSelector ? $", pass {lv.Fields} to slim each," : ",") +
            " or raise max_chars]\n";
        var spillText = SpillText(spill);
        int budget = cap - costReserve - spillText.Length - Notice(outcomes.Count).Length;
        for (int i = 0; i < outcomes.Count; i++)
        {
            if (spill?.ManifestOnly ?? false) break;   // to_file: only the manifest renders — the rows are the FILE
            var o = outcomes[i];
            int mark = sb.Length;
            var noteMark = notes.Mark();
            sb.Append('\n');
            // The scan render's exact line, so a reverse lookup un-merges the same way whichever form answered it.
            if (matches is { } mt && i < mt.Count && mt[i] is { } hit)
                sb.Append("  ").Append(FormIdToken.Of(o.FormKey)).Append("  matches=").Append(hit).Append('\n');
            if (o.Error is not null) sb.Append("error: ").Append(o.Error).Append('\n');
            else AppendRecordBlock(sb, o, new RenderCap(cap, budget), notes, lv);
            // The clause this record earned goes back with the record when the record does.
            if (sb.Length > budget - notes.Reserve)
            {
                sb.Length = mark;
                notes.Restore(noteMark);
                truncated = true;
                sb.Append(Notice(rendered));
                break;
            }
            rendered++;
        }
        AppendOwnedChildNotes(sb, notes);
        // What reading these bodies cost, stated whatever the transport: the rows were resolved before this render.
        if (bodyCost is { } bc) sb.Append(RenderBudget.BodiesLine(bc.RowsRead, bc.Millis));
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    // ---- the scan lane ----

    // The dense render's container hint is LeverNames.DenseContainerHint: dense refuses depth>1, so the hint names the format hop alongside the knob.

    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames = false, bool winnerFields = false, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The artifact-aware render: <paramref name="spill"/> carries the call's artifact disposition, and <paramref name="truncated"/> hands the row-level cut back to the tool layer, which triggers the auto-spill.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames, bool winnerFields, int depth, SpillState? spill, out bool truncated,
                                          LeverNames? levers = null, CancellationToken ct = default, string? header = null,
                                          int rowLimit = 0)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        string head = header is null ? "" : header + "\n";
        // A refusal made after the build was captured is stamped with the epoch; a pre-capture one renders bare.
        if (q.Error is not null) return head + "error: " + q.Error + Wire.EpochLine(q.Stamp);
        int cap = Cap(maxChars);
        if (q.Groups is not null) return RenderCrossQueryGroups(q, cap, rowLimit, spill, out truncated, head);   // group_by= → a count table, not per-match lines
        bool detail = fields is { Count: > 0 };          // expand matches, vs. one-line summaries
        // One session, one link cache, one chunked body prefetch for every rendered match, and the row loop's cancellation check.
        using var reader = detail
            ? new ScanDetailReader(svc, q, fields, depth, resolveNames, winnerFields, lv.ContainerHint, null, ct)
            : null;
        bool anyScoped = JsonWire.AnyScopedFieldRow(q, fields);   // the shared test: a plugins= scope shows a plugin's OWN body
        var sb = new StringBuilder();
        sb.Append(head);
        sb.Append("scan: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.ScopeLabel is not null) sb.Append(" DEFINED IN ").Append(q.ScopeLabel);   // explicit scope — NOT the 'touches' default
        if (q.Offset > 0)                                                              // name the window, and the next offset while paging
        {
            // "no records match at any offset" is a claim over the whole order, so a scan that lost a plugin names the plugins it could not read instead.
            if (q.Total == 0 && q.UnreadPlugins.Count > 0)
                sb.Append(" (offset=").Append(q.Offset).Append(" had nothing to skip — no records match in the plugins this scan could read, and it could not read ")
                  .Append(string.Join(", ", q.UnreadPlugins)).Append("; the note below names why)");
            else if (q.Total == 0) sb.Append(" (offset=").Append(q.Offset).Append(" had nothing to skip — NO records match at any offset; check the filter, not the paging)");
            else if (q.Keys.Count == 0) sb.Append(" (offset=").Append(q.Offset).Append(" skipped past the last match — nothing to show; lower offset=)");
            else
            {
                sb.Append(" (showing matches ").Append(q.Offset + 1).Append('–').Append(q.Offset + q.Keys.Count);
                if (q.Capped) sb.Append("; continue with offset=").Append(q.Offset + q.Keys.Count);
                sb.Append(')');
            }
        }
        else if (q.Capped) sb.Append(" (showing first ").Append(q.Keys.Count).Append("; raise limit=, page with offset=, or narrow to see more)");
        if (q.Stamp is not null) sb.Append(Wire.EpochInline(q.Stamp));   // offset= windows tile ONLY within one epoch
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= accounting: wrong path / no value
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // records Mutagen could not parse
        if (q.WhereSourceNote is not null) sb.Append(q.WhereSourceNote).Append('\n');   // where_source=winner is redundant under a type=-only scope
        if (q.ReverseIndexNote is not null) sb.Append(q.ReverseIndexNote).Append('\n');   // the reverse-reference index's build cost and per-plugin freshness key
        // Under a plugins= scope the per-match fields are the SCOPED plugin's own values, not the live winner's, said once from the helper the json and dense renders share.
        if (anyScoped) sb.Append("note: ").Append(JsonWire.ScopedFieldsNote(winnerFields, q.WhereWinner, lv)).Append('\n');

        int rendered = 0;
        var renderClock = System.Diagnostics.Stopwatch.StartNew();
        var notes = new ChildNotes();   // accumulated over the rows actually rendered
        int costReserve = detail ? RenderBudget.AccountingReserve : 0;
        // The slim-down clause is only true for a call that passed something to slim WITH (LeverNames.SlimScan); a call that passed nothing gets the two levers that are real on it.
        string Notice(int r) =>
            "... [truncated: rendered " + r + " of " + q.Keys.Count + " returned matches before hitting max_chars=" +
            cap + (lv.SlimScan is null
                       ? "; lower limit= or raise max_chars]\n"
                       : $"; lower limit=, drop {lv.SlimScan}, or raise max_chars]\n");
        var spillText = SpillText(spill);
        int budget = cap - costReserve - spillText.Length - Notice(q.Keys.Count).Length;
        for (int i = 0; i < q.Keys.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: only the manifest renders — the rows are the FILE
        {
            int mark = sb.Length;
            var noteMark = notes.Mark();
            var fk = q.Keys[i];
            string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;   // multi-target references= un-merge
            if (detail)
            {
                // winner_fields= reads the load-order winner's body whatever the scan scope; otherwise the body the scan filtered, pinned to the scan's own build.
                var o = reader!.Row(i);   // a collapsed cell names the caller's own expansion knob
                sb.Append('\n');
                if (matches is not null) sb.Append("  ").Append(fk).Append("  matches=").Append(matches).Append('\n');
                if (o.Error is not null) sb.Append(fk).Append(": error: ").Append(o.Error).Append('\n');
                else AppendRecordBlock(sb, o, new RenderCap(cap, budget), notes, lv);   // o carries the scan's pin
            }
            else
            {
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // lazy fill for conflicts-only, pinned to the scan's build
                sb.Append("  ").Append(FormIdToken.Of(m.FormKey));
                if (m.Error is not null) sb.Append("  error=").Append(m.Error).Append('\n');
                else
                {
                    AppendRuntime(sb, m.RuntimeFormId, m.RuntimeFormIdNote);
                    sb.Append("  type=").Append(m.Type).Append("  editorid=").Append(m.EditorId ?? "<none>")
                      .Append("  winner=").Append(m.Winner).Append("  override_depth=").Append(m.OverrideDepth);
                    if (matches is not null) sb.Append("  matches=").Append(matches);
                    sb.Append('\n');
                }
            }
            // The clause this row earned goes back with the row when the row does.
            if (sb.Length > budget - notes.Reserve)
            {
                sb.Length = mark;
                notes.Restore(noteMark);
                truncated = true;
                sb.Append(Notice(rendered));
                break;
            }
            rendered++;
        }
        renderClock.Stop();
        AppendOwnedChildNotes(sb, notes);
        // What the RENDER cost, stated in-band. A to_file= call renders its rows into the ARTIFACT, so its cost comes off the write rather than off the loop above.
        if (detail && spill?.ManifestOnly == true && spill.Spill is { RenderMs: { } artifactMs } a)
            sb.Append(RenderBudget.AccountingLine(a.Manifest.RowCount, artifactMs));
        else if (detail && !(spill?.ManifestOnly ?? false))
            sb.Append(RenderBudget.AccountingLine(rendered, renderClock.ElapsedMilliseconds));
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    /// <summary>Render a <c>group_by=</c> aggregation: a header naming the key, the true total and the group count, then one row per group, with the where= and unscannable notes surviving; only the rendering is capped, so the total stays exact.</summary>
    /// <param name="rowLimit">the caller's limit= as the TABLE's row cap (0 = uncapped): a count table caps with
    /// limit= and does not page (#810), and whichever knob stopped the rows is the one the closing marker names.</param>
    static string RenderCrossQueryGroups(CrossQueryOutcome q, int cap, int rowLimit, SpillState? spill, out bool truncated, string head = "")
    {
        truncated = false;
        var all = q.Groups!;
        // A zero group is one the call ASKED for that matched nothing, stated on its own line rather than as a row that sorts last and is what a cap discards first.
        var empties = all.Where(g => g.Count == 0).Select(g => g.Key).ToList();
        var groups = empties.Count == 0 ? all : all.Where(g => g.Count > 0).ToList();
        var sb = new StringBuilder();
        sb.Append(head);
        sb.Append("scan: grouped by ").Append(q.GroupBy).Append(" — ")
          .Append(q.Total).Append(q.Total == 1 ? " match" : " matches")
          .Append(" across ").Append(groups.Count).Append(groups.Count == 1 ? " group" : " groups");
        if (q.ScopeLabel is not null) sb.Append(" (DEFINED IN ").Append(q.ScopeLabel).Append(')');
        if (q.Stamp is not null) sb.Append(Wire.EpochInline(q.Stamp));
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');
        if (q.ReverseIndexNote is not null) sb.Append(q.ReverseIndexNote).Append('\n');
        string Notice(int r) => "... [truncated: rendered " + r + " of " + groups.Count +
                                " groups before hitting max_chars=" + cap + "; raise max_chars — the total above is exact]\n";
        string LimitNotice(int r) => "... [" + (groups.Count - r) + " more group(s) — raise limit= to see them; the " +
                                     "total above is exact]\n";
        var spillText = SpillText(spill);
        var emptyLine = Wire.EmptyGroupsLine(empties, Math.Max(cap / 2, 120));
        // Either marker can close the table, so the room held back is the wider of the two.
        int budget = cap - spillText.Length - emptyLine.Length
                   - Math.Max(Notice(groups.Count).Length, LimitNotice(0).Length);
        int shown = rowLimit > 0 ? Math.Min(rowLimit, groups.Count) : groups.Count;
        for (int i = 0; i < groups.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: rows live in the file
        {
            // limit= caps the table's rows; the count above stays the whole tally. It does NOT set `truncated`,
            // which is the ceiling auto-spill's trigger: a limit cut is the caller capping the table on purpose.
            if (i >= shown)
            {
                sb.Append(LimitNotice(i));
                break;
            }
            var row = "  " + groups[i].Key + " = " + groups[i].Count + "\n";
            if (sb.Length + row.Length > budget)
            {
                truncated = true;
                sb.Append(Notice(i));
                break;
            }
            sb.Append(row);
        }
        sb.Append(emptyLine);
        sb.Append(spillText);
        return RenderCap.Settle(sb.ToString().TrimEnd('\n'), cap);
    }

    // ---- the chain form ----
    /// <summary>Render the effect chain: a header resolving the MGEF with its confirmed MagicEffect type, then carrier rows grouped by record type; a valid but unused MGEF renders a "none" line rather than an error.</summary>
    /// <param name="carrierBound">How the CALLING tool spells the per-seed carrier bound this render may have hit. REQUIRED, with no default: a default here would guess a lever name on the caller's behalf.</param>
    public static string RenderEffectChain(EffectChainResult r, int maxChars, string carrierBound)
        => RenderEffectChain(r, new RenderCap(Cap(maxChars), Cap(maxChars)), 0, carrierBound, out _);

    /// <summary>The bounded form: <paramref name="room"/> carries the caller's max_chars and what its own tail has left, and <paramref name="used"/> what the caller has already written, since this render builds its own buffer.</summary>
    /// <param name="cut">true when carrier rows were held back inside this render's own buffer, where the caller cannot measure it; it is what drives the caller's truncation flag and its spill.</param>
    internal static string RenderEffectChain(EffectChainResult r, RenderCap room, int used, string carrierBound,
                                             out bool cut)
    {
        cut = false;
        if (r.Error is not null) return "error: " + r.Error + Wire.EpochLine(r.Stamp);
        int cap = room.Cap;
        var sb = new StringBuilder();
        sb.Append("chain for ").Append(FormIdToken.Of(r.Mgef)).Append(" (").Append(r.MgefEditorId).Append(", MagicEffect): ")
          .Append(r.Total).Append(r.Total == 1 ? " carrier row" : " carrier rows");
        if (r.Capped) sb.Append(" (showing first ").Append(r.Rows.Count).Append("; raise ").Append(carrierBound).Append(" or narrow to see more)");
        if (r.Stamp is not null) sb.Append(Wire.EpochInline(r.Stamp));
        sb.Append('\n');
        // The whole-order negative is only the scan's to make when it read the whole order; with a plugin left out it states the scope it covered instead.
        if (r.Total == 0 && r.UnreadPlugins.Count == 0)
            sb.Append("  none — ").Append(r.MgefEditorId)
              .Append(" is a valid MagicEffect but is applied by no SPEL/ENCH/ALCH/SCRL/INGR in the active order.\n");
        else if (r.Total == 0)
            sb.Append("  none in the plugins this scan could read — ").Append(r.MgefEditorId)
              .Append(" is a valid MagicEffect, and no SPEL/ENCH/ALCH/SCRL/INGR of the plugins read applies it; ")
              .Append(string.Join(", ", r.UnreadPlugins)).Append(" could not be read, so this is not the whole order.\n");
        if (r.ScanNote is not null) sb.Append(r.ScanNote).Append('\n');

        int rendered = 0;
        bool truncated = false;
        // The cut notice is charged at its widest spelling before the row it follows; docs/architecture/render-budget.md.
        string Notice(int n) =>
            "  ... [truncated: rendered " + n + " of " + r.Rows.Count + " rows before hitting max_chars=" + cap +
            "; lower limit= or raise max_chars]\n";
        int noticeRoom = Notice(r.Rows.Count).Length;
        // Group rows by carrier type, ordinal for stability, so a multi-type result reads grouped.
        foreach (var grp in r.Rows.GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (truncated) break;
            sb.Append(grp.Key).Append(" (").Append(grp.Count()).Append("):\n");
            foreach (var row in grp)
            {
                // Composed before it is priced, so the notice lands inside the budget rather than past the row that crossed.
                string line = "  " + FormIdToken.Of(row.Carrier)
                              + "  " + (row.EditorId ?? "<none>")
                              + "  winner=" + row.Winner
                              + "  mag=" + row.Magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture)
                              + "  area=" + row.Area
                              + "  dur=" + row.Duration
                              + "  [effect " + (row.EffectIndex + 1) + "/" + row.EffectCount + "]\n";
                if (used + sb.Length + line.Length + noticeRoom > room.Budget)
                {
                    sb.Append(Notice(rendered));
                    truncated = true;
                    cut = true;
                    break;
                }
                sb.Append(line);
                rendered++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- the errors family ----
    /// <summary>The errors family's own head: what it swept and what it found, above the first thing a budget can refuse and below the response title, which belongs to the caller.</summary>
    static void AppendErrorsHead(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(didDangling ? $"{r.TotalDangling} dangling ref(s)" : "dangling refs NOT CHECKED (findings= excluded 'dangling')").Append(" · ")
          .Append(didMasters ? $"{r.TotalMissingMasters} missing master(s)" : "missing masters NOT CHECKED (findings= excluded 'missing_masters')").Append(" · ")
          .Append(didDangling ? $"{r.TotalUnscannableRecords} unscannable record(s)" : "unscannable records NOT COUNTED (the record walk was skipped)");
        // The excluded roster is on the result, so the head line counts what it is holding.
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(OrderDegraded.Clause(r.ExcludedPlugins.Count)).Append(EpochOffOrderQualifier(r.OffOrderScanned));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append(string.Format(ReadSentences.SweepOffOrderScanned, string.Join(", ", off),
                                    ReadSentences.SweepOffOrderErrorsCoverage)).Append('\n');
        AppendBaselineSplit(sb, r, acct);   // how much of the dangling total is vanilla baseline
    }

    /// <summary>The errors family's two counts_only axes, built once and read by both the render and the demand pass.</summary>
    internal static HistogramAxis[] ErrorsAxes(ErrorCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByTarget, r.Histogram,
                          "dangling ref(s) by TARGET plugin (the plugin the broken refs point INTO)",
                          "counts_only=true — totals above are exact; no per-plugin listing was built.",
                          "no dangling histogram, by target or by source — the link walk was not run (findings= excluded 'dangling')."),
        new HistogramAxis(SweepSubject.HistogramBySource, r.DanglingBySource,
                          "dangling ref(s) by SOURCE plugin (the plugin the broken refs come FROM)"),
    };

    /// <summary>The scripts family's one counts_only axis. Same reason.</summary>
    internal static HistogramAxis[] ScriptsAxes(ScriptCheckResult r) => new[]
    {
        new HistogramAxis(SweepSubject.HistogramByProperty, r.Histogram, "unbound properties by NAME",
                          "counts_only=true — totals above are exact; no per-record listing was built.",
                          "no unbound histogram — findings= excluded both unbound classes, so nothing was tallied."),
    };

    /// <summary>One plugin section's fixed part, everything besides its dangling entries, as one unit emitted whole or not at all; shared so the demand pass and the write read one source.</summary>
    internal static string ComposeErrorSection(PluginErrors p)
    {
        var fixedPart = new StringBuilder("\n[ERROR] ").Append(p.Plugin).Append('\n');
        if (p.ScanError is not null)
            fixedPart.Append("  scan error: ").Append(p.ScanError).Append('\n');
        // The two shortfalls are named apart because their remedies differ, install versus enable; null means the split was not made, so the combined wording stands in.
        if (p.MissingMasters.Count > 0 && p.InstalledButInactiveMasters is { } inactive)
        {
            var notInstalled = p.MissingMasters.Where(m => !inactive.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (notInstalled.Count > 0)
                fixedPart.Append("  missing master(s) NOT installed anywhere in the MO2 install: ").Append(string.Join(", ", notInstalled))
                         .Append("   [install them — this plugin's refs into them dangle until you do]\n");
            if (inactive.Count > 0)
                fixedPart.Append("  missing master(s) installed but NOT ACTIVE in the load order (in a disabled mod, or unchecked): ")
                         .Append(string.Join(", ", inactive))
                         .Append("   [enable them — this plugin's refs into them dangle until you do]\n");
        }
        else if (p.MissingMasters.Count > 0)
            fixedPart.Append("  missing master(s): ").Append(string.Join(", ", p.MissingMasters))
                     .Append("   [declared as a dependency but not present in the active order — install/enable it, or this plugin's refs into it dangle]\n");
        if (p.UnscannableRecords > 0)
        {
            fixedPart.Append("  ").Append(p.UnscannableRecords).Append(" record(s) could not be scanned (Mutagen could not parse their content)");
            if (p.UnscannableSamples.Count > 0) fixedPart.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
            fixedPart.Append('\n');
        }
        if (p.Dangling.Count > 0)
            fixedPart.Append("  dangling reference(s) (").Append(p.Dangling.Count).Append("):\n");
        return fixedPart.ToString();
    }

    /// <summary>One dangling entry, the one thing this family's accounting states a unit at a time; see <see cref="ComposeErrorSection"/>.</summary>
    internal static string ComposeDanglingLine(DanglingRef d)
    {
            return "    " + d.Source + " (" + d.SourceType
                     + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                     + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
    }

    /// <summary>One unread-plugin row, the <c>counts_only</c> lane's honesty layer, shared for the same reason.</summary>
    internal static string ComposeUnreadRow(PluginErrors p)
    {
        var line = new StringBuilder("\n[UNREAD] ").Append(p.Plugin).Append(": ");
        if (p.ScanError is not null) line.Append(p.ScanError).Append(' ');
        if (p.UnscannableRecords > 0)
        {
            line.Append(p.UnscannableRecords).Append(" record(s) could not be scanned");
            if (p.UnscannableSamples.Count > 0) line.Append(": ").Append(string.Join("; ", p.UnscannableSamples));
        }
        line.Append('\n');
        return line.ToString();
    }

    /// <summary>One histogram row; the first row of an axis carries the axis head, so the demand pass must ask with the row index the write will use.</summary>
    internal static string ComposeHistogramRow(HistogramAxis axis, SweepCount row, bool first)
    {
        var line = "  " + row.Count.ToString().PadLeft(6) + "  " + row.Key + "\n";
        return first ? axis.Head + line : line;
    }

    /// <summary>The errors family's body: everything a cap can refuse and nothing else, the roster, accounting and boundary belonging to the response.</summary>
    static void AppendErrorsSection(StringBuilder sb, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            // Both axes are handed over together so both are reserved before either renders; the source axis carries no note and no not-computed line.
            AppendHistograms(sb, body, histogramLimit,
                ErrorsAxes(r));
            AppendScanErrorTail(sb, body, r.Reports);
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // A section is emitted whole or not at all, except for its dangling entries, which the accounting states one at a time; composing the fixed part first leaves only those two droppable units.
            var section = ComposeErrorSection(p);
            if (!body.Emit(SweepSubject.PluginSections, section.Length, () => sb.Append(section))) break;

            foreach (var d in p.Dangling)
            {
                var line = ComposeDanglingLine(d);
                if (!body.Emit(SweepSubject.DanglingEntries, line.Length, () => sb.Append(line), p.Plugin)) break;
            }
        }
    }

    /// <summary>The baseline split: how much of the dangling total came from the base-game masters and how much from the rest, naming the subset actually swept (<see cref="ErrorCheckResult.BaseMastersSwept"/>) rather than Mutagen's whole <c>BaseMasters</c> set.</summary>
    static void AppendBaselineSplit(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        if (!r.Classes.HasFlag(ErrorFindingClass.Dangling) || r.BaseMastersSwept is not { Count: > 0 } swept) return;
        sb.Append("baseline: ").Append(r.BaselineDangling).Append(" of ").Append(r.TotalDangling)
          .Append(" dangling ref(s) come from the base-game master(s) this sweep covered (").Append(string.Join(", ", swept))
          .Append(") — vanilla leftovers rather than anything this load order introduced; ")
          .Append(r.TotalDangling - r.BaselineDangling).Append(" come from the rest of the swept scope.").Append('\n');
        // Only stated where the phase order decided something and both groups exist; on a base-masters-only scope there is no "every other plugin" for the clause to order against.
        if (acct.OmittedByBudget > 0 && r.BaselineDangling > 0 && r.NonBaseInScope)
            sb.Append("  the listing budget (limit=) is spent on every other plugin BEFORE those, so baseline findings ")
              .Append("cannot crowd the rest out of the list; the sections below stay in load order.").Append('\n');
    }

    // ---- shared sweep-render pieces ----
    /// <summary>The epoch stamp's coverage qualifier: off-order file content is outside the fingerprint, so equal epochs across such sweeps do not mean equal inputs.</summary>
    static string EpochOffOrderQualifier(IReadOnlyList<string>? offOrderScanned) =>
        offOrderScanned is { Count: > 0 } ? " (indexed plugins only — off-order file content is outside the fingerprint)" : "";

    /// <summary>Reserve every axis's fixed part — its unconditional lines and its closing disclosure — then render them all, in two passes so no axis finds a sibling has spent its room.</summary>
    internal static void AppendHistogramAxes(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
        => AppendHistograms(sb, body, rowLimit, axes);

    static void AppendHistograms(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
    {
        foreach (var a in axes) body.Reserve(a.Subject, a.TextFixed);
        foreach (var a in axes) AppendHistogram(sb, body, rowLimit, a);
    }

    /// <summary>Render one <c>counts_only=</c> histogram axis, capped at <paramref name="rowLimit"/> with the true distinct-key count always stated; a null histogram reads as "not requested" and an empty one as "found nothing".</summary>
    /// <param name="body">The one bounded emission path: the axis's rows go through it and can be refused, while its own statement about itself is written out of reserved room.</param>
    static void AppendHistogram(StringBuilder sb, BoundedBody body, int rowLimit, HistogramAxis axis)
    {
        // The note and the not-computed line are fixed text no budget may drop, written through `body` so the fixed part is measured.
        if (axis.NoteLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NoteLine));
        if (axis.Rows is not { } rows)
        {
            if (axis.NotComputedLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NotComputedLine));
            body.Release(axis.Subject);
            return;
        }
        // The title rides the empty case too, and "nothing to tally" is this axis's entire answer, so it CLOSES with it rather than emitting it.
        if (rows.Count == 0) { body.Close(axis.Subject, () => sb.Append(axis.EmptyLine)); return; }
        var head = axis.Head;
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var unit = ComposeHistogramRow(axis, row, shown == 0);
            // A row pays for itself only: the closing line's cost is already held back.
            if (!body.Emit(axis.Subject, unit.Length, () => sb.Append(unit))) { cutByBudget = true; break; }
            shown++;
        }
        // The closing disclosure, from one computation the json lane reads too, naming the knob that stopped THIS axis. An axis that rendered every row says nothing and gives its room back; one admitted no rows still prints its head and count.
        if (HistogramCut.For(rows.Count, shown, cutByBudget) is not { } cut) { body.Release(axis.Subject); return; }
        if (shown == 0) body.Close(axis.Subject, () => sb.Append(head).Append(cut.Line));
        else body.Close(axis.Subject, () => sb.Append(cut.Line));
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse, shared by the listing and <c>counts_only=</c> paths; the accounting states the row count from the same registrations.</summary>
    static void AppendExcludedPlugins(StringBuilder sb, BoundedBody body, IReadOnlyDictionary<string, string> excluded)
    {
        for (int i = 0; i < excluded.Count; i++)
        {
            var unit = ComposeExcludedRow(excluded, i);
            if (!body.Emit(SweepSubject.ExcludedRows, unit.Length, () => sb.Append(unit))) return;
        }
    }

    /// <summary>One roster row, composed by the helper the demand pass measures; the head rides the first row, so the list is whole or absent.</summary>
    internal static string ComposeExcludedRow(IReadOnlyDictionary<string, string> excluded, int index)
    {
        const string head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        var kv = excluded.ElementAt(index);
        return (index == 0 ? head : "") + "  " + kv.Key + ": " + kv.Value + "\n";
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries only what could not be read, emitted verbatim so the census still names it.</summary>
    static void AppendScanErrorTail(StringBuilder sb, BoundedBody body, IReadOnlyList<PluginErrors> reports)
    {
        foreach (var p in reports)
        {
            var row = ComposeUnreadRow(p);
            if (!body.Emit(SweepSubject.UnreadRows, row.Length, () => sb.Append(row))) return;
        }
    }

    // ---- the merged, multi-family check response ----
    /// <summary>The merged sweep: one header, one section per selected family with its own accounting, one boundary block, and the excluded-plugin roster once; the body budget is divided rather than spent in series, per docs/architecture/render-budget.md.</summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the allocation it built so a test can assert what each subject was given and spent; an internal seam.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // What this response actually did, composed once and handed to everything below, the skeleton pass included.
        var o = CheckOutcome.For(s);
        if (o.Error is not null)
            // The fold frames a refusal as much as a finding: the seeds were looked for in the projection.
            return (s.Dialogue?.Folded is { } errFrame ? errFrame : "")
                   + "error: " + o.Error + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "")
                   + (o.OrderExcluded.Count > 0 ? "\n" + OrderDegraded.Sentence(o.OrderExcluded) : "");
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // The reserve: one accounting line and one boundary line per family, held back before anything renders.
        int reserve = 0;
        for (int i = 0; i < accts.Count; i++)
            reserve += accts[i].TextAccountingReserve
                     + accts[i].Boundary.Length
                     + string.Format(ReadSentences.SweepBoundaryLabelFor,
                                     SweepFamilySelection.Token(sections[i])).Length + BoundaryWrap;
        int budget = Math.Max(0, cap - reserve);

        // What each subject wants, measured before anything is written, so the allocation can water-fill over it.
        var demand = SweepDemand.ForText(o, budget, histogramLimit);
        // And the WHOLE fixed part the response owes whatever the budget says, measured by composing it.
        var skeleton = new StringBuilder();
        var skeletonAccts = o.Accountings(cap);
        var skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => skeleton.Length);
        Compose(skeleton, o, sections, skeletonAccts, skeletonBody, histogramLimit, cap);
        int fixedPart = skeleton.Length - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;

        var sb = new StringBuilder();
        var body = BoundedBody.ForFamilies(accts, budget, () => sb.Length, o.Plan(),
                                           demand.Demand, demand.Reserved + fixedPart, o.ResponseSubjects,
                                           demand.Reserved);
        measured = body;
        Compose(sb, o, sections, accts, body, histogramLimit, cap);

        // The overrun question, asked of the finished response, which the notice is part of — so it settles to a fixed point; docs/architecture/render-budget.md.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // The first accounting states it once: the sentence is about the whole response rather than any family.
        var overrun = accts.Count > 0 ? accts[0] : null;
        if (overrun is null) return response;
        // How many times this response prints the cap back, counted in the response itself.
        int sites = overrun.CapPrintsIn(response);
        if (overrun.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = overrun.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = overrun.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
    }

    /// <summary>The whole merged response bar its overrun notice, composed through one <paramref name="body"/>, run twice per render: once with a <see cref="BoundedBody.Skeleton"/> to leave the fixed part to be measured, and once for real.</summary>
    static void Compose(StringBuilder sb, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit, int cap)
    {
        var s = o.Sweep;
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n');
        // The scope sentence, above everything a budget can refuse: which families answered, which refused, and which registered ones were never asked.
        sb.Append(o.ScopeSentence()).Append('\n');
        // A short order is a response-level fact, stated here once rather than left to whichever families ran.
        if (o.OrderExcluded.Count > 0)
            sb.Append(OrderDegraded.Sentence(o.OrderExcluded)).Append('\n');
        // WHICH loose roots the asset build could not read, at the response root because ONE build feeds every
        // family that hedges on it; bounded and counted by the shared renderer.
        sb.Append(BatchRender.RootFailureLines(o.RootFailures, cap));

        // The excluded-plugin roster goes ABOVE the family sections, where each family's accounting can see its rows, and is a response-level participant in the allocation taking its share of the row budget.
        AppendExcludedPlugins(sb, body, o.ExcludedPlugins);

        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            sb.Append('\n').Append(string.Format(ReadSentences.SweepFamilySectionHead,
                                                 SweepFamilySelection.Token(f), SweepFamilySelection.Title(f)))
              .Append('\n');
            // A family that refused fills its OWN section with the refusal, never the whole response.
            if (o.Refusal(f) is { } refusal)
            {
                // A refused dialogue family still says what world it refused in, so the frame rides above it.
                if (f == SweepFamily.Dialogue && s.Dialogue?.Folded is { } foldedFrame) sb.Append(foldedFrame);
                sb.Append(refusal).Append('\n');
            }
            else if (f == SweepFamily.Errors)
            {
                AppendErrorsHead(sb, s.Errors!, accts[i]);
                AppendErrorsSection(sb, s.Errors!, body, histogramLimit);
            }
            else if (f == SweepFamily.Scripts)
            {
                AppendScriptsHead(sb, s.Scripts!);
                AppendScriptsSection(sb, s.Scripts!, body, histogramLimit);
            }
            else if (f == SweepFamily.Facegen)
            {
                FaceGenSweepRender.AppendHead(sb, s.FaceGen!);
                FaceGenSweepRender.AppendSection(sb, s.FaceGen!, body, histogramLimit);
            }
            else
            {
                // The dialogue family is SEEDED, so the scope parameters beside it did not narrow it and its head has to say so.
                DialogueSweepRender.AppendHead(sb, o);
                DialogueSweepRender.AppendSection(sb, o, body);
            }
            // This family's accounting, under this family's section, out of the room held for it.
            if (accts[i].TextLine() is { } line)
                body.Reserved(() => sb.Append('\n').Append(line).Append('\n'));
        }

        // One boundary block, one line per family that ran, written through the reserve its room came from.
        for (int i = 0; i < sections.Count; i++)
        {
            int at = i;
            body.Reserved(() => sb.Append('\n')
                                  .Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                        SweepFamilySelection.Token(sections[at])))
                                  .Append(accts[at].Boundary).Append('\n'));
        }
    }

    /// <summary>The headroom a boundary line's wrapping newlines are held back with, per block.</summary>
    internal const int BoundaryWrap = 32;

    // ---- the scripts family ----
    /// <summary>The scripts family's own head: what it swept and what it found, every count stating its own scope so no number reads as a wider claim than it is.</summary>
    static void AppendScriptsHead(StringBuilder sb, ScriptCheckResult r)
    {
        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          // A class the caller excluded reads as NOT CHECKED, never as 0.
          .Append(ReadSentences.ScriptUnboundTotal(r, didObject, didScalar))
          .Append(" · ")
          .Append(ReadSentences.ScriptNullTotal(r, didNull))
          .Append(" · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(OrderDegraded.Clause(r.ExcludedPlugins.Count)).Append(EpochOffOrderQualifier(r.OffOrderScanned));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append(string.Format(ReadSentences.SweepOffOrderScanned, string.Join(", ", off),
                                    ReadSentences.SweepOffOrderScriptsCoverage)).Append('\n');
        if (r.UnverifiableCollapsed > 0)
            sb.Append(string.Format(ReadSentences.SweepScriptUnverifiableCollapsed, r.UnverifiableCollapsed)).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA or a loose mod folder failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");
    }

    /// <summary>The scripts family's body: everything a cap can refuse, and like the errors family's no roster, accounting or boundary.</summary>
    static void AppendScriptsSection(StringBuilder sb, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            AppendHistograms(sb, body, histogramLimit,
                ScriptsAxes(r));
            // Plugins whose record enumeration faulted, its own subject so a cut response says how many it named.
            foreach (var rec in r.Reports)
            {
                if (rec.ScanError is null) continue;
                var row = ComposeScriptRecordUnit(rec);
                if (!body.Emit(SweepSubject.ScriptScanRows, row.Length, () => sb.Append(row))) break;
            }
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo unbound script properties found in the scanned scope.\n");

        foreach (var rec in r.Reports)
        {
            // A record section is emitted whole or not at all — the errors family's rule in this family's units.
            var section = ComposeScriptRecordUnit(rec);
            if (!body.Emit(SweepSubject.ScriptRecords, section.Length, () => sb.Append(section))) break;
        }
    }

    /// <summary>One record's whole section, composed before it is offered to the budget, as the errors family's plugin sections are.</summary>
    internal static string ComposeScriptRecordUnit(RecordScriptFindings rec)
    {
        if (rec.ScanError is not null)
            return "\n[SCAN ERROR] " + rec.Plugin + ": " + rec.ScanError + "\n";

        var sb = new StringBuilder();
        sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
          .Append(FormIdToken.Of(rec.Record)).Append(" (").Append(rec.RecordType);
        if (!string.IsNullOrEmpty(rec.EditorId)) sb.Append(" '").Append(rec.EditorId).Append('\'');
        sb.Append(") in ").Append(rec.Plugin).Append('\n');

        // Unbound findings, object/form types first — those are the silent None — then uninitialized scalars.
        foreach (var u in rec.Unbound.OrderByDescending(u => u.IsObjectType))
        {
            sb.Append("  ").Append(u.IsObjectType ? "! " : "· ")
              .Append(u.PropertyName).Append(" (").Append(u.PexTypeName).Append(") on script ").Append(u.Script);
            if (!string.Equals(u.DeclaringScript, u.Script, StringComparison.OrdinalIgnoreCase))
                sb.Append(" [declared in ").Append(u.DeclaringScript).Append(']');
            sb.Append(u.IsObjectType
                ? " — declared but NOT bound → None at runtime (HIGH: object/form type — the silent no-op)\n"
                : " — declared but NOT bound → defaults to 0/false/\"\" (scalar, no baked default)\n");
        }
        if (rec.NullObjects.Count > 0)
            sb.Append("  bound-but-null object propert").Append(rec.NullObjects.Count == 1 ? "y: " : "ies: ")
              .Append(string.Join(", ", rec.NullObjects.Select(n => $"{n.PropertyName} ({n.Script})")))
              .Append("   [advisory — a None link; sometimes intentional, filled at runtime]\n");
        foreach (var uv in rec.Unverifiable)
            sb.Append("  could not verify script ").Append(uv.Script).Append(": ").Append(uv.Reason).Append('\n');
        return sb.ToString();
    }

    // ---- shared building blocks ---------------------------------------------------------------------

    /// <summary>The runtime-FormID token every text lane prints beside a record's identity: the eight-hex form, the parenthetical sentence saying why there is none, or nothing at all.</summary>
    internal static void AppendRuntime(StringBuilder sb, string? runtime, string? note)
    {
        if (runtime is not null) sb.Append("  runtime=").Append(runtime);
        else if (note is not null) sb.Append("  runtime=(").Append(note).Append(')');
    }

    /// <summary><paramref name="notes"/> registers the owned-child clause as each annotated field line is written, or null on the lanes that render a record outside an annotated response.</summary>
    /// <param name="reserve">Chars held back for the response-level clause this render may still state; the notice still quotes the caller's own <paramref name="cap"/>.</param>
    static void AppendRecord(StringBuilder sb, ReadOutcome o, RenderCap cap, int reserve = 0, ChildNotes? notes = null,
                             LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        var r = o.Record!;
        sb.Append("type=").Append(r.Type)
          .Append("  formid=").Append(r.FormKey);
        AppendRuntime(sb, o.RuntimeFormId, o.RuntimeFormIdNote);   // what the console and the logs print, or why there is none
        sb.Append("  editorid=").Append(r.EditorId ?? "<none>")
          .Append("  winner=").Append(o.WinnerPlugin)
          .Append("  override_depth=").Append(o.OverrideDepth).Append('\n');
        sb.Append("fields (from ").Append(o.SourcePlugin).Append("):\n");
        // The field loop's cut notice is charged before the first line and quotes the caller's max_chars, never the reduced budget it cuts against.
        string Cut(int i) => "  ... [truncated: showing " + i + " of " + r.Fields.Count +
                             " field lines at max_chars=" + cap.Cap +
                             (lv.HasFieldSelector ? $"; narrow with {lv.Fields}, lower " : "; lower ") +
                             lv.Depth + ", or raise max_chars]\n";
        int room = cap.Budget - reserve - Cut(r.Fields.Count).Length;
        for (int i = 0; i < r.Fields.Count; i++)
        {
            int mark = sb.Length;                                          // depth= can produce many lines — cap them
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
            if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
            if (f.Link is not null) sb.Append("   (").Append(LinkText(f.Link)).Append(')');   // resolve_names target identity, DISPLAY-ONLY — never the round-trip token
            sb.Append('\n');
            if (sb.Length > room) { sb.Length = mark; sb.Append(Cut(i)); break; }
            // The clause is earned HERE, by a line that reached the caller.
            if (notes is not null && o.OwnedChildFields is { } ann && ann.TryGetValue(f.Path, out var u))
                notes.Emitted(f.Path, u is not null);
        }
    }

    /// <summary>The resolve_names parenthetical: a FormLink token's target identity, or a named "unresolved" note for a dangling target; display only, appended after the round-trip token and never in place of it.</summary>
    internal static string LinkText(ResolvedRef r) =>
        // Unresolved has two causes and the ref itself says which.
        !r.Resolved ? (r.Winner is { } w
            ? $"unresolved: '{w}' defines this target but did not yield it on fetch"
            : "unresolved: no active plugin defines this target")
        : string.IsNullOrEmpty(r.Name) ? $"→ {r.EditorId ?? "<no editorid>"}"
        : $"→ {r.EditorId ?? "<no editorid>"} \"{r.Name}\"";

    // The delta form's own "identical across the fields read" wording lives in RecordsTools.

}
