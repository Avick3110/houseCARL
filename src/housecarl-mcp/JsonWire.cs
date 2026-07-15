using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The machine-readable (format="json") twin of the text <see cref="Wire"/> renderer (Wave 2 / P6). ONE
/// serializer per read tool, each consuming the SAME outcome objects the text Wire consumes — so text and JSON can
/// only differ in FORMATTING, never in DATA (decision D2: one read path, two renders). Field VALUES are the SAME wire
/// tokens the text mode emits (round-trip parity: a token read out of JSON is still a value a write can reuse
/// verbatim). Q3 accounting (total / capped / truncated / notes) rides INSIDE the document, so JSON is never a
/// silently degraded mode.
///
/// <para>Truncation drops trailing ROWS and flags it (<c>truncated:true</c> + <c>rendered</c>) — the emitted
/// document ALWAYS stays valid JSON. Cutting the serialized string at a byte budget the way the text render cuts its
/// StringBuilder would emit malformed JSON, itself a silent-degrade Q3 break, so the JSON path never does that.</para>
///
/// <para>The resolve_names annotation (P7) rides as a STRUCTURED sibling on each field object
/// (<c>resolved:{editorid,name,type}</c>), never a mangled token — the JSON counterpart of the text render's
/// parenthetical.</para></summary>
static class JsonWire
{
    static readonly JsonWriterOptions Opts = new() { Indented = true };

    static string Finish(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray());

    static void WriteNullable(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }

    // ---- housecarl_resolve (P3) ---------------------------------------------------------------------
    /// <summary>Render the bulk name-resolution result as JSON: <c>{count, resolved:[…], rendered, truncated}</c> —
    /// one <c>{formid,type,editorid,name,winner}</c> row per resolvable input, or <c>{formid,error}</c> for a
    /// bad/absent one (per-item, the batch survives — Q3). Budget-aware like the other JSON renders: over max_chars it
    /// drops trailing rows and flags <c>truncated</c>, keeping the document valid JSON with an exact <c>count</c>.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteNumber("count", rows.Count);
            w.WriteStartArray("resolved");
            int rendered = 0; bool truncated = false;
            foreach (var r in rows)
            {
                w.Flush();
                if (ms.Length >= cap) { truncated = true; break; }
                WriteResolvedRow(w, r);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", truncated);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One housecarl_resolve row. Resolved ⇒ the identity fields; not resolved ⇒ a single <c>error</c>
    /// (the malformed-FormID reason, or "not present in the active order" for a valid-but-absent FormKey).</summary>
    static void WriteResolvedRow(Utf8JsonWriter w, ResolvedRef r)
    {
        w.WriteStartObject();
        w.WriteString("formid", r.Token);
        if (r.Resolved)
        {
            WriteNullable(w, "type", r.Type);
            WriteNullable(w, "editorid", r.EditorId);
            WriteNullable(w, "name", r.Name);
            WriteNullable(w, "winner", r.Winner);
        }
        else w.WriteString("error", r.Error ?? "not present in the active order");
        w.WriteEndObject();
    }

    // ---- housecarl_diff_record (P8c) ----------------------------------------------------------------
    /// <summary>Render a pairwise record diff as JSON: <c>{formid, a:{plugin,where,in_order,type,editorid}, b:{…},
    /// complete, deltas:[…], delta_count, rendered, truncated, agreed_count, agreed_sample:[…]}</c>. Deltas are the SAME
    /// strings text emits; budget-aware (drops trailing deltas past max_chars, flags <c>truncated</c>) and always valid
    /// JSON. On refusal a single <c>{formid, error}</c>.</summary>
    public static string RenderDiffRecord(LoadOrderService.DiffRecordOutcome o, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("formid", o.Formid);
            if (o.Error is not null) w.WriteString("error", o.Error);
            else
            {
                WriteDiffPole(w, "a", o.A!);
                WriteDiffPole(w, "b", o.B!);
                var d = o.Diff!;
                w.WriteBoolean("complete", d.Complete);
                w.WriteStartArray("deltas");
                int rendered = 0; bool truncated = false;
                foreach (var delta in d.Deltas)
                {
                    w.Flush();
                    if (ms.Length >= cap) { truncated = true; break; }
                    w.WriteStringValue(delta);
                    rendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("delta_count", d.Deltas.Count);
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", truncated);
                w.WriteNumber("agreed_count", d.AgreedCount);
                WriteStringArray(w, "agreed_sample", d.AgreedSample);
            }
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    static void WriteDiffPole(Utf8JsonWriter w, string name, LoadOrderService.DiffPole p)
    {
        w.WriteStartObject(name);
        w.WriteString("plugin", p.Plugin);
        w.WriteString("where", p.Where);
        w.WriteBoolean("in_order", p.InOrder);
        WriteNullable(w, "type", p.RecordType);
        WriteNullable(w, "editorid", p.EditorId);
        w.WriteEndObject();
    }

    static int Cap(int maxChars) => maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

    static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    // ---- shared record + field writers (P6/P7) ------------------------------------------------------
    /// <summary>Serialize the fields array. Each leaf is <c>{path, value}</c> for a round-trippable leaf (value = the
    /// SAME wire token the text mode emits) or <c>{path, note}</c> for a no-value leaf; the display-only <c>display</c>
    /// (biped slots) and the resolve_names <c>link</c> sibling (P7) ride alongside, never in place of the token.
    /// BUDGET-AWARE: a fat record (deep list expansion) is field-truncated the same way the text render caps field
    /// lines — a sentinel field names the cut and the array closes, so the document stays valid JSON (never silently
    /// over budget — Q3).</summary>
    static void WriteFieldsArray(Utf8JsonWriter w, RecordFields r, MemoryStream ms, int cap)
    {
        w.WriteStartArray("fields");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            w.Flush();
            if (ms.Length >= cap)
            {
                w.WriteStartObject();
                w.WriteString("path", "…");   // …
                w.WriteString("note", $"[truncated at max_chars: {i} of {r.Fields.Count} fields shown; narrow with fields=, lower depth=, or raise max_chars]");
                w.WriteEndObject();
                break;
            }
            var f = r.Fields[i];
            w.WriteStartObject();
            w.WriteString("path", f.Path);
            if (f.HasValue) w.WriteString("value", f.Token);   // round-trip parity: identical token to the text render
            else WriteNullable(w, "note", f.Note);
            if (f.Display is not null) w.WriteString("display", f.Display);
            if (f.Link is { } link)
            {
                w.WriteStartObject("link");
                w.WriteBoolean("resolved", link.Resolved);
                if (link.Resolved)
                {
                    WriteNullable(w, "type", link.Type);
                    WriteNullable(w, "editorid", link.EditorId);
                    WriteNullable(w, "name", link.Name);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>Serialize a resolved record: identity + winner/override_depth/source + the fields array. Shared by
    /// read_record, batch_record_detail, and the cross_plugin_query detail path (one shape, no drift). <paramref
    /// name="matches"/> carries the multi-target references= un-merge when present.</summary>
    static void WriteReadRecord(Utf8JsonWriter w, ReadOutcome o, MemoryStream ms, int cap, string? matches = null)
    {
        var r = o.Record!;
        w.WriteStartObject();
        w.WriteString("formid", r.FormKey);
        w.WriteString("type", r.Type);
        WriteNullable(w, "editorid", r.EditorId);
        WriteNullable(w, "winner", o.WinnerPlugin);
        w.WriteNumber("override_depth", o.OverrideDepth);
        WriteNullable(w, "source", o.SourcePlugin);   // the body these field VALUES came from (scoped plugin vs winner)
        if (matches is not null) w.WriteString("matches", matches);
        WriteFieldsArray(w, r, ms, cap);
        w.WriteEndObject();
    }

    // ---- housecarl_read_record (P6) -----------------------------------------------------------------
    /// <summary>read_record as JSON: the record object at top level, or <c>{error}</c>. conflict_tree is refused at
    /// the tool layer for json (a text-only diff view), so only the field data reaches here.</summary>
    public static string RenderRecord(ReadOutcome o, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            if (o.Error is not null) { w.WriteStartObject(); w.WriteString("error", o.Error); w.WriteEndObject(); }
            else WriteReadRecord(w, o, ms, cap);
        }
        return Finish(ms);
    }

    // ---- housecarl_batch_record_detail (P6) ---------------------------------------------------------
    /// <summary>batch_record_detail as JSON: <c>{count, records:[…], rendered, truncated}</c>. A bad/absent formid is
    /// a per-item <c>{formid,error}</c> (the batch survives). Truncation drops trailing records and flags it — the
    /// document stays valid JSON (Q3), and count is exact.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteNumber("count", outcomes.Count);
            w.WriteStartArray("records");
            int rendered = 0; bool truncated = false;
            foreach (var o in outcomes)
            {
                w.Flush();
                if (ms.Length >= cap) { truncated = true; break; }
                if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", o.FormKey.ToString()); w.WriteString("error", o.Error); w.WriteEndObject(); }
                else WriteReadRecord(w, o, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", truncated);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_cross_plugin_query (P6) ----------------------------------------------------------
    /// <summary>cross_plugin_query as JSON — three shapes matching the text render: group_by count table
    /// (<c>{group_by, total, groups:[…]}</c>), detail rows (full record objects with fields), or summary rows
    /// (<c>{formid,type,editorid,winner,override_depth}</c>). Q3 accounting (total/capped/notes/truncated) rides
    /// in-band. The detail path threads resolve_names through the SAME ResolveRead the text render uses, so the two
    /// modes read one path.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            if (q.Error is not null) w.WriteString("error", q.Error);
            else if (q.Groups is not null)                                   // group_by= → count table
            {
                WriteNullable(w, "group_by", q.GroupBy);
                w.WriteNumber("total", q.Total);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q);
                w.WriteStartArray("groups");
                int gRendered = 0; bool gTrunc = false;
                foreach (var g in q.Groups)
                {
                    w.Flush();
                    if (ms.Length >= cap) { gTrunc = true; break; }
                    w.WriteStartObject(); w.WriteString("key", g.Key); w.WriteNumber("count", g.Count); w.WriteEndObject();
                    gRendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", gRendered);
                w.WriteBoolean("truncated", gTrunc);
            }
            else                                                            // per-match: detail (fields=) or summary
            {
                bool detail = fields is { Count: > 0 };
                bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // P5
                string? p5 = anyScoped
                    ? (winnerFields ? "field values are the load-order WINNER's (winner_fields=true); each match was SELECTED on its scoped plugin's body."
                                    : "field values are each match's SCOPED plugin's OWN version, NOT the live load-order winner — pass winner_fields=true for load-order truth.")
                    : null;
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, p5);
                var linkMemo = resolveNames && detail ? new Dictionary<FormKey, ResolvedRef>() : null;
                w.WriteStartArray("matches");
                int rendered = 0; bool truncated = false;
                for (int i = 0; i < q.Keys.Count; i++)
                {
                    w.Flush();
                    if (ms.Length >= cap) { truncated = true; break; }
                    var fk = q.Keys[i];
                    string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                    if (detail)
                    {
                        // winner_fields=: read the WINNER's body (source=null) regardless of scan scope; the record's
                        // "source" field still names the body read, so the json carries the same source/winner truth.
                        var o = svc.ResolveRead(fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, resolveNames: resolveNames, linkMemo: linkMemo);
                        if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", fk.ToString()); w.WriteString("error", o.Error); if (matches is not null) w.WriteString("matches", matches); w.WriteEndObject(); }
                        else WriteReadRecord(w, o, ms, cap, matches);
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummary(fk);
                        WriteSummaryRow(w, m, matches);
                    }
                    rendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", truncated);
            }
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    static void WriteSummaryRow(Utf8JsonWriter w, RecordSummary m, string? matches)
    {
        w.WriteStartObject();
        w.WriteString("formid", m.FormKey.ToString());
        if (m.Error is not null) w.WriteString("error", m.Error);
        else
        {
            w.WriteString("type", m.Type);
            WriteNullable(w, "editorid", m.EditorId);
            w.WriteString("winner", m.Winner);
            w.WriteNumber("override_depth", m.OverrideDepth);
        }
        if (matches is not null) w.WriteString("matches", matches);
        w.WriteEndObject();
    }

    /// <summary>Q3 accounting notes (where= predicate note, unscannable-record note) carried IN the JSON document —
    /// so json is never a silently degraded mode vs text. Omitted when there are none.</summary>
    static void WriteNotes(Utf8JsonWriter w, CrossQueryOutcome q, string? extra = null)
    {
        if (q.PredicateNote is null && q.ScanNote is null && extra is null) return;
        w.WriteStartArray("notes");
        if (q.PredicateNote is not null) w.WriteStringValue(q.PredicateNote);
        if (q.ScanNote is not null) w.WriteStringValue(q.ScanNote);
        if (extra is not null) w.WriteStringValue(extra);   // P5 scoped-vs-winner fields note
        w.WriteEndArray();
    }

    // ---- housecarl_read_plugin_file (P6) ------------------------------------------------------------
    /// <summary>read_plugin_file as JSON — always stamped <c>out_of_load_order:true</c> (the load-bearing raw-file
    /// caveat), then the file/masters context and the mode payload: <c>record</c> (the FILE's own record — no winner,
    /// it's not resolved), <c>records</c> (enumerate), or <c>type_counts</c> (summary). <c>error</c>/<c>ambiguous</c>
    /// on failure.</summary>
    public static string RenderPluginFile(PluginFileOutcome o, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            if (o.Mode == "error") { w.WriteString("error", o.Error); }
            else if (o.Mode == "ambiguous")
            {
                w.WriteString("error", $"'{Path.GetFileName(o.Requested)}' is provided by {o.Ambiguous.Count} locations — specify which with mod= (or pass an absolute path).");
                w.WriteStartArray("ambiguous");
                foreach (var h in o.Ambiguous) { w.WriteStartObject(); w.WriteString("where", h.Where); w.WriteString("path", h.Path); w.WriteEndObject(); }
                w.WriteEndArray();
            }
            else
            {
                w.WriteBoolean("out_of_load_order", true);
                WriteNullable(w, "file", o.FilePath);
                WriteNullable(w, "where", o.Where);
                w.WriteBoolean("enabled", o.Enabled);
                WriteStringArray(w, "masters", o.Masters);
                WriteStringArray(w, "missing_masters", o.MissingMasters);
                WriteStringArray(w, "inactive_masters", o.InactiveMasters);
                w.WriteString("mode", o.Mode);
                if (o.Mode == "read" && o.Record is { } rf)
                {
                    w.WritePropertyName("record");
                    w.WriteStartObject();
                    w.WriteString("formid", rf.FormKey);
                    w.WriteString("type", rf.Type);
                    WriteNullable(w, "editorid", rf.EditorId);
                    WriteFieldsArray(w, rf, ms, cap);
                    w.WriteEndObject();
                }
                else if (o.Mode == "enumerate")
                {
                    w.WriteNumber("total", o.RowTotal);
                    w.WriteBoolean("capped", o.Capped);
                    w.WriteStartArray("records");
                    int rendered = 0; bool truncated = false;
                    foreach (var row in o.Rows)
                    {
                        w.Flush();
                        if (ms.Length >= cap) { truncated = true; break; }
                        w.WriteStartObject(); w.WriteString("formid", row.FormKey); w.WriteString("type", row.Type); WriteNullable(w, "editorid", row.EditorId); w.WriteEndObject();
                        rendered++;
                    }
                    w.WriteEndArray();
                    w.WriteNumber("rendered", rendered);
                    w.WriteBoolean("truncated", truncated);
                }
                else   // summary
                {
                    w.WriteNumber("total", o.RecordTotal);
                    w.WriteStartArray("type_counts");
                    foreach (var tc in o.TypeCounts) { w.WriteStartObject(); w.WriteString("type", tc.Type); w.WriteNumber("count", tc.Count); w.WriteEndObject(); }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Finish(ms);
    }
}
