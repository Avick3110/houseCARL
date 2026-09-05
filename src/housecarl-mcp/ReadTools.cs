using System.Text;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The read tools this file was named for are gone; housecarl_records absorbs them. What is left is the render
// layer they shared.

/// <summary>The record reads' render: compact parseable `key = value` output, the winner-relative conflict diff,
/// and response-size estimation whose cut is always explicit, never silent.</summary>
static class Wire
{
    /// <summary>Server default char budget for one tool response (~20k tokens). A caller raises it per-call via max_chars.</summary>
    public const int DefaultMaxChars = 80_000;

    /// <summary>Default char budget for any write-tool read-back dump. Well below <see cref="DefaultMaxChars"/>
    /// because an 80k truncated string still exceeds the host's per-result ceiling and spills to a file silently.
    /// Global on purpose: the host ceiling is a transport property, so every read-back caller wants this bound. A
    /// caller raises it per call via max_chars.</summary>
    public const int ReadbackMaxChars = 24_000;

    /// <summary>How many distinct contested parent hosts a create render names before it says "and N further".
    /// Shared by the text render and its json twin because the two are the same bound and a second literal drifts;
    /// uncapped, one lane writes hundreds of bytes per host ahead of the budgeted array and truncates it. Both
    /// lanes publish the full distinct count beside the capped list.</summary>
    public const int ContestedHostsShown = 10;

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : DefaultMaxChars;

    /// <summary>Parse the shared format= param: null or "text" is the default text render, "json" is json, and
    /// anything else sets a named error in <paramref name="error"/> rather than falling through to text on a typo.
    /// Case- and whitespace-insensitive.</summary>
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

    /// <summary>The one refusal render path for the read surface: a tool body states its refusal once in the text
    /// spelling and this gives it the shape the caller asked for. <see cref="Guard"/> hands a body's return value
    /// straight back, so without this a caller who asked for json gets a bare string. Text is returned unchanged;
    /// json strips <see cref="RefusalPrefix"/> and renders through <see cref="JsonWire.RenderError"/>. Whole-call
    /// only — a failed row inside a successful call keeps its per-row <c>error</c> field.</summary>
    internal static string Refuse(bool json, string message, string? epoch = null)
    {
        if (!json) return message;
        var bare = message.StartsWith(RefusalPrefix, StringComparison.Ordinal)
            ? message[RefusalPrefix.Length..]
            : message;
        return JsonWire.RenderError(bare, epoch);
    }

    /// <summary>The scan lane's format vocabulary — the one lane with a third format, the columnar <c>dense</c>
    /// render. Every other tool stays on the two-value <see cref="WantsJson"/>.</summary>
    internal enum QueryFormat { Text, Json, Dense }

    /// <summary>Parse the scan lane's <c>format=</c>: text (default), json or dense; anything else is a named
    /// refusal listing all three.</summary>
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
    /// <summary>Render the bulk name-resolution result: one compact identity line per input FormID, or
    /// <c>error=</c> for a bad or absent one — per item, so the batch survives. Budget-bounded with the same
    /// explicit cut the other reads use.</summary>
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

    /// <summary>Whether this response has earned the owned-child clause, and over which fields. Registered at
    /// emission, as each annotated field line is written, never where the annotation was decided: a response can
    /// annotate a field and then not show it (the field loop hits max_chars, the json array truncates, a spill
    /// sends the rows to a file), and a clause would then describe a field the caller cannot see.
    /// <see cref="May"/> runs the other way, before a record's fields render, so the clause it could earn is
    /// reserved out of the budget rather than appended past it.</summary>
    internal sealed class ChildNotes
    {
        readonly SortedSet<string> _notRead = new(StringComparer.Ordinal);
        bool _mayNotRead;

        /// <summary>The clause this response may still state — reserved from here on.</summary>
        public void May() => _mayNotRead = true;

        /// <summary>An annotated field line just went into the medium: the clause is now stated, over this
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

    /// <summary>The owned-child clause, stated once per response after the body — never per field, which costs
    /// ~275 identical chars on every annotated row. It names the fields it was earned over, so it claims nothing
    /// about where in the response they are.</summary>
    internal static void AppendOwnedChildNotes(StringBuilder sb, ChildNotes n)
    {
        var fields = n.Fields();
        if (fields.Count == 0) return;
        sb.Append('\n').Append(ReadSentences.NotReadClause(fields)).Append('\n');
    }

    /// <summary>Render one record, keeping the cheap index-only annotation the service already put on the outcome.
    /// This lane renders no conflict tree at all; the precise tier lives on the tree form.</summary>
    static void AppendRecordBlock(StringBuilder sb, ReadOutcome o, int cap, ChildNotes notes, LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        // Hold back the clause this record could earn before its fields render, so an annotated response answers
        // inside max_chars instead of overrunning it. Only a record that can annotate pays.
        if (o.OwnedChildFields is { Count: > 0 }) notes.May();
        // The reserve comes off the budget, never off the number the render quotes: a cut must report the
        // max_chars the caller actually passed.
        AppendRecord(sb, o, cap, notes.Reserve, notes, lv);
    }

    // ---- many records ----
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    /// <summary><paramref name="levers"/> is the caller's own parameter vocabulary for the remedy sentences below;
    /// this renderer is shared by callers that spell the selector differently. Omitted means the legacy
    /// spelling.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars,
                                     SpillState? spill, out bool truncated, LeverNames? levers = null)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        int cap = Cap(maxChars);
        var notes = new ChildNotes();   // accumulated over the rows actually rendered, not over the input list
        var sb = new StringBuilder();
        sb.Append("batch: ").Append(outcomes.Count).Append(outcomes.Count == 1 ? " record" : " records");
        // The whole batch reads one captured build, so the epoch is response-level: take the first non-null, since
        // a malformed-FormID row never consulted a view and carries none.
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
                  .Append("; ").Append(lv.BatchSelection)
                  .Append(lv.HasFieldSelector ? $", pass {lv.Fields} to slim each," : ",")   // a form with no field selector must not be told to narrow with one
                  .Append(" or raise max_chars]\n");
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

    // ---- the scan lane ----

    // The dense render's container hint is LeverNames.DenseContainerHint: dense refuses depth>1, so the hint has
    // to name the format hop alongside the knob, spelled in the caller's own vocabulary.

    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames = false, bool winnerFields = false, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The artifact-aware render: <paramref name="spill"/> carries the call's artifact disposition, and
    /// <paramref name="truncated"/> hands the row-level max_chars cut back to the tool layer, which is what
    /// triggers the auto-spill.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars,
                                          bool resolveNames, bool winnerFields, int depth, SpillState? spill, out bool truncated,
                                          LeverNames? levers = null)
    {
        truncated = false;
        var lv = levers ?? LeverNames.Legacy;
        // A refusal made after the build was captured is stamped with the epoch; a pre-capture validation refusal
        // carries null and renders bare.
        if (q.Error is not null) return "error: " + q.Error + (q.Epoch is not null ? $"\nepoch={q.Epoch}" : "");
        int cap = Cap(maxChars);
        if (q.Groups is not null) return RenderCrossQueryGroups(q, cap, spill, out truncated);   // group_by= → a count table, not per-match lines
        bool detail = fields is { Count: > 0 };          // expand matches, vs. one-line summaries
        var linkMemo = resolveNames && detail ? new LoadOrderService.LinkMemo() : null;   // one link cache across all rendered matches
        bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // a plugins= scope shows a plugin's OWN body
        var sb = new StringBuilder();
        sb.Append("scan: ").Append(q.Total).Append(q.Total == 1 ? " match" : " matches");
        if (q.ScopeLabel is not null) sb.Append(" DEFINED IN ").Append(q.ScopeLabel);   // explicit scope — NOT the 'touches' default
        if (q.Offset > 0)                                                              // name the window, and the next offset while paging
        {
            // "no records match at any offset" is a claim over the whole order; a scan that lost a plugin names the
            // lock instead, because the filter is the one thing that is not the cause.
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
        if (q.Epoch is not null) sb.Append("  epoch=").Append(q.Epoch);   // offset= windows tile ONLY within one epoch
        sb.Append('\n');
        if (q.PredicateNote is not null) sb.Append(q.PredicateNote).Append('\n');   // where= accounting: wrong path / no value
        if (q.ScanNote is not null) sb.Append(q.ScanNote).Append('\n');             // records Mutagen could not parse
        if (q.WhereSourceNote is not null) sb.Append(q.WhereSourceNote).Append('\n');   // where_source=winner is redundant under a type=-only scope
        // Under a plugins= scope the per-match fields are the SCOPED plugin's own values, not the live winner's —
        // silently wrong otherwise, so name it once. The helper covers all four winner_fields=/where_source=
        // combinations so the note never claims a scoped-body match the scan did not make, and the json and dense
        // renders share it.
        if (anyScoped) sb.Append("note: ").Append(JsonWire.ScopedFieldsNote(winnerFields, q.WhereWinner, lv)).Append('\n');

        int rendered = 0;
        var notes = new ChildNotes();   // accumulated over the rows actually rendered
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
                // winner_fields= reads the load-order winner's body regardless of scan scope; otherwise read the
                // body the scan filtered, so display never contradicts filter. Pinned to the scan's build, since
                // the header's epoch names one build and every fill must read that one.
                var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, depth, resolveNames: resolveNames, linkMemo: linkMemo,
                                          containerHint: lv.ContainerHint);   // a collapsed cell names the caller's own expansion knob
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
                    AppendRuntime(sb, m.RuntimeFormId, m.RuntimeFormIdNote);
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

    /// <summary>Render a <c>group_by=</c> aggregation: a header naming the key, true total and group count, then
    /// one row per group (already sorted descending by the core). The where= and unscannable notes survive the
    /// aggregation. Over max_chars it stops with an explicit truncation notice; the total stays exact because only
    /// the rendering is capped, not the aggregation.</summary>
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

    // ---- the chain form ----
    /// <summary>Render the effect chain: a header resolving the MGEF (editorid plus the confirmed MagicEffect
    /// type, so the caller can see the match was typed), then carrier rows grouped by record type. A valid but
    /// unused MGEF renders a clean "none" line rather than an error — the error path is the bad or mistyped
    /// FormID, handled in core. Over max_chars it stops with the same explicit notice the other reads
    /// use.</summary>
    /// <param name="carrierBound">How the CALLING tool spells the per-seed carrier bound this render may have hit.
    /// On housecarl_records that is walk.max_nodes — limit= there windows the SEEDS and raising it changes nothing
    /// about a capped carrier list, so the sentence has to carry the caller's own knob. REQUIRED, with no default:
    /// a default here is a lever name guessed on the caller's behalf, which is exactly the bug this parameter was
    /// added to fix — a new caller must state its own spelling.</param>
    public static string RenderEffectChain(EffectChainResult r, int maxChars, string carrierBound)
    {
        if (r.Error is not null) return "error: " + r.Error + (r.Epoch is not null ? $"\nepoch={r.Epoch}" : "");
        int cap = Cap(maxChars);
        var sb = new StringBuilder();
        sb.Append("chain for ").Append(r.Mgef).Append(" (").Append(r.MgefEditorId).Append(", MagicEffect): ")
          .Append(r.Total).Append(r.Total == 1 ? " carrier row" : " carrier rows");
        if (r.Capped) sb.Append(" (showing first ").Append(r.Rows.Count).Append("; raise ").Append(carrierBound).Append(" or narrow to see more)");
        if (r.Epoch is not null) sb.Append("  epoch=").Append(r.Epoch);
        sb.Append('\n');
        // The whole-order negative is only the scan's to make when the scan read the whole order; with a plugin left
        // out it states the scope it covered and leaves the note below to name what it missed.
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
        // Group rows by carrier type, ordinal for stability, so a multi-type result reads grouped.
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

    // ---- the errors family ----
    /// <summary>The errors family's own head: what it swept and what it found. Everything above the first thing a
    /// budget can refuse, and everything below the response's title, which belongs to the caller — the merged
    /// surface titles a section, not a whole response.</summary>
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
        AppendBaselineSplit(sb, r, acct);   // how much of the dangling total is vanilla baseline
    }

    /// <summary>The errors family's two counts_only axes, built once and read by both the render and the demand
    /// pass: a second construction would give the demand a different head from the row actually written.</summary>
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

    /// <summary>One plugin section's fixed part — everything the section says besides its dangling entries. It is
    /// a unit, emitted whole or not at all: a scan error, the missing masters and the unscannable-record count are
    /// each a finding in their own right, and no accounting subject covers half of one. Shared so the demand pass
    /// and the write read one source and the measured demand cannot drift from what is written.</summary>
    internal static string ComposeErrorSection(PluginErrors p)
    {
        var fixedPart = new StringBuilder("\n[ERROR] ").Append(p.Plugin).Append('\n');
        if (p.ScanError is not null)
            fixedPart.Append("  scan error: ").Append(p.ScanError).Append('\n');
        // The two shortfalls are named apart because their remedies differ — install versus enable — and a caller
        // told "install/enable it" has to go find out which. Null means the split was not made, so fall back to
        // the combined wording rather than assert the uninstalled case.
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

    /// <summary>One dangling entry — the one thing this family's accounting states a unit at a time. Shared by the
    /// demand pass and the write; see <see cref="ComposeErrorSection"/>.</summary>
    internal static string ComposeDanglingLine(DanglingRef d)
    {
            return "    " + d.Source + " (" + d.SourceType
                     + (string.IsNullOrEmpty(d.SourceEditorId) ? "" : " '" + d.SourceEditorId + "'")
                     + ") -> " + d.Target + "   [target not defined by any active plugin]\n";
            // Registered where the line lands: the accounting counts the response and the by-source roster is
            // tallied off the same registration, so the count and the roster cannot disagree.
    }

    /// <summary>One unread-plugin row, the <c>counts_only</c> lane's honesty layer. Shared, for the same
    /// reason.</summary>
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

    /// <summary>One histogram row. The first row of an axis carries the axis head, so its width differs from the
    /// rest — the demand pass must ask with the same row index the write will use.</summary>
    internal static string ComposeHistogramRow(HistogramAxis axis, SweepCount row, bool first)
    {
        var line = "  " + row.Count.ToString().PadLeft(6) + "  " + row.Key + "\n";
        return first ? axis.Head + line : line;
    }

    /// <summary>The errors family's body — everything a cap can refuse, and nothing else. It writes no roster,
    /// accounting or boundary: those belong to the response, and a section renderer that wrote them could not be
    /// called twice in one response.</summary>
    static void AppendErrorsSection(StringBuilder sb, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            // Both axes are handed over together so both are reserved before either renders. The source axis
            // carries no note and no not-computed line: both would repeat what the target axis just said.
            AppendHistograms(sb, body, histogramLimit,
                ErrorsAxes(r));
            AppendScanErrorTail(sb, body, r.Reports);
            return;
        }

        if (r.Reports.Count == 0 && r.ExcludedPlugins.Count == 0)
            sb.Append("\nNo errors found in the scanned scope.\n");

        foreach (var p in r.Reports)
        {
            // A section is emitted whole or not at all, except for its dangling entries, which the accounting can
            // account for one at a time. Everything else a section says is a finding in its own right that no
            // accounting subject covers, so a per-line "append if it fits" would drop it silently. Composing the
            // fixed part first leaves only the two droppable units the accounting states: a section, or an entry.
            var section = ComposeErrorSection(p);
            if (!body.Emit(SweepSubject.PluginSections, section.Length, () => sb.Append(section))) break;

            foreach (var d in p.Dangling)
            {
                var line = ComposeDanglingLine(d);
                if (!body.Emit(SweepSubject.DanglingEntries, line.Length, () => sb.Append(line), p.Plugin)) break;
            }
        }
    }

    /// <summary>The baseline split: how much of the dangling total came from the base-game masters and how much
    /// from everything else. Vanilla leftovers are permanent and nothing a load order can fix, so the raw total
    /// cannot be acted on without this. The line names the plugins it counted as baseline rather than saying
    /// "base-game": the set is Mutagen's <c>BaseMasters</c>, which excludes Creation Club plugins that the
    /// load-order status groups with the base masters. Keeping to Mutagen's set exactly is what makes this
    /// by-construction; excluding CC is a caller's choice via <c>exclude=</c>. Printed only when a base master was
    /// actually swept, and it names that subset (<see cref="ErrorCheckResult.BaseMastersSwept"/>) rather than the
    /// whole definition, or the sentence is true and teaches something false.</summary>
    static void AppendBaselineSplit(StringBuilder sb, ErrorCheckResult r, CheckAccounting acct)
    {
        if (!r.Classes.HasFlag(ErrorFindingClass.Dangling) || r.BaseMastersSwept is not { Count: > 0 } swept) return;
        sb.Append("baseline: ").Append(r.BaselineDangling).Append(" of ").Append(r.TotalDangling)
          .Append(" dangling ref(s) come from the base-game master(s) this sweep covered (").Append(string.Join(", ", swept))
          .Append(") — vanilla leftovers rather than anything this load order introduced; ")
          .Append(r.TotalDangling - r.BaselineDangling).Append(" come from the rest of the swept scope.").Append('\n');
        // Only stated where the phase order actually decided something: nothing was crowded out of a sweep that
        // listed everything it found. The "spent on every other plugin BEFORE those" clause claims an ordering
        // between two groups, so both must exist — on a base-masters-only scope there is no "every other plugin".
        // NonBaseInScope is computed in the sweep from the resolved targets; comparing PluginsScanned against the
        // swept-base count instead subtracts two numbers that measure different things.
        if (acct.OmittedByBudget > 0 && r.BaselineDangling > 0 && r.NonBaseInScope)
            sb.Append("  the listing budget (limit=) is spent on every other plugin BEFORE those, so baseline findings ")
              .Append("cannot crowd the rest out of the list; the sections below stay in load order.").Append('\n');
    }

    // ---- shared sweep-render pieces ----
    /// <summary>The epoch stamp's coverage qualifier: off-order files are located on disk, outside the
    /// fingerprinted order, so the fingerprint does not cover their content and equal epochs must not be read as
    /// "same inputs" across such sweeps. The fact itself is data; this is its header-line rendering.</summary>
    static string EpochOffOrderQualifier(ErrorCheckResult r) =>
        r.OffOrderScanned is { Count: > 0 } ? " (indexed plugins only — off-order file content is outside the fingerprint)" : "";

    /// <summary>The scripts family has no off-order lane, so its stamp always covers everything it swept. The
    /// overload exists so the two sweep headers stay textually identical.</summary>
    static string EpochOffOrderQualifier(ScriptCheckResult r) => "";

    /// <summary>Reserve every axis's fixed part — its unconditional lines and its closing disclosure — then render
    /// them all. Two passes, because an axis reserving its own room only when its turn came would find a sibling
    /// had already spent the budget.</summary>
    static void AppendHistograms(StringBuilder sb, BoundedBody body, int rowLimit, params HistogramAxis[] axes)
    {
        foreach (var a in axes) body.Reserve(a.Subject, a.TextFixed);
        foreach (var a in axes) AppendHistogram(sb, body, rowLimit, a);
    }

    /// <summary>Render one <c>counts_only=</c> histogram axis, capped at <paramref name="rowLimit"/> with the true
    /// distinct-key count always stated. A null histogram means the mode was not requested and an empty one means
    /// the sweep found nothing; the two must read differently.</summary>
    /// <param name="body">the one bounded emission path. The axis's rows go through it and can be refused; the
    /// axis's own statement about itself cannot, because that room was reserved out of <c>max_chars</c> before the
    /// body rendered. Charging the statement to the row budget lets the pressure that cut the rows cut the
    /// sentence reporting the cut.</param>
    static void AppendHistogram(StringBuilder sb, BoundedBody body, int rowLimit, HistogramAxis axis)
    {
        // The note and the not-computed line are fixed text, and the second is this axis's whole answer, so no
        // budget may drop it. They go through `body` rather than straight to the builder so the fixed part is
        // measured; otherwise the overrun notice cannot see them and blames a body unit instead.
        if (axis.NoteLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NoteLine));
        if (axis.Rows is not { } rows)
        {
            if (axis.NotComputedLine.Length > 0) body.Fixed(axis.Subject, () => sb.Append(axis.NotComputedLine));
            body.Release(axis.Subject);
            return;
        }
        // The title rides the empty case too, or two empty axes render as identical untitled sentences with no way
        // to tell them apart. "Nothing to tally" is this axis's entire answer, so it CLOSES with it rather than
        // emitting it: an answer a tight cap can refuse leaves "no findings" indistinguishable from "not run".
        if (rows.Count == 0) { body.Close(axis.Subject, () => sb.Append(axis.EmptyLine)); return; }
        var head = axis.Head;
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var unit = ComposeHistogramRow(axis, row, shown == 0);
            // A row pays for itself only: the closing line's cost is already held back, so a subject may spend the
            // budget on its rows but never on its own disclosure.
            if (!body.Emit(axis.Subject, unit.Length, () => sb.Append(unit))) { cutByBudget = true; break; }
            shown++;
        }
        // The closing disclosure, from one computation the json lane reads too. The remedy must name the knob that
        // stopped THIS axis: "raise limit=" on rows the response had no room for moves nothing, and neither does
        // "raise max_chars=" on rows the row budget refused. An axis that rendered every row says nothing and
        // gives its reserved room back. An axis admitted no rows still prints head and count, or an axis that
        // exists and renders nothing is the same silent cut one level down.
        if (HistogramCut.For(rows.Count, shown, cutByBudget) is not { } cut) { body.Release(axis.Subject); return; }
        if (shown == 0) body.Close(axis.Subject, () => sb.Append(head).Append(cut.Line));
        else body.Close(axis.Subject, () => sb.Append(cut.Line));
    }

    /// <summary>The named, reasoned list of plugins the index build could not parse. Shared by the listing and
    /// <c>counts_only=</c> paths, so a counts-only caller can still learn which plugin went unchecked. It returns
    /// no row count: the accounting states that from the same registrations, in both transports.</summary>
    static void AppendExcludedPlugins(StringBuilder sb, BoundedBody body, IReadOnlyDictionary<string, string> excluded)
    {
        for (int i = 0; i < excluded.Count; i++)
        {
            var unit = ComposeExcludedRow(excluded, i);
            if (!body.Emit(SweepSubject.ExcludedRows, unit.Length, () => sb.Append(unit))) return;
        }
    }

    /// <summary>One roster row, composed by the same helper the demand pass measures. The head rides the first
    /// row, so the list is whole or absent rather than a head with nothing under it.</summary>
    internal static string ComposeExcludedRow(IReadOnlyDictionary<string, string> excluded, int index)
    {
        const string head = "\nexcluded plugins (could not be parsed — NOT checked):\n";
        var kv = excluded.ElementAt(index);
        return (index == 0 ? head : "") + "  " + kv.Key + ": " + kv.Value + "\n";
    }

    /// <summary>Under <c>counts_only=</c> the reports list carries only what could not be read. Emit it verbatim
    /// so a counts-only answer still names what it could not check.</summary>
    static void AppendScanErrorTail(StringBuilder sb, BoundedBody body, IReadOnlyList<PluginErrors> reports)
    {
        foreach (var p in reports)
        {
            var row = ComposeUnreadRow(p);
            if (!body.Emit(SweepSubject.UnreadRows, row.Length, () => sb.Append(row))) return;
        }
    }

    // ---- the merged, multi-family check response ----
    /// <summary>The merged sweep: one header, one section per selected family, each family's own accounting under
    /// its section, one boundary block, and the excluded-plugin roster once for the whole response. The body
    /// budget is DIVIDED rather than spent in series — one <see cref="BoundedBody"/> carries the allocation plan,
    /// so a family rendering second does not inherit whatever the first left, which at defaults can be a few
    /// hundred characters of an 80,000 budget. Each family's accounting comes out of the reserve rather than the
    /// rows, so the next family is not charged for a sentence the reserve already bought.</summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the allocation it built so a test can assert what each subject was
    /// given and spent rather than what the response printed. An internal seam; callers use the public
    /// render.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // What this response actually did, composed once and handed to everything below, the skeleton pass
        // included, so the fixed part is measured over the same claims the response writes.
        var o = CheckOutcome.For(s);
        if (o.Error is not null) return "error: " + o.Error + (o.Epoch is not null ? $"\nepoch={o.Epoch}" : "");
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // The reserve: one accounting line and one boundary line per family, summed here and held back before
        // anything renders.
        int reserve = 0;
        for (int i = 0; i < accts.Count; i++)
            reserve += accts[i].TextAccountingReserve
                     + accts[i].Boundary.Length
                     + string.Format(ReadSentences.SweepBoundaryLabelFor,
                                     SweepFamilySelection.Token(sections[i])).Length + BoundaryWrap;
        int budget = Math.Max(0, cap - reserve);

        // What each subject wants, measured before anything is written, so the allocation can water-fill over it
        // rather than discover shortfalls at render time.
        var demand = SweepDemand.ForText(o, budget, histogramLimit);
        // And what the response owes whatever the budget says, measured the same way by composing it. The row
        // budget must exclude the WHOLE fixed part — title, scope sentence, every section head, the "no findings"
        // line — not only the pieces that happen to call Reserve, or the allocation divides room that does not
        // exist and render order decides who loses.
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

        // The overrun question, asked of the finished response. The notice is part of the response whose length it
        // states, so the composition runs to a fixed point.
        var response = sb.ToString().TrimEnd('\n');
        int needed = body.FixedPart(response.Length);
        // The first accounting states it: the sentence is about the whole response rather than any family, and
        // every accounting was built with the same cap. Once only — a notice per family would say it three times.
        var overrun = accts.Count > 0 ? accts[0] : null;
        if (overrun is null) return response;
        // How many times this response prints the cap back, counted in the response itself: the remedy must name a
        // cap that already covers the characters those numbers gain when they widen.
        int sites = overrun.CapPrintsIn(response);
        if (overrun.CapTooSmall(response.Length, needed, 0, sites) is not { } notice) return response;
        var settled = overrun.CapTooSmall(response.Length + notice.Length, needed, notice.Length, sites)!;
        if (settled.Length != notice.Length)
            settled = overrun.CapTooSmall(response.Length + settled.Length, needed, settled.Length, sites)!;
        return response + settled;
    }

    /// <summary>The whole merged response bar its overrun notice, composed through one <paramref name="body"/>.
    /// Run twice per render: once with a <see cref="BoundedBody.Skeleton"/>, which refuses every unit and so
    /// leaves exactly the fixed part to be measured, and once for real. One routine, because a second spelling of
    /// the fixed part would be free to drift from the response it describes.</summary>
    static void Compose(StringBuilder sb, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit)
    {
        var s = o.Sweep;
        sb.Append(ReadSentences.SweepMergedTitle).Append('\n');
        // The scope sentence, above everything a budget can refuse: which families answered, which selected ones
        // refused, and which registered ones were never asked, with the spelling that gets them.
        sb.Append(o.ScopeSentence()).Append('\n');

        // The excluded-plugin roster goes ABOVE the family sections: it is part of what the scope sentence claims,
        // and its position is load-bearing. Each family's accounting is composed in the loop below and can only
        // report what has already been emitted, so a roster written after the sections registers its rows too late
        // and every accounting claims a cut that did not happen. It is a response-level participant in the
        // allocation, taking its share of the row budget like a family; held as a reserve instead it would take
        // the whole body budget before the first family head is written.
        AppendExcludedPlugins(sb, body, o.ExcludedPlugins);

        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            sb.Append('\n').Append(string.Format(ReadSentences.SweepFamilySectionHead,
                                                 SweepFamilySelection.Token(f), SweepFamilySelection.Title(f)))
              .Append('\n');
            // The off-order asymmetry goes above whatever this family says next, refusal included. It is a fact
            // about the caller's SCOPE for this family, not about the sweep it ran, so gating it on the family
            // having run would drop it from the call that needs it most: a "0 unbound" over a scope this family
            // could not sweep reads as "looked, found none".
            if (o.OffOrder(f) is { } skipped) sb.Append(skipped).Append('\n');
            // A family that refused fills its OWN section with the refusal, never the whole response: exclude= is
            // validated against each family's own scope, so one family's scope refusal must not discard a
            // completed sweep beside it.
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
                // The dialogue family's asymmetry sits in the same place, for the same reason: it is seeded, so the
                // scope parameters beside it did not narrow it and a plugins= caller would misread it as scoped.
                DialogueSweepRender.AppendHead(sb, o);
                DialogueSweepRender.AppendSection(sb, o, body);
            }
            // This family's accounting, under this family's section, out of the room held for it.
            if (accts[i].TextLine() is { } line)
                body.Reserved(() => sb.Append('\n').Append(line).Append('\n'));
        }

        // One boundary block, one line per family that ran: the families claim different things, so a single
        // sentence for both would be a claim neither makes. Written through the reserve, where its room came from;
        // counted as body it would be charged to the rows a second time.
        for (int i = 0; i < sections.Count; i++)
        {
            int at = i;
            body.Reserved(() => sb.Append('\n')
                                  .Append(string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                        SweepFamilySelection.Token(sections[at])))
                                  .Append(accts[at].Boundary).Append('\n'));
        }
    }

    /// <summary>The headroom a boundary line's wrapping newlines are held back with, per block rather than once
    /// for the lot.</summary>
    internal const int BoundaryWrap = 32;

    // ---- the scripts family ----
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
          // A class the caller excluded reads as NOT CHECKED, never as 0 — a 0 would say "looked, found none"
          // about a class nobody looked for.
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

    /// <summary>The scripts family's body — everything a cap can refuse. Like the errors family's, it writes no
    /// roster, accounting or boundary: those belong to the response.</summary>
    static void AppendScriptsSection(StringBuilder sb, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        if (r.CountsOnly)
        {
            AppendHistograms(sb, body, histogramLimit,
                ScriptsAxes(r));
            // Plugins whose record enumeration faulted. Its own subject, so a response that could not carry every
            // row states how many it named instead of stopping with a bare marker.
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
            // Everything inside one is a finding in its own right, and a per-line "append if it fits" would leave
            // half a record's findings under a header claiming the whole record, with nothing accounting for it.
            var section = ComposeScriptRecordUnit(rec);
            if (!body.Emit(SweepSubject.ScriptRecords, section.Length, () => sb.Append(section))) break;
        }
    }

    /// <summary>One record's whole section, composed before it is offered to the budget — the same construction
    /// the errors family's plugin sections use: a unit measured before the write cannot land the response over its
    /// cap.</summary>
    internal static string ComposeScriptRecordUnit(RecordScriptFindings rec)
    {
        if (rec.ScanError is not null)
            return "\n[SCAN ERROR] " + rec.Plugin + ": " + rec.ScanError + "\n";

        var sb = new StringBuilder();
        sb.Append('\n').Append(rec.Unbound.Count > 0 ? "[UNBOUND] " : "[CHECK] ")
          .Append(rec.Record).Append(" (").Append(rec.RecordType);
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

    /// <summary>The runtime-FormID token every text lane prints beside a record's identity: the eight-hex form, or
    /// the parenthetical sentence saying why there is none. Nothing when the order gives the record no runtime
    /// address at all.</summary>
    internal static void AppendRuntime(StringBuilder sb, string? runtime, string? note)
    {
        if (runtime is not null) sb.Append("  runtime=").Append(runtime);
        else if (note is not null) sb.Append("  runtime=(").Append(note).Append(')');
    }

    /// <summary><paramref name="notes"/> registers the owned-child clause as each annotated field line is written,
    /// so a field the cap truncates away earns nothing. Null on the lanes that render a record outside an
    /// annotated response, such as readback and verify.</summary>
    /// <param name="reserve">Chars held back for the response-level clause this render may still state: the field
    /// loop stops that much earlier, while the notice still quotes the caller's own <paramref name="cap"/>.</param>
    static void AppendRecord(StringBuilder sb, ReadOutcome o, int cap, int reserve = 0, ChildNotes? notes = null,
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
        for (int i = 0; i < r.Fields.Count; i++)
        {
            if (sb.Length >= cap - reserve)                                 // depth= can produce many lines — cap them
            {
                sb.Append("  ... [truncated: showing ").Append(i).Append(" of ").Append(r.Fields.Count)
                  .Append(" field lines at max_chars=").Append(cap)
                  .Append(lv.HasFieldSelector ? $"; narrow with {lv.Fields}, lower " : "; lower ")
                  .Append(lv.Depth).Append(", or raise max_chars]\n");
                break;
            }
            var f = r.Fields[i];
            sb.Append("  ").Append(f.Path).Append(" = ").Append(f.HasValue ? f.Token : f.Note);
            if (f.Display is not null) sb.Append("   (").Append(f.Display).Append(')');   // display-only annotation (e.g. decoded biped slots) — never the round-trip token
            if (f.Link is not null) sb.Append("   (").Append(LinkText(f.Link)).Append(')');   // resolve_names target identity, DISPLAY-ONLY — never the round-trip token
            sb.Append('\n');
            // The clause is earned HERE, by a line that reached the caller, not where the annotation was decided.
            if (notes is not null && o.OwnedChildFields is { } ann && ann.ContainsKey(f.Path))
                notes.Emitted(f.Path);
        }
    }

    /// <summary>The resolve_names parenthetical: a FormLink token's target identity, or an "unresolved" note for a
    /// dangling target, which is named rather than dropped. Display only — appended after the round-trip token,
    /// never in place of it. Internal so the dense render's cells reuse the same wording.</summary>
    internal static string LinkText(ResolvedRef r) =>
        // Unresolved has two causes and the ref itself says which: a named Winner means a plugin DOES define the
        // target and the fetch did not yield it, so the sentence must not assert that nothing defines it.
        !r.Resolved ? (r.Winner is { } w
            ? $"unresolved: '{w}' defines this target but did not yield it on fetch"
            : "unresolved: no active plugin defines this target")
        : string.IsNullOrEmpty(r.Name) ? $"→ {r.EditorId ?? "<no editorid>"}"
        : $"→ {r.EditorId ?? "<no editorid>"} \"{r.Name}\"";

    // The delta form's own "identical across the fields read" wording lives in RecordsTools.

}
