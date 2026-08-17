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
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    /// <summary>W2 `records`: optional response-envelope pairs (form=, the resolved source arm, …) written as
    /// top-level string fields at the START of a json document — so a json consumer sees the same call context
    /// the text header line states, in-band, without any per-render shape change.
    /// CONTRACT (PR #307 round 3): envelope keys must not collide with any renderer's own top-level keys
    /// (count/epoch/records/rendered/truncated/total/…) — Utf8JsonWriter does not dedupe, so a collision would
    /// emit a duplicate-key document. Today's set (form/source/window/epoch_covers_source/total) is disjoint;
    /// keep it that way when adding pairs.</summary>
    static void WriteEnvelope(Utf8JsonWriter w, IReadOnlyList<KeyValuePair<string, string>>? envelope)
    {
        if (envelope is null) return;
        foreach (var kv in envelope) w.WriteString(kv.Key, kv.Value);
    }

    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, string epoch, SpillState? spill, out bool truncated,
                                       IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", rows.Count);
            w.WriteString("epoch", epoch);   // §2.1.1: the ONE captured build the whole batch resolved against
            w.WriteStartArray("resolved");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var r in rows)
            {
                if (manifestOnly) break;   // to_file: the rows are the FILE
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                WriteResolvedRow(w, r);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One housecarl_resolve row. Resolved ⇒ the identity fields; not resolved ⇒ a single <c>error</c>
    /// (the malformed-FormID reason, or "not present in the active order" for a valid-but-absent FormKey).</summary>
    internal static void WriteResolvedRow(Utf8JsonWriter w, ResolvedRef r)
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
            // Refusals carry the bare stamp only — coverage is an assertion about RESOLVED inputs, and a refusal's
            // poles were never resolved (emitting true there claimed full coverage on e.g. an off-order-path
            // refusal; PR #305 third round, finding 2). Text refusals carry no qualifier either — D2 restored.
            if (o.Error is not null) { w.WriteString("error", o.Error); WriteNullable(w, "epoch", o.Epoch); }
            else
            {
                WriteEpochWithCoverage(w, o);   // §2.1.1: the INDEX build + whether it covers every input
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

    /// <summary>The diff stamp + its coverage AS DATA (PR #305 re-review): the epoch names the INDEX build, and an
    /// OUT-OF-LOAD-ORDER pole's file content is outside that fingerprint — so a machine consumer comparing epochs
    /// for "same inputs ⇒ same answer" gets told in-band, not in a C# comment. <c>epoch_covers_all_inputs</c> is
    /// false exactly when an off-order pole contributed (derivable from the poles' <c>in_order</c>, emitted as a
    /// sibling so equality checks need no join); the text render's "(active-order inputs only …)" qualifier is this
    /// same fact's prose form (D2 — one datum, two renders). SUCCESS path only: a refusal's poles were never
    /// resolved, so it carries the bare stamp without a coverage claim (third-round finding 2).</summary>
    static void WriteEpochWithCoverage(Utf8JsonWriter w, LoadOrderService.DiffRecordOutcome o)
    {
        if (o.Epoch is null) return;
        w.WriteString("epoch", o.Epoch);
        w.WriteBoolean("epoch_covers_all_inputs",
                       o.A is null or { InOrder: true } && o.B is null or { InOrder: true });
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

    /// <summary>A bare whole-call refusal document: <c>{error, epoch?}</c> — for tool-layer refusals that have no
    /// outcome object to render (e.g. the §2.1.1 artifact epoch-mismatch handed back beside the batch), matching
    /// the outcome-borne refusal shape so json consumers see ONE refusal grammar. The stamp rides when the refusal
    /// consulted a build (the PR #305 contract).</summary>
    internal static string RenderError(string error, string? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("error", error);
            WriteNullable(w, "epoch", epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>Is the document already at its char ceiling? The writer BUFFERS, so <c>ms.Length</c> lags what has
    /// been written — the row loops that budget by stream length flush first for exactly this reason. Shared by the
    /// post-write report writers so all three judge the budget the same way.</summary>
    static bool Over(Utf8JsonWriter w, MemoryStream ms, int cap)
    {
        w.Flush();
        return ms.Length >= cap;
    }

    /// <summary>The post-write READ-BACK block, one construction for all three write documents (apply / create /
    /// forward). It was written out three times, verbatim down to the comment — and the two facts it tells apart
    /// are exactly the kind that drift when copied: <c>readback_source</c> (the WRITTEN FILE's content, or a dry
    /// run's in-memory would-be content — never load-order truth, which is what the text render spells out in a
    /// sentence), and the <c>readback_full</c>/<c>readback_requested</c> split.
    /// <para><c>readback_full</c> describes THIS DOCUMENT: the json renders emit every field of every row, so a
    /// present read-back is always the full one. It used to carry the caller's ASK, which the in-place lanes
    /// override (the service forces fullReadback), so <c>false</c> sat next to a complete field dump and a consumer
    /// branching on "are these fields complete?" got the opposite of the truth (PR #311 review 7 [nit]).
    /// <c>readback_requested</c> keeps the ask, which is what answers "why did I get one I did not ask for?"</para>
    /// <para>Rows and their field lists both stop at the budget and set <paramref name="truncated"/> — the document
    /// stays valid JSON and says it was cut, never a string severed mid-token.</para></summary>
    static void WriteReadbackBlock(Utf8JsonWriter w, MemoryStream ms, int cap,
        IReadOnlyList<WritePatchBuilder.FullReadback> rb, bool dryRun, bool requested, ref bool truncated)
    {
        w.WriteString("readback_source", dryRun ? "in_memory_would_be_content" : "written_file");
        w.WriteBoolean("readback_full", true);
        w.WriteBoolean("readback_requested", requested);
        w.WriteStartArray("readback");
        foreach (var r in rb)
        {
            if (Over(w, ms, cap)) { truncated = true; break; }
            w.WriteStartObject();
            w.WriteString("formid", r.Target.ToString());
            if (r.Error is not null) w.WriteString("error", r.Error);
            else
            {
                var rec = r.Record!;
                w.WriteString("type", rec.Type);
                WriteNullable(w, "editorid", rec.EditorId);
                w.WriteNumber("field_count", rec.Fields.Count);
                w.WriteStartArray("fields");
                foreach (var f in rec.Fields)
                {
                    if (Over(w, ms, cap)) { truncated = true; break; }
                    w.WriteStartObject();
                    w.WriteString("path", f.Path);
                    if (f.HasValue) w.WriteString("value", f.Token); else w.WriteString("note", f.Note);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

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
    internal static void WriteReadRecord(Utf8JsonWriter w, ReadOutcome o, MemoryStream ms, int cap, string? matches = null,
                                         string? epoch = null, bool ownedChildNote = false)
    {
        var r = o.Record!;
        w.WriteStartObject();
        if (epoch is not null) w.WriteString("epoch", epoch);   // single-read top level ONLY — see RenderRecord
        // The single-read record object IS the response, so the #342 clause belongs on it; batch/query rows never
        // repeat it per row (the caller passes false and the response object states it once), exactly as epoch does.
        WriteOwnedChildNote(w, ownedChildNote);
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
    /// the tool layer for json (a text-only diff view), so only the field data reaches here. The single-read record
    /// object carries <c>epoch</c> top-level (it IS the response); batch/query rows never repeat it per row — there
    /// the epoch is response-level accounting.</summary>
    public static string RenderRecord(ReadOutcome o, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            if (o.Error is not null)
            {
                // A stamped refusal carries its stamp on the wire too (PR #305 review) — same contract as text.
                w.WriteStartObject(); w.WriteString("error", o.Error); WriteNullable(w, "epoch", o.Epoch); w.WriteEndObject();
            }
            else WriteReadRecord(w, o, ms, cap, epoch: o.Epoch, ownedChildNote: o.OwnedChildNoted);
        }
        return Finish(ms);
    }

    /// <summary>The #342 clause on the json lane, written ONCE per response when a rendered record carries the
    /// annotation — the same const the text lane states, so the two transports cannot drift. Gated on the
    /// outcome's structural flag, never on the prose.
    ///
    /// <para>json only ever states the CHEAP tier's clause: <c>conflict_tree=true</c> is refused in json mode (a
    /// text-only diff view), so the lane that has the bodies to name declarers does not exist here. A json caller
    /// who wants the precise answer takes the same route the clause names — the text lane.</para></summary>
    static void WriteOwnedChildNote(Utf8JsonWriter w, bool noted)
    {
        if (noted) w.WriteString("owned_child_note", ReadSentences.NotReadClause);
    }

    // ---- housecarl_batch_record_detail (P6) ---------------------------------------------------------
    /// <summary>batch_record_detail as JSON: <c>{count, records:[…], rendered, truncated}</c>. A bad/absent formid is
    /// a per-item <c>{formid,error}</c> (the batch survives). Truncation drops trailing records and flags it — the
    /// document stays valid JSON (Q3), and count is exact.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars, SpillState? spill, out bool truncated,
                                     IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", outcomes.Count);
            // The whole batch reads ONE captured build (ResolveBatch) — response-level accounting, first non-null
            // (a malformed-FormID row never consulted a view and carries none).
            WriteNullable(w, "epoch", outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false; bool childNoted = false;   // #342: over rows RENDERED
            foreach (var o in outcomes)
            {
                if (manifestOnly) break;   // to_file: the rows are the FILE
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", o.FormKey.ToString()); w.WriteString("error", o.Error); w.WriteEndObject(); }
                else { WriteReadRecord(w, o, ms, cap); childNoted |= o.OwnedChildNoted; }
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            // Over the rows this document actually carries — never the input list. A manifest-only (to_file) or
            // truncated response renders no annotated field, and a clause pointing at "an annotated field above"
            // with nothing above it is the text lane's own guarded mistake, one transport over.
            WriteOwnedChildNote(w, childNoted);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records (W2 PR 1) ----------------------------------------------------------------

    /// <summary>records counts_only on the list lane: the census document, no rows.</summary>
    public static string RenderCounts(IReadOnlyList<KeyValuePair<string, string>> envelope, int count, int ok, int errors, string? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", count);
            w.WriteNumber("ok", ok);
            w.WriteNumber("errors", errors);
            WriteNullable(w, "epoch", epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records counts_only for forms whose census has named counters (delta: differing/identical;
    /// tree: contested) — the envelope plus the counters, no rows.</summary>
    public static string RenderNamedCounts(IReadOnlyList<KeyValuePair<string, string>> envelope,
                                           IReadOnlyList<KeyValuePair<string, int>> counts, string? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var c in counts) w.WriteNumber(c.Key, c.Value);
            WriteNullable(w, "epoch", epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=summary on the list lane: one identity+winner row per outcome (or its per-item
    /// error) — the json twin of the text summary lines, spill marker in-band.</summary>
    public static string RenderRecordsSummary(IReadOnlyList<ReadOutcome> outcomes, int maxChars,
                                              IReadOnlyList<KeyValuePair<string, string>> envelope,
                                              SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", outcomes.Count);
            WriteNullable(w, "epoch", outcomes.FirstOrDefault(o => o.Epoch is not null)?.Epoch);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false;   // summary rows carry no fields, so no #342 annotation
            foreach (var o in outcomes)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", o.FormKey.ToString());
                if (o.Error is not null) w.WriteString("error", o.Error);
                else
                {
                    w.WriteString("type", o.Record!.Type);
                    WriteNullable(w, "editorid", o.Record.EditorId);
                    WriteNullable(w, "source", o.SourcePlugin);
                    WriteNullable(w, "winner", o.WinnerPlugin);
                    w.WriteNumber("override_depth", o.OverrideDepth);
                }
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=aggregate on the list lane: the count table over resolved rows, per-item errors
    /// counted apart (never silently dropped from a census — Q3).</summary>
    public static string RenderListAggregate(string groupBy, IReadOnlyList<KeyValuePair<string, int>> rows,
                                             int count, int errors, string? epoch,
                                             IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);   // form + the resolved source arm + coverage qualifiers (review fold)
            w.WriteString("group_by", groupBy);
            w.WriteNumber("count", count);
            if (errors > 0) w.WriteNumber("errors", errors);
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("groups");
            foreach (var (key, n) in rows.Select(r => (r.Key, r.Value)))
            {
                w.WriteStartObject();
                w.WriteString("key", key);
                w.WriteNumber("count", n);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records (W2 PR 2): the delta / tree comparison forms -----------------------------

    /// <summary>One §4.1 delta row — shared verbatim by the json render and the artifact writer (one shape, no
    /// drift). A per-item refusal is <c>{formid, error, stack_above?}</c>; a compared row carries both poles, the
    /// §4.3 stack-above FACT when the subject sits mid-stack, and the same delta strings the text render emits.</summary>
    internal static void WriteDeltaRow(Utf8JsonWriter w, LoadOrderService.DeltaRow row, MemoryStream ms, int cap)
    {
        w.WriteStartObject();
        w.WriteString("formid", row.Formid);
        if (row.Error is not null)
        {
            w.WriteString("error", row.Error);
            if (row.StackAbove is { Count: > 0 }) WriteStringArray(w, "stack_above", row.StackAbove);
            w.WriteEndObject();
            return;
        }
        var s = row.Subject!; var r = row.Reference!;
        WriteNullable(w, "type", s.RecordType);
        WriteNullable(w, "editorid", s.EditorId);
        WriteDiffPole(w, "subject", s);
        WriteDiffPole(w, "reference", r);
        if (row.StackAbove is { Count: > 0 }) WriteStringArray(w, "stack_above", row.StackAbove);
        if (row.Note is not null) w.WriteString("note", row.Note);
        var d = row.Diff!;
        w.WriteBoolean("complete", d.Complete);
        w.WriteStartArray("deltas");
        int rendered = 0; bool cut = false;
        foreach (var delta in d.Deltas)
        {
            w.Flush();
            if (ms.Length >= cap) { cut = true; break; }
            w.WriteStringValue(delta);
            rendered++;
        }
        w.WriteEndArray();
        w.WriteNumber("delta_count", d.Deltas.Count);
        if (cut) { w.WriteNumber("deltas_rendered", rendered); w.WriteBoolean("deltas_truncated", true); }
        w.WriteNumber("agreed_count", d.AgreedCount);
        w.WriteEndObject();
    }

    /// <summary>records form=delta: <c>{…envelope, count, differing, identical, errors, epoch, rows:[…]}</c>.
    /// The identical count only counts COMPLETE comparisons — a truncated deep read is neither (its row says so
    /// via <c>complete:false</c>, the §4.4 truncation-honesty rule).</summary>
    public static string RenderDelta(IReadOnlyList<LoadOrderService.DeltaRow> rows, int maxChars, string? epoch,
                                     IReadOnlyList<KeyValuePair<string, string>> envelope,
                                     IReadOnlyList<KeyValuePair<string, int>> counts,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // The census covers the COMPLETE list (review F8): rows may be a WINDOW, so the caller computes the
            // counters over everything and hands them in — recomputing here reported the window as the world.
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                WriteDeltaRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One §4.1 tree row — the provider stack with per-node deltas against the row's reference pole.
    /// Shared by the json render and the artifact writer: THIS is what makes trees spillable (PR #306
    /// fold-decision 1 — the 1.x conflict_tree had no row form; the tree FORM does).</summary>
    internal static void WriteTreeRow(Utf8JsonWriter w, LoadOrderService.TreeRow row, MemoryStream ms, int cap)
    {
        w.WriteStartObject();
        w.WriteString("formid", row.Formid);
        if (row.Error is not null)
        {
            w.WriteString("error", row.Error);
            if (row.Touchers.Count > 0) WriteStringArray(w, "touchers", row.Touchers);
            w.WriteEndObject();
            return;
        }
        WriteNullable(w, "type", row.Type);
        WriteNullable(w, "editorid", row.EditorId);
        WriteNullable(w, "reference", row.ReferencePlugin);
        WriteStringArray(w, "touchers", row.Touchers);   // priority order, winner LAST
        w.WriteStartArray("nodes");
        foreach (var n in row.Nodes)
        {
            w.Flush();
            if (ms.Length >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", "[nodes truncated at max_chars — raise max_chars or narrow with project.fields]");
                w.WriteEndObject();
                break;
            }
            w.WriteStartObject();
            w.WriteString("plugin", n.Plugin);
            w.WriteBoolean("is_winner", n.IsWinner);
            w.WriteBoolean("is_reference", n.IsReference);
            if (!n.IsReference)
            {
                w.WriteBoolean("complete", n.Complete);
                WriteStringArray(w, "deltas", n.Deltas);
                w.WriteNumber("delta_count", n.Deltas.Count);
                w.WriteNumber("agreed_count", n.AgreedCount);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>records form=tree: <c>{…envelope, count, contested, errors, epoch, rows:[…]}</c> — the committed
    /// json tree render (§6.1: "the tree/delta forms get a built json render"; the 1.x text-only refusal dies by
    /// construction here).</summary>
    public static string RenderTree(IReadOnlyList<LoadOrderService.TreeRow> rows, int maxChars, string? epoch,
                                    IReadOnlyList<KeyValuePair<string, string>> envelope,
                                    IReadOnlyList<KeyValuePair<string, int>> counts,
                                    SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // complete-list census (review F8)
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                WriteTreeRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records (W2 PR 2): the chain form (walk=) ----------------------------------------

    /// <summary>One chain row — shared by the json render and the artifact writer. Node status is 'expanded'
    /// (entered) or 'kept' (a boundary: exclusion stop, depth cap, unresolved link — the note names which).</summary>
    internal static void WriteChainRow(Utf8JsonWriter w, LoadOrderService.WalkSeedResult row, MemoryStream ms, int cap)
    {
        w.WriteStartObject();
        w.WriteString("formid", row.Seed);
        if (row.Error is not null) { w.WriteString("error", row.Error); w.WriteEndObject(); return; }
        WriteNullable(w, "type", row.Type);
        WriteNullable(w, "editorid", row.EditorId);
        w.WriteStartArray("nodes");
        foreach (var n in row.Nodes)
        {
            w.Flush();
            if (ms.Length >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", "[nodes truncated at max_chars — raise max_chars, or to_file= for the complete walk]");
                w.WriteEndObject();
                break;
            }
            w.WriteStartObject();
            w.WriteString("key", n.Key);
            WriteNullable(w, "type", n.Type);
            WriteNullable(w, "editorid", n.EditorId);
            w.WriteNumber("depth", n.Depth);
            w.WriteString("pulled_by", n.PulledBy);
            w.WriteString("status", n.Status);
            WriteNullable(w, "note", n.Note);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (row.Cycles.Count > 0) WriteStringArray(w, "cycles", row.Cycles);
        WriteNullable(w, "truncation", row.TruncationNote);
        if (row.TemplateReport is { } tr)
        {
            w.WriteStartArray("template_inheritance");
            foreach (var c in tr)
            {
                w.WriteStartObject();
                w.WriteString("category", c.Category);
                w.WriteBoolean("inherited", c.InheritedAtSeed);
                WriteNullable(w, "provider", c.ProviderKey);
                WriteNullable(w, "provider_editorid", c.ProviderEditorId);
                WriteNullable(w, "note", c.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>records form=chain: <c>{…envelope, seeds, errors, epoch, rows:[…]}</c>.</summary>
    public static string RenderChain(IReadOnlyList<LoadOrderService.WalkSeedResult> rows, int maxChars, string? epoch,
                                     IReadOnlyList<KeyValuePair<string, string>> envelope,
                                     IReadOnlyList<KeyValuePair<string, int>> counts,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // complete-list census (review F8)
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                WriteChainRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=chain, walk=reverse (the typed MGEF carrier lane): per seed the carriers with the
    /// MATCHING entry's payload — magnitudes AS AUTHORED (conditions not evaluated), the effect_chain contract.</summary>
    public static string RenderEffectChains(IReadOnlyList<(string Seed, EffectChainResult Result)> results,
                                            int maxChars, IReadOnlyList<KeyValuePair<string, string>> envelope,
                                            IReadOnlyList<KeyValuePair<string, int>> counts, string? epoch,
                                            SpillState? spill, out bool outTruncated)
    {
        outTruncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // complete-list census (review F8)
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("rows");
            bool truncated = false;
            foreach (var (seed, r) in results)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("seed", seed);
                if (r.Error is not null) { w.WriteString("error", r.Error); w.WriteEndObject(); continue; }
                w.WriteString("mgef_editorid", r.MgefEditorId);
                w.WriteNumber("total", r.Total);
                w.WriteBoolean("capped", r.Capped);
                WriteNullable(w, "scan_note", r.ScanNote);
                w.WriteStartArray("carriers");
                foreach (var row in r.Rows)
                {
                    w.Flush();
                    if (ms.Length >= cap) { truncated = true; break; }
                    w.WriteStartObject();
                    w.WriteString("formid", row.Carrier.ToString());
                    w.WriteString("type", row.Type);
                    WriteNullable(w, "editorid", row.EditorId);
                    w.WriteString("winner", row.Winner);
                    w.WriteNumber("effect_index", row.EffectIndex);
                    w.WriteNumber("effect_count", row.EffectCount);
                    w.WriteNumber("magnitude", row.Magnitude);
                    w.WriteNumber("area", row.Area);
                    w.WriteNumber("duration", row.Duration);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("truncated", truncated);
            outTruncated = truncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records (W2 PR 2): the info_order form -------------------------------------------

    /// <summary>One info_order row — shared by the json render and the artifact writer. Positions are 1-based
    /// (matching the text render's #N). The honesty gates ride as data: <c>complete</c> (every touching plugin's
    /// list read), <c>moves_computed</c> (the move analysis ran), <c>baseline_trusted</c> (origin positions
    /// anchored on the true definer) — negative claims about moves hold only when the first two are both true.</summary>
    internal static void WriteInfoOrderRow(Utf8JsonWriter w, LoadOrderService.InfoOrderRow row, MemoryStream ms, int cap)
    {
        w.WriteStartObject();
        w.WriteString("formid", row.Formid);
        if (row.Error is not null) { w.WriteString("error", row.Error); w.WriteEndObject(); return; }
        WriteNullable(w, "type", row.Type);
        WriteNullable(w, "editorid", row.EditorId);
        WriteNullable(w, "winner", row.WinnerPlugin);
        if (row.Order is not { } io)
        {
            w.WriteString("note", "the merge could not be computed for this topic (its key did not resolve in the touching index)");
            w.WriteEndObject();
            return;
        }
        w.WriteBoolean("contested", io.Contested);
        w.WriteBoolean("complete", io.Complete);
        w.WriteBoolean("moves_computed", io.MovesComputed);
        w.WriteBoolean("baseline_trusted", io.BaselineTrusted);
        WriteStringArray(w, "contributing", io.ContributingPlugins);
        if (io.UnreadContributors.Count > 0) WriteStringArray(w, "unread", io.UnreadContributors);
        WriteNullable(w, "note", io.Note);
        w.WriteNumber("moved_count", io.Moved.Count);
        w.WriteStartArray("order");
        foreach (var e in io.Order)
        {
            w.Flush();
            if (ms.Length >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", "[order truncated at max_chars — raise max_chars]");
                w.WriteEndObject();
                break;
            }
            w.WriteStartObject();
            w.WriteNumber("position", e.Index + 1);
            w.WriteString("info", e.Info.ToString());
            w.WriteString("placed_by", e.PlacedBy);
            if (e.Deleted) w.WriteBoolean("deleted", true);
            if (e.Moved) { w.WriteBoolean("moved", true); w.WriteNumber("origin_position", e.OriginIndex!.Value + 1); }
            else if (e.OriginIndex is null && io.BaselineTrusted) w.WriteBoolean("added_by_later_plugin", true);
            if (e.Placement == InfoPlacement.HeadFirstMarker) w.WriteString("placement", "pinned first by its own PNAM marker (deliberate)");
            else if (e.Placement == InfoPlacement.HeadUnresolvable) w.WriteString("placement", "PNAM names no reachable line — forced to the top");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>records form=info_order: <c>{…envelope, count, contested, errors, epoch, rows:[…]}</c>.</summary>
    public static string RenderInfoOrder(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, int maxChars, string? epoch,
                                         IReadOnlyList<KeyValuePair<string, string>> envelope,
                                         IReadOnlyList<KeyValuePair<string, int>> counts,
                                         SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // complete-list census (review F8)
            WriteNullable(w, "epoch", epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                WriteInfoOrderRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
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
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The §2.1.1-aware render — see the text twin: <paramref name="spill"/> rides IN the document (a
    /// marker outside the json body would be invisible to a json consumer), <paramref name="truncated"/> is the
    /// auto-spill trigger handed back to the tool layer.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth,
                                          SpillState? spill, out bool truncated,
                                          IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // Post-capture refusals are stamped (PR #305 contract — e.g. the artifact epoch-mismatch refusal);
            // pre-capture validation refusals carry null and render bare, same as the text twin.
            if (q.Error is not null) { w.WriteString("error", q.Error); if (q.Epoch is not null) w.WriteString("epoch", q.Epoch); }
            else if (q.Groups is not null)                                   // group_by= → count table
            {
                WriteNullable(w, "group_by", q.GroupBy);
                w.WriteNumber("total", q.Total);
                WriteNullable(w, "epoch", q.Epoch);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q);
                w.WriteStartArray("groups");
                int gRendered = 0; bool gTrunc = false;
                foreach (var g in q.Groups)
                {
                    if (manifestOnly) break;   // to_file: the rows are the FILE
                    w.Flush();
                    if (ms.Length >= cap) { gTrunc = true; break; }
                    w.WriteStartObject(); w.WriteString("key", g.Key); w.WriteNumber("count", g.Count); w.WriteEndObject();
                    gRendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", gRendered);
                w.WriteBoolean("truncated", gTrunc);
                truncated = gTrunc;
            }
            else                                                            // per-match: detail (fields=) or summary
            {
                bool detail = fields is { Count: > 0 };
                bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // P5
                string? p5 = anyScoped ? ScopedFieldsNote(winnerFields, q.WhereWinner) : null;
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                WriteNullable(w, "epoch", q.Epoch);                         // §2.1.1: offset= windows tile ONLY within one epoch
                if (q.Offset > 0) w.WriteNumber("offset", q.Offset);        // #223 pagination — the window's start, in-band
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, p5);
                var linkMemo = resolveNames && detail ? new Dictionary<FormKey, ResolvedRef>() : null;
                w.WriteStartArray("matches");
                int rendered = 0; bool rowsTruncated = false; bool childNoted = false;   // #342: the clause once, after the rows
                for (int i = 0; i < q.Keys.Count && !manifestOnly; i++)      // to_file: the rows are the FILE
                {
                    w.Flush();
                    if (ms.Length >= cap) { rowsTruncated = true; break; }
                    var fk = q.Keys[i];
                    string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                    if (detail)
                    {
                        // winner_fields=: read the WINNER's body (source=null) regardless of scan scope; the record's
                        // "source" field still names the body read, so the json carries the same source/winner truth.
                        // Pinned to the scan's build (PR #305 review) — the document's epoch names ONE build.
                        var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, depth, resolveNames: resolveNames, linkMemo: linkMemo);
                        if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", fk.ToString()); w.WriteString("error", o.Error); if (matches is not null) w.WriteString("matches", matches); w.WriteEndObject(); }
                        else WriteReadRecord(w, o, ms, cap, matches);
                        childNoted |= o.OwnedChildNoted;
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                        WriteSummaryRow(w, m, matches);
                    }
                    rendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", rowsTruncated);
                WriteOwnedChildNote(w, childNoted);
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The P5 scoped-vs-winner fields note, shared verbatim by the json and dense renders (D2 — one wording,
    /// two renders that can't drift).</summary>
    /// <summary>The P5 scoped-vs-winner field-source note, as one of a 4-way matrix over (winner_fields=, where_source=).
    /// <paramref name="whereWinner"/> (#233) is true when the MATCH decided on the live winner (where_source=winner) —
    /// then the note must NOT claim the match was selected on the scoped body (the D2 no-drift rule). Shared by the
    /// text, json, and dense renders so the note can never drift across the three.</summary>
    internal static string ScopedFieldsNote(bool winnerFields, bool whereWinner)
    {
        if (whereWinner)
            return winnerFields
                ? "the MATCH and the field values are both the load-order WINNER's (where_source=winner, winner_fields=true)."
                : "the MATCH was selected on the load-order WINNER (where_source=winner), but the field values shown are each match's SCOPED plugin's OWN version — pass winner_fields=true to display the winner too.";
        return winnerFields
            ? "field values are the load-order WINNER's (winner_fields=true); each match was SELECTED on its scoped plugin's body."
            : "field values are each match's SCOPED plugin's OWN version, NOT the live load-order winner — pass winner_fields=true for load-order truth.";
    }

    // ---- housecarl_cross_plugin_query format=dense (#223) -------------------------------------------
    /// <summary>The COLUMNAR render: a <c>columns</c> array once, then ONE positional row array per match —
    /// <c>[formid, editorid, field values…]</c> under fields= (plus a <c>source</c> column under a plugins= scope,
    /// naming the body each row's values were read from — the per-row P5 provenance text and json carry),
    /// <c>[formid, type, editorid, winner, override_depth]</c>
    /// for summaries — killing the per-field {path,value} envelopes and repeated identity keys that made format=json
    /// the context-budget drain in bulk enumerations (#223: ~80 records per 40k chars at two fields). Reads the SAME
    /// path as the other renders (ResolveRead / Prefilled — D2), and cells use the SAME display vocabulary as the
    /// text render: the round-trip token, else the parenthetical note (an absent field is "(absent)", never a silent
    /// hole), with Display/resolve_names annotations appended. Q3 accounting (total/capped/offset/notes/truncated)
    /// rides in-band; a row whose read FAILS lands in a separate <c>errors</c> array — never a silently missing row.
    /// group_by= never reaches here (the tool renders its count table via <see cref="RenderCrossQuery"/>).</summary>
    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields)
        => RenderCrossQueryDense(svc, q, fields, maxChars, resolveNames, winnerFields, null, out _);

    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields,
                                               SpillState? spill, out bool truncated,
                                               IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // Post-capture refusals are stamped (PR #305 contract); pre-capture validation refusals stay bare.
            if (q.Error is not null) { w.WriteString("error", q.Error); if (q.Epoch is not null) w.WriteString("epoch", q.Epoch); }
            else
            {
                bool detail = fields is { Count: > 0 };
                bool anyScoped = detail && q.Sources is { } ss && ss.Take(q.Keys.Count).Any(s => s is not null);   // P5
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                WriteNullable(w, "epoch", q.Epoch);                           // §2.1.1
                if (q.Offset > 0) w.WriteNumber("offset", q.Offset);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, anyScoped ? ScopedFieldsNote(winnerFields, q.WhereWinner) : null);

                bool hasMatches = q.MatchedTargets is not null;               // multi-target references= → one extra column
                w.WriteStartArray("columns");
                if (detail)
                {
                    w.WriteStringValue("formid"); w.WriteStringValue("editorid");
                    foreach (var f in fields!) w.WriteStringValue(f);         // cells align positionally: ReadFields returns exactly one value per requested path, in order
                    // Under a plugins= scope each row's values are SOME scoped plugin's own body — with 2+ scoped
                    // plugins the caller can't reconstruct WHICH from the row alone, and that's the P5 silent-wrong
                    // trap (a defining esp's stale value read as live truth). Carry the provenance per row, exactly
                    // like text ("fields (from X):") and json ("source") do — D2, renders must not drift. (PR #239
                    // review, MEDIUM.)
                    if (anyScoped) w.WriteStringValue("source");
                }
                else
                    foreach (var c in new[] { "formid", "type", "editorid", "winner", "override_depth" }) w.WriteStringValue(c);
                if (hasMatches) w.WriteStringValue("matches");
                w.WriteEndArray();

                var linkMemo = resolveNames && detail ? new Dictionary<FormKey, ResolvedRef>() : null;
                List<(string Formid, string Error)>? errors = null;
                int rendered = 0; bool rowsTruncated = false; bool childNoted = false;   // #342: the clause once, after the rows
                w.WriteStartArray("rows");
                for (int i = 0; i < q.Keys.Count && !manifestOnly; i++)      // to_file: the rows are the FILE
                {
                    w.Flush();
                    if (ms.Length >= cap) { rowsTruncated = true; break; }
                    var fk = q.Keys[i];
                    string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                    if (detail)
                    {
                        var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false,
                                                  resolveNames: resolveNames, linkMemo: linkMemo, containerHint: Wire.DenseContainerHint);   // dense refuses depth>1 — hint the format hop with the knob (#231); pinned to the scan's build
                        if (o.Error is not null) { (errors ??= new()).Add((fk.ToString(), o.Error)); rendered++; continue; }
                        var r = o.Record!;
                        w.WriteStartArray();
                        w.WriteStringValue(r.FormKey);
                        WriteCell(w, r.EditorId);
                        foreach (var f in r.Fields) WriteCell(w, DenseCell(f));
                        if (anyScoped) WriteCell(w, o.SourcePlugin);          // the body this row's values were read from (winner_fields=true → the winner)
                        if (hasMatches) WriteCell(w, matches);
                        w.WriteEndArray();
                        childNoted |= o.OwnedChildNoted;
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                        if (m.Error is not null) { (errors ??= new()).Add((m.FormKey.ToString(), m.Error)); rendered++; continue; }
                        w.WriteStartArray();
                        w.WriteStringValue(m.FormKey.ToString());
                        w.WriteStringValue(m.Type);
                        WriteCell(w, m.EditorId);
                        w.WriteStringValue(m.Winner);
                        w.WriteNumberValue(m.OverrideDepth);
                        if (hasMatches) WriteCell(w, matches);
                        w.WriteEndArray();
                    }
                    rendered++;
                }
                w.WriteEndArray();
                if (errors is not null)
                {
                    w.WriteStartArray("errors");
                    foreach (var (efk, err) in errors)
                    { w.WriteStartObject(); w.WriteString("formid", efk); w.WriteString("error", err); w.WriteEndObject(); }
                    w.WriteEndArray();
                }
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", rowsTruncated);
                WriteOwnedChildNote(w, childNoted);
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One dense cell: the round-trip token, else the leaf's parenthetical note ("(absent)", "(no field …)")
    /// so a no-value field is VISIBLE in its cell, never a silent hole (Q3) — with the Display and resolve_names
    /// annotations appended in the text render's exact vocabulary.</summary>
    static string? DenseCell(HousecarlCore.FieldValue f)
    {
        var s = f.HasValue ? f.Token : f.Note;
        if (f.Display is not null) s = $"{s}   ({f.Display})";
        if (f.Link is not null) s = $"{s}   ({Wire.LinkText(f.Link)})";
        return s;
    }

    static void WriteCell(Utf8JsonWriter w, string? v)
    {
        if (v is null) w.WriteNullValue(); else w.WriteStringValue(v);
    }

    internal static void WriteSummaryRow(Utf8JsonWriter w, RecordSummary m, string? matches)
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
        if (q.PredicateNote is null && q.ScanNote is null && q.WhereSourceNote is null && extra is null) return;
        w.WriteStartArray("notes");
        if (q.PredicateNote is not null) w.WriteStringValue(q.PredicateNote);
        if (q.ScanNote is not null) w.WriteStringValue(q.ScanNote);
        if (q.WhereSourceNote is not null) w.WriteStringValue(q.WhereSourceNote);   // #233: where_source=winner redundancy under a type=-only scope
        if (extra is not null) w.WriteStringValue(extra);   // P5 scoped-vs-winner fields note
        w.WriteEndArray();
    }

    // ---- housecarl_check_errors (#282) --------------------------------------------------------------
    /// <summary>The integrity sweep as JSON: <c>{scanned_plugins, dangling, missing_masters, unscannable, classes,
    /// filter_note, off_order_scanned, excluded_plugins, plugins:[…], capped, rendered, truncated, boundary}</c>, or the
    /// <c>counts_only</c> shape with <c>histogram</c> in place of <c>plugins</c>. An error CLASS the caller excluded is
    /// emitted as <c>null</c>, NOT as 0 — the json counterpart of the text render's "NOT CHECKED", so a skipped check
    /// cannot be parsed as a clean one (Q3). Budget-aware: drops trailing rows and flags <c>truncated</c>, always
    /// leaving valid JSON.</summary>
    public static string RenderCheckErrors(ErrorCheckResult r, int maxChars, int histogramLimit = 1000)
    {
        int cap = Cap(maxChars);
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            // w.Flush() before Finish: this early return sits INSIDE the using, so without it the writer's buffered
            // bytes never reach the stream and the refusal rendered as an EMPTY STRING — a latent, pre-existing Q3
            // break on every json-mode sweep refusal, surfaced by the epoch guard's refusal-render arm (PR #305).
            // Bare stamp only: coverage is an assertion about SWEPT inputs, and a refusal swept none (finding 2).
            if (r.Error is not null) { w.WriteString("error", r.Error); WriteNullable(w, "epoch", r.Epoch); w.WriteEndObject(); w.Flush(); return Finish(ms); }

            w.WriteNumber("scanned_plugins", r.PluginsScanned);
            WriteSweepEpoch(w, r);   // §2.1.1: the swept INDEXED build + whether it covers every swept input
            // null (not 0) for a class nobody looked for — see the summary.
            if (didDangling) { w.WriteNumber("dangling", r.TotalDangling); w.WriteNumber("unscannable_records", r.TotalUnscannableRecords); }
            else { w.WriteNull("dangling"); w.WriteNull("unscannable_records"); }
            if (didMasters) w.WriteNumber("missing_masters", r.TotalMissingMasters); else w.WriteNull("missing_masters");
            WriteStringArray(w, "classes_checked", ClassNames(r.Classes));
            WriteNullable(w, "filter_note", r.FilterNote);
            WriteStringArray(w, "off_order_scanned", r.OffOrderScanned ?? Array.Empty<string>());
            WriteExcluded(w, r.ExcludedPlugins);
            w.WriteBoolean("counts_only", r.CountsOnly);

            if (r.CountsOnly)
            {
                WriteHistogram(w, "dangling_by_target_plugin", r.Histogram, histogramLimit);
                WriteUnreadPlugins(w, r.Reports, ms, cap);
            }
            else
            {
                w.WriteBoolean("capped", r.Capped);
                w.WriteStartArray("plugins");
                int rendered = 0; bool truncated = false;
                foreach (var p in r.Reports)
                {
                    w.Flush();
                    if (ms.Length >= cap) { truncated = true; break; }
                    w.WriteStartObject();
                    w.WriteString("plugin", p.Plugin);
                    WriteNullable(w, "scan_error", p.ScanError);
                    WriteStringArray(w, "missing_masters", p.MissingMasters);
                    w.WriteStartArray("dangling");
                    foreach (var d in p.Dangling)
                    {
                        w.WriteStartObject();
                        w.WriteString("source", d.Source.ToString());
                        w.WriteString("source_type", d.SourceType);
                        WriteNullable(w, "source_editorid", d.SourceEditorId);
                        w.WriteString("target", d.Target.ToString());
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteNumber("unscannable_records", p.UnscannableRecords);
                    WriteStringArray(w, "unscannable_samples", p.UnscannableSamples);
                    w.WriteEndObject();
                    rendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", truncated);
            }

            w.WriteString("boundary",
                "checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain spatial " +
                "integrity (CRC/grid), flag required-but-null fields, list unused-master cleanup, or link-check an owned " +
                "item's ownership 'variable' word; a null FormLink is a legal optional.");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>check_errors' stamp + coverage as data (PR #305 re-review) — the sweep twin of
    /// <see cref="WriteEpochWithCoverage"/>: <c>epoch_covers_all_inputs</c> is false exactly when off-order files
    /// were swept beside the index (their content is outside the fingerprint; <c>off_order_scanned</c> names them).
    /// validate_scripts needs no twin — it has no off-order lane, so its stamp always covers everything swept.
    /// SUCCESS path only — a refusal swept nothing, so it carries the bare stamp (third-round finding 2).</summary>
    static void WriteSweepEpoch(Utf8JsonWriter w, ErrorCheckResult r)
    {
        if (r.Epoch is null) return;
        w.WriteString("epoch", r.Epoch);
        w.WriteBoolean("epoch_covers_all_inputs", r.OffOrderScanned is not { Count: > 0 });
    }

    // ---- housecarl_validate_scripts (#282) ---------------------------------------------------------
    /// <summary>The script-property sweep as JSON: <c>{scanned_plugins, records_with_scripts, unbound, unbound_object,
    /// unbound_scalar, bound_but_null, unverifiable, classes_checked, filter_note, read_incomplete, excluded_plugins,
    /// records:[…], capped, rendered, truncated, boundary}</c>, or the <c>counts_only</c> shape with
    /// <c>unbound_by_property</c> in place of <c>records</c>. A finding CLASS the caller excluded is emitted as
    /// <c>null</c>, NOT as 0 — the json counterpart of the text render's "NOT CHECKED", so a class nobody looked for
    /// cannot be parsed as one that came back clean (PR #288 review, finding 1). <c>unverifiable</c> is never null: it
    /// cannot be filtered out. Same data as the text render off the same result object (D2 — the two can differ only in
    /// formatting).</summary>
    public static string RenderScriptCheck(ScriptCheckResult r, int maxChars, int histogramLimit = 1000)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            // w.Flush() before Finish — same latent empty-refusal bug as RenderCheckErrors' early return (see there).
            if (r.Error is not null) { w.WriteString("error", r.Error); WriteNullable(w, "epoch", r.Epoch); w.WriteEndObject(); w.Flush(); return Finish(ms); }

            bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
            bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
            bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

            w.WriteNumber("scanned_plugins", r.PluginsScanned);
            WriteNullable(w, "epoch", r.Epoch);   // §2.1.1: the swept build
            w.WriteNumber("records_with_scripts", r.RecordsWithScripts);
            // null, NOT 0, for a class the caller excluded — a 0 here is parsed as "looked, found none" about a class
            // nobody looked for (PR #288 review, finding 1). The per-class keys make each number's scope self-evident
            // rather than something the consumer has to cross-reference against classes_checked.
            if (didObject || didScalar) w.WriteNumber("unbound", r.TotalUnbound); else w.WriteNull("unbound");
            if (didObject) w.WriteNumber("unbound_object", r.TotalUnboundObject); else w.WriteNull("unbound_object");
            if (didScalar) w.WriteNumber("unbound_scalar", r.TotalUnboundScalar); else w.WriteNull("unbound_scalar");
            if (didNull) w.WriteNumber("bound_but_null", r.TotalNullObject); else w.WriteNull("bound_but_null");
            w.WriteNumber("unverifiable", r.TotalUnverifiable);   // never filterable — always a real count
            WriteStringArray(w, "classes_checked", ScriptClassNames(r.Classes));
            // The property filter rides as DATA, not just prose in filter_note: `unbound` / `bound_but_null` count only
            // matching findings, while `records_with_scripts` and `unverifiable` are plugin-wide regardless of it — a
            // consumer needs to be able to read that asymmetry off the document (round-3 review).
            WriteNullable(w, "property_contains", r.PropertyContains);
            WriteNullable(w, "filter_note", r.FilterNote);
            w.WriteBoolean("read_incomplete", r.ReadIncomplete);
            WriteExcluded(w, r.ExcludedPlugins);
            w.WriteBoolean("counts_only", r.CountsOnly);

            if (r.CountsOnly)
            {
                WriteHistogram(w, "unbound_by_property", r.Histogram, histogramLimit);
                // Wrapped + budget-flagged for the same reason check_errors' `unread` is (#288 review finding 4): a
                // silently short honesty list reads as a complete one.
                var scanErrors = r.Reports.Where(x => x.ScanError is not null).ToList();
                w.WriteStartObject("scan_errors");
                w.WriteNumber("total", scanErrors.Count);
                w.WriteStartArray("rows");
                int seRendered = 0; bool seTruncated = false;
                foreach (var rec in scanErrors)
                {
                    w.Flush();
                    if (ms.Length >= cap) { seTruncated = true; break; }
                    w.WriteStartObject(); w.WriteString("plugin", rec.Plugin); w.WriteString("scan_error", rec.ScanError!); w.WriteEndObject();
                    seRendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", seRendered);
                w.WriteBoolean("truncated", seTruncated);
                w.WriteEndObject();
            }
            else
            {
                w.WriteBoolean("capped", r.Capped);
                w.WriteStartArray("records");
                int rendered = 0; bool truncated = false;
                foreach (var rec in r.Reports)
                {
                    w.Flush();
                    if (ms.Length >= cap) { truncated = true; break; }
                    w.WriteStartObject();
                    if (rec.ScanError is not null)
                    {
                        w.WriteString("plugin", rec.Plugin);
                        w.WriteString("scan_error", rec.ScanError);
                        w.WriteEndObject();
                        rendered++;
                        continue;
                    }
                    w.WriteString("formid", rec.Record.ToString());
                    w.WriteString("type", rec.RecordType);
                    WriteNullable(w, "editorid", rec.EditorId);
                    w.WriteString("plugin", rec.Plugin);
                    w.WriteStartArray("unbound");
                    // Object/form types first — the same severity ordering the text render applies (D2).
                    foreach (var u in rec.Unbound.OrderByDescending(u => u.IsObjectType))
                    {
                        w.WriteStartObject();
                        w.WriteString("property", u.PropertyName);
                        w.WriteString("pex_type", u.PexTypeName);
                        w.WriteString("script", u.Script);
                        w.WriteString("declared_in", u.DeclaringScript);
                        w.WriteString("class", u.IsObjectType ? "unbound_object" : "unbound_scalar");
                        w.WriteString("severity", u.IsObjectType ? "high" : "medium");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("bound_but_null");
                    foreach (var n in rec.NullObjects)
                    { w.WriteStartObject(); w.WriteString("property", n.PropertyName); w.WriteString("script", n.Script); w.WriteEndObject(); }
                    w.WriteEndArray();
                    w.WriteStartArray("unverifiable");
                    foreach (var uv in rec.Unverifiable)
                    { w.WriteStartObject(); w.WriteString("script", uv.Script); w.WriteString("reason", uv.Reason); w.WriteEndObject(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    rendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", truncated);
            }

            w.WriteString("boundary",
                "checks Auto (CK-editable) properties across the extends chain — not code-driven full properties. An " +
                "unbound object property is the silent-None footgun but CAN be intentional (filled at runtime) — a finding " +
                "is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed clean.");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- shared sweep writers (#282) ---------------------------------------------------------------
    /// <summary>A counts_only histogram: <c>{distinct, rows:[{key,count}], rendered}</c>. Absent when the mode was not
    /// requested; PRESENT with an empty <c>rows</c> when the sweep genuinely found nothing — the two must not look alike.</summary>
    static void WriteHistogram(Utf8JsonWriter w, string name, IReadOnlyList<SweepCount>? rows, int rowLimit)
    {
        if (rows is null) return;
        w.WriteStartObject(name);
        w.WriteNumber("distinct", rows.Count);
        w.WriteStartArray("rows");
        int shown = 0;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            w.WriteStartObject(); w.WriteString("key", row.Key); w.WriteNumber("count", row.Count); w.WriteEndObject();
            shown++;
        }
        w.WriteEndArray();
        w.WriteNumber("rendered", shown);
        w.WriteEndObject();
    }

    /// <summary>Under counts_only, check_errors' reports carry the honesty layer only — plugins whose records could not
    /// be read. Emitted so a counts-only answer still names what it could not check (Q3).
    /// <para>Wrapped in <c>{total, rows, rendered, truncated}</c> rather than a bare array: a budget cut used to drop
    /// trailing rows with NO flag, so a consumer iterating the array believed it had the complete set of what went
    /// unchecked — and the text render said "truncated" for the same result (PR #288 review, finding 4).</para></summary>
    static void WriteUnreadPlugins(Utf8JsonWriter w, IReadOnlyList<PluginErrors> reports, MemoryStream ms, int cap)
    {
        w.WriteStartObject("unread");
        w.WriteNumber("total", reports.Count);
        w.WriteStartArray("rows");
        int rendered = 0; bool truncated = false;
        foreach (var p in reports)
        {
            w.Flush();
            if (ms.Length >= cap) { truncated = true; break; }
            w.WriteStartObject();
            w.WriteString("plugin", p.Plugin);
            WriteNullable(w, "scan_error", p.ScanError);
            w.WriteNumber("unscannable_records", p.UnscannableRecords);
            WriteStringArray(w, "unscannable_samples", p.UnscannableSamples);
            w.WriteEndObject();
            rendered++;
        }
        w.WriteEndArray();
        w.WriteNumber("rendered", rendered);
        w.WriteBoolean("truncated", truncated);
        w.WriteEndObject();
    }

    static void WriteExcluded(Utf8JsonWriter w, IReadOnlyDictionary<string, string> excluded)
    {
        w.WriteStartArray("excluded_plugins");
        foreach (var kv in excluded)
        { w.WriteStartObject(); w.WriteString("plugin", kv.Key); w.WriteString("reason", kv.Value); w.WriteEndObject(); }
        w.WriteEndArray();
    }

    static List<string> ClassNames(ErrorFindingClass c)
    {
        var names = new List<string>(2);
        if (c.HasFlag(ErrorFindingClass.Dangling)) names.Add("dangling");
        if (c.HasFlag(ErrorFindingClass.MissingMasters)) names.Add("missing_masters");
        return names;
    }

    static List<string> ScriptClassNames(ScriptFindingClass c)
    {
        var names = new List<string>(3);
        if (c.HasFlag(ScriptFindingClass.UnboundObject)) names.Add("unbound_object");
        if (c.HasFlag(ScriptFindingClass.UnboundScalar)) names.Add("unbound_scalar");
        if (c.HasFlag(ScriptFindingClass.BoundNull)) names.Add("bound_null");
        return names;
    }

    // ---- housecarl_read_plugin_file (P6) ------------------------------------------------------------
    /// <summary>read_plugin_file as JSON — always stamped <c>out_of_load_order:true</c> (the load-bearing raw-file
    /// caveat), then the file/masters context and the mode payload: <c>record</c> (the FILE's own record — no winner,
    /// it's not resolved), <c>records</c> (enumerate), or <c>type_counts</c> (summary). <c>error</c>/<c>ambiguous</c>
    /// on failure.</summary>
    public static string RenderPluginFile(PluginFileOutcome o, int maxChars,
                                          IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        int cap = Cap(maxChars);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
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
                // The JSON lane surfaces this state too, so it gets the cause as well (#271) — a consumer reading
                // enabled=false here would otherwise have to go re-derive WHY, which is the whole cost this fixes.
                // Always PRESENT, explicitly null when the game loads the file (the WriteNullable house style), so a
                // consumer can tell "no cause" from "field not emitted by an older build".
                WriteNullable(w, "why_not_active", o.WhyNotActive);
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

    // ---- housecarl_apply (W3 — the 2.0 write surface) -----------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.Render"/>: ONE write outcome, the SAME data the
    /// text render states (decision D2 — one write path, two renders). Everything the text lane treats as prose is a
    /// typed field here: the lane the CALL NAMED (see below), whether it was a dry run, the epoch of the build the winners resolved from,
    /// per-op results, and the read-back. A REFUSAL is a document too (<c>ok:false</c> with the reason), not an empty
    /// body — a json caller must never have to parse "error: …" out of a string to learn the call failed. The
    /// first-touch in-place CONSENT prompt is its own flag: it is a required confirmation, not a failure (Q3).
    /// Budget handling matches every other json render — trailing ROWS drop and <c>truncated</c> says so, so the
    /// document is always valid JSON rather than a string cut mid-token.
    /// <para><b><paramref name="lane"/> is passed in, not derived from the outcome</b> (PR #311 review [medium]).
    /// <c>Fail</c> and <c>NeedsAck</c> construct their outcome with <c>InPlace</c>/<c>Extended</c> at their
    /// defaults, so deriving the lane from those flags reported <c>"patch"</c> for a refusal on an <c>into=</c>
    /// call and — worse — for the first-touch in-place CONSENT PROMPT, a response that exists ONLY because the
    /// caller asked to rewrite their own file. The tool layer knows which lane the call named; it says so, and the
    /// value agrees with the outcome's flags on every success.</para></summary>
    public static string RenderPatchOutcome(WritePatchBuilder.PatchOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteBoolean("dry_run", o.DryRun);
            w.WriteString("lane", lane);
            WriteNullable(w, "epoch", o.Epoch);
            if (!o.Success)
            {
                // NeedsAcknowledge carries its prompt in Error — labelled as a prompt, never as an error string.
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                // Flush BEFORE reading the stream: this return is INSIDE the writer's using-block, so without it
                // the buffered document is still unwritten and the caller gets an EMPTY string — exactly the
                // silent-degrade class PR #306 found on the json sweep refusals (a refusal that renders as nothing
                // is worse than the failure it was reporting). The success path below returns after disposal.
                w.Flush();
                return Finish(ms);
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            w.WriteStartArray("masters");
            foreach (var m in o.Masters) w.WriteStringValue(m);
            w.WriteEndArray();

            // Did the per-op file check RUN at all? A patch-lane or dry-run document has no per-op file readings in
            // it, and a consumer needs to tell that from "it ran and everything came off the file". Outside the ops
            // budget, because that is the one fact a max_chars cut must not remove.
            w.WriteBoolean("verify_ran", o.Ops.Any(op => op.VerifyAttempted));

            w.WriteNumber("total_ops", o.Ops.Count);
            w.WriteStartArray("ops");
            int renderedOps = 0;
            bool truncated = false;
            foreach (var op in o.Ops)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", op.Target.ToString());
                w.WriteString("record_type", op.RecordType);
                w.WriteString("label", op.Label);
                w.WriteBoolean("applied", op.Applied);
                WriteNullable(w, "error", op.Error);
                WriteNullable(w, "after", op.After);
                WriteNullable(w, "landed", op.Landed);
                // #308 — the D2 twin of the text render's file-vs-memory split, and it REPORTS rather than judges.
                // `landed` is the applied edit's own read (in memory, before the serialize); `landed_on_disk` is the
                // same descriptor re-derived from the WRITTEN FILE, null when the file could not answer for this op.
                WriteNullable(w, "landed_on_disk", op.LandedOnDisk);
                // WHERE the clause came from, as a word rather than a verdict:
                //   "written_file"  the file answered for this op — `landed_on_disk` is its reading
                //   "superseded"    a later op in this call wrote the same field, so the file's final state is that
                //                   op's result and cannot speak for this one
                //   "no_answer"     the file was re-opened and did not yield this op's leaf (or the read failed)
                //   "not_checked"   this op was never asked — a lane that runs no per-op file check (patch, dry run),
                //                   or an op appended after the resolved edits (the SNAM topic-marker sync)
                // Deliberately NOT a judgement about whether the write "landed": telling a real difference from a
                // representational one (a byte-quantised Percent, an overlay's type name) is what nine review rounds
                // showed cannot be done reliably, and every attempt told a caller to re-issue a write that HAD landed.
                // The two readings are both here; a caller comparing them decides.
                w.WriteString("landed_source",
                    op.SupersededInCall ? "superseded"
                    : op.LandedOnDisk is not null ? "written_file"
                    : op.VerifyAttempted ? "no_answer" : "not_checked");
                w.WriteEndObject();
                renderedOps++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_ops", renderedOps);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, o.DryRun, readback, ref truncated);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Lane-aware, shared with forward (PR #311 review 6): this document budgets the `ops` array, and a
            // re-issue to widen it is safe on into=/dry-run but cuts a second patch on the default lane and
            // re-serializes the caller's own file on in_place.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "applied")} — "
                    + WriteTools.ApplyAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_create (W3 PR 2) -----------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderCreate"/> — the SAME data the text render
    /// states (decision D2), on the same contract <see cref="RenderPatchOutcome"/> established: a refusal is a
    /// document (<c>ok:false</c> with the reason), the first-touch consent prompt is its own flag rather than an
    /// error, the epoch rides on every response, and truncation drops trailing ROWS so the document stays valid JSON.
    /// <para>The three post-write REPORTS ride as data, not prose: a silent line, an inert result script and an empty
    /// cell are the Q3 hazards the text render shouts about, and a json consumer that could not see them would be
    /// exactly the silently-degraded mode this project refuses.</para></summary>
    public static string RenderCreateOutcome(WritePatchBuilder.CreateOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteString("lane", lane);
            WriteNullable(w, "epoch", o.Epoch);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — without it the buffered document is unwritten and the
                return Finish(ms);  // caller gets an EMPTY string (the PR #306 class; PR #310 hit it again).
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            WriteStringArray(w, "masters", o.Masters.ToList());

            // #300's trade, hoisted ABOVE the budgeted `created` array (review [medium]) — the json twin of the text
            // render's "!" lines, and the same rule the divergence rows follow: a statement that this artifact will
            // out-rank a mod on a parent record it only meant to host a child in must survive a max_chars cut. One
            // entry per distinct contested parent; empty when every host was uncontested.
            // BOUNDED on the SAME constant as the text twin (PR #323 review [medium]): hoisting an UNBOUNDED
            // set-valued block above the budget just moves the overflow — at ~600-700 chars per host, a bulk_create
            // fanning children into many distinct contested cells spent the whole budget here and left `created`
            // rendering "0 of N, truncated". The text side was capped for exactly this and the json side was missed,
            // which made the two lanes disagree about the same call (D2). `total_contested_parent_hosts` carries the
            // full distinct count regardless — the same total/list pair `created` uses below — so the cut is stated,
            // never silent (Q3); each host past the cap is still named on its own record's `parent_host`.
            var contestedHosts = o.Created.Where(c => c.ParentContested && c.ParentHost is not null)
                                  .Select(c => c.ParentHost!).Distinct(StringComparer.Ordinal).ToList();
            w.WriteNumber("total_contested_parent_hosts", contestedHosts.Count);
            w.WriteStartArray("contested_parent_hosts");
            foreach (var host in contestedHosts.Take(Wire.ContestedHostsShown)) w.WriteStringValue(host);
            w.WriteEndArray();

            w.WriteNumber("total_created", o.Created.Count);
            w.WriteStartArray("created");
            int rendered = 0;
            bool truncated = false;
            foreach (var c in o.Created)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", c.FormKey.ToString());
                w.WriteString("record_type", c.RecordType);
                w.WriteString("editorid", c.EditorId);
                // A replace is never silent (the CreatedRecord contract): the same fact the text render puts in
                // brackets, as a flag a consumer can branch on.
                w.WriteBoolean("replaced_existing", c.ReplacedExisting);
                // #300 — the parent override this nested create hosted the child in, and whose version was copied.
                WriteNullable(w, "parent_host", c.ParentHost);
                w.WriteBoolean("parent_contested", c.ParentContested);
                w.WriteStartArray("ops");
                foreach (var op in c.Ops)
                {
                    w.WriteStartObject();
                    w.WriteString("label", op.Label);
                    w.WriteBoolean("applied", op.Applied);
                    WriteNullable(w, "error", op.Error);
                    WriteNullable(w, "after", op.After);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_created", rendered);

            // The three post-write reports are INSIDE the budget (PR #311 round-2 review [low-medium]): the TEXT
            // twin already stops each with an explicit notice, so leaving them unguarded here both blew the
            // document past max_chars and closed it with truncated:false — the silent cut max_chars exists to
            // prevent, and a D2 divergence in the direction that matters.
            WriteVoiceReport(w, o.Voice, ms, cap, ref truncated);
            WriteScriptBindingReport(w, o.ScriptBinding, ms, cap, ref truncated);
            WriteCellShellReport(w, o.CellShell, ms, cap, ref truncated);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, false, readback, ref truncated);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // NOT the sibling renders' "raise max_chars to see the rest" (PR #311 review 4 [medium]). That remedy is
            // safe on remove/forward/apply — a repeated remove is refused, a repeated forward re-copies identical
            // bodies — but a repeated CREATE allocates the records AGAIN, and the trap does not care which transport
            // asked: the text twin was moved off this wording one fold earlier and the json document kept it, so a
            // json client raising max_chars and re-issuing walked into exactly what the fix existed to prevent.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; "
                    + WriteSentences.CreateRowsCutRemedy(WriteTools.ReadBackCall(o, Path.GetFileName(o.OutputPath))) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_write_seq (W3 PR 2) --------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="SeqTools.Render"/>. Two Q3 facts the text render states in
    /// prose are typed here: <c>written:false</c> with <c>quest_count:0</c> is the "no SGE quests, so no .seq is
    /// needed" no-op (never a silent empty file), and <c>epoch:null</c> carries its own reason — this call consults
    /// no load-order build, so an absent stamp is a fact rather than a missing field.
    /// <para>#312 adds the third: <c>written:false</c> with <c>unchanged:true</c> and a non-null <c>seq_path</c> is
    /// "the destination already held exactly these bytes". <c>written</c> is therefore the fact "this call wrote the
    /// file", never merely "a path exists" — the two had been the same thing until a lane could decline to write.</para></summary>
    public static string RenderSeqOutcome(SeqOutcome o, int maxChars, string? outputNote = null)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteNull("epoch");
            w.WriteString("epoch_note", WriteSentences.Twins.SeqNoEpoch);
            if (!o.Success)
            {
                WriteNullable(w, "error", o.Error);
                WriteNullable(w, "lane_note", outputNote);   // an ignored lane stays stated on a refusal too (review round 2)
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — see RenderPatchOutcome (an unflushed refusal renders EMPTY).
                return Finish(ms);
            }

            w.WriteString("plugin", o.PluginFileName);
            WriteNullable(w, "source_read_from", o.ResolvedFrom);
            WriteNullable(w, "source_path", o.PluginPath);
            w.WriteBoolean("written", o.SeqPath is not null && !o.Unchanged);
            w.WriteBoolean("unchanged", o.Unchanged);
            w.WriteBoolean("replaced", o.Replaced);
            w.WriteBoolean("replaced_same_bytes", o.ReplacedSameBytes);
            w.WriteBoolean("timestamp_refreshed", o.TimestampRefreshed);
            if (o.Unchanged)
                w.WriteString("unchanged_note", WriteSentences.Twins.SeqUnchanged
                    + " — seq_path names the file that was already current. Stated rather than reported as a write (Q3: a skipped write and a done one must not look alike)."
                    + (o.TimestampRefreshed ? " Also: " + WriteSentences.Twins.SeqTimestampRefreshed : ""));
            if (o.Replaced)
                w.WriteString("replaced_note", o.ReplacedSameBytes
                    ? WriteSentences.Twins.SeqReplacedSameBytes
                    : o.UserChoseOutput
                    ? WriteSentences.Twins.SeqReplacedUserFolder
                    : WriteSentences.Twins.SeqReplacedOwnFolder);
            WriteNullable(w, "seq_path", o.SeqPath);
            WriteNullable(w, "mod_folder", o.ModFolder);
            w.WriteBoolean("wrote_into_plugin_folder", o.WroteIntoPluginFolder);
            w.WriteBoolean("user_chose_output_dir", o.UserChoseOutput);
            WriteNullable(w, "deploy_warning", o.DeployWarning);
            WriteNullable(w, "lane_note", outputNote);
            w.WriteNumber("quest_count", o.Quests.Count);
            if (o.Quests.Count == 0)
                w.WriteString("note", "no start-game-enabled quests in this plugin — " + WriteSentences.Twins.SeqNoQuests + "."
                    // The lane was ACKNOWLEDGED but never resolved on this path, and the json shape shows that more
                    // starkly than the prose does: user_chose_output_dir true, no seq_path, no deploy_warning. Say
                    // which of the two it is (PR #318 review [low]).
                    + (o.UserChoseOutput ? " output_dir= was not resolved or checked either — no destination was touched, so an unusable one would not have been reported here." : ""));

            w.WriteStartArray("quests");
            int rendered = 0;
            bool truncated = false;
            foreach (var q in o.Quests)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                WriteNullable(w, "editorid", q.EditorId is { Length: > 0 } e ? e : null);
                w.WriteString("on_disk_formid", $"0x{q.OnDiskFormId:X8}");
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_quests", rendered);
            w.WriteBoolean("truncated", truncated);
            // Not "raise max_chars to see the rest" (PR #311 review 5 [medium]): widening the ceiling means
            // re-issuing a WRITE. This PR moved SeqTools.Render off exactly this wording and left its json twin on
            // it — the same D2 divergence, in the same fold that fixed it one renderer up.
            if (truncated)
                w.WriteString("truncated_note",
                    $"the render hit max_chars={cap} and dropped trailing quest rows — " + WriteSentences.Twins.SeqListCutRemedy + ".");
            w.WriteString("standing_limit", WriteSentences.Twins.SeqStandingLimit);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_remove (W3 PR 2) -----------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderRemoval"/> — the SAME data (decision D2),
    /// on <see cref="RenderPatchOutcome"/>'s contract: a refusal is a document, the consent prompt is its own flag,
    /// the epoch rides on every response. <c>remaining_records:0</c> is the "this file is now an inert shell" fact
    /// the text render spells out in a sentence.</summary>
    public static string RenderRemovalOutcome(WritePatchBuilder.RemovalOutcome o, int maxChars, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteString("lane", lane);
            WriteNullable(w, "epoch", o.Epoch);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — see RenderPatchOutcome (an unflushed refusal renders EMPTY).
                return Finish(ms);
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            w.WriteNumber("remaining_records", o.RemainingRecords);
            WriteStringArray(w, "masters", o.Masters.ToList());

            w.WriteNumber("total_removed", o.Removed.Count);
            w.WriteStartArray("removed");
            int rendered = 0;
            bool truncated = false;
            foreach (var r in o.Removed)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", r.Target.ToString());
                w.WriteString("record_type", r.RecordType);
                WriteNullable(w, "editorid", r.EditorId);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_removed", rendered);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Same remedy as the text twin, from the same constant (PR #311 review 6 [medium]): a repeated remove
            // is REFUSED, so "raise max_chars" named the one call guaranteed to fail.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(false, "removed")} — "
                    + WriteTools.RemovedRowsRemedy + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_forward (W3 PR 2) ----------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderForward"/> — the SAME data (decision D2),
    /// on <see cref="RenderPatchOutcome"/>'s contract. The two per-record facts the text render puts in brackets are
    /// flags here: <c>replaced_existing</c> (an override this artifact already carried had its FIELDS replaced, with
    /// <c>preserved_children</c> naming how many nested records rode across the replace — #324) and
    /// <c>was_already_winner</c> (the forward re-asserts content that already wins — a no-op in effect, reported
    /// rather than silent).</summary>
    public static string RenderForwardOutcome(WritePatchBuilder.ForwardOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteBoolean("dry_run", o.DryRun);
            w.WriteString("lane", lane);
            WriteNullable(w, "epoch", o.Epoch);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — see RenderPatchOutcome (an unflushed refusal renders EMPTY).
                return Finish(ms);
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            WriteStringArray(w, "masters", o.Masters.ToList());

            // The text twin's `source:` disclosure line. `source_in_order` is emitted on BOTH arms so a consumer reads
            // the fact positively rather than inferring it from an absent object; `source_read` names WHICH copy on disk
            // an off-order read opened (a filename alone does not identify it).
            w.WriteBoolean("source_in_order", o.OffOrderSource is null);
            if (o.OffOrderSource is { } oo)
            {
                w.WriteStartObject("source_read");
                w.WriteString("source", oo.Plugin);
                w.WriteString("path", oo.Path);
                w.WriteString("where", oo.Where);
                // Non-null ⇒ the file is the order's copy of a plugin EXCLUDED as unparseable, reached by PATH. Allowed
                // (copying one body out is not a re-serialize) but never silent — the text twin says the same.
                WriteNullable(w, "excluded_from_index", oo.ExcludedReason);
                // Same honesty the read surface's `epoch_covers_all_inputs` carries: the stamp fingerprints the ACTIVE
                // order, and this file's content sits outside it.
                w.WriteBoolean("epoch_covers_source", false);
                w.WriteEndObject();
            }

            w.WriteNumber("total_forwarded", o.Forwarded.Count);
            w.WriteStartArray("forwarded");
            int rendered = 0;
            bool truncated = false;
            foreach (var f in o.Forwarded)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", f.Target.ToString());
                w.WriteString("record_type", f.RecordType);
                WriteNullable(w, "editorid", f.EditorId);
                w.WriteString("source", f.FromPlugin);
                w.WriteBoolean("replaced_existing", f.ReplacedExisting);
                // #324 — how many records nested under the replaced one were carried across. The text render states
                // it in words; a consumer branching on replaced_existing alone would still read the replace as total.
                w.WriteNumber("preserved_children", f.PreservedChildren);
                w.WriteBoolean("was_already_winner", f.WasAlreadyWinner);
                WriteNullable(w, "prior_winner", f.PriorWinner);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_forwarded", rendered);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, o.DryRun, readback, ref truncated);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Lane-aware, same rule and same helper as the text twin (PR #311 review 5 [low]): a re-issue is
            // idempotent on in_place=/into= and free on a dry run, but on the DEFAULT lane it cuts a second patch.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "forwarded")} — "
                    + WriteTools.ForwardAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The voice-coverage report as data (a created dialogue line with no .fuz plays SILENT in game). The
    /// text render's "[!] WILL BE SILENT" becomes <c>fuz_present:false</c> + the path to put the audio at.</summary>
    static void WriteVoiceReport(Utf8JsonWriter w, VoiceReport? report, MemoryStream ms, int cap, ref bool truncated)
    {
        if (report is null || report.IsEmpty) return;
        w.WriteStartObject("voice_coverage");
        WriteNullable(w, "check_error", report.CheckError);
        int renderedLines = 0, renderedUndet = 0;
        bool blockCut = false;
        w.WriteStartArray("lines");
        foreach (var l in report.Lines)
        {
            if (Over(w, ms, cap)) { truncated = true; blockCut = true; break; }
            w.WriteStartObject();
            w.WriteString("info", l.Info.ToString());
            WriteNullable(w, "topic_editorid", l.TopicEditorId);
            w.WriteNumber("response", l.ResponseNumber);
            w.WriteBoolean("fuz_present", l.FuzPresent);
            w.WriteBoolean("lip_present", l.LipPresent);
            WriteNullable(w, "fuz_path", l.FuzPath);
            WriteNullable(w, "lip_path", l.LipPath);
            WriteNullable(w, "fuz_winner", l.FuzWinner);
            w.WriteBoolean("fuz_contended", l.FuzAmbiguous);
            // An "absent" that merely went unscanned is not the same claim as an absent that was looked for and
            // not found — the text render says so in a note; here it is per-line data.
            w.WriteBoolean("read_incomplete", l.ReadIncomplete);
            w.WriteEndObject();
            renderedLines++;
        }
        w.WriteEndArray();
        w.WriteStartArray("undetermined");
        foreach (var u in report.Undetermined)
        {
            if (Over(w, ms, cap)) { truncated = true; blockCut = true; break; }
            w.WriteStartObject();
            w.WriteString("info", u.Info.ToString());
            WriteNullable(w, "topic_editorid", u.TopicEditorId);
            w.WriteString("reason", u.Reason);
            w.WriteEndObject();
            renderedUndet++;
        }
        w.WriteEndArray();
        WriteBlockCensus(w, blockCut, ("lines", renderedLines, report.Lines.Count),
                                      ("undetermined", renderedUndet, report.Undetermined.Count),
            "voice coverage", cap, WriteSentences.Twins.VoiceStake);
        w.WriteEndObject();
    }

    /// <summary>The per-BLOCK truncation census the three post-write reports carry (PR #311 review 5 [medium]).
    /// <para>Without it a cut block renders as <c>lines: []</c> — indistinguishable from "nothing to report", which
    /// is the exact inversion of what these blocks exist to say: an empty voice list reads as "every created line
    /// is voiced" when it may mean "150 silent lines were dropped by the budget". The text renders have said so
    /// since they were written (<c>AppendVoiceTrunc</c>); only the json twins were silent. And it is not an edge —
    /// <see cref="RenderCreateOutcome"/>'s created rows budget against the SAME <c>cap</c> and run first, so
    /// whenever they truncate, <c>Over</c> is already true at each report's first row and every block renders
    /// empty, deterministically. The document-level <c>truncated</c> flag is a weaker claim: it does not say WHICH
    /// block lost rows.</para>
    /// <para>Counts ride even when nothing was cut — <c>rendered == total</c> is the positive statement that the
    /// list IS complete, so a consumer never has to infer completeness from the absence of a marker.</para></summary>
    static void WriteBlockCensus(Utf8JsonWriter w, bool cut, (string name, int rendered, int total) a,
                                 (string name, int rendered, int total)? b, string blockLabel, int cap, string stakes,
                                 string? cutLoss = null)
    {
        w.WriteNumber($"total_{a.name}", a.total);
        w.WriteNumber($"rendered_{a.name}", a.rendered);
        if (b is { } bb)
        {
            w.WriteNumber($"total_{bb.name}", bb.total);
            w.WriteNumber($"rendered_{bb.name}", bb.rendered);
        }
        w.WriteBoolean("truncated", cut);
        // Deliberately NOT "raise max_chars": these blocks ride the WRITE renders, and re-issuing a create or an
        // apply on the default lane auto-suffixes a second patch (the class this PR has now been told about three
        // times). The note states the cut and the stakes and stops there — the write is done, the report is only a
        // render of it, and the counts above are what a consumer branches on.
        if (cut)
            w.WriteString("truncated_note",
                $"the {blockLabel} block hit max_chars={cap} and its rows were CUT. Why it matters: {stakes}"
                + (cutLoss is null ? "" : $", and {cutLoss}")
                + ". An empty or short array here is a RENDER cut, not a clean bill of health — "
                + WriteSentences.Twins.ReportBlockCut + " (the counts are the total_* / rendered_* members above).");
    }

    /// <summary>The result-script binding report as data (a bound script that is unwired or uncompiled runs NOTHING
    /// in game). <c>status</c> is the enum name the text render turns into "WILL NOT FIRE".</summary>
    static void WriteScriptBindingReport(Utf8JsonWriter w, ScriptBindingReport? report, MemoryStream ms, int cap, ref bool truncated)
    {
        if (report is null || report.IsEmpty) return;
        w.WriteStartObject("result_script_coverage");
        WriteNullable(w, "check_error", report.CheckError);
        int renderedFindings = 0;
        bool blockCut = false;
        w.WriteStartArray("findings");
        foreach (var f in report.Findings)
        {
            if (Over(w, ms, cap)) { truncated = true; blockCut = true; break; }
            w.WriteStartObject();
            w.WriteString("info", f.Info.ToString());
            WriteNullable(w, "topic_editorid", f.TopicEditorId);
            w.WriteString("status", f.Status.ToString());
            w.WriteString("detail", f.Detail);
            WriteStringArray(w, "missing_pex", f.MissingPex);
            w.WriteBoolean("read_incomplete", f.ReadIncomplete);
            w.WriteEndObject();
            renderedFindings++;
        }
        w.WriteEndArray();
        WriteBlockCensus(w, blockCut, ("findings", renderedFindings, report.Findings.Count), null,
            "result-script coverage", cap, WriteSentences.Twins.ScriptStake);
        w.WriteEndObject();
    }

    /// <summary>The cell-shell report as data (a created cell is a valid, correctly-placed record but EMPTY —
    /// houseCARL does not author world content). <c>must_provide</c> is the Creation-Kit work list.</summary>
    static void WriteCellShellReport(Utf8JsonWriter w, CellShellReport? report, MemoryStream ms, int cap, ref bool truncated)
    {
        if (report is null || report.IsEmpty) return;
        w.WriteStartObject("cell_shell");
        WriteNullable(w, "check_error", report.CheckError);
        int renderedCells = 0;
        bool blockCut = false;
        w.WriteStartArray("cells");
        foreach (var c in report.Cells)
        {
            if (Over(w, ms, cap)) { truncated = true; blockCut = true; break; }
            w.WriteStartObject();
            w.WriteString("cell", c.Cell.ToString());
            w.WriteString("editorid", c.EditorId);
            w.WriteBoolean("interior", c.Interior);
            WriteStringArray(w, "must_provide", c.MustProvide);
            w.WriteEndObject();
            renderedCells++;
        }
        w.WriteEndArray();
        WriteBlockCensus(w, blockCut, ("cells", renderedCells, report.Cells.Count), null, "cell shell", cap,
            WriteSentences.Twins.CellStake, WriteSentences.CellRowsCutLoss);
        // The grid-occupancy seam the text render declares — a json consumer must not read "cells: []" as "checked".
        if (report.Cells.Any(c => !c.Interior))
            w.WriteString("grid_occupancy_note", WriteSentences.Twins.GridOccupancy);
        w.WriteEndObject();
    }
}
