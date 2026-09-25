using System.Text;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The render layer the retired read tools shared; housecarl_records absorbs the tools themselves.

/// <summary>The record reads' text render: compact `key = value` output, the winner-relative conflict diff, and an always-explicit cut.</summary>
static partial class Wire
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

    internal static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

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
