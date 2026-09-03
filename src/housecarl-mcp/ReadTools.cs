using System.Text;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The seven 1.x read tools this file was named for were deleted at the 1.x cut; housecarl_records absorbs
// all of them. What is left is the render layer they shared, which housecarl_records calls.

/// <summary>Compact, parseable `key = value` rendering (Q4.8 lever 1) + the winner-relative conflict diff
/// (lever 2) + response-size estimation (Q3 / Q4.9 — explicit cut, never silent). The record reads' render.</summary>
static class Wire
{
    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    /// <summary>Default char budget for ANY write-tool READ-BACK dump (HCBR-2026-06-28-01). Deliberately well BELOW
    /// <see cref="DefaultMaxChars"/> / the host's per-result token ceiling: the forced in-place verify deep-dumped
    /// every touched record and, at the 80k default, the "gracefully truncated" 80k string STILL exceeded the host
    /// limit and spilled to a file (silent, Q3-breaking). The compact default render is tiny; this bounds the opt-in
    /// full_readback=true dump, whose truncation note now actually reaches the caller. INTENTIONALLY GLOBAL (PR #127
    /// review #3): the host ceiling is a transport property, not an in-place-lane one, so this cap applies to EVERY
    /// AppendFullReadback caller — the in-place verify, forward_record, and the new-file full_readback=true dump all
    /// hit the same spill bug, and all want the same bound. A caller raises it per-call via max_chars.</summary>
    public const int ReadbackMaxChars = 24_000;

    /// <summary>How many distinct contested parent HOSTS a create render names before it says "and N further"
    /// (#300). Lives here, shared by the text render and its json twin, because the two ARE the same bound and a
    /// second literal is how they drift: the text side was capped on review [medium] and the json side was not,
    /// so a bulk_create fanning children into many contested cells wrote ~700 bytes per host AHEAD of the budgeted
    /// `created` array and truncated it at "0 of N" — the HCBR-2026-06-28-01 shape the text cap exists to prevent
    /// (PR #323 review [medium]). Both lanes still publish the FULL distinct count beside the capped list, so a
    /// cut caller knows how many it is not seeing.</summary>
    public const int ContestedHostsShown = 10;

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

    /// <summary>Parse the shared format= param (Wave 2 / P6): null/"text" ⇒ false (the default text render), "json" ⇒
    /// true, anything else ⇒ false with a NAMED error in <paramref name="error"/> (never a silent fall-through to
    /// text on a typo — Q3). Case/whitespace-insensitive.</summary>
    public static bool WantsJson(string? format, out string? error)
    {
        error = null;
        var f = format?.Trim();
        if (string.IsNullOrEmpty(f) || f.Equals("text", StringComparison.OrdinalIgnoreCase)) return false;
        if (f.Equals("json", StringComparison.OrdinalIgnoreCase)) return true;
        error = $"error: format='{format}' is not recognized — use 'text' (the default) or 'json'.";
        return false;
    }

    /// <summary>The read surface's refusal literal prefix. The text lane states a refusal as <c>"error: …"</c>;
    /// the json lane carries the same sentence in an <c>error</c> property WITHOUT the prefix (the property name
    /// already says what it is). One definition so the two lanes cannot disagree about where the sentence starts.</summary>
    internal const string RefusalPrefix = "error: ";

    /// <summary>THE ONE REFUSAL RENDER PATH for the read surface. A tool body states its refusal once, in the text
    /// spelling, and this decides the shape the caller actually asked for.
    ///
    /// <para><b>Why it exists.</b> A read tool decides its transport early and then refuses in prose regardless —
    /// <c>housecarl_records</c> settles <c>json</c> at the top of its body and 60-odd later refusals returned a bare
    /// string anyway. <see cref="Guard"/> hands a body's return value straight back, so nothing downstream converted
    /// it: a caller who asked for json got a non-json body for a third of the surface's refusals. The discriminant
    /// (#403) was the smaller half of that; this is the half that decides whether a refusal is json at all.</para>
    ///
    /// <para><b>Text is byte-identical to what it was.</b> The message passed here is the sentence as authored, so
    /// the text lane returns it unchanged — this call site is not a wording change. The json lane strips
    /// <see cref="RefusalPrefix"/> and renders <c>{ok:false, error, epoch}</c> through
    /// <see cref="JsonWire.RenderError"/>, matching the shape the already-wrapped sites emit.</para>
    ///
    /// <para><b>Not for per-row failures.</b> A row that failed inside a call that succeeded is not a refusal; it
    /// keeps its per-row <c>error</c> field. This path is whole-call only.</para></summary>
    internal static string Refuse(bool json, string message, string? epoch = null)
    {
        if (!json) return message;
        var bare = message.StartsWith(RefusalPrefix, StringComparison.Ordinal)
            ? message[RefusalPrefix.Length..]
            : message;
        return JsonWire.RenderError(bare, epoch);
    }

    /// <summary>The cross_plugin_query format vocabulary — the one tool with a third format (<c>dense</c>, the #223
    /// columnar render). Every other tool stays on the two-value <see cref="WantsJson"/>.</summary>
    internal enum QueryFormat { Text, Json, Dense }

    /// <summary>Parse cross_plugin_query's <c>format=</c>: text (default) / json / dense; anything else is a named
    /// refusal (Q3) listing all three.</summary>
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

    // ---- the identity form (was housecarl_resolve) --------------------------------------------------
    /// <summary>Render the bulk name-resolution result (P3; the identity form): one compact identity line per input
    /// FormID (type/editorid/name/winner), or <c>error=</c> for a bad/absent input (per-item, the batch survives — Q3).
    /// Budget-bounded with the same explicit cut the other reads use.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch, SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("resolve: ").Append(rows.Count).Append(rows.Count == 1 ? " formid" : " formids")
          .Append("  epoch=").Append(epoch).Append('\n');
        for (int i = 0; i < rows.Count && !(spill?.ManifestOnly ?? false); i++)
        {
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(rows.Count)
                  .Append(" at max_chars=").Append(cap).Append("; request fewer formids or raise max_chars]\n");
                break;
            }
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
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Whether this response has earned the #342 clause, and over WHICH fields — accumulated at EMISSION, as
    /// each annotated field line is written, never where the annotation was decided.
    ///
    /// <para>That distinction is the whole fix. A response can annotate a field and then not show it: the field
    /// loop hits <c>max_chars</c> before reaching it, the json fields array truncates, a <c>to_file</c> spill sends
    /// the rows to a file. A clause committed when the annotation was DECIDED then describes a field the caller
    /// cannot see. Registering at emission makes that unrepresentable — no field line, no clause.</para>
    ///
    /// <para><see cref="May"/> is the other half, and it runs the other way: it is set BEFORE a record's fields
    /// render, so the clause that record could still earn is RESERVED out of the caller's budget rather than
    /// appended past it. Reserve decides the clause FITS; emission decides it is STATED.</para></summary>
    internal sealed class ChildNotes
    {
        readonly SortedSet<string> _notRead = new(StringComparer.Ordinal);
        bool _mayNotRead;

        /// <summary>The clause this response may still state — reserved from here on.</summary>
        public void May() => _mayNotRead = true;

        /// <summary>An annotated field line just went into the medium: the clause is now STATED, over this
        /// field.</summary>
        public void Emitted(string field)
        {
            May();
            _notRead.Add(field);
        }

        /// <summary>The chars to hold back from <c>max_chars</c> for the clause this response may still state.</summary>
        public int Reserve => ReadSentences.ClauseReserve(_mayNotRead);

        internal IReadOnlyCollection<string> Fields() => _notRead;
    }

    /// <summary>The #342 clause, stated ONCE per response after the body — never per field, which cost ~275
    /// identical chars on every annotated row and pushed real rows out of a bulk response's budget. It NAMES the
    /// fields it was earned over, so it claims nothing about where in the response they are.</summary>
    internal static void AppendOwnedChildNotes(StringBuilder sb, ChildNotes n)
    {
        var fields = n.Fields();
        if (fields.Count == 0) return;
        sb.Append('\n').Append(ReadSentences.NotReadClause(fields)).Append('\n');
    }

    /// <summary>Render one record. The outcome keeps the cheap index-only #342 annotation the service already put
    /// on it — the precise tier lives on <c>records project={"form":"tree"}</c> now (#485), and this lane renders
    /// no tree at all: the render, and the <c>conflict_tree=true</c> flag that reached it, went with the last
    /// caller that could set it (#486).</summary>
    static void AppendRecordBlock(StringBuilder sb, ReadOutcome o, int cap, ChildNotes notes, LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        // Hold back the clause this record could earn BEFORE its fields render, so an annotated response answers
        // inside the caller's max_chars instead of overrunning it (finding 6). Only a record that CAN annotate
        // pays: one with no child-bearing field reserves nothing.
        if (o.OwnedChildFields is { Count: > 0 }) notes.May();
        // The RESERVE is held back from the budget, never from the number the render QUOTES: a cut that told the
        // caller "at max_chars=1124" when they passed 2000 would be a wrong sentence of its own.
        AppendRecord(sb, o, cap, notes.Reserve, notes, lv);
    }

    // ---- many records (was housecarl_batch_record_detail) -------------------------------------------
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    /// <summary><paramref name="levers"/> is the CALLER's lever vocabulary for the remedy sentences below —
    /// this renderer is shared by both tool generations and they spell the selector differently (#439).
    /// Omitted means the 1.x spelling, so a 1.x call site renders byte-identically.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars,
                                     SpillState? spill, out bool truncated, LeverNames? levers = null)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        int cap = Cap(maxChars);
        var notes = new ChildNotes();   // #342: accumulated over the rows actually rendered, not over the input list
        var sb = new StringBuilder();
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record" : " records");
        // The whole batch reads ONE captured build (ResolveBatch), so its epoch is response-level accounting —
        // first non-null (a malformed-FormID row never consulted a view and carries none).
        if (outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch is { } epoch) sb.Append("  epoch=").Append(epoch);
        sb.Append('\n');
        int rendered = 0;
        foreach (var o in outcomes)
        {
            if (spill?.ManifestOnly ?? false) break;   // to_file: only the manifest renders — the rows are the FILE
            if (sb.Length >= cap - notes.Reserve)      // the clauses this response has already earned are SPOKEN FOR
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(outcomes.Count)
                  .Append(" records before hitting max_chars=").Append(cap)
                  .Append("; ").Append(lv.BatchSelection).Append(", pass ").Append(lv.Fields).Append(" to slim each, or raise max_chars]\n");
                break;
            }
            sb.Append('\n');
            if (o.Error is not null) sb.Append("error: ").Append(o.Error).Append('\n');
            else AppendRecordBlock(sb, o, cap, notes, lv);
            rendered++;
        }
        AppendOwnedChildNotes(sb, notes);
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    // ---- the scan lane (was housecarl_cross_plugin_query) -------------------------------------------

    // The dense render's container hint moved to LeverNames.DenseContainerHint (#439): dense refuses depth>1, so
    // the hint names the format hop with the knob — and the knob is spelled per caller like every other lever.

    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames = false, bool winnerFields = false, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The §2.1.1-aware render: <paramref name="spill"/> carries the call's artifact disposition (the
    /// spilled marker / to_file manifest-only mode / failed-spill warning), and <paramref name="truncated"/> hands
    /// the row-level max_chars cut back to the tool layer — the auto-spill trigger.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames, bool winnerFields, int depth, SpillState? spill, out bool truncated,
                                          LeverNames? levers = null)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        // A post-capture refusal is stamped (PR #305 contract) — e.g. the artifact epoch-mismatch refusal, which
        // consulted the build to compare against it. Pre-capture validation refusals carry null and render bare.
        if (q.Error is not null) return "error: " + q.Error + (q.Epoch is not null ? $"\nepoch={q.Epoch}" : "");
        int cap = Cap(maxChars);
        if (q.Groups is not null) return RenderCrossQueryGroups(q, cap, spill, out truncated);   // group_by= → a count table, not per-match lines
        bool detail = fields is { Count: > 0 };          // expand matches, vs. one-line summaries
        var linkMemo = resolveNames && detail ? new Dictionary<FormKey, ResolvedRef>() : null;   // P7: one link cache across all rendered matches
        bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // P5: plugins= scope shows a plugin's OWN body
        var sb = new StringBuilder();
        sb.Append("scan: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.ScopeLabel is not null) sb.Append(" DEFINED IN ").Append(q.ScopeLabel);   // P1: explicit scope — NOT the 'touches' default
        if (q.Offset > 0)                                                              // #223 pagination — name the window, and the next offset while paging
        {
            if (q.Total == 0) sb.Append(" (offset=").Append(q.Offset).Append(" had nothing to skip — NO records match at any offset; check the filter, not the paging)");
            else if (q.Keys.Count == 0) sb.Append(" (offset=").Append(q.Offset).Append(" skipped past the last match — nothing to show; lower offset=)");
            else
            {
                sb.Append(" (showing matches ").Append(q.Offset + 1).Append('–').Append(q.Offset + q.Keys.Count);
                if (q.Capped) sb.Append("; continue with offset=").Append(q.Offset + q.Keys.Count);
                sb.Append(')');
            }
        }
        else if (q.Capped) sb.Append(" (showing first ").Append(q.Keys.Count).Append("; raise limit=, page with offset=, or narrow to see more)");
        if (q.Epoch is not null) sb.Append("  epoch=").Append(q.Epoch);   // §2.1.1: offset= windows tile ONLY within one epoch
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= Q3 accounting (wrong-path/no-value surface)
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // unscannable-record Q3 accounting (Mutagen-unparseable content)
        if (q.WhereSourceNote is not null) sb.Append(q.WhereSourceNote).Append('\n');   // #233: where_source=winner redundancy under a type=-only scope
        // P5: under a plugins= scope the per-match fields are the SCOPED plugin's OWN values, not the live winner's —
        // the silent-wrong trap (a defining esp's AR 38 vs the winner's live AR 200). Name it loud, once (Q3). The
        // helper is 4-way over (winner_fields=, where_source=) so the note never claims a scoped-body MATCH the
        // where_source=winner scan didn't make (D2 no-drift). Shared with the json/dense renders — one source of truth.
        if (anyScoped) sb.Append("note: ").Append(JsonWire.ScopedFieldsNote(winnerFields, q.WhereWinner, lv)).Append('\n');

        int rendered = 0;
        var notes = new ChildNotes();   // #342: accumulated over the rows actually rendered
        for (int i = 0; i < q.Keys.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: only the manifest renders — the rows are the FILE
        {
            if (sb.Length >= cap - notes.Reserve)      // the clauses this response has already earned are SPOKEN FOR
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(rendered).Append(" of ").Append(q.Keys.Count)
                  .Append(" returned matches before hitting max_chars=").Append(cap)
                  // The slim-down clause is only true for a call that passed something to slim WITH — see
                  // LeverNames.SlimScan. A call that passed nothing gets the two levers that are real on it.
                  .Append(lv.SlimScan is null
                              ? "; lower limit= or raise max_chars]\n"
                              : $"; lower limit=, drop {lv.SlimScan}, or raise max_chars]\n");
                break;
            }
            var fk = q.Keys[i];
            string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;   // multi-target references= un-merge
            if (detail)
            {
                // winner_fields=: read the load-order WINNER's body (source=null) regardless of scan scope; else the
                // body the scan filtered (scoped plugin under plugins=, else winner) — so display never contradicts filter.
                // PINNED to the scan's build (ResolveReadOn): the header's epoch names ONE build, so every fill
                // must read it (PR #305 review).
                var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, depth, resolveNames: resolveNames, linkMemo: linkMemo,
                                          containerHint: lv.ContainerHint);   // a collapsed cell names the caller's own expansion knob (#439)
                sb.Append('\n');
                if (matches is not null) sb.Append("  ").Append(fk).Append("  matches=").Append(matches).Append('\n');
                if (o.Error is not null) sb.Append(fk).Append(": error: ").Append(o.Error).Append('\n');
                else AppendRecordBlock(sb, o, cap, notes, lv);   // o carries the scan's pin
            }
            else
            {
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // lazy fill for conflicts-only, pinned to the scan's build
                sb.Append("  ").Append(m.FormKey);
                if (m.Error is not null) sb.Append("  error=").Append(m.Error).Append('\n');
                else
                {
                    sb.Append("  type=").Append(m.Type).Append("  editorid=").Append(m.EditorId ?? "<none>")
                      .Append("  winner=").Append(m.Winner).Append("  override_depth=").Append(m.OverrideDepth);
                    if (matches is not null) sb.Append("  matches=").Append(matches);
                    sb.Append('\n');
                }
            }
            rendered++;
        }
        AppendOwnedChildNotes(sb, notes);
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Render a cross_plugin_query <c>group_by=</c> aggregation: a header naming the key + true total + group
    /// count, then one "  &lt;key&gt; = &lt;count&gt;" row per group (already sorted desc by the core). Q3 accounting
    /// (where= / unscannable notes) survives the aggregation. Over max_chars it stops with the explicit truncation
    /// notice — the count is exact even when the row LIST is clipped (aggregation isn't limit-capped; only rendering is).</summary>
    static string RenderCrossQueryGroups(CrossQueryOutcome q, int cap, SpillState? spill, out bool truncated)
    {
        truncated = false;
        var groups = q.Groups!;
        var sb = new StringBuilder();
        sb.Append("scan: grouped by ").Append(q.GroupBy).Append(" — ")
          .Append(q.Total).Append(q.Total == 1 ? " match" : " matches")
          .Append(" across ").Append(groups.Count).Append(groups.Count == 1 ? " group" : " groups");
        if (q.ScopeLabel is not null) sb.Append(" (DEFINED IN ").Append(q.ScopeLabel).Append(')');
        if (q.Epoch is not null) sb.Append("  epoch=").Append(q.Epoch);
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');
        for (int i = 0; i < groups.Count && !(spill?.ManifestOnly ?? false); i++)   // to_file: rows live in the file
        {
            if (sb.Length >= cap)
            {
                truncated = true;
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(groups.Count)
                  .Append(" groups before hitting max_chars=").Append(cap).Append("; raise max_chars — the total above is exact]\n");
                break;
            }
            sb.Append("  ").Append(groups[i].Key).Append(" = ").Append(groups[i].Count).Append('\n');
        }
        if (spill is not null) Artifacts.AppendSpillStateText(sb, spill);
        return sb.ToString().TrimEnd('\n');
    }

    // ---- the chain form (was housecarl_effect_chain) ------------------------------------------------
    /// <summary>Render the effect chain: a header that RESOLVES the MGEF (its editorid + the confirmed MagicEffect
    /// type — the Q3 typed-match proof), then carrier rows grouped by record type. A valid-but-unused MGEF renders a
    /// clean "none" line, NOT an error (the error path is the bad/mistyped FormID, handled in core). Over max_chars it
    /// stops with the same explicit notice the other read renders use (Q3 — never silent).</summary>
    public static string RenderEffectChain(EffectChainResult r, int maxChars)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("chain for ").Append(r.Mgef).Append(" (").Append(r.MgefEditorId).Append(", MagicEffect): ")
          .Append(r.Total).Append(r.Total == 1 ? " carrier row" : " carrier rows");
        if (r.Capped) sb.Append(" (showing first ").Append(r.Rows.Count).Append("; raise limit= or narrow to see more)");
        if (r.Epoch is not null) sb.Append("  epoch=").Append(r.Epoch);
        sb.Append('\n');
        if (r.Total == 0)
            sb.Append("  none — ").Append(r.MgefEditorId)
              .Append(" is a valid MagicEffect but is applied by no SPEL/ENCH/ALCH/SCRL/INGR in the active order.\n");
        if (r.ScanNote is not null) sb.Append(r.ScanNote).Append('\n');

        int rendered = 0;
        bool truncated = false;
        // Group rows by carrier type (stable, ordinal) so a multi-type result reads grouped — like cross_plugin_query's type=.
        foreach (var grp in r.Rows.GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (truncated) break;
            sb.Append(grp.Key).Append(" (").Append(grp.Count()).Append("):\n");
            foreach (var row in grp)
            {
                if (sb.Length >= cap)
                {
                    sb.Append("  ... [truncated: rendered ").Append(rendered).Append(" of ").Append(r.Rows.Count)
                      .Append(" rows before hitting max_chars=").Append(cap).Append("; lower limit= or raise max_chars]\n");
                    truncated = true;
                    break;
                }
                sb.Append("  ").Append(row.Carrier)
                  .Append("  ").Append(row.EditorId ?? "<none>")
                  .Append("  winner=").Append(row.Winner)
                  .Append("  mag=").Append(row.Magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append("  area=").Append(row.Area)
                  .Append("  dur=").Append(row.Duration)
                  .Append("  [effect ").Append(row.EffectIndex + 1).Append('/').Append(row.EffectCount).Append("]\n");
                rendered++;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ---- the errors family (was housecarl_check_errors) ---------------------------------------------
    /// <summary>The errors family's own head: what it swept and what it found. Everything above the first thing a
    /// budget can refuse, and everything below the response's title — which is the caller's, because the merged
    /// surface titles a SECTION where the single-family tool titles the whole response.</summary>
    static void AppendErrorsHead(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(didDangling ? $"{r.TotalDangling} dangling ref(s)" : "dangling refs NOT CHECKED (findings= excluded 'dangling')").Append(" · ")
          .Append(didMasters ? $"{r.TotalMissingMasters} missing master(s)" : "missing masters NOT CHECKED (findings= excluded 'missing_masters')").Append(" · ")
          .Append(didDangling ? $"{r.TotalUnscannableRecords} unscannable record(s)" : "unscannable records NOT COUNTED (the record walk was skipped)");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(EpochOffOrderQualifier(r));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.OffOrderScanned is { Count: > 0 } off)
            sb.Append("swept OFF-ORDER (on disk, not in the active load order): ").Append(string.Join(", ", off))
              .Append("   [the file's own records; links resolved against the active order + the file's own definitions]\n");
        AppendBaselineSplit(sb, r, acct);   // #344 — how much of the dangling total is vanilla baseline
    }

    /// <summary>The errors family's two counts_only axes, built ONCE and read by both the render and the
    /// DEMAND pass — a second construction here would be a second Head, and the demand would be measuring a
    /// row that is not the row written.</summary>
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

    /// <summary>ONE plugin section's fixed part — everything the section says besides its dangling entries. It
    /// is a UNIT: emitted whole or not at all, because a scan error, the missing masters it declares and its
    /// unscannable-record count are each a finding in their own right, and no accounting subject covers half of
    /// one.
    ///
    /// <para><b>Extracted so the DEMAND pass and the WRITE read ONE source.</b> The allocation water-fills over
    /// MEASURED demand (<see cref="BodyAllocation"/>), and a demand taken from a second spelling of this composer
    /// would be free to drift from what is written — the divergence ALLOCATION-EQUALS-SPEND makes a stop rather
    /// than a slack factor.</para></summary>
    internal static string ComposeErrorSection(PluginErrors p)
    {
        var fixedPart = new StringBuilder("\n[ERROR] ").Append(p.Plugin).Append('\n');
        if (p.ScanError is not null)
            fixedPart.Append("  scan error: ").Append(p.ScanError).Append('\n');
        // The two shortfalls are named apart because their REMEDIES differ, and a caller told "install/enable it"
        // has to go find out which (PR #148's distinction, carried onto the surface that still reports the finding).
        // Null means the split was not made — say what was said before rather than assert the uninstalled case.
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

    /// <summary>ONE dangling entry — the one thing this family's accounting states a unit at a time. Shared by the
    /// demand pass and the write; see <see cref="ComposeErrorSection"/>.</summary>
    internal static string ComposeDanglingLine(DanglingRef d)
    {
            return "    " + d.Source + " (" + d.SourceType
                     + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                     + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
            // Registered where the line LANDS — the accounting counts the response, and the by-source roster is
            // tallied off the same registration, so the count and the roster cannot disagree.
    }

    /// <summary>ONE unread-plugin row, the <c>counts_only</c> lane's honesty layer. Shared, for the same reason.</summary>
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

    /// <summary>ONE histogram row. The FIRST row of an axis carries the axis head, so its width differs from the
    /// rest — the demand pass asks with the same row index the write will use.</summary>
    internal static string ComposeHistogramRow(HistogramAxis axis, SweepCount row, bool first)
    {
        var line = "  " + row.Count.ToString().PadLeft(6) + "  " + row.Key + "\n";
        return first ? axis.Head + line : line;
    }

    /// <summary>The errors family's BODY — everything a cap can refuse, and nothing else. It writes no roster, no
    /// accounting and no boundary: those are the response's, and a section renderer that wrote them could not be
    /// called twice in one response.</summary>
    static void AppendErrorsSection(StringBuilder sb, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            // Both axes are handed over TOGETHER so both are reserved before either renders. #344's SOURCE axis —
            // which plugin the broken refs come FROM — carries no note and no not-computed line of its own: both
            // would repeat what the TARGET axis directly above has just said.
            AppendHistograms(sb, body, histogramLimit,
                ErrorsAxes(r));
            AppendScanErrorTail(sb, body, r.Reports);
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // A SECTION IS EMITTED WHOLE, OR NOT AT ALL — except for its dangling entries, which are the one thing
            // the accounting can account for one at a time. Everything else a section says (the scan error, the
            // missing masters, the unscannable-record count) is a finding in its own right, and a per-line "append
            // if it fits" dropped those silently: they are not entries, so no accounting subject covered them, and
            // the tool's own parameter text promises master-table findings are "always listed in full". Composing
            // the fixed part first makes the only droppable units the two the accounting already states — a whole
            // section, or an entry.
            var section = ComposeErrorSection(p);
            if (!body.Emit(SweepSubject.PluginSections, section.Length, () => sb.Append(section))) break;

            foreach (var d in p.Dangling)
            {
                var line = ComposeDanglingLine(d);
                if (!body.Emit(SweepSubject.DanglingEntries, line.Length, () => sb.Append(line), p.Plugin)) break;
            }
        }
    }

    /// <summary>#344 — the baseline split: how much of the dangling total came from the base-game masters, and how
    /// much from everything else. The sweep's one number ("4996 dangling refs") cannot be acted on without it: the
    /// vanilla leftovers are permanent, present in every install, and nothing a load order can fix. The line NAMES the
    /// plugins it counted as baseline rather than saying "base-game", because that word is the whole claim — houseCARL's
    /// own load-order status groups Creation Club plugins WITH the base masters as "implicit", and the set here is
    /// Mutagen's <c>BaseMasters</c>, which does not contain them.
    /// <para>Creation Club and <c>_ResourcePack.esl</c> are DELIBERATELY not baseline (ruled 2026-08-18). Baseline is
    /// Mutagen's set exactly, which is what keeps it by-construction rather than a list kept here; "keep CC out of my
    /// listing" is a caller's choice, and it routes to the <c>exclude=</c> pole on the successor <c>check</c> surface
    /// (#344's second stage), not to a classification baked into the sweep.</para>
    /// <para>Printed only when a base master was actually SWEPT, and it names THAT subset
    /// (<see cref="ErrorCheckResult.BaseMastersSwept"/>) rather than the whole definition: "0 of 12 are vanilla" over
    /// a scope that never included vanilla is a true sentence that teaches something false — and so is a "3 of 3"
    /// naming five plugins when the sweep opened one (round-1 review, found independently by two reviewers).</para></summary>
    static void AppendBaselineSplit(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        if (!r.Classes.HasFlag(ErrorFindingClass.Dangling) || r.BaseMastersSwept is not { Count: > 0 } swept) return;
        sb.Append("baseline: ").Append(r.BaselineDangling).Append(" of ").Append(r.TotalDangling)
          .Append(" dangling ref(s) come from the base-game master(s) this sweep covered (").Append(string.Join(", ", swept))
          .Append(") — vanilla leftovers rather than anything this load order introduced; ")
          .Append(r.TotalDangling - r.BaselineDangling).Append(" come from the rest of the swept scope.").Append('\n');
        // The phase-order sentence only where the phase order DECIDED something. Nothing was crowded out of a sweep
        // that listed everything it found, and saying so there explains a mechanism the reader did not just hit.
        // (Capped is never set under counts_only — that mode lists nothing — so it carries the mode gate too.)
        // "spent on every other plugin BEFORE those" is a statement about an ordering between two groups, so it needs
        // both groups to exist: on a sweep scoped to base masters alone there is no "every other plugin", and what the
        // budget dropped is the caller's own requested findings (round-2 review). NonBaseInScope is that fact, computed
        // in the sweep from the resolved targets — comparing PluginsScanned against the swept-base count subtracts two
        // numbers that measure different things and prints this sentence over a base-only scope whenever they diverge
        // (Aaron's PR #360 review: a record scope that filters a base master out, or a repeated plugins= name).
        if (acct.OmittedByBudget > 0 && r.BaselineDangling > 0 && r.NonBaseInScope)
            sb.Append("  the listing budget (limit=) is spent on every other plugin BEFORE those, so baseline findings ")
              .Append("cannot crowd the rest out of the list; the sections below stay in load order.").Append('\n');
    }

    // ---- shared sweep-render pieces (#282) ----------------------------------------------------------
    /// <summary>The epoch stamp's coverage qualifier (PR #305 review): when off-order files were swept beside the
    /// index, the fingerprint does NOT cover their content (they are located on disk, outside the fingerprinted
    /// order) — say so next to the stamp, so equal epochs are never read as "same inputs" across such sweeps. The
    /// fact itself is data (the OffOrderScanned list / the json <c>off_order_scanned</c> array); this qualifier is
    /// its header-line rendering.</summary>
    static string EpochOffOrderQualifier(ErrorCheckResult r) =>
        r.OffOrderScanned is { Count: > 0 } ? " (indexed plugins only — off-order file content is outside the fingerprint)" : "";

    /// <summary>validate_scripts has no off-order lane — its stamp always covers everything it swept. The overload
    /// exists so the two sweep headers stay textually identical (one shape, no drift).</summary>
    static string EpochOffOrderQualifier(ScriptCheckResult r) => "";

    /// <summary>Reserve EVERY axis's FIXED PART — its unconditional lines and its closing disclosure — then render
    /// them all. The two passes are the point: an axis that reserved its own room only when its turn came would
    /// find a sibling had already spent the budget, which is the silence the reserve exists to remove. Reserving is
    /// therefore not something each axis does for itself.</summary>
    static void AppendHistograms(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
    {
        foreach (var a in axes) body.Reserve(a.Subject, a.TextFixed);
        foreach (var a in axes) AppendHistogram(sb, body, rowLimit, a);
    }

    /// <summary>Render ONE <c>counts_only=</c> histogram axis, capped at <paramref name="rowLimit"/> with the true
    /// distinct-key count always stated. A null histogram means the mode was not requested; an EMPTY one means the
    /// sweep genuinely found nothing, and the two read differently (Q3).</summary>
    /// <param name="body">the ONE bounded emission path. The axis's ROWS go through it and can be refused; the
    /// axis's own statement about itself cannot — that room was reserved out of <c>max_chars</c> before the body
    /// rendered, and <see cref="BoundedBody.Close"/> spends it without asking. Charging the statement to the same
    /// budget as the rows is what let a whole axis disappear with nothing saying so (#392): the pressure that cut
    /// the rows cut the sentence reporting the cut.</param>
    static void AppendHistogram(StringBuilder sb, BoundedBody body, int rowLimit, HistogramAxis axis)
    {
        // The note and the not-computed line are fixed text that does not grow with the findings, and the second is
        // this axis's whole answer — a "the walk was not run" that a budget could drop would be the silence the
        // sentence exists to break. They are part of the response's fixed part, deliberately — and they go through
        // `body` so that fixed part is MEASURED. Appended straight to the builder they were invisible to the
        // overrun notice, which then explained a cap too small for them as a body unit overshooting (#391 review).
        if (axis.NoteLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NoteLine));
        if (axis.Rows is not { } rows)
        {
            if (axis.NotComputedLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NotComputedLine));
            body.Release(axis.Subject);
            return;
        }
        // The title rides the empty case too: two axes that both came back empty rendered as two identical untitled
        // sentences, with no way to tell which was which — or that a second axis existed at all (round-1 review).
        // "Nothing to tally" is this axis's entire answer, so it closes with it rather than emitting it: an answer a
        // tight cap can refuse leaves the caller unable to tell "no findings" from "this axis was never computed".
        if (rows.Count == 0) { body.Close(axis.Subject, () => sb.Append(axis.EmptyLine)); return; }
        var head = axis.Head;
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var unit = ComposeHistogramRow(axis, row, shown == 0);
            // The row pays for itself only. What the closing line costs is already held back, against this axis's
            // own tests as much as its siblings' — a subject may spend the budget on its rows, never on its own
            // disclosure.
            if (!body.Emit(axis.Subject, unit.Length, () => sb.Append(unit))) { cutByBudget = true; break; }
            shown++;
        }
        // The closing disclosure, from ONE computation the json lane reads too. The remedy names the knob that
        // STOPPED THIS AXIS: "raise limit=" on rows the response had no room for is a knob that moves nothing, and so
        // is "raise max_chars=" on rows the row budget refused. An axis that rendered every row it had says nothing,
        // and gives its reserved room back for the subjects rendering after it.
        //
        // An axis the budget admitted NO rows of still says so, head and count together: an axis that exists and
        // renders nothing at all is the same silent cut, one level down.
        if (HistogramCut.For(rows.Count, shown, cutByBudget) is not { } cut) { body.Release(axis.Subject); return; }
        if (shown == 0) body.Close(axis.Subject, () => sb.Append(head).Append(cut.Line));
        else body.Close(axis.Subject, () => sb.Append(cut.Line));
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse. Shared by the listing and
    /// <c>counts_only=</c> paths — counts_only used to return before it, leaving the header's bare count with no way to
    /// learn WHICH plugin went unchecked without re-running (PR #288 review, finding 5).
    /// <para>How many rows it emitted is no longer returned: the accounting states that fact, from the same
    /// registrations, in both transports. The one caller that counted rows here composed a second spelling of it —
    /// and got it wrong first, claiming every plugin was unnamed on a response that had just named one.</para></summary>
    static void AppendExcludedPlugins(StringBuilder sb, BoundedBody body, IReadOnlyDictionary<string, string> excluded)
    {
        for (int i = 0; i < excluded.Count; i++)
        {
            var unit = ComposeExcludedRow(excluded, i);
            if (!body.Emit(SweepSubject.ExcludedRows, unit.Length, () => sb.Append(unit))) return;
        }
    }

    /// <summary>ONE roster row, composed by the same helper the DEMAND pass measures. The head rides the FIRST row,
    /// so the list is whole or absent: a head with nothing under it says a roster exists and then names none of it.
    /// </summary>
    internal static string ComposeExcludedRow(IReadOnlyDictionary<string, string> excluded, int index)
    {
        const string head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        var kv = excluded.ElementAt(index);
        return (index == 0 ? head : "") + "  " + kv.Key + ": " + kv.Value + "\n";
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries the honesty layer only (records/plugins houseCARL
    /// could not read). Emit it verbatim so a counts-only answer still names what it could not check (Q3).</summary>
    static void AppendScanErrorTail(StringBuilder sb, BoundedBody body, IReadOnlyList<PluginErrors> reports)
    {
        foreach (var p in reports)
        {
            var row = ComposeUnreadRow(p);
            if (!body.Emit(SweepSubject.UnreadRows, row.Length, () => sb.Append(row))) return;
        }
    }

    // ---- housecarl_check — the merged, multi-family response (SPEC §6.1) ---------------------------
    /// <summary>The merged sweep: ONE header, one section per selected family, each family's own accounting under
    /// its section, one boundary block, and the excluded-plugin roster once for the whole response.
    ///
    /// <para><b>The families' section renderers are the ancestors' own bodies</b> with their title, roster,
    /// accounting and boundary lifted out — so what a family says about itself here is what it said as a whole
    /// response, and the 131 arms that hold those renders hold this one's sections too.</para>
    ///
    /// <para><b>The body budget is DIVIDED, not spent in series</b> (#394, ruling item 1): one
    /// <see cref="BoundedBody"/> for the response, carrying the allocation plan, so a family that renders second
    /// does not inherit whatever the first one left. Measured on the live order at plain defaults, a serial second
    /// family inherited 400 characters of an 80,000 budget — at the no-arguments call, not at a cap anyone
    /// tightened.</para>
    ///
    /// <para><b>Each family's accounting is written under its own section</b>, and its room comes out of the
    /// reserve rather than out of the rows: <see cref="BoundedBody.Reserved"/> discounts what it wrote, so the
    /// family rendering after it is not charged for a sentence the reserve already bought.</para></summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the ALLOCATION it built — for <c>ALLOCATION-EQUALS-SPEND</c>, which
    /// asserts against what each subject was given and what it spent rather than against anything the response
    /// printed. An internal seam: the public render is the one every caller uses.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // WHAT THIS RESPONSE ACTUALLY DID, composed ONCE and handed to everything below — the skeleton pass
        // included, so the fixed part is measured over the same claims the response writes.
        var o = CheckOutcome.For(s);
        if (o.Error is not null) return "error: " + o.Error + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "");
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // The reserve: one accounting line PER FAMILY, and ONE boundary line per family, held before anything
        // renders. Summing whole TextReserves would hold the boundary once per family — room for a sentence
        // written once, taken out of the rows that could have used it.
        int reserve = 0;
        for (int i = 0; i < accts.Count; i++)
            reserve += accts[i].TextAccountingReserve
                     + accts[i].Boundary.Length
                     + string.Format(ReadSentences.SweepBoundaryLabelFor,
                                     SweepFamilySelection.Token(sections[i])).Length + BoundaryWrap;
        int budget = Math.Max(0, cap - reserve);

        // WHAT EACH SUBJECT WANTS, measured before anything is written, so the allocation can water-fill
        // over it rather than discover shortfalls at render time (SweepDemand, BodyAllocation).
        var demand = SweepDemand.ForText(o, budget, histogramLimit);
        // AND WHAT THE RESPONSE OWES WHATEVER THE BUDGET SAYS, measured the same way: composed, not assembled.
        // The row budget has to exclude the WHOLE fixed part — the title, the scope sentence, every family's
        // section head and its own head, the "no findings" line — and not only the pieces that happen to call
        // Reserve. Left in, the allocation divides room that does not exist, the global test bites before any
        // subject reaches its share, and render order decides who loses: the order-dependence water-filling is
        // here to remove, re-entering one level up.
        var skeleton = new StringBuilder();
        var skeletonAccts = o.Accountings(cap);
        var skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => skeleton.Length);
        Compose(skeleton, o, sections, skeletonAccts, skeletonBody, histogramLimit);
        int fixedPart = skeleton.Length - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;

        var sb = new StringBuilder();
        var body = BoundedBody.ForFamilies(accts, budget, () => sb.Length, o.Plan(),
                                           demand.Demand, demand.Reserved + fixedPart, o.ResponseSubjects,
                                           demand.Reserved);
        measured = body;
        Compose(sb, o, sections, accts, body, histogramLimit);

        // The overrun question, asked of the FINISHED response exactly as the single-family close asks it. The
        // notice is part of the response whose length it states, so the composition runs to a fixed point.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // Which accounting states it: the FIRST, because the sentence is about the whole response rather than about
        // any family, and every accounting was built with the same cap. Stating it once is the point — a notice per
        // family would tell the caller three times that one response was too long.
        var overrun = accts.Count > 0 ? accts[0] : null;
        if (overrun is null) return response;
        // How many times this response prints the cap back, COUNTED in the response itself: the remedy has to name
        // a cap that already covers the characters those numbers gain when they widen.
        int sites = overrun.CapPrintsIn(response);
        if (overrun.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = overrun.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = overrun.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
    }

    /// <summary>THE WHOLE MERGED RESPONSE BAR ITS OVERRUN NOTICE, composed through one <paramref name="body"/>.
    /// Run twice per render: once with a <see cref="BoundedBody.Skeleton"/>, which refuses every unit and so leaves
    /// exactly the fixed part behind to be measured, and once for real. One routine rather than two, because a
    /// second spelling of the fixed part is a number free to drift from the response it is meant to describe.
    /// </summary>
    static void Compose(StringBuilder sb, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit)
    {
        var s = o.Sweep;
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n');
        // The scope sentence, above everything a budget can refuse: which families ANSWERED, which selected ones
        // refused, and which registered ones were never asked, with the spelling that gets them. The default
        // narrows only because the response says so (Q3).
        sb.Append(o.ScopeSentence()).Append('\n');

        // THE EXCLUDED-PLUGIN ROSTER, ABOVE THE FAMILY SECTIONS. It is part of what the scope sentence claims —
        // which plugins this response did NOT check — so it reads where the scope is stated. Its POSITION is also
        // load-bearing: every family's accounting is composed inside the loop below, and an accounting can only
        // report what has already been emitted. Written after the sections, the roster's rows were registered
        // AFTER every accounting had spoken, so each one reported none of N roster rows rendered, claimed a cut
        // that had not happened, and set truncated on a response carrying the whole roster — in both transports,
        // on every merged call with an unparseable plugin (round-1 review, found by two reviewers). Reading FIRST
        // is not the same as spending first: the roster is a response-level participant in the allocation
        // (CheckSweep.ResponseSubjects), so it takes min(its demand, lambda) of the row budget exactly as a family
        // does. Held as a reserve instead — its room subtracted from the rows, its rows spent against the
        // response-wide test, which no plan governed — it took the whole body budget before the first family head
        // was written and the fixed part landed past the cap: 4,494 chars against a 4,000 cap.
        AppendExcludedPlugins(sb, body, o.ExcludedPlugins);

        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            sb.Append('\n').Append(string.Format(ReadSentences.SweepFamilySectionHead,
                                                 SweepFamilySelection.Token(f), SweepFamilySelection.Title(f)))
              .Append('\n');
            // THE OFF-ORDER ASYMMETRY, above whatever this family goes on to say — including a refusal. It is a fact
            // about the caller's SCOPE for this family, not about the sweep it did or did not run: gated on the
            // family having run, and written only in the non-refusal branch, it vanished from the one call that
            // needs it most (round-2 finding B4). A "0 unbound" over a scope this family could not sweep reads as
            // "looked, found none" — the exact Q3 misreading the NOT CHECKED wording exists to prevent — and so does
            // a refusal about a DIFFERENT plugin with nothing said about this one.
            if (o.OffOrder(f) is { } skipped) sb.Append(skipped).Append('\n');
            // A FAMILY THAT REFUSED fills its own section with the refusal — the rule the dialogue family has
            // always followed, now followed by all three. A scripts-family scope refusal used to be raised to
            // response level and discard a completed errors sweep beside it: exclude= is validated against each
            // family's OWN scope, and the scripts family is handed the ACTIVE subset of plugins=, so a call
            // naming an off-order file and excluding the active one refused outright and told the caller to narrow
            // exclude= — while the errors family had swept the off-order file perfectly well (round-1 review).
            if (o.Refusal(f) is { } refusal)
            {
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
            else
            {
                // The dialogue family's own asymmetry sits in the same place and for the same reason: it is SEEDED,
                // so the scope parameters beside it did not narrow it, and a caller who passed plugins= would
                // otherwise read a seeded answer as a scoped one.
                DialogueSweepRender.AppendHead(sb, o);
                DialogueSweepRender.AppendSection(sb, o, body);
            }
            // This family's accounting, under this family's section, out of the room held for it.
            if (accts[i].TextLine() is { } line)
                body.Reserved(() => sb.Append('\n').Append(line).Append('\n'));
        }

        // ONE boundary block, one line per family that ran — the two families claim different things, so a single
        // sentence for both would be a claim neither of them makes. Written through the reserve, because that is
        // where its room came from: counted as body it would be charged to the rows a second time.
        for (int i = 0; i < sections.Count; i++)
        {
            int at = i;
            body.Reserved(() => sb.Append('\n')
                                  .Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                        SweepFamilySelection.Token(sections[at])))
                                  .Append(accts[at].Boundary).Append('\n'));
        }
    }

    /// <summary>The newlines a boundary line is wrapped in, held back with it. Two in the render; this is the same
    /// headroom the accounting's own wrap uses, per block rather than once for the lot.</summary>
    internal const int BoundaryWrap = 32;

    // ---- the scripts family (was housecarl_validate_scripts) ----------------------------------------
    /// <summary>The scripts family's own head: what it swept and what it found. Every count states its own scope —
    /// a class the caller excluded reads NOT CHECKED and never 0, and the two counts <c>property_contains=</c>
    /// narrows carry their own label — so no number here can be read as a wider claim than it is.</summary>
    static void AppendScriptsHead(StringBuilder sb, ScriptCheckResult r)
    {
        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        sb.Append("scanned ").Append(r.PluginsScanned).Append(r.PluginsScanned == 1 ? " plugin · " : " plugins · ")
          .Append(r.RecordsWithScripts).Append(" record(s) with scripts · ")
          // A class the caller excluded reads as NOT CHECKED, never as a 0 — a 0 would say "looked, found none" about
          // the HIGH silent-None class nobody looked for (PR #288 review, finding 1).
          .Append(ReadSentences.ScriptUnboundTotal(r, didObject, didScalar))
          .Append(" · ")
          .Append(ReadSentences.ScriptNullTotal(r, didNull))
          .Append(" · ")
          .Append(r.TotalUnverifiable).Append(" unverifiable");
        if (r.ExcludedPlugins.Count > 0)
            sb.Append(" · ").Append(r.ExcludedPlugins.Count).Append(" plugin(s) excluded (unparseable)");
        if (r.Epoch is not null) sb.Append(" · epoch=").Append(r.Epoch).Append(EpochOffOrderQualifier(r));
        sb.Append('\n');
        if (r.FilterNote is not null) sb.Append(r.FilterNote).Append('\n');
        if (r.ReadIncomplete)
            sb.Append("note: a BSA failed to read this build — a '.pex not on disk' below may merely be unscanned, not truly absent (Q3).\n");
    }

    /// <summary>The scripts family's BODY — everything a cap can refuse. Like the errors family's, it writes no
    /// roster, no accounting and no boundary: those are the response's.</summary>
    static void AppendScriptsSection(StringBuilder sb, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            AppendHistograms(sb, body, histogramLimit,
                ScriptsAxes(r));
            // The honesty layer: plugins whose record enumeration faulted. Its own subject, so a response that could
            // not carry every row states how many it named instead of stopping with a bare marker.
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
            // A RECORD SECTION IS EMITTED WHOLE, OR NOT AT ALL — the errors family's rule, in this family's units.
            // Everything inside one is a finding in its own right (an unbound property, the bound-but-null advisory,
            // a "could not verify" note), and the per-line "append if it fits" this replaces dropped them with no
            // subject accounting for the loss: half a record's findings under a header claiming the whole record.
            var section = ComposeScriptRecordUnit(rec);
            if (!body.Emit(SweepSubject.ScriptRecords, section.Length, () => sb.Append(section))) break;
        }
    }

    /// <summary>One record's whole section, composed before it is offered to the budget — the same construction the
    /// errors family's plugin sections use, and for the same reason: a unit measured before the write is a unit the
    /// response cannot land over its cap with.</summary>
    internal static string ComposeScriptRecordUnit(RecordScriptFindings rec)
    {
        if (rec.ScanError is not null)
            return "\n[SCAN ERROR] " + rec.Plugin + ": " + rec.ScanError + "\n";

        var sb = new StringBuilder();
        sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
          .Append(rec.Record).Append(" (").Append(rec.RecordType);
        if (!string.IsNullOrEmpty(rec.EditorId)) sb.Append(" '").Append(rec.EditorId).Append('\'');
        sb.Append(") in ").Append(rec.Plugin).Append('\n');

        // Unbound findings, object/form types (silent None) FIRST, then uninitialized scalars.
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

    /// <summary><paramref name="notes"/> registers the #342 clause as each ANNOTATED field line is written — so a
    /// field the cap truncates away earns nothing. It is null on the lanes that render a record outside a
    /// #342-annotated response (readback, verify).</summary>
    /// <param name="reserve">Chars held back for the response-level clause this render may still state: the field
    /// loop stops that much earlier, while the notice still quotes the caller's own <paramref name="cap"/>.</param>
    static void AppendRecord(StringBuilder sb, ReadOutcome o, int cap, int reserve = 0, ChildNotes? notes = null,
                             LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        var r = o.Record!;
        sb.Append("type=").Append(r.Type)
          .Append("  formid=").Append(r.FormKey)
          .Append("  editorid=").Append(r.EditorId ?? "<none>")
          .Append("  winner=").Append(o.WinnerPlugin)
          .Append("  override_depth=").Append(o.OverrideDepth).Append('\n');
        sb.Append("fields (from ").Append(o.SourcePlugin).Append("):\n");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            if (sb.Length >= cap - reserve)                                 // depth= can produce many lines — cap them (Q3)
            {
                sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                  .Append(" field lines at max_chars=").Append(cap)
                  .Append("; narrow with ").Append(lv.Fields).Append(", lower ").Append(lv.Depth).Append(", or raise max_chars]\n");
                break;
            }
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
            if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
            if (f.Link is not null) sb.Append("   (").Append(LinkText(f.Link)).Append(')');   // resolve_names: target identity, DISPLAY-ONLY — never the round-trip token
            sb.Append('\n');
            // The #342 clause is earned HERE, by a line that reached the caller — not where the annotation was decided.
            if (notes is not null && o.OwnedChildFields is { } ann && ann.ContainsKey(f.Path))
                notes.Emitted(f.Path);
        }
    }

    /// <summary>The resolve_names parenthetical (P7): a FormLink token's target identity as "→ editorid "Name"", or
    /// "unresolved: not in the active order" for a dangling target (named, never dropped — Q3). DISPLAY-ONLY: this
    /// is appended AFTER the round-trip token, never in place of it. Internal: the dense render's cells reuse the
    /// SAME display vocabulary (D2 — renders must not drift).</summary>
    internal static string LinkText(ResolvedRef r) =>
        !r.Resolved ? "unresolved: target not in the active order"
        : string.IsNullOrEmpty(r.Name) ? $"→ {r.EditorId ?? "<no editorid>"}"
        : $"→ {r.EditorId ?? "<no editorid>"} \"{r.Name}\"";

    /// <summary>No-delta render for a WHOLE-record compare. Node-NEUTRAL: this renders for every touching plugin,
    /// including the master node (Nodes[0]), which is not an override — so it reports the COUNT of fields read
    /// identical to the winner (the ITM-restate-vs-no-op signal lives in N) without asserting an "override" or a
    /// "not a no-op" intent the diff can't know. A whole-record compare always agrees on housekeeping/identity
    /// leaves, so N is typically >0; that's stated as a plain fact, not a verdict. The sample names a few agreed
    /// paths; presence is claimed only for the value leaves the read engine actually surfaced (Q3 — never claim a
    /// non-nullable subrecord bit the read can't prove).</summary>
    static string IdenticalWholeRecord(FieldsDiff.Result diff) =>
        diff.AgreedCount > 0
            ? $"(no field deltas; {diff.AgreedCount} modeled field(s) read identical to the winner ({SampleOf(diff)}))"
            : "(identical to winner — full modeled content compared)";

    /// <summary>No-delta render for a fields=-narrowed compare — node-neutral, and the identity claim must not
    /// outrun the compared paths (PR #28 review #2).</summary>
    static string IdenticalAcrossFields(FieldsDiff.Result diff) =>
        diff.AgreedCount > 0
            ? $"(no deltas across the requested fields; {diff.AgreedCount} of them read identical to the winner ({SampleOf(diff)}) — other fields NOT compared)"
            : "(identical to winner across the requested fields — other fields NOT compared)";

    static string SampleOf(FieldsDiff.Result diff)
    {
        var s = string.Join(", ", diff.AgreedSample);
        return diff.AgreedCount > diff.AgreedSample.Count ? $"e.g. {s}, …" : s;
    }

}
