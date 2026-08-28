using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>What the response says when its full result lives in a §2.1.1 artifact: the file, its parsed-back
/// manifest, and WHY it was written — <c>to_file</c> (the caller forced the disposition) or <c>ceiling</c>
/// (auto-spill: the inline render hit max_chars). Threaded into the renders so the <c>spilled</c> marker rides
/// IN-BAND in both formats (a marker appended outside the json document would be invisible to a json consumer —
/// the D2 pairing rule).</summary>
internal sealed record SpillInfo(string Path, ResultArtifact.Manifest Manifest, string Reason)
{
    public bool ToFile => Reason == "to_file";
}

/// <summary>How a render learns its call's artifact disposition, as ONE value: a successful spill (with whether
/// the rows are omitted — the to_file manifest-only render), or a FAILED auto-spill (the write refused — the
/// response must then say its truncation has NO complete artifact behind it, because "truncation never loses data"
/// is §2.1.1's promise and a silently broken promise is the Q3 case). Null = ordinary inline response.</summary>
internal sealed record SpillState(SpillInfo? Spill, string? Failure, bool ManifestOnly)
{
    public static SpillState Spilled(SpillInfo s, bool manifestOnly) => new(s, null, manifestOnly);

    /// <summary>The spill WRITE failed: the artifact was promised and could not be produced — say so, loud, with
    /// the recovery moves (PR #306 review: the emitters render this verbatim, so the message is the whole story).</summary>
    public static SpillState WriteFailed(string error) => new(null,
        "the response is truncated and the auto-spill artifact could NOT be written — " + error +
        " The complete result exists NOWHERE; re-run with a narrower filter, a higher max_chars, or to_file= at a writable path.", false);

    /// <summary>The result HAS no spillable row form (conflict_tree — the same reason to_file= refuses it), so no
    /// artifact was attempted: writing thinner summary rows under a completeness claim would be the silent-substitution
    /// Q3 case (PR #306 review, finding 1).</summary>
    public static SpillState NoRowForm() => new(null,
        "the response is truncated and was NOT auto-spilled: conflict_tree=true has no JSONL row form (the same reason " +
        "to_file= refuses it), and spilling thinner tree-less rows under a completeness claim would misrepresent the file. " +
        "The complete trees exist only inline — raise max_chars, narrow the set, or drop conflict_tree (plain rows spill fine).", false);
}

/// <summary>The §2.1.1 artifact layer for the bulk read lanes (tool-surface 2.0, W1): per-lane artifact BUILDERS
/// (each writes the SAME rows its json render emits — shared row writers, so the file and the wire can only differ
/// in formatting, never in data) and the shared in-band ACCOUNTING EMITTER for the <c>spilled</c> marker (one
/// wording for text, one shape for json, used by every wired lane — the drift-killer the epoch stamps already
/// established).
///
/// <para>Wired lanes (W1): cross_plugin_query (all three formats + group_by), batch_record_detail, resolve —
/// the record-bulk lanes where unbounded output actually bites. The sweep lanes (check_errors/validate_scripts)
/// get their artifacts when W2 folds them into the findings= family: their JSONL row shape IS that redesign, and
/// wiring a throwaway nested shape now would ship a second schema W2 immediately retires.</para></summary>
internal static class Artifacts
{
    // ---- the shared spilled-marker emitter (text) ---------------------------------------------------

    /// <summary>Append the <c>spilled</c> block to a text response. Contract (SPEC §2.1.1): the marker MUST name
    /// the artifact path — a pathless spill makes well-behaved callers refuse, re-pay the scan, or fabricate a
    /// path (E4.2 run 1). The block also carries the manifest facts a caller needs to decide its next move
    /// without opening the file: row count, schema, identity column, epoch.</summary>
    public static void AppendSpillText(StringBuilder sb, SpillInfo s)
    {
        var m = s.Manifest;
        // "complete result" is claimed ONLY when the file holds every match (PR #306 review, finding 3): an
        // auto-spill of a limit= window is complete AS A WINDOW — the matches beyond limit= are in no file, and
        // saying otherwise is exactly the silent data-loss claim this PR exists to kill.
        bool whole = m.Total == m.RowCount;
        sb.Append('\n')
          .Append(whole ? "spilled: complete result (" : "spilled: the returned WINDOW (")
          .Append(m.RowCount).Append(m.RowCount == 1 ? " row" : " rows")
          .Append(whole ? "" : $" of {m.Total} total matches")
          .Append(") -> ").Append(s.Path).Append('\n')
          .Append(s.ToFile
              ? "  written at your request (to_file=): only this manifest is rendered inline.\n"
              : whole
                  ? "  the inline render hit max_chars, so the COMPLETE result was auto-spilled (nothing is lost; the rows above are a prefix).\n"
                  : $"  the inline render hit max_chars; the spilled WINDOW is complete in the file, but the {m.Total - m.RowCount} matches beyond limit= are in NO file — page with offset=, raise limit=, or use to_file= for the full result.\n")
          .Append("  manifest: rows=").Append(m.RowCount)
          .Append(whole ? "" : $" of total={m.Total}")
          .Append("  identity=").Append(m.Identity ?? "<none>")
          .Append("  epoch=").Append(m.Epoch).Append('\n')
          .Append("  row_schema: ").Append(string.Join(", ", m.RowSchema)).Append('\n')
          .Append("  sort: ").Append(m.Sort).Append('\n');
        if (m.TypeCounts is { Count: > 0 })
            sb.Append("  type_counts: ")
              .Append(string.Join(", ", m.TypeCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                                    .Select(kv => $"{kv.Key}={kv.Value}"))).Append('\n');
        sb.Append("  the file is JSONL (line 1 = this manifest, one row per line) — grep/read it with your own file tools, ")
          .Append(m.Identity is null
              ? "or re-run the producing query for fresh values.\n"
              : $"or re-enter it server-side via formids=@{s.Path} / where=[\"formid in @{s.Path}\"] (epoch-checked against the current build).\n");
    }

    // ---- the shared spilled-marker emitter (json) ---------------------------------------------------

    /// <summary>Write the <c>spilled</c> member into an OPEN json object — the same facts as the text block
    /// (D2: one datum, two renders). The path is the value of <c>spilled.path</c>; its presence IS the marker.</summary>
    public static void WriteSpillJson(Utf8JsonWriter w, SpillInfo s)
    {
        var m = s.Manifest;
        w.WriteStartObject("spilled");
        w.WriteString("path", s.Path);
        w.WriteString("reason", s.ToFile ? "to_file" : "over_inline_ceiling");
        w.WriteNumber("row_count", m.RowCount);
        w.WriteNumber("total", m.Total);
        // Explicit, not derivable-only (finding 3's json half): false = the file is a WINDOW, matches beyond
        // limit= are in no file.
        w.WriteBoolean("complete", m.Total == m.RowCount);
        if (m.Identity is null) w.WriteNull("identity"); else w.WriteString("identity", m.Identity);
        w.WriteStartArray("row_schema");
        foreach (var c in m.RowSchema) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteString("sort", m.Sort);
        if (m.TypeCounts is { Count: > 0 })
        {
            w.WriteStartObject("type_counts");
            foreach (var kv in m.TypeCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
        }
        w.WriteString("epoch", m.Epoch);
        w.WriteEndObject();
    }

    // ---- per-lane artifact builders -----------------------------------------------------------------

    /// <summary>Build + save the artifact for a cross_plugin_query result: group_by count rows, detail rows
    /// (fields=), or summary rows — the SAME row shapes the json render emits, via the SAME row writers, filled
    /// off the SAME pinned view the response's epoch names (ResolveReadOn/ResolveSummaryOn — a spill must never
    /// mix builds the header claims it didn't). Returns the SpillInfo for the response marker, or a named error
    /// the caller renders (an unwritable path must fail LOUD, not produce a response claiming a file — Q3).
    /// <para><paramref name="rowCap"/> is the per-row field budget. The artifact IS the answer, so production
    /// writes rows uncapped and never passes it; it exists so the row writer's truncation seam can be DRIVEN,
    /// because a seam nothing can reach is a seam nothing can prove (#439 gate review).</para></summary>
    public static (SpillInfo? Spill, string? Error) WriteCrossQuery(
        LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields,
        bool resolveNames, bool winnerFields, int depth,
        string path, string reason, IReadOnlyList<KeyValuePair<string, string>> query,
        LeverNames? levers = null, int rowCap = int.MaxValue)
    {
        using var writer = new ResultArtifact.Writer();
        string[] schema;
        string? identity;
        string sort;
        var annotated = new SortedSet<string>(StringComparer.Ordinal);   // #342: which fields the ROWS carry annotated

        if (q.Groups is not null)                                             // group_by= → count-table rows
        {
            identity = null;                                                  // aggregate rows carry no per-record identity
            schema = new[] { "key", "count" };
            sort = "count desc, then key asc";
            foreach (var g in q.Groups)
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("key", g.Key); w.WriteNumber("count", g.Count); w.WriteEndObject(); });
        }
        else if (fields is { Count: > 0 })                                    // detail rows — full record objects
        {
            identity = "formid";
            schema = new[] { "formid", "type", "editorid", "winner", "override_depth", "source", "matches?", "fields" };
            sort = "load-order scan order (deterministic within one epoch)";
            var linkMemo = resolveNames ? new Dictionary<FormKey, ResolvedRef>() : null;
            for (int i = 0; i < q.Keys.Count; i++)
            {
                var fk = q.Keys[i];
                string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, depth,
                                          resolveNames: resolveNames, linkMemo: linkMemo,
                                          containerHint: (levers ?? LeverNames.Legacy).ContainerHint);   // an artifact row is read by the same caller (#439)
                if (o.Error is null && o.OwnedChildFields is { } af)   // #342: the rows' labels need their clause on line 1
                    foreach (var annotatedPath in af.Keys) annotated.Add(annotatedPath);
                if (o.Error is not null)
                    writer.WriteRow((w, _) =>
                    {
                        w.WriteStartObject(); w.WriteString("formid", fk.ToString()); w.WriteString("error", o.Error);
                        if (matches is not null) w.WriteString("matches", matches);
                        w.WriteEndObject();
                    });
                else
                    // The row writer composes its own field-truncation note, so it needs the caller's vocabulary
                    // like every other seam — a records artifact row must not say "narrow with fields=" (#439).
                    writer.WriteRow((w, ms) => JsonWire.WriteReadRecord(w, o, ms, rowCap, matches, levers: levers), o.Record!.Type);
            }
        }
        else                                                                  // summary rows
        {
            identity = "formid";
            schema = new[] { "formid", "type", "editorid", "winner", "override_depth", "matches?" };
            sort = "load-order scan order (deterministic within one epoch)";
            for (int i = 0; i < q.Keys.Count; i++)
            {
                var fk = q.Keys[i];
                string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                writer.WriteRow((w, _) => JsonWire.WriteSummaryRow(w, m, matches), m.Error is null ? m.Type : null);
            }
        }

        var (manifest, err) = writer.Save(path, "housecarl_cross_plugin_query", query, identity, schema, sort,
                                          q.Groups is not null ? q.Groups.Count : q.Total, q.Epoch ?? "",
                                          OwnedChildNotes(annotated));
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>The response-level #342 statements an artifact's ROWS depend on. An artifact is re-entered with no
    /// conversation attached, so a row's "N other plugins touch this record; their declarations were not read"
    /// label has to travel with the sentence that says what a child record is and where the precise answer lives.
    /// Only the CHEAP tier ever reaches a file: the artifact lanes read with conflict_tree off (it is a text-only
    /// diff view), so a row can only ever carry the cheap note.
    ///
    /// <para>The manifest is LINE 1 and the annotated rows are lines 2..N, so the clause's old "an annotated field
    /// above" pointed the wrong way for exactly the reader it was added for (Aaron's finding 2). It now names the
    /// annotated fields instead of pointing at them, which is true from line 1 and from anywhere else.</para></summary>
    static IReadOnlyList<string>? OwnedChildNotes(IReadOnlyCollection<string> annotatedFields) =>
        annotatedFields.Count == 0 ? null : new[] { ReadSentences.NotReadClause(annotatedFields) };

    /// <summary>The annotated field paths an artifact's rows CARRY. Rows are written uncapped (the file is the
    /// answer), so every annotated field of every row reaches the file — but the set is collected from the rows all
    /// the same, so the manifest cannot state a clause over an annotation no row wrote.</summary>
    static SortedSet<string> AnnotatedFields(IEnumerable<ReadOutcome> outcomes)
    {
        var s = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in outcomes)
            if (o.Error is null && o.OwnedChildFields is { } f)
                foreach (var path in f.Keys) s.Add(path);
        return s;
    }

    /// <summary>Build + save the artifact for a batch_record_detail result — one row per input, in input order,
    /// exactly the rows the json render emits (per-item errors included: the artifact is the complete answer, and
    /// a dropped error row would make the file claim a cleaner batch than the call returned).
    /// <para><paramref name="levers"/> and <paramref name="rowCap"/> carry the same contract as
    /// <see cref="WriteCrossQuery"/>: this writer's rows compose a field-truncation note too, so they need the
    /// caller's vocabulary, and production writes them uncapped.</para></summary>
    public static (SpillInfo? Spill, string? Error) WriteBatch(
        IReadOnlyList<ReadOutcome> outcomes, string path, string reason, IReadOnlyList<KeyValuePair<string, string>> query,
        LeverNames? levers = null, int rowCap = int.MaxValue)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var o in outcomes)
        {
            if (o.Error is not null)
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("formid", o.FormKey.ToString()); w.WriteString("error", o.Error); w.WriteEndObject(); });
            else
                writer.WriteRow((w, ms) => JsonWire.WriteReadRecord(w, o, ms, rowCap, levers: levers), o.Record!.Type);
        }
        // The batch's ONE build (first consulted row). A batch of pure parse-failures never consulted a build and
        // carries "" — such an artifact refuses epoch-checked re-entry against ANY build, which is the honest answer.
        var epoch = outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch ?? "";
        var (manifest, err) = writer.Save(path, "housecarl_batch_record_detail", query, "formid",
                                          new[] { "formid", "type", "editorid", "winner", "override_depth", "source", "fields" },
                                          "input order", outcomes.Count, epoch, OwnedChildNotes(AnnotatedFields(outcomes)));
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for a resolve result — one identity row per input, in input order,
    /// the json render's exact rows (per-item errors included).</summary>
    public static (SpillInfo? Spill, string? Error) WriteResolve(
        IReadOnlyList<ResolvedRef> rows, string epoch, string path, string reason, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var r in rows)
            writer.WriteRow((w, _) => JsonWire.WriteResolvedRow(w, r), r.Resolved ? r.Type : null);
        var (manifest, err) = writer.Save(path, "housecarl_resolve", query, "formid",
                                          new[] { "formid", "type", "editorid", "name", "winner" },
                                          "input order", rows.Count, epoch);
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for a records form=delta result — one row per input, in input order,
    /// exactly the rows the json render emits (per-item refusals included: a dropped P3/P4/untouched row would
    /// make the file claim a cleaner comparison than the call returned).</summary>
    public static (SpillInfo? Spill, string? Error) WriteDelta(
        IReadOnlyList<LoadOrderService.DeltaRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteDeltaRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Subject?.RecordType : null);
        var (manifest, err) = writer.Save(path, "housecarl_records", query, "formid",
                                          new[] { "formid", "type", "editorid", "subject", "reference", "stack_above?", "note?", "complete", "deltas", "delta_count", "agreed_count" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for a records form=tree result — the row form that makes trees
    /// SPILLABLE (PR #306 fold-decision 1): one row per record, the provider stack with per-node deltas, exactly
    /// the json render's rows.</summary>
    public static (SpillInfo? Spill, string? Error) WriteTree(
        IReadOnlyList<LoadOrderService.TreeRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteTreeRow(w, row, ms, int.MaxValue, LeverNames.Records),
                            row.Error is null ? row.Type : null);   // a records-only artifact: the rows speak the records vocabulary (#439)
        var (manifest, err) = writer.Save(path, "housecarl_records", query, "formid",
                                          new[] { "formid", "type", "editorid", "reference", "touchers", "nodes" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for a records form=chain result — one row per seed, in input order,
    /// exactly the json render's rows (nodes with provenance; cycles; truncation notes; the template report).</summary>
    public static (SpillInfo? Spill, string? Error) WriteChain(
        IReadOnlyList<LoadOrderService.WalkSeedResult> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteChainRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Type : null);
        var (manifest, err) = writer.Save(path, "housecarl_records", query, "formid",
                                          new[] { "formid", "type", "editorid", "nodes", "cycles?", "truncation?", "template_inheritance?" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for the reverse MGEF walk (records form=chain,
    /// walk.direction='reverse') — one row per (seed, carrier) with the matching entry's payload; a failed
    /// seed is an identity-less error row (errors are never identity-bearing).</summary>
    public static (SpillInfo? Spill, string? Error) WriteEffectChains(
        IReadOnlyList<(string Seed, EffectChainResult Result)> results, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        int total = 0;
        foreach (var (seed, r) in results)
        {
            if (r.Error is not null)
            {
                writer.WriteRow((w, _) => { w.WriteStartObject(); w.WriteString("seed", seed); w.WriteString("error", r.Error); w.WriteEndObject(); });
                total++;
                continue;
            }
            foreach (var row in r.Rows)
            {
                writer.WriteRow((w, _) =>
                {
                    w.WriteStartObject();
                    w.WriteString("seed", seed);
                    w.WriteString("formid", row.Carrier.ToString());
                    w.WriteString("type", row.Type);
                    if (row.EditorId is not null) w.WriteString("editorid", row.EditorId);
                    w.WriteString("winner", row.Winner);
                    w.WriteNumber("effect_index", row.EffectIndex);
                    w.WriteNumber("effect_count", row.EffectCount);
                    w.WriteNumber("magnitude", row.Magnitude);
                    w.WriteNumber("area", row.Area);
                    w.WriteNumber("duration", row.Duration);
                    w.WriteEndObject();
                }, row.Type);
                total++;
            }
        }
        var (manifest, err) = writer.Save(path, "housecarl_records", query, "formid",
                                          new[] { "seed", "formid", "type", "editorid", "winner", "effect_index", "effect_count", "magnitude", "area", "duration" },
                                          "seed order, then carrier scan order", total, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build + save the artifact for a records form=info_order result — one row per topic, in input
    /// order, exactly the json render's rows (honesty gates carried as data; per-item errors included).</summary>
    public static (SpillInfo? Spill, string? Error) WriteInfoOrder(
        IReadOnlyList<LoadOrderService.InfoOrderRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteInfoOrderRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Type : null);
        var (manifest, err) = writer.Save(path, "housecarl_records", query, "formid",
                                          new[] { "formid", "type", "editorid", "winner", "contested", "complete", "moves_computed", "baseline_trusted", "contributing", "unread?", "note?", "moved_count", "order" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Append the whole SpillState to a text response: the spilled block, or the failed-spill warning
    /// (a truncated response whose promised artifact could NOT be written must say so — the §2.1.1 "nothing is
    /// ever lost silently" promise would otherwise break exactly when the disk does).</summary>
    public static void AppendSpillStateText(StringBuilder sb, SpillState s)
    {
        if (s.Spill is not null) AppendSpillText(sb, s.Spill);
        else if (s.Failure is not null)
            sb.Append('\n').Append("WARNING: ").Append(s.Failure).Append('\n');
    }

    /// <summary>The json twin of <see cref="AppendSpillStateText"/> — written into an OPEN object (D2 pairing).</summary>
    public static void WriteSpillStateJson(Utf8JsonWriter w, SpillState s)
    {
        if (s.Spill is not null) WriteSpillJson(w, s.Spill);
        else if (s.Failure is not null) w.WriteString("spill_error", s.Failure);
    }

    /// <summary>Split a PLAIN list file's content into tokens — the same grammar the where-grammar's @file uses:
    /// commas/newlines separate (never bare spaces — plugin filenames contain them), brackets/quotes stripped per
    /// token so a pasted JSON array parses as-is.</summary>
    public static IEnumerable<string> SplitListTokens(string content)
    {
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length > 0) yield return tok;
        }
    }

    static readonly char[] ListSeparators = { ',', '\r', '\n' };

    /// <summary>Expand a list-valued tool input under the <c>@file</c> convention (SPEC §5.1): a single
    /// <c>"@&lt;absolute path&gt;"</c> element IN PLACE OF the inline list reads the file — a §2.1.1 ARTIFACT
    /// yields its identity column (formids) plus the epoch demand the consuming call must check; a plain file
    /// yields its comma/newline-separated tokens (no epoch claim). Mixing an @ element WITH inline entries is a
    /// named refusal (one spelling for the whole list, not a splice grammar). Non-@ input passes through
    /// untouched. <c>EchoSource</c> is what the query echo / manifest should say the list WAS ("@path" or null
    /// for inline).</summary>
    public static (string[]? Tokens, ArtifactDemand? Demand, string? EchoSource, string? Error) ExpandListInput(string[] items, string paramName)
    {
        // `is not null && TrimStart() is { Length: > 0 }` — a whitespace-only element must fall through to the
        // per-item "not a FormID" path, not throw on [0] and surface as a fake internal failure (PR #306 review).
        int atCount = items.Count(i => i is not null && i.TrimStart() is { Length: > 0 } t && t[0] == '@');
        if (atCount == 0) return (items, null, null, null);
        if (items.Length > 1)
            return (null, null, null, $"error: {paramName}= mixes an '@file' entry with inline entries — '@<path>' stands IN PLACE OF the whole list. " +
                                      $"Pass {paramName}=[\"@<path>\"] alone, or put every entry in the file.");
        var path = items[0].TrimStart().Substring(1).Trim().Trim('"', '\'');
        if (path.Length == 0)
            return (null, null, null, $"error: {paramName}= '@' names a list file but no path follows it.");
        if (!Path.IsPathRooted(path))
            return (null, null, null, $"error: {paramName}= list file '{path}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.");
        string content;
        try { content = File.ReadAllText(path); }
        catch (Exception ex) { return (null, null, null, $"error: could not read {paramName}= list file '{path}' — {ex.GetType().Name}: {ex.Message}"); }

        if (ResultArtifact.LooksLikeArtifact(content))
        {
            var (manifest, tokens, aerr) = ResultArtifact.ReadIdentity(path, content);
            if (aerr is not null) return (null, null, null, "error: " + aerr);
            if (!manifest!.Identity!.Equals("formid", StringComparison.OrdinalIgnoreCase))
                return (null, null, null, $"error: artifact '{path}' (from {manifest.Tool}) carries '{manifest.Identity}' identities, " +
                                          $"not FormIDs — there is no formid list in it for {paramName}=.");
            return (tokens!.ToArray(), new ArtifactDemand(path, manifest.Epoch), "@" + path, null);
        }

        var plain = SplitListTokens(content).ToArray();
        if (plain.Length == 0)
            return (null, null, null, $"error: {paramName}= list file '{path}' is empty — give one entry per line (or comma-separated).");
        return (plain, null, "@" + path, null);
    }

    // ---- to_file validation -------------------------------------------------------------------------

    /// <summary>Validate a caller-named <c>to_file=</c> target: absolute, .jsonl-suffixed (the artifact IS jsonl —
    /// a .csv/.txt name would promise a format the file doesn't have), and NOT inside the auto-spill results
    /// directory — the server prunes that folder by age, so a caller-named artifact there would be silently
    /// destroyed by hygiene (PR #306 review: the check the doc claimed, now real). Null = fine; else the named
    /// refusal.</summary>
    public static string? ValidateToFile(string toFile)
    {
        var p = toFile.Trim();
        if (p.Length == 0) return "error: to_file= is empty — give the ABSOLUTE path the artifact should be written to (e.g. 'C:\\work\\weapons.jsonl').";
        if (!Path.IsPathRooted(p)) return $"error: to_file='{toFile}' must be an ABSOLUTE path — the server resolves relative paths against its OWN working directory, not yours.";
        if (!p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            return $"error: to_file='{toFile}' — the artifact is a JSONL file (line 1 = manifest, one JSON row per line); name it with a .jsonl extension so the file says what it is.";
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(p) ?? "");
            var results = Path.GetFullPath(ResultsStore.Dir);
            if (string.Equals(dir.TrimEnd('\\', '/'), results.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return $"error: to_file='{toFile}' points into the server's auto-spill results directory ('{results}'), " +
                       $"which is pruned by age after {ResultsStore.PruneAfterDays} days — your artifact would be silently deleted by that hygiene. " +
                       "Name a path outside it; the server owns that folder's lifecycle.";
        }
        catch (Exception) { /* an unnormalizable path fails later with the writer's own named error */ }
        return null;
    }
}
