using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>What the response says when its full result lives in an artifact file: the path, its parsed-back
/// manifest, and why it was written — <c>to_file</c> (the caller asked) or <c>ceiling</c> (the inline render hit
/// max_chars). Threaded into the renders so the <c>spilled</c> marker rides in-band in both formats; appending it
/// outside the json document would make it invisible to a json consumer.</summary>
internal sealed record SpillInfo(string Path, ResultArtifact.Manifest Manifest, string Reason)
{
    public bool ToFile => Reason == "to_file";
}

/// <summary>How a render learns its call's artifact disposition as one value: a successful spill (with whether the
/// rows are omitted, the to_file manifest-only render), or a failed one, where the response must say its
/// truncation has no complete artifact behind it. Null means an ordinary inline response.</summary>
internal sealed record SpillState(SpillInfo? Spill, string? Failure, bool ManifestOnly)
{
    public static SpillState Spilled(SpillInfo s, bool manifestOnly) => new(s, null, manifestOnly);

    /// <summary>The spill write failed: the artifact was promised and could not be produced. The emitters render
    /// this text verbatim, so it carries the recovery moves itself.</summary>
    public static SpillState WriteFailed(string error) => new(null,
        "the response is truncated and the auto-spill artifact could NOT be written — " + error +
        " The complete result exists NOWHERE; re-run with a narrower filter, a higher max_chars, or to_file= at a writable path.", false);

    /// <summary>The result has no spillable row form, the same reason to_file= refuses it, so no artifact was
    /// attempted: writing thinner rows under a completeness claim would misrepresent the file.</summary>
    public static SpillState NoRowForm() => new(null,
        "the response is truncated and was NOT auto-spilled: conflict_tree=true has no JSONL row form (the same reason " +
        "to_file= refuses it), and spilling thinner tree-less rows under a completeness claim would misrepresent the file. " +
        "The complete trees exist only inline — raise max_chars, narrow the set, or drop conflict_tree (plain rows spill fine).", false);
}

/// <summary>The artifact layer for the bulk read lanes: per-lane builders, each writing the rows its json render
/// emits through the same row writers so file and wire can differ only in formatting, plus the shared in-band
/// emitter for the <c>spilled</c> marker (one wording for text, one shape for json, used by every lane).</summary>
internal static class Artifacts
{
    // ---- the shared spilled-marker emitter (text) ---------------------------------------------------

    /// <summary>Append the <c>spilled</c> block to a text response. The marker must name the artifact path, or a
    /// caller can only refuse, re-pay the scan, or fabricate one. It also carries the manifest facts needed to
    /// pick the next move without opening the file: row count, schema, identity column, epoch.</summary>
    public static void AppendSpillText(StringBuilder sb, SpillInfo s)
    {
        var m = s.Manifest;
        // "complete result" may be claimed only when the file holds every match: a spilled limit= window is
        // complete as a window, and the matches beyond limit= are in no file at all.
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

    /// <summary>Write the <c>spilled</c> member into an open json object — the same facts as the text block. The
    /// presence of <c>spilled.path</c> is itself the marker.</summary>
    public static void WriteSpillJson(Utf8JsonWriter w, SpillInfo s)
    {
        var m = s.Manifest;
        w.WriteStartObject("spilled");
        w.WriteString("path", s.Path);
        w.WriteString("reason", s.ToFile ? "to_file" : "over_inline_ceiling");
        w.WriteNumber("row_count", m.RowCount);
        w.WriteNumber("total", m.Total);
        // Stated explicitly rather than left derivable: false means the file is a window and the matches beyond
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

    /// <summary>Build and save the artifact for a scan result: group_by count rows, detail rows, or summary rows —
    /// the same row shapes the json render emits, filled off the same pinned view the response's epoch names, so a
    /// spill can never mix builds the header did not claim. Returns the SpillInfo, or a named error the caller
    /// renders. <paramref name="rowCap"/> is the per-row field budget; production writes rows uncapped, and it
    /// exists so the row writer's truncation seam can be driven by a test.</summary>
    public static (SpillInfo? Spill, string? Error) WriteCrossQuery(
        LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields,
        bool resolveNames, bool winnerFields, int depth,
        string path, string reason, IReadOnlyList<KeyValuePair<string, string>> query,
        LeverNames? levers = null, int rowCap = int.MaxValue, FoldPlan? fold = null,
        CancellationToken ct = default)
    {
        using var writer = new ResultArtifact.Writer();
        string[] schema;
        string? identity;
        string sort;
        var annotated = new SortedSet<string>(StringComparer.Ordinal);   // which fields the rows carry annotated
        bool annotatedUnioned = false;                                   // and which TIER they stated

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
            schema = new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth", "source", "matches?", "fields" };
            sort = "load-order scan order (deterministic within one epoch)";
            var foldDepths = fold?.Read().Depths;   // the quantified paths' depth, and the caller's own for the rest
            // The artifact reads through the SAME reader the inline renders do: one session, one chunked body
            // prefetch, and the per-row cancellation check. A cancel here throws before Save, so no half artifact
            // reaches disk — the rows only exist in the writer's buffer until then.
            using var reader = new ScanDetailReader(svc, q, fields, depth, resolveNames, winnerFields,
                                                    (levers ?? LeverNames.Legacy).ContainerHint, foldDepths, ct);
            for (int i = 0; i < q.Keys.Count; i++)
            {
                var fk = q.Keys[i];
                string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                var o = reader.Row(i);   // an artifact row is read by the same caller
                if (fold is not null) o = fold.Apply(o);   // an artifact row carries the same folded fields the render does
                if (o.Error is null && o.OwnedChildFields is { } af)   // the rows' labels need their clause on line 1
                {
                    foreach (var annotatedPath in af.Keys) annotated.Add(annotatedPath);
                    annotatedUnioned |= o.OwnedChildUnioned;
                }
                if (o.Error is not null)
                    writer.WriteRow((w, _) =>
                    {
                        w.WriteStartObject(); w.WriteString("formid", fk.ToString()); w.WriteString("error", o.Error);
                        if (matches is not null) w.WriteString("matches", matches);
                        w.WriteEndObject();
                    });
                else
                    // The row writer composes its own field-truncation note, so it needs the caller's vocabulary:
                    // an artifact row must not name a parameter the calling tool does not have.
                    writer.WriteRow((w, ms) => JsonWire.WriteReadRecord(w, o, ms, rowCap, matches, levers: levers), o.Record!.Type);
            }
        }
        else                                                                  // summary rows
        {
            identity = "formid";
            schema = new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth", "matches?" };
            sort = "load-order scan order (deterministic within one epoch)";
            for (int i = 0; i < q.Keys.Count; i++)
            {
                var fk = q.Keys[i];
                string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                writer.WriteRow((w, _) => JsonWire.WriteSummaryRow(w, m, matches), m.Error is null ? m.Type : null);
            }
        }

        // The manifest stamps which tool wrote the artifact; see WriteResolve for why it names records.
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, identity, schema, sort,
                                          q.Groups is not null ? q.Groups.Count : q.Total, q.Epoch ?? "",
                                          CrossQueryNotes(q, fields, winnerFields, annotated, annotatedUnioned, levers));
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>The response-level statement that an artifact's annotated rows depend on. An artifact is re-entered
    /// with no conversation attached, so a row's union or "not read" label must travel with the sentence explaining
    /// it. The manifest is line 1 and the rows are lines 2..N, so this names the annotated fields rather than
    /// pointing at a position. <paramref name="unioned"/> picks the tier the rows actually stated. The precise
    /// tier's note is <see cref="PreciseChildNotes"/>.</summary>
    static IReadOnlyList<string>? OwnedChildNotes(IReadOnlyCollection<string> annotatedFields, bool unioned) =>
        annotatedFields.Count == 0 ? null : new[] { ReadSentences.OwnedChildClause(annotatedFields, unioned) };

    /// <summary>The manifest notes a scan artifact carries: the scoped-vs-winner field-source note the three inline
    /// transports state, then the owned-child clause. The artifact holds the same values the inline render would have
    /// shown and is read later with no conversation attached, so the sentence saying WHOSE body those values came
    /// from has to travel with them. The scoped test is <see cref="JsonWire.AnyScopedFieldRow"/> — the very function
    /// the inline renders call, not a copy of it — so the two cannot disagree about when the note is owed.</summary>
    static IReadOnlyList<string>? CrossQueryNotes(CrossQueryOutcome q, IReadOnlyList<string>? fields, bool winnerFields,
                                                  IReadOnlyCollection<string> annotatedFields, bool annotatedUnioned,
                                                  LeverNames? levers)
    {
        var notes = new List<string>();
        if (JsonWire.AnyScopedFieldRow(q, fields))
            notes.Add(JsonWire.ScopedFieldsNote(winnerFields, q.WhereWinner, levers));
        if (OwnedChildNotes(annotatedFields, annotatedUnioned) is { } child) notes.AddRange(child);
        return notes.Count > 0 ? notes : null;
    }

    /// <summary>The precise tier's response-level note for a tree artifact: <see cref="ReadSentences.DeclarersLead"/>
    /// stated once rather than per row, when any row's <c>child_declarers</c> reached the file.</summary>
    static IReadOnlyList<string>? PreciseChildNotes(IReadOnlyList<LoadOrderService.TreeRow> rows) =>
        rows.Any(r => r.Error is null && r.ChildDeclarers.Count > 0) ? new[] { ReadSentences.DeclarersLead } : null;

    /// <summary>The annotated field paths an artifact's rows actually carry. The set is collected from the rows
    /// themselves so the manifest can never state a clause over an annotation no row wrote.</summary>
    static SortedSet<string> AnnotatedFields(IEnumerable<ReadOutcome> outcomes)
    {
        var s = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in outcomes)
            if (o.Error is null && o.OwnedChildFields is { } f)
                foreach (var path in f.Keys) s.Add(path);
        return s;
    }

    /// <summary>Build and save the artifact for a batch read — one row per input, in input order, exactly the rows
    /// the json render emits. Per-item errors are included: dropping them would make the file claim a cleaner
    /// batch than the call returned. <paramref name="levers"/> and <paramref name="rowCap"/> carry the same
    /// contract as on <see cref="WriteCrossQuery"/>.</summary>
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
        // The batch's one build, from the first row that consulted one. A batch of pure parse failures carries "",
        // and such an artifact refuses epoch-checked re-entry against any build.
        var epoch = outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch ?? "";
        // The manifest's tool stamp; see WriteResolve.
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth", "source", "fields" },
                                          "input order", outcomes.Count, epoch, OwnedChildNotes(AnnotatedFields(outcomes), outcomes.Any(o => o.OwnedChildUnioned)));
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for an identity result — one row per input, in input order, the json
    /// render's exact rows, per-item errors included.</summary>
    public static (SpillInfo? Spill, string? Error) WriteResolve(
        IReadOnlyList<ResolvedRef> rows, string epoch, string path, string reason, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var r in rows)
            writer.WriteRow((w, _) => JsonWire.WriteResolvedRow(w, r), r.Resolved ? r.Type : null);
        // The manifest records which tool wrote the artifact, and a re-entry refusal reads it back and prints it.
        // It must name a tool the surface still has, or the refusal quotes a dead name.
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "type", "editorid", "name", "winner" },
                                          "input order", rows.Count, epoch);
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for a delta result — one row per input, in input order, exactly the
    /// rows the json render emits. Per-item refusals are included: dropping one would make the file claim a
    /// cleaner comparison than the call returned.</summary>
    public static (SpillInfo? Spill, string? Error) WriteDelta(
        IReadOnlyList<LoadOrderService.DeltaRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteDeltaRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Subject?.RecordType : null);
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "type", "editorid", "subject", "reference", "stack_above?", "note?", "complete", "deltas", "delta_count", "agreed_count" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for a tree result — the row form that makes trees spillable: one row
    /// per record, the provider stack with per-node deltas, exactly the json render's rows.</summary>
    public static (SpillInfo? Spill, string? Error) WriteTree(
        IReadOnlyList<LoadOrderService.TreeRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteTreeRow(w, row, ms, int.MaxValue, LeverNames.Records),
                            row.Error is null ? row.Type : null);   // a records-only artifact: the rows speak the records vocabulary
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "type", "editorid", "reference", "touchers", "child_declarers?", "nodes" },
                                          "input order", rows.Count, epoch ?? "", PreciseChildNotes(rows));
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for a chain result — one row per seed, in input order, exactly the
    /// json render's rows: nodes with provenance, cycles, truncation notes, the template report.</summary>
    public static (SpillInfo? Spill, string? Error) WriteChain(
        IReadOnlyList<LoadOrderService.WalkSeedResult> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteChainRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Type : null);
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "type", "editorid", "nodes", "cycles?", "truncation?", "template_inheritance?" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for the reverse MGEF walk — one row per (seed, carrier) with the
    /// matching entry's payload. A failed seed is an identity-less error row.</summary>
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
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "seed", "formid", "type", "editorid", "winner", "effect_index", "effect_count", "magnitude", "area", "duration" },
                                          "seed order, then carrier scan order", total, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Build and save the artifact for an info_order result — one row per topic, in input order, exactly
    /// the json render's rows, with the confidence gates carried as data and per-item errors included.</summary>
    public static (SpillInfo? Spill, string? Error) WriteInfoOrder(
        IReadOnlyList<LoadOrderService.InfoOrderRow> rows, string? epoch, string path, string reason,
        IReadOnlyList<KeyValuePair<string, string>> query)
    {
        using var writer = new ResultArtifact.Writer();
        foreach (var row in rows)
            writer.WriteRow((w, ms) => JsonWire.WriteInfoOrderRow(w, row, ms, int.MaxValue),
                            row.Error is null ? row.Type : null);
        var (manifest, err) = writer.Save(path, ToolNames.Records, query, "formid",
                                          new[] { "formid", "type", "editorid", "winner", "contested", "complete", "moves_computed", "baseline_trusted", "contributing", "unread?", "note?", "moved_count", "order" },
                                          "input order", rows.Count, epoch ?? "");
        return err is not null ? (null, err) : (new SpillInfo(path, manifest!, reason), null);
    }

    /// <summary>Append the whole SpillState to a text response: the spilled block, or the failed-spill warning. A
    /// truncated response whose promised artifact could not be written must say so.</summary>
    public static void AppendSpillStateText(StringBuilder sb, SpillState s)
    {
        if (s.Spill is not null) AppendSpillText(sb, s.Spill);
        else if (s.Failure is not null)
            sb.Append('\n').Append("WARNING: ").Append(s.Failure).Append('\n');
    }

    /// <summary>The json twin of <see cref="AppendSpillStateText"/>, written into an open object.</summary>
    public static void WriteSpillStateJson(Utf8JsonWriter w, SpillState s)
    {
        if (s.Spill is not null) WriteSpillJson(w, s.Spill);
        else if (s.Failure is not null) w.WriteString("spill_error", s.Failure);
    }

    /// <summary>Split a plain list file's content into tokens, the same grammar the where-grammar's @file uses:
    /// commas and newlines separate — never bare spaces, since plugin filenames contain them — and brackets and
    /// quotes are stripped per token so a pasted JSON array parses as-is.</summary>
    public static IEnumerable<string> SplitListTokens(string content)
    {
        foreach (var t in content.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tok = t.Trim('[', ']', '"', '\'', ' ', '\t');
            if (tok.Length > 0) yield return tok;
        }
    }

    static readonly char[] ListSeparators = { ',', '\r', '\n' };

    /// <summary>Expand a list-valued tool input under the <c>@file</c> convention: a single
    /// <c>"@&lt;absolute path&gt;"</c> element standing in place of the inline list reads the file. An artifact
    /// yields its identity column plus the epoch demand the consuming call must check; a plain file yields its
    /// tokens and claims no epoch. Mixing an @ element with inline entries is a named refusal — it is one
    /// spelling for the whole list, not a splice grammar. Non-@ input passes through untouched.
    /// <c>EchoSource</c> is what the query echo and manifest should say the list was.</summary>
    public static (string[]? Tokens, ArtifactDemand? Demand, string? EchoSource, string? Error) ExpandListInput(string[] items, string paramName)
    {
        // The null/length guards matter: a whitespace-only element must fall through to the per-item "not a
        // FormID" path rather than index [0] and surface as an internal failure.
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

    /// <summary>Validate a caller-named <c>to_file=</c> target: absolute, .jsonl-suffixed (the artifact is jsonl,
    /// and another extension would promise a format the file does not have), and not inside the auto-spill
    /// results directory, which the server prunes by age. Null means fine; else the named refusal.</summary>
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
