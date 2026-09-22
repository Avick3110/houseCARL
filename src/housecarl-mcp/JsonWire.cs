using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The format="json" twin of the text <see cref="Wire"/> renderer: one serializer per read tool over the
/// SAME outcome objects, so text and JSON differ only in formatting; contracts in docs/architecture/json-wire.md.</summary>
static class JsonWire
{
    /// <summary>The options every json response is written under: indented, and the server's one text encoder.</summary>
    internal static readonly JsonWriterOptions Opts =
        new() { Indented = true, Encoder = JsonTextEncoder.Encoder };

    /// <summary>The same options, so <see cref="CheckAccounting"/> measures its reserve in the encoding it will be written in.</summary>
    internal static JsonWriterOptions WriterOptions => Opts;

    static string Finish(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray());

    static void WriteNullable(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }

    /// <summary>The ONE way a json document writes its epoch stamp and the degraded-order marker beside it (#353);
    /// the marker is a sibling of the stamp, never inside it — docs/architecture/json-wire.md.</summary>
    static void WriteEpoch(Utf8JsonWriter w, OrderStamp? stamp) =>
        WriteEpoch(w, stamp?.Epoch, stamp?.ExcludedPlugins);

    /// <summary>The same writer for a lane carrying the epoch and the excluded roster as two values, not one stamp.</summary>
    static void WriteEpoch(Utf8JsonWriter w, string? epoch, IReadOnlyCollection<string>? excluded)
    {
        WriteNullable(w, "epoch", epoch);
        WriteOrderDegraded(w, excluded);
    }

    /// <summary>The marker on its own, for a document that states it at the ROOT rather than beside an epoch.</summary>
    static void WriteOrderDegraded(Utf8JsonWriter w, IReadOnlyCollection<string>? excluded)
    {
        if (excluded is not { Count: > 0 }) return;
        w.WriteBoolean("order_degraded", true);
        w.WriteString("order_degraded_note", OrderDegraded.Sentence(excluded));
    }

    /// <summary>A record's runtime address: <c>runtime_formid</c> always, <c>runtime_formid_note</c> only when owed.</summary>
    static void WriteRuntime(Utf8JsonWriter w, string? runtime, string? note)
    {
        WriteNullable(w, "runtime_formid", runtime);
        if (note is not null) w.WriteString("runtime_formid_note", note);
    }

    /// <summary>The array twin of <see cref="WriteNullable"/>: null says NOT COMPUTED, <c>[]</c> says computed and empty.</summary>
    static void WriteNullableStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string>? items)
    {
        if (items is null) w.WriteNull(name); else WriteStringArray(w, name, items);
    }

    /// <summary>The ONE way a json document declares itself a refusal: <c>ok:false</c> then the message; the
    /// discriminant's grammar is in docs/architecture/json-wire.md.</summary>
    internal static void WriteRefusal(Utf8JsonWriter w, string? error)
    {
        w.WriteBoolean("ok", false);
        // `error` is nullable because a caller can pass an optional DTO field straight in.
        WriteNullable(w, "error", error);
    }

    // ---- housecarl_resolve ---------------------------------------------------------------------------
    /// <summary>Render the bulk name-resolution result as JSON: one identity row per input, <c>{formid,error}</c> for a bad one.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    /// <summary>Optional response-envelope pairs written as top-level string fields at the START of a document;
    /// their keys must stay disjoint from every renderer's own — docs/architecture/json-wire.md.</summary>
    static void WriteEnvelope(Utf8JsonWriter w, IReadOnlyList<KeyValuePair<string, string>>? envelope)
    {
        if (envelope is null) return;
        foreach (var kv in envelope) w.WriteString(kv.Key, kv.Value);
    }

    /// <param name="bodyCost">What resolving these FormIDs cost, when measured; the count is the ids that RESOLVED (#607).</param>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch, SpillState? spill, out bool truncated,
                                       IReadOnlyList<KeyValuePair<string, string>>? envelope = null,
                                       (int RowsRead, long Millis)? bodyCost = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", rows.Count);
            WriteEpoch(w, epoch);   // the ONE captured build the whole batch resolved against
            w.WriteStartArray("resolved");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var r in rows)
            {
                if (manifestOnly) break;   // to_file: the rows are the FILE
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                WriteResolvedRow(w, r);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            // The count is the bodies READ, not this window's rows.
            if (bodyCost is { } bc) { w.WriteNumber("rows_read", bc.RowsRead); w.WriteNumber("render_ms", bc.Millis); }
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One housecarl_resolve row: the identity fields when it resolved, else a single <c>error</c>.</summary>
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

    // ---- housecarl_diff_record ----------------------------------------------------------------------
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

    /// <summary>A whole-call refusal document: <c>{ok:false, error, epoch}</c>, for tool-layer refusals with no
    /// outcome object to render; the read surface's <c>ok</c> asymmetry is in docs/architecture/json-wire.md.</summary>
    internal static string RenderError(string error, OrderStamp? epoch)
    {
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteRefusal(w, error);
            WriteEpoch(w, epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>Is the document already at its char ceiling? Flushes first, because the writer buffers.</summary>
    static bool Over(Utf8JsonWriter w, CharCountedStream ms, int cap)
    {
        w.Flush();
        return Chars(ms) >= cap;
    }

    /// <summary>The post-write read-back block, one construction for apply / create / forward: <c>readback_source</c>
    /// names the WRITTEN FILE's content or a dry run's in-memory would-be content, never load-order truth, and
    /// <c>readback_requested</c> carries the caller's ask; <c>readback_full</c> describes this document and must not
    /// be made to carry that ask, pinned by <c>WriteSurfaceGuardProbe</c> ("an in-place lane that FORCED the
    /// read-back reports readback_full:true, ask kept separately").</summary>
    static void WriteReadbackBlock(Utf8JsonWriter w, CharCountedStream ms, int cap,
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
            w.WriteString("formid", FormIdToken.Of(r.Target));
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
                    // An opaque blob's annotation rides here too, gated on the bytes marker.
                    if (f.Bytes is { } n)
                    {
                        if (f.Display is not null) w.WriteString("display", f.Display);
                        w.WriteNumber("opaque_bytes", n);
                        if (f.BytesFormVersion is { } bfv) w.WriteNumber("opaque_form_version", bfv);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>The loose roots a render names, as a bounded array plus its sibling count — the json twin of
    /// <see cref="BatchRender.RootFailureLines"/>, cut by that same rule so both transports name the same roots.</summary>
    internal static void WriteRootFailuresCut(Utf8JsonWriter w, IReadOnlyList<string> failures, int cap)
    {
        var (shown, omitted) = BatchRender.RootFailureCut(failures, cap);
        WriteStringArray(w, "root_read_failures", shown);
        w.WriteNumber("root_read_failures_omitted", omitted);
    }

    static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    /// <summary>The json twin of <see cref="BatchRender.AppendLines"/>: a caveat list bounded by the SAME budget the row
    /// loop is, cut as a sibling <c>_omitted</c> count. Pinned by
    /// <c>AssetStatusJsonLaneTests.TheCaveatBlocksAreCappedByMaxCharsToo</c>.</summary>
    internal static int WriteCappedStringArray(Utf8JsonWriter w, CharCountedStream ms, string name, IReadOnlyList<string> items,
                                              int budget)
    {
        w.WriteStartArray(name);
        int shown = 0;
        foreach (var s in items)
        {
            // shown > 0: the first line always renders, exactly as it does on the text lane.
            if (shown > 0 && Over(w, ms, budget)) break;
            w.WriteStringValue(s);
            shown++;
        }
        w.WriteEndArray();
        int omitted = items.Count - shown;
        w.WriteNumber(name + "_omitted", omitted);
        return omitted;
    }

    // ---- shared record + field writers --------------------------------------------------------------
    /// <summary>Serialize the fields array: <c>{path, value}</c> for a round-trippable leaf, <c>{path, note}</c> for a
    /// no-value one, with a sentinel field naming a field-count cut.</summary>
    /// <param name="emitted">Collects the annotated paths this array ACTUALLY carried.</param>
    static void WriteFieldsArray(Utf8JsonWriter w, RecordFields r, CharCountedStream ms, int cap,
                                 IReadOnlyDictionary<string, ChildUnion?>? annotated = null, IDictionary<string, bool>? emitted = null,
                                 LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        w.WriteStartArray("fields");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            w.Flush();
            if (Chars(ms) >= cap)
            {
                w.WriteStartObject();
                w.WriteString("path", "…");   // …
                var narrow = lv.HasFieldSelector ? $"narrow with {lv.Fields}, " : "";   // the form may have no field selector to narrow with
                w.WriteString("note", $"[truncated at max_chars: {i} of {r.Fields.Count} fields shown; {narrow}lower {lv.Depth}, or raise max_chars]");
                w.WriteEndObject();
                break;
            }
            var f = r.Fields[i];
            w.WriteStartObject();
            WriteLeaf(w, f);
            if (annotated is not null && annotated.TryGetValue(f.Path, out var union) && union is not null) WriteChildUnion(w, union, ms, cap);
            if (f.Cells is { } cells)
            {
                // A folded row (the 'rows' form): the leaves it folded ride here with their tokens intact.
                w.WriteStartArray("cells");
                foreach (var c in cells) { w.WriteStartObject(); WriteLeaf(w, c); w.WriteEndObject(); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            // The TIER travels with the field: a clause is stated per tier.
            if (annotated is not null && emitted is not null && annotated.TryGetValue(f.Path, out var tier)) emitted[f.Path] = tier is not null;
        }
        w.WriteEndArray();
    }

    /// <summary>One leaf's members into the open object: path, value or note, and the display-only annotations.</summary>
    static void WriteLeaf(Utf8JsonWriter w, FieldValue f)
    {
        w.WriteString("path", f.Path);
        if (f.HasValue) w.WriteString("value", f.Token);   // round-trip parity: identical token to the text render
        else if (f.Cells is null) WriteNullable(w, "note", f.Note);
        // The FormID a no-value summary note SPELLED, beside the prose that spells it.
        if (f.NoteRef is { } noteRef && f.Cells is null) w.WriteString("note_ref", noteRef);
        if (f.Display is not null) w.WriteString("display", f.Display);
        // The blob's byte length as a NUMBER beside its hex value.
        if (f.Bytes is { } opaque) w.WriteNumber("opaque_bytes", opaque);
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
    }

    /// <summary>The additive union of a child-bearing field as structure, capped at <see cref="ChildUnionMemberCap"/>.</summary>
    static void WriteChildUnion(Utf8JsonWriter w, ChildUnion u, CharCountedStream ms, int cap)
    {
        w.WriteStartObject("owned_child_union");
        w.WriteString("shape", u.Shape.ToString());
        w.WriteNumber("total", u.Total);
        w.WriteNumber("own", u.OwnCount);
        // Stated only where it changes what `own`/`total` mean against the field's `value`.
        if (u.Nested) w.WriteBoolean("nested", true);
        // A SINGULAR field's declarers override one record, so one IS live; a COLLECTION is additive (#342).
        WriteNullable(w, u.Shape == OwnedChildShape.Singular ? "live_plugin" : "highest_declarer", u.LivePlugin);
        w.WriteStartArray("declarers");
        foreach (var d in u.Declarers)
        {
            w.WriteStartObject();
            w.WriteString("plugin", d.Plugin);
            w.WriteNumber("count", d.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (u.Unreadable.Count > 0)
        {
            w.WriteStartArray("unreadable");
            foreach (var p in u.Unreadable) w.WriteStringValue(p);
            w.WriteEndArray();
        }
        // Bounded by BOTH the flat cap and what is left of max_chars; whatever is not listed is counted.
        w.Flush();
        int room = Math.Max(0, cap - Chars(ms)) / ChildUnionMemberChars;
        int listed = Math.Min(u.Members.Count, Math.Min(ChildUnionMemberCap, room));
        w.WriteStartArray("members");
        for (int i = 0; i < listed; i++) w.WriteStringValue(FormIdToken.Of(u.Members[i]));
        w.WriteEndArray();
        if (u.Members.Count > listed) w.WriteNumber("members_omitted", u.Members.Count - listed);
        w.WriteEndObject();
    }

    /// <summary>The budget one listed member costs in CHARACTERS, deliberately generous — under-counting overruns the cap.</summary>
    const int ChildUnionMemberChars = 40;

    /// <summary>How many union members one field object lists; the array is a sample with its remainder counted.</summary>
    internal const int ChildUnionMemberCap = 100;

    /// <summary>Serialize a resolved record: identity, winner/override_depth/source, and the fields array. Shared by
    /// housecarl_records' single-record, batch and scan detail paths.</summary>
    /// <param name="stateChildNote">Single-read lane only: this record object IS the response, so the clause goes on it.</param>
    internal static void WriteReadRecord(Utf8JsonWriter w, ReadOutcome o, CharCountedStream ms, int cap, string? matches = null,
                                         OrderStamp? epoch = null, IDictionary<string, bool>? childFields = null, bool stateChildNote = false,
                                         LeverNames? levers = null)
    {
        var r = o.Record!;
        w.WriteStartObject();
        if (epoch is not null) WriteEpoch(w, epoch);   // single-read top level ONLY
        w.WriteString("formid", r.FormKey);
        WriteRuntime(w, o.RuntimeFormId, o.RuntimeFormIdNote);
        w.WriteString("type", r.Type);
        WriteNullable(w, "editorid", r.EditorId);
        WriteNullable(w, "winner", o.WinnerPlugin);
        w.WriteNumber("override_depth", o.OverrideDepth);
        WriteNullable(w, "source", o.SourcePlugin);   // the body these field VALUES came from (scoped plugin vs winner)
        if (matches is not null) w.WriteString("matches", matches);
        WriteFieldsArray(w, r, ms, cap, o.OwnedChildFields, childFields, levers);
        if (stateChildNote && childFields is { Count: > 0 }) WriteOwnedChildNote(w, childFields);
        w.WriteEndObject();
    }

    // ---- housecarl_read_record ----------------------------------------------------------------------
    /// <summary>The owned-child clause, written ONCE per response over the annotated fields the document CARRIES.
    /// json only ever states the cheap tier's clause — <c>conflict_tree=true</c> is refused in json mode.</summary>
    static void WriteOwnedChildNote(Utf8JsonWriter w, IDictionary<string, bool> fields)
    {
        // One clause per tier the document actually stated, in one member.
        var said = ReadSentences.OwnedChildClauses(ReadSentences.Tier(fields, true), ReadSentences.Tier(fields, false));
        if (said.Count > 0) w.WriteString("owned_child_note", string.Join(" ", said));
    }

    // ---- housecarl_records: the batch read ----------------------------------------------------------
    /// <summary>A batch read as JSON; a bad or absent formid is a per-item <c>{formid,error}</c>.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    /// <summary><paramref name="levers"/>: the CALLER's lever vocabulary; omitted means the 1.x spelling.</summary>
    /// <param name="bodyCost">What reading these bodies cost; the count is the BODIES READ, short of the list (#582, #607).</param>
    /// <param name="matches">Which multi-target references= target(s) each row hit; null when there was no such lookup (#576).</param>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars, SpillState? spill, out bool truncated,
                                     IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null,
                                     (int RowsRead, long Millis)? bodyCost = null,
                                     IReadOnlyList<string?>? matches = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", outcomes.Count);
            // The whole batch reads ONE captured build (ResolveBatch): the first non-null stamp.
            WriteEpoch(w, outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false;
            var childFields = new SortedDictionary<string, bool>(StringComparer.Ordinal);   // the annotated fields the rows RENDERED carried, per tier
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (manifestOnly) break;   // to_file: the rows are the FILE
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                var o = outcomes[i];
                string? hit = matches is { } mt && i < mt.Count ? mt[i] : null;   // multi-target references= un-merge
                if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", FormIdToken.Of(o.FormKey)); w.WriteString("error", o.Error); if (hit is not null) w.WriteString("matches", hit); w.WriteEndObject(); }
                else WriteReadRecord(w, o, ms, cap, hit, childFields: childFields, levers: levers);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            // What the bodies this batch renders cost to read, when the caller clocked them (#582).
            if (bodyCost is { } bc) { w.WriteNumber("rows_read", bc.RowsRead); w.WriteNumber("render_ms", bc.Millis); }
            w.WriteBoolean("truncated", rowsTruncated);
            // Over the annotated fields this document actually carries, never the input list.
            WriteOwnedChildNote(w, childFields);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records --------------------------------------------------------------------------

    /// <summary>records counts_only on the list lane: the census document, no rows. The resolved count is named
    /// <c>resolved</c>, never <c>ok</c> — that key is the refusal grammar's discriminant.</summary>
    /// <param name="maxChars">the caller's max_chars: this document has no rows to cut, so the cap can only be
    /// missed outright, and it says so with the member every other capped document closes on (#809). REQUIRED, with
    /// no default, so a call site that forgets it does not compile into a silent 80k ceiling.</param>
    public static string RenderCounts(IReadOnlyList<KeyValuePair<string, string>> envelope, int count, int ok, int errors, OrderStamp? epoch,
                                      int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", count);
            w.WriteNumber("resolved", ok);
            w.WriteNumber("errors", errors);
            WriteEpoch(w, epoch);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records counts_only for forms with named counters (delta, tree): the envelope and the counters, no rows.</summary>
    /// <param name="maxChars">as <see cref="RenderCounts"/>: no rows to cut, so an over-cap census says so.</param>
    public static string RenderNamedCounts(IReadOnlyList<KeyValuePair<string, string>> envelope,
                                           IReadOnlyList<KeyValuePair<string, int>> counts, OrderStamp? epoch,
                                           int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var c in counts) w.WriteNumber(c.Key, c.Value);
            WriteEpoch(w, epoch);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=summary on the list lane: one identity+winner row per outcome, or its per-item error.</summary>
    public static string RenderRecordsSummary(IReadOnlyList<ReadOutcome> outcomes, int maxChars,
                                              IReadOnlyList<KeyValuePair<string, string>> envelope,
                                              SpillState? spill, (int RowsRead, long Millis) bodyCost, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", outcomes.Count);
            WriteEpoch(w, outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false;   // summary rows carry no fields, so no owned-child annotation
            foreach (var o in outcomes)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", FormIdToken.Of(o.FormKey));
                WriteRuntime(w, o.RuntimeFormId, o.RuntimeFormIdNote);
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
            // The count is the LIST's, not this window's: every id was read before the render.
            w.WriteNumber("rows_read", bodyCost.RowsRead);
            w.WriteNumber("render_ms", bodyCost.Millis);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The types the call asked for that matched nothing, written BEFORE the array the cut bounds.</summary>
    static void WriteEmptyGroups(Utf8JsonWriter w, IReadOnlyList<string>? empty)
    {
        if (empty is not { Count: > 0 }) return;
        w.WriteStartArray("empty_groups");
        foreach (var name in empty) w.WriteStringValue(name);
        w.WriteEndArray();
    }

    /// <summary>records form=aggregate on the list lane: the count table over resolved rows, per-item errors apart.</summary>
    /// <param name="maxChars">the caller's max_chars. REQUIRED, with no default, for the reason every other capped
    /// renderer's is: a call site that omitted it would compile and answer against the 80k default, and the document
    /// would ship over the caller's ceiling with no <c>max_chars_overrun</c> to say so (#809).</param>
    public static string RenderListAggregate(string groupBy, IReadOnlyList<KeyValuePair<string, int>> rows,
                                             int count, int errors, OrderStamp? epoch,
                                             (int RowsRead, long Millis) bodyCost, int maxChars,
                                             IReadOnlyList<KeyValuePair<string, string>>? envelope = null,
                                             IReadOnlyList<string>? emptyGroups = null,
                                             int rowLimit = 0)
    {
        int cap = Cap(maxChars);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);   // form + the resolved source arm + coverage qualifiers
            w.WriteString("group_by", groupBy);
            w.WriteNumber("count", count);
            // What reading the bodies this table counted cost, the same accounting every body form reports.
            w.WriteNumber("rows_read", bodyCost.RowsRead);
            w.WriteNumber("render_ms", bodyCost.Millis);
            if (errors > 0) w.WriteNumber("errors", errors);
            WriteEpoch(w, epoch);
            // How many groups the table HAS, stated before the rows, so a cut document can be sized.
            w.WriteNumber("groups_total", rows.Count);
            WriteEmptyGroups(w, emptyGroups);
            w.WriteStartArray("groups");
            int rendered = 0; bool truncated = false, byBudget = false;
            int shown = rowLimit > 0 ? Math.Min(rowLimit, rows.Count) : rows.Count;
            foreach (var (key, n) in rows.Select(r => (r.Key, r.Value)))
            {
                if (rendered >= shown) { truncated = true; break; }   // limit= caps the table's rows
                w.Flush();
                if (Chars(ms) >= cap) { truncated = true; byBudget = true; break; }
                w.WriteStartObject();
                w.WriteString("key", key);
                w.WriteNumber("count", n);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", truncated);
            // WHICH knob stopped the table, the histogram's own member; null where the table is whole.
            if (HistogramCut.For(rows.Count, rendered, byBudget) is { } cut) w.WriteString("cut_by", cut.Knob);
            else w.WriteNull("cut_by");
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records: the delta / tree comparison forms ---------------------------------------

    /// <summary>One delta row — shared by the json render and the artifact writer; a per-item refusal is
    /// <c>{formid, error, stack_above?}</c>, a compared row carries both poles and the text lane's delta strings.</summary>
    internal static void WriteDeltaRow(Utf8JsonWriter w, LoadOrderService.DeltaRow row, CharCountedStream ms, int cap)
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
            if (Chars(ms) >= cap) { cut = true; break; }
            w.WriteStringValue(delta);
            rendered++;
        }
        w.WriteEndArray();
        w.WriteNumber("delta_count", d.Deltas.Count);
        // How many of those lines are NO-VERDICTS rather than value differences.
        w.WriteNumber("no_verdict_count", d.NoVerdictCount);
        if (cut) { w.WriteNumber("deltas_rendered", rendered); w.WriteBoolean("deltas_truncated", true); }
        w.WriteNumber("agreed_count", d.AgreedCount);
        w.WriteEndObject();
    }

    /// <summary>records form=delta. The identical count only counts COMPLETE comparisons (<c>complete:false</c> otherwise).</summary>
    public static string RenderDelta(IReadOnlyList<LoadOrderService.DeltaRow> rows, int maxChars, OrderStamp? epoch,
                                     IReadOnlyList<KeyValuePair<string, string>> envelope,
                                     IReadOnlyList<KeyValuePair<string, int>> counts,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // The census covers the COMPLETE list: rows may be a WINDOW, so the caller hands the counters in.
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);
            WriteEpoch(w, epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                WriteDeltaRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One tree row — the provider stack with per-node deltas, shared by the json render and the artifact writer.</summary>
    /// <returns>true if any part of the row hit <paramref name="cap"/>; the caller must merge it into <c>truncated</c>.</returns>
    internal static bool WriteTreeRow(Utf8JsonWriter w, LoadOrderService.TreeRow row, CharCountedStream ms, int cap,
                                      LeverNames? levers = null)
    {
        // The notice vocabulary comes from the carrier, not a literal; both callers pass Records explicitly.
        var lv = levers ?? LeverNames.Legacy;
        bool truncated = false;
        w.WriteStartObject();
        w.WriteString("formid", row.Formid);
        if (row.Error is not null)
        {
            w.WriteString("error", row.Error);
            if (row.Touchers.Count > 0) WriteStringArray(w, "touchers", row.Touchers);
            w.WriteEndObject();
            return false;
        }
        WriteNullable(w, "type", row.Type);
        WriteNullable(w, "editorid", row.EditorId);
        WriteNullable(w, "reference", row.ReferencePlugin);
        WriteStringArray(w, "touchers", row.Touchers);   // priority order, winner LAST
        // The precise owned-child answer, from the same TreeRow the text lane renders.
        if (row.ChildDeclarers.Count > 0)
        {
            w.WriteStartArray("child_declarers");
            foreach (var cd in row.ChildDeclarers)
            {
                w.Flush();
                if (Chars(ms) >= cap)
                {
                    w.WriteStartObject();
                    w.WriteString("note", $"[child declarers cut at max_chars — raise max_chars or narrow with {lv.Fields}]");
                    w.WriteEndObject();
                    truncated = true;
                    break;
                }
                w.WriteStartObject();
                w.WriteString("field", cd.Field);
                w.WriteString("shape", cd.Shape.ToString());
                WriteStringArray(w, "declaring", cd.Declaring);
                WriteStringArray(w, "unreadable", cd.Unreadable);
                w.WriteString("note", ReadSentences.DeclarersNote(cd.Shape, cd.Declaring, cd.Unreadable));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteStartArray("nodes");
        foreach (var n in row.Nodes)
        {
            w.Flush();
            if (Chars(ms) >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", $"[nodes truncated at max_chars — raise max_chars or narrow with {lv.Fields}]");
                w.WriteEndObject();
                truncated = true;
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
        return truncated;
    }

    /// <summary>records form=tree: <c>{…envelope, count, contested, errors, epoch, rows:[…]}</c>.</summary>
    public static string RenderTree(IReadOnlyList<LoadOrderService.TreeRow> rows, int maxChars, OrderStamp? epoch,
                                    IReadOnlyList<KeyValuePair<string, string>> envelope,
                                    IReadOnlyList<KeyValuePair<string, int>> counts,
                                    SpillState? spill, out bool truncated, LeverNames? levers = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false; bool anyDeclarers = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                if (WriteTreeRow(w, row, ms, cap, levers)) rowsTruncated = true;
                if (row.ChildDeclarers.Count > 0) anyDeclarers = true;
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            // The reserve MUST be checked before `truncated` is written: a Utf8JsonWriter cannot un-write a property.
            w.Flush();
            bool leadOverCap = anyDeclarers
                && Chars(ms) + TruncatedPropertyReserve + DeclarersLeadReserve + Framing.RootClose >= cap;
            if (leadOverCap) rowsTruncated = true;
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (anyDeclarers && !leadOverCap) w.WriteString("child_declarers_note", ReadSentences.DeclarersLead);
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records: the chain form (walk=) --------------------------------------------------

    /// <summary>One chain row — shared by the json render and the artifact writer. Node status is 'expanded' or
    /// 'kept'. Returns whether anything in THIS row was held back at <paramref name="cap"/>.</summary>
    internal static bool WriteChainRow(Utf8JsonWriter w, LoadOrderService.WalkSeedResult row, CharCountedStream ms, int cap)
    {
        bool cut = false;
        w.WriteStartObject();
        w.WriteString("formid", row.Seed);
        if (row.Error is not null) { w.WriteString("error", row.Error); w.WriteEndObject(); return false; }
        WriteNullable(w, "type", row.Type);
        WriteNullable(w, "editorid", row.EditorId);
        w.WriteStartArray("nodes");
        foreach (var n in row.Nodes)
        {
            w.Flush();
            if (Chars(ms) >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", "[nodes truncated at max_chars — raise max_chars, or to_file= for the complete walk]");
                w.WriteEndObject();
                cut = true;
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
        if (row.Cycles.Count > 0)
        {
            // Bounded the way the nodes are: one entry per closing link, each the whole loop.
            w.WriteStartArray("cycles");
            int written = 0;
            foreach (var c in row.Cycles)
            {
                w.Flush();
                if (Chars(ms) >= cap)
                {
                    w.WriteStringValue($"[{row.Cycles.Count - written} more cycle(s) held back at max_chars — raise max_chars, or to_file= for the complete walk]");
                    cut = true;
                    break;
                }
                w.WriteStringValue(c);
                written++;
            }
            w.WriteEndArray();
        }
        if (row.CyclesCapped) w.WriteBoolean("cycles_capped", true);
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
        return cut;
    }

    /// <summary>records form=chain: <c>{…envelope, seeds, errors, epoch, rows:[…]}</c>.</summary>
    public static string RenderChain(IReadOnlyList<LoadOrderService.WalkSeedResult> rows, int maxChars, OrderStamp? epoch,
                                     IReadOnlyList<KeyValuePair<string, string>> envelope,
                                     IReadOnlyList<KeyValuePair<string, int>> counts,
                                     SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                if (WriteChainRow(w, row, ms, cap)) rowsTruncated = true;   // a row that elided inside itself counts
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=chain, walk=reverse: per seed the carriers with the MATCHING entry's payload —
    /// magnitudes AS AUTHORED, conditions not evaluated.</summary>
    public static string RenderEffectChains(IReadOnlyList<(string Seed, EffectChainResult Result)> results,
                                            int maxChars, IReadOnlyList<KeyValuePair<string, string>> envelope,
                                            IReadOnlyList<KeyValuePair<string, int>> counts, OrderStamp? epoch,
                                            SpillState? spill, out bool outTruncated)
    {
        outTruncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
            w.WriteStartArray("rows");
            bool truncated = false;
            foreach (var (seed, r) in results)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { truncated = true; break; }
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
                    if (Chars(ms) >= cap) { truncated = true; break; }
                    w.WriteStartObject();
                    w.WriteString("formid", FormIdToken.Of(row.Carrier));
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
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records: the info_order form -----------------------------------------------------

    /// <summary>One info_order row, shared by the json render and the artifact writer; positions are 1-based. The
    /// honesty gates ride as data — a negative claim about moves holds only when <c>complete</c> and
    /// <c>moves_computed</c> are both true.</summary>
    internal static void WriteInfoOrderRow(Utf8JsonWriter w, LoadOrderService.InfoOrderRow row, CharCountedStream ms, int cap)
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
        // The projection, in band: a consumer reading rows must see that this order is not the live one.
        if (io.FoldedPlugin is { } foldedBy)
        {
            w.WriteString("folded_plugin", foldedBy);
            w.WriteBoolean("folded_contributed", io.FoldContributed);
        }
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
            if (Chars(ms) >= cap)
            {
                w.WriteStartObject();
                w.WriteString("note", "[order truncated at max_chars — raise max_chars]");
                w.WriteEndObject();
                break;
            }
            w.WriteStartObject();
            w.WriteNumber("position", e.Index + 1);
            w.WriteString("info", FormIdToken.Of(e.Info));
            w.WriteString("placed_by", e.PlacedBy);
            if (io.FoldedPlugin is { } fp && e.PlacedBy.Equals(fp, StringComparison.OrdinalIgnoreCase))
                w.WriteBoolean("folded", true);
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
    public static string RenderInfoOrder(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, int maxChars, OrderStamp? epoch,
                                         IReadOnlyList<KeyValuePair<string, string>> envelope,
                                         IReadOnlyList<KeyValuePair<string, int>> counts,
                                         SpillState? spill, out bool truncated)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
            w.WriteStartArray("rows");
            int rendered = 0; bool rowsTruncated = false;
            foreach (var row in rows)
            {
                if (manifestOnly) break;
                w.Flush();
                if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                WriteInfoOrderRow(w, row, ms, cap);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records: the cross-plugin scan ---------------------------------------------------
    /// <summary>A cross-plugin scan as JSON — three shapes matching the text render: a group_by count table, detail
    /// rows with fields, or summary rows, with the accounting in-band.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The spill-aware render: <paramref name="spill"/> rides IN the document, and
    /// <paramref name="truncated"/> is the auto-spill trigger handed back to the tool layer.</summary>
    /// <param name="rowLimit">the caller's limit= as the group_by TABLE's row cap (0 = uncapped), the text twin's
    /// own term (#810); <c>cut_by</c> names whichever knob stopped it.</param>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth,
                                          SpillState? spill, out bool truncated,
                                          IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null,
                                          CancellationToken ct = default, int rowLimit = 0)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // Post-capture refusals are epoch-stamped; pre-capture validation refusals carry null, as in text.
            if (q.Error is not null) { WriteRefusal(w, q.Error); if (q.Stamp is not null) WriteEpoch(w, q.Stamp); }
            else if (q.Groups is not null)                                   // group_by= → count table
            {
                WriteNullable(w, "group_by", q.GroupBy);
                w.WriteNumber("total", q.Total);
                WriteEpoch(w, q.Stamp);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q);
                // A zero group is one the scan ASKED for that matched nothing, listed ahead of the counted rows.
                var gEmpty = q.Groups.Where(g => g.Count == 0).Select(g => g.Key).ToList();
                var gCounted = gEmpty.Count == 0 ? q.Groups : q.Groups.Where(g => g.Count > 0).ToList();
                // total above is the MATCH count; this is how many groups they fell into.
                w.WriteNumber("groups_total", gCounted.Count);
                WriteEmptyGroups(w, gEmpty);
                w.WriteStartArray("groups");
                int gRendered = 0; bool gTrunc = false, gByBudget = false;
                int gShown = rowLimit > 0 ? Math.Min(rowLimit, gCounted.Count) : gCounted.Count;
                foreach (var g in gCounted)
                {
                    if (manifestOnly) break;   // to_file: the rows are the FILE
                    if (gRendered >= gShown) { gTrunc = true; break; }   // limit= caps the table's rows
                    w.Flush();
                    if (Chars(ms) >= cap) { gTrunc = true; gByBudget = true; break; }
                    w.WriteStartObject(); w.WriteString("key", g.Key); w.WriteNumber("count", g.Count); w.WriteEndObject();
                    gRendered++;
                }
                w.WriteEndArray();
                w.WriteNumber("rendered", gRendered);
                w.WriteBoolean("truncated", gTrunc);
                // WHICH knob stopped the table, the histogram's own member; null where the table is whole, and null
                // under to_file=, where no knob cut anything — the rows are the FILE.
                if (!manifestOnly && HistogramCut.For(gCounted.Count, gRendered, gByBudget) is { } gCut)
                    w.WriteString("cut_by", gCut.Knob);
                else w.WriteNull("cut_by");
                // Only the BUDGET cut triggers the caller's ceiling auto-spill; a limit cut is the caller's own ask.
                truncated = gByBudget;
            }
            else                                                            // per-match: detail (fields=) or summary
            {
                bool detail = fields is { Count: > 0 };
                bool anyScoped = AnyScopedFieldRow(q, fields);   // the shared test: any row read from a scoped body
                string? p5 = anyScoped ? ScopedFieldsNote(winnerFields, q.WhereWinner, levers) : null;
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                WriteEpoch(w, q.Stamp);                         // offset= windows tile ONLY within one epoch
                if (q.Offset > 0) w.WriteNumber("offset", q.Offset);        // the window's start, in-band
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, p5);
                // One session, one link cache and one chunked body prefetch for every rendered match.
                using var reader = detail
                    ? new ScanDetailReader(svc, q, fields, depth, resolveNames, winnerFields, (levers ?? LeverNames.Legacy).ContainerHint, null, ct)
                    : null;
                w.WriteStartArray("matches");
                int rendered = 0; bool rowsTruncated = false;
                var renderClock = System.Diagnostics.Stopwatch.StartNew();
                var childFields = new SortedDictionary<string, bool>(StringComparer.Ordinal);   // the clause per tier, over the fields the rows carried
                for (int i = 0; i < q.Keys.Count && !manifestOnly; i++)      // to_file: the rows are the FILE
                {
                    w.Flush();
                    if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                    var fk = q.Keys[i];
                    string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                    if (detail)
                    {
                        // winner_fields=: the WINNER's body whatever the scan scope; pinned to the scan's build.
                        var o = reader!.Row(i);   // a collapsed cell names the caller's own expansion knob
                        if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", FormIdToken.Of(fk)); w.WriteString("error", o.Error); if (matches is not null) w.WriteString("matches", matches); w.WriteEndObject(); }
                        else WriteReadRecord(w, o, ms, cap, matches, childFields: childFields, levers: levers);
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                        WriteSummaryRow(w, m, matches);
                    }
                    rendered++;
                }
                renderClock.Stop();
                w.WriteEndArray();
                w.WriteNumber("rendered", rendered);
                // The other half of a call's cost, stated in-band beside the rows it bought (#582).
                if (detail && manifestOnly && spill?.Spill is { RenderMs: { } artifactMs } a)
                { w.WriteNumber("rendered_to_file", a.Manifest.RowCount); w.WriteNumber("render_ms", artifactMs); }
                else if (detail && !manifestOnly) w.WriteNumber("render_ms", renderClock.ElapsedMilliseconds);
                w.WriteBoolean("truncated", rowsTruncated);
                WriteOwnedChildNote(w, childFields);
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            // A REFUSAL is not bounded by max_chars — it ships whole, and "raise max_chars" would not change it — so
            // the member is guarded like the spill beside it. The other renderers' refusal arms return before this.
            if (q.Error is null) WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>Is the scoped-vs-winner field-source note owed at all? True when a <c>fields=</c> detail render carried
    /// a row read from a scoped plugin's OWN body; every render that states the note asks HERE.</summary>
    internal static bool AnyScopedFieldRow(CrossQueryOutcome q, IReadOnlyList<string>? fields)
        => q.Groups is null && fields is { Count: > 0 }
           && q.Sources is { } sources && sources.Take(q.Keys.Count).Any(s => s is not null);

    /// <summary>The scoped-vs-winner field-source note, one of a 4-way matrix over (winner_fields=, where_source=),
    /// shared by the text, json and dense renders.</summary>
    internal static string ScopedFieldsNote(bool winnerFields, bool whereWinner, LeverNames? levers = null)
    {
        // Two arms are REMEDIES naming a lever and two are labels; where_source= is one spelling, so it is a literal.
        var wf = (levers ?? LeverNames.Legacy).WinnerFields;
        if (whereWinner)
            return winnerFields
                ? $"the MATCH and the field values are both the load-order WINNER's (where_source=winner, {wf})."
                : $"the MATCH was selected on the load-order WINNER (where_source=winner), but the field values shown are each match's SCOPED plugin's OWN version — pass {wf} to display the winner too.";
        return winnerFields
            ? $"field values are the load-order WINNER's ({wf}); each match was SELECTED on its scoped plugin's body."
            : $"field values are each match's SCOPED plugin's OWN version, NOT the live load-order winner — pass {wf} for load-order truth.";
    }

    // ---- housecarl_records: the cross-plugin scan, format=dense ------------------------------------
    /// <summary>The columnar render: a <c>columns</c> array once, then ONE positional row array per match. A row whose
    /// read FAILS lands in a separate <c>errors</c> array; group_by= never reaches here.</summary>
    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields)
        => RenderCrossQueryDense(svc, q, fields, maxChars, resolveNames, winnerFields, null, out _);

    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields,
                                               SpillState? spill, out bool truncated,
                                               IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null,
                                               FoldPlan? fold = null, CancellationToken ct = default)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // Post-capture refusals are epoch-stamped; pre-capture validation refusals stay bare.
            if (q.Error is not null) { WriteRefusal(w, q.Error); if (q.Stamp is not null) WriteEpoch(w, q.Stamp); }
            else
            {
                bool detail = fields is { Count: > 0 };
                bool anyScoped = AnyScopedFieldRow(q, fields);   // the shared test: any row read from a scoped body
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                WriteEpoch(w, q.Stamp);                           // offset= windows tile ONLY within one epoch
                if (q.Offset > 0) w.WriteNumber("offset", q.Offset);
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, anyScoped ? ScopedFieldsNote(winnerFields, q.WhereWinner, levers) : null);

                bool hasMatches = q.MatchedTargets is not null;               // multi-target references= → one extra column
                w.WriteStartArray("columns");
                if (detail)
                {
                    w.WriteStringValue("formid"); w.WriteStringValue("runtime_formid"); w.WriteStringValue("editorid");
                    foreach (var f in fold?.Requested ?? fields!) w.WriteStringValue(f);   // cells align positionally: one column per REQUESTED path, in order — a quantified path keeps its own spelling
                    // Under a plugins= scope a row's values are SOME scoped plugin's own body, so provenance is per row.
                    if (anyScoped) w.WriteStringValue("source");
                }
                else
                    foreach (var c in new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth" }) w.WriteStringValue(c);
                if (hasMatches) w.WriteStringValue("matches");
                w.WriteEndArray();

                var foldDepths = fold?.Read().Depths;   // the quantified paths' depth, and the caller's own for the rest
                // One session, one link cache and one chunked body prefetch for every rendered match.
                using var reader = detail
                    ? new ScanDetailReader(svc, q, fields, fold?.Depth ?? 1, resolveNames, winnerFields,
                                           (levers ?? LeverNames.Legacy).DenseContainerHint, foldDepths, ct)
                    : null;
                List<(string Formid, string Error)>? errors = null;
                int rendered = 0; bool rowsTruncated = false;
                var renderClock = System.Diagnostics.Stopwatch.StartNew();
                var childFields = new SortedDictionary<string, bool>(StringComparer.Ordinal);   // the clause per tier, over the cells the rows carried
                var foldNotes = new SortedSet<string>(StringComparer.Ordinal);   // what the read said that no column carries — the truncation note above all
                int foldRows = 0;            // rows, which a fold makes ELEMENTS; `rendered` stays records
                w.WriteStartArray("rows");
                for (int i = 0; i < q.Keys.Count && !manifestOnly; i++)      // to_file: the rows are the FILE
                {
                    w.Flush();
                    if (Chars(ms) >= cap) { rowsTruncated = true; break; }
                    var fk = q.Keys[i];
                    string? matches = q.MatchedTargets is { } mt && i < mt.Count ? mt[i] : null;
                    if (detail)
                    {
                        var o = reader!.Row(i);   // dense refuses depth>1 unless a quantifier asks for it; pinned to the scan's build
                        if (o.Error is not null) { (errors ??= new()).Add((FormIdToken.Of(fk), o.Error)); rendered++; continue; }
                        var r = o.Record!;
                        if (fold is not null)
                        {
                            // A quantified path makes the requested paths PER ELEMENT: one row each, identity repeated.
                            var (cols, carried, ferr) = fold.Columns(r);
                            if (ferr is not null) { (errors ??= new()).Add((FormIdToken.Of(fk), ferr)); rendered++; continue; }
                            // A row is keyed by its ELEMENT KEY as TEXT, never by its place in the column; key order
                            // for a positional list, read order otherwise.
                            var byKey = new Dictionary<string, HousecarlCore.FieldValue>?[cols!.Length];
                            var keys = new List<string>();
                            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                            for (int c = 0; c < cols.Length; c++)
                            {
                                if (fold.Folds[c] is not { Fold: HousecarlCore.PathFold.Set } fc) continue;
                                var map = byKey[c] = new Dictionary<string, HousecarlCore.FieldValue>(StringComparer.Ordinal);
                                for (int k = 0; k < cols[c].Count; k++)
                                {
                                    var key = FoldPlan.ElementKey(cols[c][k].Path, fc.Root) ?? k.ToString();
                                    map[key] = cols[c][k];
                                    if (seenKeys.Add(key)) keys.Add(key);
                                }
                            }
                            if (keys.Count > 1 && keys.All(k => int.TryParse(k, out _)))
                                keys.Sort((x, y) => int.Parse(x).CompareTo(int.Parse(y)));   // a positional list reads in index order whatever order the columns name
                            int elems = Math.Max(1, keys.Count);   // no set column, or an empty list: still one row
                            // The owned-child clause is earned per CELL, keyed on the column's own list path.
                            for (int c = 0; c < cols.Length; c++)
                            {
                                var owner = fold.Folds[c]?.Root ?? (cols[c].Count > 0 ? cols[c][0].Path : null);
                                if (owner is not null && o.OwnedChildFields?.TryGetValue(owner, out var tier) == true) childFields[owner] = tier is not null;
                            }
                            foreach (var note in carried) if (note.Note is { } n) foldNotes.Add(n);
                            bool cut = false;
                            for (int e = 0; e < elems; e++)
                            {
                                // The cap is per ROW, not per record: one record's element rows are unbounded.
                                w.Flush();
                                if (Chars(ms) >= cap) { rowsTruncated = true; cut = true; break; }
                                w.WriteStartArray();
                                w.WriteStringValue(r.FormKey);
                                WriteCell(w, RuntimeCell(o.RuntimeFormId, o.RuntimeFormIdNote));
                                WriteCell(w, r.EditorId);
                                for (int c = 0; c < cols.Length; c++)
                                {
                                    var col = cols[c];
                                    var cell = byKey[c] is { } map ? (e < keys.Count && map.TryGetValue(keys[e], out var v) ? v : null)
                                                                   : (col.Count > 0 ? col[0] : null);
                                    WriteCell(w, cell is null ? null : DenseCell(cell));
                                }
                                if (anyScoped) WriteCell(w, o.SourcePlugin);
                                if (hasMatches) WriteCell(w, matches);
                                w.WriteEndArray();
                                foldRows++;
                            }
                            if (cut) break;                                   // a half-written record is not a rendered one
                            rendered++;
                            continue;
                        }
                        w.WriteStartArray();
                        w.WriteStringValue(r.FormKey);
                        WriteCell(w, RuntimeCell(o.RuntimeFormId, o.RuntimeFormIdNote));
                        WriteCell(w, r.EditorId);
                        foreach (var f in r.Fields)
                        {
                            WriteCell(w, DenseCell(f));
                            // Registered at EMISSION, so the clause is earned by what the document carries.
                            if (o.OwnedChildFields?.TryGetValue(f.Path, out var cellTier) == true) childFields[f.Path] = cellTier is not null;
                        }
                        if (anyScoped) WriteCell(w, o.SourcePlugin);          // the body this row's values were read from (winner_fields=true → the winner)
                        if (hasMatches) WriteCell(w, matches);
                        w.WriteEndArray();
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                        if (m.Error is not null) { (errors ??= new()).Add((FormIdToken.Of(m.FormKey), m.Error)); rendered++; continue; }
                        w.WriteStartArray();
                        w.WriteStringValue(FormIdToken.Of(m.FormKey));
                        WriteCell(w, RuntimeCell(m.RuntimeFormId, m.RuntimeFormIdNote));
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
                renderClock.Stop();
                w.WriteNumber("rendered", rendered);
                // A fold makes a row an ELEMENT, so `rendered` (records) no longer counts the rows: say both.
                if (fold is not null) w.WriteNumber("rows_rendered", foldRows);
                // The other half of a call's cost, stated in-band beside the rows it bought (#582).
                if (detail && manifestOnly && spill?.Spill is { RenderMs: { } artifactMs } a)
                { w.WriteNumber("rendered_to_file", a.Manifest.RowCount); w.WriteNumber("render_ms", artifactMs); }
                else if (detail && !manifestOnly) w.WriteNumber("render_ms", renderClock.ElapsedMilliseconds);
                w.WriteBoolean("truncated", rowsTruncated);
                WriteOwnedChildNote(w, childFields);
                // The read's own note — a truncated expansion above all — belongs to this document.
                if (foldNotes.Count > 0) w.WriteString("read_note", string.Join(" ", foldNotes));
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            // A REFUSAL is not bounded by max_chars — it ships whole, and "raise max_chars" would not change it — so
            // the member is guarded like the spill beside it. The other renderers' refusal arms return before this.
            if (q.Error is null) WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One dense cell: the round-trip token, else the leaf's parenthetical note, with annotations appended.</summary>
    static string? DenseCell(HousecarlCore.FieldValue f)
    {
        var s = f.HasValue ? f.Token : f.Note;
        if (f.Display is not null) s = $"{s}   ({f.Display})";
        if (f.Link is not null) s = $"{s}   ({Wire.LinkText(f.Link)})";
        return s;
    }

    /// <summary>The runtime-FormID cell in a dense row: the eight-hex form, else the reason in parentheses, else null.</summary>
    static string? RuntimeCell(string? runtime, string? note) => runtime ?? (note is null ? null : $"({note})");

    static void WriteCell(Utf8JsonWriter w, string? v)
    {
        if (v is null) w.WriteNullValue(); else w.WriteStringValue(v);
    }

    internal static void WriteSummaryRow(Utf8JsonWriter w, RecordSummary m, string? matches)
    {
        w.WriteStartObject();
        w.WriteString("formid", FormIdToken.Of(m.FormKey));
        WriteRuntime(w, m.RuntimeFormId, m.RuntimeFormIdNote);
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

    /// <summary>Accounting notes carried IN the JSON document, so json is never a degraded mode; omitted when none.</summary>
    static void WriteNotes(Utf8JsonWriter w, CrossQueryOutcome q, string? extra = null)
    {
        if (q.PredicateNote is null && q.ScanNote is null && q.WhereSourceNote is null && q.ReverseIndexNote is null && extra is null) return;
        w.WriteStartArray("notes");
        if (q.PredicateNote is not null) w.WriteStringValue(q.PredicateNote);
        if (q.ScanNote is not null) w.WriteStringValue(q.ScanNote);
        if (q.WhereSourceNote is not null) w.WriteStringValue(q.WhereSourceNote);   // where_source=winner redundancy under a type=-only scope
        if (q.ReverseIndexNote is not null) w.WriteStringValue(q.ReverseIndexNote);   // the reverse-reference index's build cost + per-plugin freshness key
        if (extra is not null) w.WriteStringValue(extra);   // the scoped-vs-winner fields note
        w.WriteEndArray();
    }

    // ---- housecarl_check_errors ---------------------------------------------------------------------
    /// <summary>The errors family's own head members — everything above the first thing a budget can refuse.</summary>
    static void WriteErrorsHead(Utf8JsonWriter w, ErrorCheckResult r)
    {
        bool didDangling = r.Classes.HasFlag(ErrorFindingClass.Dangling);
        bool didMasters = r.Classes.HasFlag(ErrorFindingClass.MissingMasters);
        w.WriteNumber("scanned_plugins", r.PluginsScanned);
        WriteSweepEpoch(w, r.Epoch, r.ExcludedPlugins.Count, r.OffOrderScanned);   // the swept INDEXED build + whether it covers every swept input
        // null (not 0) for a class nobody looked for — see the summary.
        if (didDangling) { w.WriteNumber("dangling", r.TotalDangling); w.WriteNumber("unscannable_records", r.TotalUnscannableRecords); }
        else { w.WriteNull("dangling"); w.WriteNull("unscannable_records"); }
        if (didMasters) w.WriteNumber("missing_masters", r.TotalMissingMasters); else w.WriteNull("missing_masters");
        WriteStringArray(w, "classes_checked", ClassNames(r.Classes));
        WriteNullable(w, "filter_note", r.FilterNote);
        WriteOffOrder(w, r.OffOrderScanned, ReadSentences.SweepOffOrderErrorsCoverage);
        w.WriteBoolean("counts_only", r.CountsOnly);

        // The baseline split as DATA: base_masters is the set counted (Mutagen's, which excludes Creation Club).
        if (didDangling)
        {
            w.WriteNumber("baseline_dangling", r.BaselineDangling);
            w.WriteNumber("non_baseline_dangling", r.TotalDangling - r.BaselineDangling);
        }
        else { w.WriteNull("baseline_dangling"); w.WriteNull("non_baseline_dangling"); }
        // base_masters_swept is what THIS sweep opened; base_masters is what counts as baseline at all.
        WriteStringArray(w, "base_masters_swept", r.BaseMastersSwept ?? Array.Empty<string>());
        WriteStringArray(w, "base_masters", HousecarlCore.ErrorCheck.BaseMasters);
    }

    /// <summary>The errors family's BODY — everything a cap can refuse; the roster, accounting and boundary are the RESPONSE's.</summary>
    static void WriteErrorsSection(Utf8JsonWriter w, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        // The depths every unit in this section is measured at, read off the writer rather than passed in.
        var depths = new JsonUnitDepths(w.CurrentDepth);
        if (r.CountsOnly)
        {
            // Both axes handed over together, so both frames are reserved before either writes.
            WriteHistograms(w, body, histogramLimit, depths,
                ("dangling_by_target_plugin", SweepSubject.HistogramByTarget, r.Histogram),
                ("dangling_by_source_plugin", SweepSubject.HistogramBySource, r.DanglingBySource));
            WriteUnreadPlugins(w, r.Reports, body, depths);
        }
        else
        {
            w.WriteStartArray("plugins");
            int sections = 0;
            foreach (var p in r.Reports)
            {
                // A section is whole or absent and its cost is MEASURED: its fixed part carries unbounded strings,
                // and a Utf8JsonWriter can only be measured by writing.
                var head = p;
                bool opened = body.Emit(SweepSubject.PluginSections,
                                        PluginHeadCost(p, depths.PluginSections, sections > 0),
                                        () => WritePluginHead(w, head));
                if (!opened) break;
                sections++;
                int entries = 0;
                foreach (var d in p.Dangling)
                {
                    // Per ENTRY: one plugin's array can be thousands of rows, so its cost is measured like the rest.
                    var entry = d;
                    if (!body.Emit(SweepSubject.DanglingEntries,
                                   DanglingEntryCost(d, depths.DanglingEntries, entries > 0),
                                   () => WriteDanglingEntry(w, entry), p.Plugin)) break;
                    entries++;
                }
                // The section's closing brackets FINISH an admitted unit, so PluginHeadCost measures them with it.
                body.Complete(SweepSubject.PluginSections, () => { w.WriteEndArray(); w.WriteEndObject(); });
            }
            w.WriteEndArray();
        }
    }

    /// <summary>One plugin object's FIXED head, opening the <c>dangling</c> array; the cost helper writes this same method.</summary>
    static void WritePluginHead(Utf8JsonWriter w, PluginErrors p)
    {
        w.WriteStartObject();
        w.WriteString("plugin", p.Plugin);
        WriteNullable(w, "scan_error", p.ScanError);
        WriteStringArray(w, "missing_masters", p.MissingMasters);
        // The install-vs-enable split as DATA, a SUBSET of the array above; null where the split was not made.
        WriteNullableStringArray(w, "installed_but_inactive_masters", p.InstalledButInactiveMasters);
        // The unscannable fields sit before the dangling array, so a mid-plugin break leaves only closing brackets.
        w.WriteNumber("unscannable_records", p.UnscannableRecords);
        WriteStringArray(w, "unscannable_samples", p.UnscannableSamples);
        w.WriteStartArray("dangling");
    }

    /// <summary>ONE dangling entry. Shared by the write and its measurement so the two cannot differ.</summary>
    static void WriteDanglingEntry(Utf8JsonWriter w, DanglingRef d)
    {
        w.WriteStartObject();
        w.WriteString("source", FormIdToken.Of(d.Source));
        w.WriteString("source_type", d.SourceType);
        WriteNullable(w, "source_editorid", d.SourceEditorId);
        w.WriteString("target", FormIdToken.Of(d.Target));
        w.WriteEndObject();
    }

    /// <summary>What ONE plugin object costs — its head AND the brackets that close it, one unit and one subject.</summary>
    static int PluginHeadCost(PluginErrors p, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, (w, size) =>
        {
            int before = size();
            WritePluginHead(w, p);
            int head = size() - before;
            // A non-empty array closes on a line of its own; one throwaway entry buys the right close.
            if (p.Dangling.Count > 0) WriteDanglingEntry(w, p.Dangling[0]);
            before = size();
            w.WriteEndArray();
            w.WriteEndObject();
            return head + (size() - before);
        });

    /// <summary>What ONE dangling entry costs, at its own depth and sibling position.</summary>
    static int DanglingEntryCost(DanglingRef d, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteDanglingEntry(w, d));

    /// <summary>What THIS writer's own framing costs, read off the writer rather than kept by hand: the root open, the
    /// root close, and the separator a property pays for not being the first one inside it.</summary>
    static readonly WriterFraming Framing = MeasureFraming();

    /// <summary>The three costs <see cref="MeasureFraming"/> reads off the writer, in characters.</summary>
    readonly record struct WriterFraming(int Open, int RootClose, int Separator);

    /// <summary>Two BYTE-IDENTICAL properties into a root object, and the deltas: one property, that property plus the separator, the root close.</summary>
    static WriterFraming MeasureFraming()
    {
        using var ms = new CharCountedStream();
        int opened, first, second;
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            opened = Size(w, ms);
            w.WriteString("f", "");
            first = Size(w, ms);
            w.WriteString("f", "");
            second = Size(w, ms);
            w.WriteEndObject();
        }
        return new WriterFraming(Open: opened, RootClose: Chars(ms) - second,
                                 Separator: (second - first) - (first - opened));
    }

    /// <summary>What the overrun notice costs, encoded as the response will encode it: the scratch document less its own wrapper, plus the separator it owes.</summary>
    static int OverrunNoticeCost(string notice)
    {
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("max_chars_overrun", notice);
            w.WriteEndObject();
        }
        return Chars(ms) - (Framing.Open + Framing.RootClose) + Framing.Separator;
    }

    /// <summary>The closing fact a capped json document owes when it shipped over its ceiling: <c>max_chars_overrun</c>,
    /// the same sentence the text lane's <see cref="RenderCap.Settle"/> appends, naming the document's length, the cap
    /// it was given and the cap that clears it. Called just before the root close, and settled to a fixed point because
    /// the member is part of the length it states. Contract in docs/architecture/json-wire.md.</summary>
    internal static void WriteCapOverrun(Utf8JsonWriter w, CharCountedStream ms, int cap)
    {
        int closed = Size(w, ms) + Framing.RootClose;
        if (closed <= cap) return;
        // Settled to a fixed point, not to a round count: the notice grows by a fixed width plus whatever digits
        // the length gains, so each round can only add digits and the loop terminates — and it must not write a
        // notice it has not verified, which is the one place a stated number could be silently off.
        var notice = RenderCap.Overran(closed, cap);
        while (true)
        {
            var next = RenderCap.Overran(closed + OverrunNoticeCost(notice), cap);
            if (next.Length == notice.Length) { notice = next; break; }
            notice = next;
        }
        w.WriteString("max_chars_overrun", notice);
    }

    /// <summary>The document's size so far, in CHARACTERS. It FLUSHES first: no count can be taken of bytes the writer still holds.</summary>
    static int Size(Utf8JsonWriter w, CharCountedStream ms)
    {
        w.Flush();
        return Chars(ms);
    }

    /// <summary>What a json buffer holds, in CHARACTERS — the unit <c>max_chars</c> is stated in, where the stream's
    /// own <c>Length</c> is UTF-8 BYTES (#754). Every cap test, reserve and length comes through here.</summary>
    internal static int Chars(CharCountedStream ms) => ms.Chars;

    /// <summary>What <c>child_declarers_note</c> costs, measured as <see cref="Framing"/> measures punctuation.</summary>
    static readonly int DeclarersLeadReserve =
        MeasureRootProperty(w => w.WriteString("child_declarers_note", ReadSentences.DeclarersLead));

    /// <summary>What the <c>truncated</c> boolean costs, measured against <c>false</c>, the wider spelling.</summary>
    static readonly int TruncatedPropertyReserve = MeasureRootProperty(w => w.WriteBoolean("truncated", false));

    /// <summary>One property's cost in the root object, from the writer itself, written after a property so it pays the same separator.</summary>
    static int MeasureRootProperty(Action<Utf8JsonWriter> write)
    {
        using var ms = new CharCountedStream();
        int before, after;
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("x", "");
            before = Size(w, ms);
            write(w);
            after = Size(w, ms);
            w.WriteEndObject();
        }
        return after - before;
    }

    /// <summary>The document written SO FAR, as text, without closing it — for the overrun remedy, which reads it.</summary>
    static string SoFar(Utf8JsonWriter w, MemoryStream ms)
    {
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Where each json unit sits, from one anchor: the depth of the object a family writes its members into.
    /// Depth is load-bearing because the response is written INDENTED; read by both the demand pass and the write.</summary>
    /// <param name="Section">the depth of the object holding a family's members.</param>
    internal readonly record struct JsonUnitDepths(int Section)
    {
        /// <summary>Elements of <c>plugins</c>.</summary>
        internal int PluginSections => Section + 1;
        /// <summary>Elements of a plugin object's <c>dangling</c>.</summary>
        internal int DanglingEntries => Section + 3;
        /// <summary>Elements of <c>records</c>.</summary>
        internal int ScriptRecords => Section + 1;
        /// <summary>Elements of a histogram axis's <c>rows</c>, and of the <c>unread</c> and <c>scan_errors</c> layers'.</summary>
        internal int HistogramRows => Section + 2;
        /// <summary>A histogram axis OBJECT, written as a member of the family object itself.</summary>
        internal int AxisFrame => Section;
        /// <summary>Elements of <c>seeds</c> and of <c>seeds_unreachable</c>.</summary>
        internal int DialogueSeeds => Section + 1;
        /// <summary>Elements of a seed's <c>topics</c>.</summary>
        internal int DialogueTopics => Section + 3;
        /// <summary>Elements of the facegen family's <c>findings</c>.</summary>
        internal int FaceGenRows => Section + 1;
        /// <summary>Elements of a <c>housecarl_skse</c> family document's row lists.</summary>
        internal int SkseRows => Section + 1;
        /// <summary>Elements of a config file row's <c>references</c>.</summary>
        internal int SkseConfigRefs => Section + 3;
    }

    /// <summary>What ONE UNIT costs the finished document, written into a throwaway document under the same
    /// <see cref="Opts"/>, depth and sibling position, returning the DELTA it appended; why measuring means writing
    /// is in docs/architecture/json-wire.md.</summary>
    /// <param name="subsequent">is something already in that array? A later element pays a separator.</param>
    /// <param name="measure">writes the unit and returns its cost, so a unit written in TWO spans measures both.</param>
    internal static int MeasureUnit(int depth, bool subsequent, Func<Utf8JsonWriter, Func<int>, int> measure)
    {
        using var ms = new CharCountedStream();
        using var w = new Utf8JsonWriter(ms, Opts);
        w.WriteStartObject();
        for (int i = 2; i < depth; i++) w.WriteStartObject("n");
        w.WriteStartArray("rows");
        if (subsequent) w.WriteNullValue();
        return measure(w, () => Size(w, ms));
    }

    /// <summary>The common case: a unit written in one span.</summary>
    internal static int MeasureUnit(int depth, bool subsequent, Action<Utf8JsonWriter> write)
        => MeasureUnit(depth, subsequent, (w, size) => { int before = size(); write(w); return size() - before; });

    /// <summary>The same measurement for a NAMED MEMBER rather than an array element — a histogram axis's own object.</summary>
    static int MeasureMember(int depth, Action<Utf8JsonWriter> write)
    {
        using var ms = new CharCountedStream();
        using var w = new Utf8JsonWriter(ms, Opts);
        w.WriteStartObject();
        for (int i = 1; i < depth; i++) w.WriteStartObject("n");
        w.WriteString("before", "");   // the member is never the first thing in a family object, so it pays a separator
        int before = Size(w, ms);
        write(w);
        return Size(w, ms) - before;
    }

    /// <summary>A family's stamp and coverage as data: <c>epoch_covers_all_inputs</c> is false when off-order files
    /// were swept beside the index, or when <paramref name="uncovered"/> names another substrate. Success path only.</summary>
    /// <param name="excludedCount">how many plugins the build this family read had lost to a load failure.</param>
    internal static void WriteSweepEpoch(Utf8JsonWriter w, string? epoch, int excludedCount,
                                         IReadOnlyList<string>? offOrderScanned,
                                         IReadOnlyList<string>? uncovered = null)
    {
        if (epoch is null) return;
        WriteNullable(w, "epoch", epoch);
        // The flag and the count, not the sentence, which the merged document's ROOT states once. Pinned by
        // DegradedOrderMarkerTests.TheCheckDocumentCarriesTheMarkerAtItsRootAndOnTheErrorsFamily.
        if (excludedCount > 0)
        {
            w.WriteBoolean("order_degraded", true);
            w.WriteNumber("order_degraded_plugins", excludedCount);
        }
        w.WriteBoolean("epoch_covers_all_inputs", offOrderScanned is not { Count: > 0 } && uncovered is not { Count: > 0 });
        // Named only where there are classes to name, so the key is never a caveat over a stamp that covers everything.
        if (uncovered is { Count: > 0 }) WriteStringArray(w, "epoch_uncovered", uncovered);
    }

    /// <summary>A swept family's off-order roster AND the coverage caveat that makes it readable; null when none.</summary>
    static void WriteOffOrder(Utf8JsonWriter w, IReadOnlyList<string>? offOrderScanned, string coverage)
    {
        WriteStringArray(w, "off_order_scanned", offOrderScanned ?? Array.Empty<string>());
        WriteNullable(w, "off_order_coverage", offOrderScanned is { Count: > 0 } ? coverage : null);
    }

    // ---- housecarl_check — the merged, multi-family document ----------------------------------------
    /// <summary>The merged sweep as json: the scope facts flat at the top, then a <c>families</c> object keyed by family
    /// token. The excluded roster and the overrun notice are RESPONSE-level and written once.</summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the allocation it built, for the tests.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // WHAT THIS RESPONSE ACTUALLY DID, composed ONCE and handed to everything below — see the text lane.
        var o = CheckOutcome.For(s);
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // One accounting + boundary reserve per family, and ONE entry slack for the response.
        int reserve = CheckAccounting.JsonEntrySlack;
        foreach (var a in accts) reserve += a.JsonAccountingReserve;
        int budget = Math.Max(0, cap - reserve);

        if (o.Error is not null)
        {
            using var ems = new CharCountedStream();
            using (var ew = new Utf8JsonWriter(ems, Opts))
            {
                ew.WriteStartObject();
                WriteRefusal(ew, o.Error);
                // The same frame the text lane leads with: a refusal read out of a folded call is about the projection.
                if (s.Dialogue?.Folded is { } errFrame) ew.WriteString("folded", errFrame.Trim());
                WriteEpoch(ew, o.Epoch, o.OrderExcluded);
                ew.WriteEndObject();
                ew.Flush();
            }
            return Finish(ems);
        }

        // A family's members sit under `families` in the root, so a unit's depth is anchored two levels below it.
        var depths = new JsonUnitDepths(FamilySectionDepth);
        // WHAT EACH SUBJECT WANTS, measured before anything is written (SweepDemand, BodyAllocation).
        var demand = SweepDemand.ForJson(o, budget, histogramLimit, depths);
        // AND WHAT THE DOCUMENT OWES WHATEVER THE BUDGET SAYS: composed with no units in it and measured.
        int fixedPart;
        {
            using var sms = new CharCountedStream();
            var skeletonAccts = o.Accountings(cap);
            BoundedBody skeletonBody;
            using (var sw = new Utf8JsonWriter(sms, Opts))
            {
                skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => Size(sw, sms));
                sw.WriteStartObject();
                Compose(sw, o, sections, skeletonAccts, skeletonBody, histogramLimit, cap);
                sw.WriteEndObject();
            }
            fixedPart = Chars(sms) - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;
        }

        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            var body = BoundedBody.ForFamilies(accts, budget, () => Size(w, ms), o.Plan(),
                                               demand.Demand, demand.Reserved + fixedPart, o.ResponseSubjects,
                                               demand.Reserved);
            measured = body;
            Compose(w, o, sections, accts, body, histogramLimit, cap);

            int closed = Size(w, ms) + Framing.RootClose;
            int needed = body.FixedPart(closed);
            var overrun = accts.Count > 0 ? accts[0] : null;
            // How many times this document prints the cap back, COUNTED in it, and read before the notice is written.
            int sites = overrun is null ? 0 : overrun.CapPrintsIn(SoFar(w, ms));
            if (overrun?.CapTooSmall(closed, needed, 0, sites) is { } notice)
            {
                int cost = OverrunNoticeCost(notice);
                var settled = overrun.CapTooSmall(closed + cost, needed, cost, sites)!;
                if (OverrunNoticeCost(settled) != cost)
                    settled = overrun.CapTooSmall(closed + OverrunNoticeCost(settled), needed, OverrunNoticeCost(settled), sites)!;
                w.WriteString("max_chars_overrun", settled);
            }
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The depth a family writes its members at, named once because the demand pass has no writer to read it off.</summary>
    internal const int FamilySectionDepth = 3;

    /// <summary>The whole merged document bar the root braces and the overrun notice. Run twice: once with a
    /// <see cref="BoundedBody.Skeleton"/>, which leaves the fixed part to be measured, and once for real.</summary>
    static void Compose(Utf8JsonWriter w, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit, int cap)
    {
        var s = o.Sweep;
        // The scope facts, as data and as the sentence. THREE LISTS, because a family can be in three states:
        // what ANSWERED (off the outcome, not the selection), what refused with its ground, what was never asked.
        WriteStringArray(w, "families_ran", o.Ran.Select(SweepFamilySelection.Token).ToArray());
        w.WriteBoolean("findings_defaulted", o.Defaulted);
        w.WriteStartArray("families_refused");
        foreach (var f in o.Refused)
        {
            w.WriteStartObject();
            w.WriteString("family", SweepFamilySelection.Token(f));
            w.WriteString("refused", o.Refusal(f)!);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("families_not_selected");
        foreach (var f in o.NotSelected)
        {
            w.WriteStartObject();
            w.WriteString("family", SweepFamilySelection.Token(f));
            w.WriteString("describes", SweepFamilySelection.Describe(f));
            w.WriteString("findings", SweepFamilySelection.Spelling(f));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("findings_scope", o.ScopeSentence());
        // A response-level fact, like the roster below: this call's order was short of plugins (#353).
        WriteOrderDegraded(w, o.OrderExcluded);
        // WHICH loose roots the asset build could not read — at the document root, because ONE build feeds every
        // family that hedges on it, and cut by the same rule the text lane's lines are.
        WriteRootFailuresCut(w, o.RootFailures, cap);

        // Above `families` because an accounting reports what has been emitted, and every family's is in the loop.
        WriteExcluded(w, o.ExcludedPlugins, body);

        w.WriteStartObject("families");
        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            w.WriteStartObject(SweepFamilySelection.Token(f));
            // A family that refused says so HERE, rather than the refusal becoming the whole call's error.
            if (o.Refusal(f) is { } refusal)
            {
                // The frame rides a refused dialogue family too: the seeds were looked for in the projection.
                if (f == SweepFamily.Dialogue && s.Dialogue?.Folded is { } foldedFrame)
                    w.WriteString("folded", foldedFrame.Trim());
                w.WriteString("refused", refusal);
            }
            else if (f == SweepFamily.Errors)
            {
                WriteErrorsHead(w, s.Errors!);
                WriteErrorsSection(w, s.Errors!, body, histogramLimit);
            }
            else if (f == SweepFamily.Scripts)
            {
                WriteScriptsHead(w, s.Scripts!);
                WriteScriptsSection(w, s.Scripts!, body, histogramLimit);
            }
            else if (f == SweepFamily.Facegen)
            {
                FaceGenSweepRender.WriteHead(w, s.FaceGen!);
                FaceGenSweepRender.WriteSection(w, s.FaceGen!, body, histogramLimit);
            }
            else
            {
                DialogueSweepRender.WriteHead(w, o);
                DialogueSweepRender.WriteSection(w, o, body);
            }
            // This family's accounting and boundary, out of the room held for them.
            var acct = accts[i];
            body.Reserved(() => { acct.WriteJson(w); w.WriteString("boundary", acct.Boundary); });
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    // ---- housecarl_validate_scripts -----------------------------------------------------------------
    /// <summary>The scripts family's own head members. A finding CLASS the caller excluded is <c>null</c>, NOT 0 —
    /// the json counterpart of the text render's NOT CHECKED. <c>unverifiable</c> is never null.</summary>
    static void WriteScriptsHead(Utf8JsonWriter w, ScriptCheckResult r)
    {
        bool didObject = r.Classes.HasFlag(ScriptFindingClass.UnboundObject);
        bool didScalar = r.Classes.HasFlag(ScriptFindingClass.UnboundScalar);
        bool didNull = r.Classes.HasFlag(ScriptFindingClass.BoundNull);

        w.WriteNumber("scanned_plugins", r.PluginsScanned);
        WriteSweepEpoch(w, r.Epoch, r.ExcludedPlugins.Count, r.OffOrderScanned);   // the swept INDEXED build + whether it covers every swept input
        w.WriteNumber("records_with_scripts", r.RecordsWithScripts);
        if (didObject || didScalar) w.WriteNumber("unbound", r.TotalUnbound); else w.WriteNull("unbound");
        if (didObject) w.WriteNumber("unbound_object", r.TotalUnboundObject); else w.WriteNull("unbound_object");
        if (didScalar) w.WriteNumber("unbound_scalar", r.TotalUnboundScalar); else w.WriteNull("unbound_scalar");
        if (didNull) w.WriteNumber("bound_but_null", r.TotalNullObject); else w.WriteNull("bound_but_null");
        w.WriteNumber("unverifiable", r.TotalUnverifiable);   // never filterable — always a real count
        WriteStringArray(w, "classes_checked", ScriptClassNames(r.Classes));
        // The property filter rides as DATA: records_with_scripts and unverifiable are plugin-wide regardless of it.
        WriteNullable(w, "property_contains", r.PropertyContains);
        WriteNullable(w, "filter_note", r.FilterNote);
        WriteOffOrder(w, r.OffOrderScanned, ReadSentences.SweepOffOrderScriptsCoverage);
        w.WriteNumber("unverifiable_collapsed", r.UnverifiableCollapsed);
        w.WriteBoolean("read_incomplete", r.ReadIncomplete);
        w.WriteBoolean("counts_only", r.CountsOnly);
    }

    /// <summary>The scripts family's BODY — everything a cap can refuse; the roster, accounting, boundary,
    /// <c>capped</c>, <c>rendered</c> and <c>truncated</c> are the RESPONSE's.</summary>
    static void WriteScriptsSection(Utf8JsonWriter w, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        var depths = new JsonUnitDepths(w.CurrentDepth);
        if (r.CountsOnly)
        {
            WriteHistograms(w, body, histogramLimit, depths,
                ("unbound_by_property", SweepSubject.HistogramByProperty, r.Histogram));
            // The honesty layer, on its own subject and bound, wrapped so a cut list can say it was cut.
            var scanErrors = r.Reports.Where(x => x.ScanError is not null).ToList();
            w.WriteStartObject("scan_errors");
            w.WriteNumber("total", scanErrors.Count);
            w.WriteStartArray("rows");
            int rows = 0;
            foreach (var rec in scanErrors)
            {
                var row = rec;
                if (!body.Emit(SweepSubject.ScriptScanRows,
                               ScanErrorRowCost(row, depths.HistogramRows, rows > 0),
                               () => WriteScanErrorRow(w, row))) break;
                rows++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rows);
            w.WriteBoolean("truncated", rows < scanErrors.Count);
            w.WriteEndObject();
            return;
        }

        w.WriteStartArray("records");
        int records = 0;
        foreach (var rec in r.Reports)
        {
            // A record object is whole or absent and its cost is MEASURED: EditorID, property names and the
            // per-script reason are all unbounded.
            var row = rec;
            if (!body.Emit(SweepSubject.ScriptRecords,
                           ScriptRecordCost(row, depths.ScriptRecords, records > 0),
                           () => WriteScriptRecord(w, row))) break;
            records++;
        }
        w.WriteEndArray();
    }

    /// <summary>ONE <c>scan_errors</c> row, shared by the write and the measurement.</summary>
    static void WriteScanErrorRow(Utf8JsonWriter w, RecordScriptFindings rec)
    {
        w.WriteStartObject();
        w.WriteString("plugin", rec.Plugin);
        w.WriteString("scan_error", rec.ScanError ?? "");
        w.WriteEndObject();
    }

    /// <summary>One record object, written at the response's own nesting depth.</summary>
    static void WriteScriptRecord(Utf8JsonWriter w, RecordScriptFindings rec)
    {
        w.WriteStartObject();
        if (rec.ScanError is not null)
        {
            w.WriteString("plugin", rec.Plugin);
            w.WriteString("scan_error", rec.ScanError);
            w.WriteEndObject();
            return;
        }
        w.WriteString("formid", FormIdToken.Of(rec.Record));
        w.WriteString("type", rec.RecordType);
        WriteNullable(w, "editorid", rec.EditorId);
        w.WriteString("plugin", rec.Plugin);
        w.WriteStartArray("unbound");
        // Object/form types first — the same severity ordering the text render applies.
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
    }

    /// <summary>What ONE record object costs, at its own depth and sibling position (see <see cref="MeasureUnit"/>).</summary>
    static int ScriptRecordCost(RecordScriptFindings rec, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteScriptRecord(w, rec));

    /// <summary>What one <c>scan_errors</c> row costs, same construction, same depth.</summary>
    static int ScanErrorRowCost(RecordScriptFindings rec, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteScanErrorRow(w, rec));
    // ---- shared sweep writers ----------------------------------------------------------------------
    /// <summary>The same two-pass axis write, keyed off the axes' own subjects, so a family holding
    /// <see cref="HistogramAxis"/> values does not spell its json field names twice.</summary>
    internal static void WriteHistogramAxes(Utf8JsonWriter w, BoundedBody? body, int rowLimit, params HistogramAxis[] axes)
        => WriteHistograms(w, body, rowLimit, new JsonUnitDepths(w.CurrentDepth),
                           axes.Select(a => (AxisJsonName(a.Subject), a.Subject, a.Rows)).ToArray());

    /// <summary>Reserve every axis's OBJECT FRAME out of the body budget, then write the axes: the frame is this
    /// transport's whole disclosure that the axis exists, so its room comes out of <c>max_chars</c> first. It covers
    /// the leading members too, so an axis over-reserves against itself — the safe direction.</summary>
    static void WriteHistograms(Utf8JsonWriter w, BoundedBody? body, int rowLimit, JsonUnitDepths depths,
                                params (string Name, SweepSubject Subject, IReadOnlyList<SweepCount>? Rows)[] axes)
    {
        if (body is not null)
            foreach (var a in axes)
                if (a.Rows is not null)
                    body.Reserve(a.Subject, HistogramFrameCost(a.Name, a.Rows.Count, depths.AxisFrame));
        foreach (var a in axes) WriteHistogram(w, a.Name, a.Subject, a.Rows, rowLimit, body, depths);
    }
    /// <summary>What ONE axis object costs with no rows in it, at its own depth, with every member at its widest.</summary>
    static int HistogramFrameCost(string name, int distinct, int depth)
        => MeasureMember(depth, w =>
        {
            w.WriteStartObject(name);
            w.WriteNumber("distinct", distinct);
            w.WriteStartArray("rows");
            w.WriteEndArray();
            w.WriteNumber("rendered", distinct);
            w.WriteString("cut_by", "max_chars");
            w.WriteEndObject();
        });

    /// <summary>A counts_only histogram. Absent when the mode was not requested; PRESENT with an empty <c>rows</c>
    /// when the sweep genuinely found nothing.</summary>
    /// <param name="body">the ONE bounded emission path, or null for validate_scripts, which passes no budget.</param>
    /// <param name="subject">this axis's OWN subject — two axes sharing one would let the first to stop stop the second.</param>
    static void WriteHistogram(Utf8JsonWriter w, string name, SweepSubject subject, IReadOnlyList<SweepCount>? rows,
                               int rowLimit, BoundedBody? body, JsonUnitDepths depths)
    {
        if (rows is null) { body?.Release(subject); return; }
        // The object's fixed members are part of the fixed part; the ROWS are what the budget gates.
        Unconditional(body, subject, () =>
        {
            w.WriteStartObject(name);
            w.WriteNumber("distinct", rows.Count);
            w.WriteStartArray("rows");
        });
        int shown = 0;
        bool cutByBudget = false;
        foreach (var row in rows)
        {
            if (shown >= rowLimit) break;
            var r = row;
            // The row's cost is MEASURED like every other unit the allocation divides room by.
            if (body is not null
                && !body.Emit(subject, HistogramRowCost(r, depths.HistogramRows, shown > 0),
                              () => WriteHistogramRow(w, r)))
            { cutByBudget = true; break; }
            if (body is null) WriteHistogramRow(w, row);
            shown++;
        }
        int rendered = shown;
        Unconditional(body, subject, () =>
        {
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            // WHICH knob stopped it, from the text lane's own computation; null where the axis is whole.
            if (HistogramCut.For(rows.Count, rendered, cutByBudget) is { } cut) w.WriteString("cut_by", cut.Knob);
            else w.WriteNull("cut_by");
            w.WriteEndObject();
        });
        // The frame is written and charged, so whatever room is still held for it goes back.
        body?.Release(subject);
    }

    /// <summary>ONE histogram row, shared by the write and the measurement.</summary>
    static void WriteHistogramRow(Utf8JsonWriter w, SweepCount row)
    {
        w.WriteStartObject();
        w.WriteString("key", row.Key);
        w.WriteNumber("count", row.Count);
        w.WriteEndObject();
    }

    /// <summary>What one histogram row costs, at its own depth and sibling position.</summary>
    static int HistogramRowCost(SweepCount row, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteHistogramRow(w, row));

    /// <summary>Write part of an axis object's own FRAME: unconditional, never refused, measured into the fixed part.</summary>
    static void Unconditional(BoundedBody? body, SweepSubject subject, Action commit)
    {
        if (body is null) commit();
        else body.Fixed(subject, commit);
    }

    /// <summary>Under counts_only, check_errors' reports carry the honesty layer only — plugins whose records could not
    /// be read. Wrapped rather than a bare array, because an array a budget cut cannot say so.</summary>
    static void WriteUnreadPlugins(Utf8JsonWriter w, IReadOnlyList<PluginErrors> reports, BoundedBody body,
                                   JsonUnitDepths depths)
    {
        w.WriteStartObject("unread");
        w.WriteNumber("total", reports.Count);
        w.WriteStartArray("rows");
        int rendered = 0;
        foreach (var p in reports)
        {
            // The exact sibling of the plugin head: its scan error and samples are unbounded, so its cost is measured.
            var row = p;
            if (!body.Emit(SweepSubject.UnreadRows,
                           UnreadRowCost(p, depths.HistogramRows, rendered > 0),
                           () => WriteUnreadRow(w, row))) break;
            rendered++;
        }
        w.WriteEndArray();
        w.WriteNumber("rendered", rendered);
        w.WriteBoolean("truncated", rendered < reports.Count);
        w.WriteEndObject();
    }

    /// <summary>ONE unread row, shared by the write and the measurement.</summary>
    static void WriteUnreadRow(Utf8JsonWriter w, PluginErrors p)
    {
        w.WriteStartObject();
        w.WriteString("plugin", p.Plugin);
        WriteNullable(w, "scan_error", p.ScanError);
        w.WriteNumber("unscannable_records", p.UnscannableRecords);
        WriteStringArray(w, "unscannable_samples", p.UnscannableSamples);
        w.WriteEndObject();
    }

    /// <summary>What one unread row costs, at its own depth and sibling position.</summary>
    static int UnreadRowCost(PluginErrors p, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteUnreadRow(w, p));

    /// <summary>The excluded-plugin roster; <paramref name="body"/> is the bounded emission path, null for validate_scripts.</summary>
    static void WriteExcluded(Utf8JsonWriter w, IReadOnlyDictionary<string, string> excluded, BoundedBody? body = null)
    {
        // A RESPONSE-level roster, so its depth is the writer's own here rather than any family's.
        int depth = w.CurrentDepth + 1;
        w.WriteStartArray("excluded_plugins");
        int rendered = 0;
        foreach (var kv in excluded)
        {
            var row = kv;
            void Write() { w.WriteStartObject(); w.WriteString("plugin", row.Key); w.WriteString("reason", row.Value); w.WriteEndObject(); }
            if (body is null) { Write(); continue; }
            // Measured rather than post-checked: `reason` is a Mutagen parse-failure message with no length of its own.
            if (!body.Emit(SweepSubject.ExcludedRows, ExcludedRowCost(row, depth, rendered > 0), Write)) break;
            rendered++;
        }
        w.WriteEndArray();
    }

    /// <summary>What one roster row costs, for the demand pass; the roster is a ROOT member in every render that has one.</summary>
    internal static int ExcludedRowCostFor(IReadOnlyDictionary<string, string> excluded, int index)
        => ExcludedRowCost(excluded.ElementAt(index), RosterDepth, index > 0);

    /// <summary>The root object, then the roster array: where every excluded-plugin row is written.</summary>
    const int RosterDepth = 2;

    /// <summary>What one excluded-roster row costs, at its own depth and sibling position.</summary>
    static int ExcludedRowCost(KeyValuePair<string, string> row, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w =>
        {
            w.WriteStartObject();
            w.WriteString("plugin", row.Key);
            w.WriteString("reason", row.Value);
            w.WriteEndObject();
        });

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

    // ---- housecarl_apply ----------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.Render"/>: ONE write outcome, the SAME data the text
    /// render states, with what the text lane treats as prose typed here. A REFUSAL is a document too, and the
    /// first-touch in-place consent prompt is its own flag.</summary>
    /// <param name="lane">the lane the call named, passed in rather than derived off the outcome's defaulted flags.</param>
    public static string RenderPatchOutcome(WritePatchBuilder.PatchOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteBoolean("dry_run", o.DryRun);
            w.WriteString("lane", lane);
            WriteEpoch(w, o.Stamp);
            if (!o.Success)
            {
                // NeedsAcknowledge carries its prompt in Error — labelled as a prompt, never as an error string.
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                // Flush BEFORE reading the stream: this return is INSIDE the writer's using-block.
                w.Flush();
                return Finish(ms);
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            w.WriteStartArray("masters");
            foreach (var m in o.Masters) w.WriteStringValue(m);
            w.WriteEndArray();

            // Did the per-op file check RUN at all? Outside the ops budget: a max_chars cut must not remove it.
            w.WriteBoolean("verify_ran", o.Ops.Any(op => op.VerifyAttempted));
            // …and the stronger fact beside it: how many edits targeted a record the written file does not contain.
            int absent = o.Ops.Count(op => op.RecordAbsentFromFile);
            w.WriteNumber("ops_record_absent", absent);
            if (absent > 0)
            {
                var absentIds = o.Ops.Where(op => op.RecordAbsentFromFile).Select(op => FormIdToken.Of(op.Target))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                w.WriteStartArray("record_absent_formids");
                foreach (var id in absentIds.Take(WriteSentences.AbsentRecordsShown)) w.WriteStringValue(id);
                w.WriteEndArray();
                // The array is bounded; the count above is not, so a consumer can always tell it was cut.
                w.WriteNumber("record_absent_formids_total", absentIds.Count);
            }

            w.WriteNumber("total_ops", o.Ops.Count);
            w.WriteStartArray("ops");
            int renderedOps = 0;
            bool truncated = false;
            foreach (var op in o.Ops)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", FormIdToken.Of(op.Target));
                w.WriteString("record_type", op.RecordType);
                w.WriteString("label", op.Label);
                w.WriteBoolean("applied", op.Applied);
                WriteNullable(w, "error", op.Error);
                WriteNullable(w, "after", op.After);
                // The leaf as the WRITTEN FILE holds it, with `landed_source` naming a null. ON A SUPERSEDED OP it is
                // the leaf's FINAL state after every op in the call, which `after_on_disk_is_final_leaf` marks.
                WriteNullable(w, "after_on_disk", op.AfterOnDisk);
                if (op.AfterOnDisk is not null && op.SupersededInCall)
                    w.WriteBoolean("after_on_disk_is_final_leaf", true);
                // The value came off the file and NOTHING parsed it: an opaque blob, with its length (#529).
                if (op.AfterOnDiskBytes is { } ob) w.WriteNumber("after_on_disk_opaque_bytes", ob);
                WriteNullable(w, "landed", op.Landed);
                // What the write DID that the file cannot say afterwards — today only the duplicate Add.
                WriteNullable(w, "apply_note", op.ApplyNote);
                // The file-vs-memory split, REPORTED not judged: `landed` in memory, `landed_on_disk` off the file.
                WriteNullable(w, "landed_on_disk", op.LandedOnDisk);
                // WHERE the clause came from, as a word rather than a verdict; the five values are in
                // docs/architecture/json-wire.md.
                w.WriteString("landed_source",
                    op.RecordAbsentFromFile ? "record_absent"
                    : op.SupersededInCall ? "superseded"
                    : op.LandedOnDisk is not null ? "written_file"
                    : op.VerifyAttempted ? "no_answer" : "not_checked");
                w.WriteEndObject();
                renderedOps++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_ops", renderedOps);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, o.DryRun, readback, ref truncated);

            WriteNullable(w, "warning", o.Warning);
            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Lane-aware, shared with forward: widening the `ops` array is safe on into=/dry-run but cuts a second
            // patch on the default lane and re-serializes the caller's own file on in_place.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "applied", absent > 0)} — "
                    + WriteTools.ApplyAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_create ---------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderCreate"/>, on
    /// <see cref="RenderPatchOutcome"/>'s contract; the three post-write reports ride as data, not prose.</summary>
    public static string RenderCreateOutcome(WritePatchBuilder.CreateOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteString("lane", lane);
            WriteEpoch(w, o.Stamp);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — without it the buffered document is unwritten and the
                return Finish(ms);  // caller gets an EMPTY string.
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            WriteStringArray(w, "masters", o.Masters.ToList());

            // Hoisted ABOVE the budgeted `created` array: that this artifact out-ranks a mod on a parent it only meant
            // to host a child in must survive a cut. Bounded as the text twin is, with the full count beside it.
            var contestedHosts = o.Created.Where(c => c.ParentContested && c.ParentHost is not null)
                                  .Select(c => c.ParentHost!).Distinct(StringComparer.Ordinal).ToList();
            w.WriteNumber("total_contested_parent_hosts", contestedHosts.Count);
            w.WriteStartArray("contested_parent_hosts");
            foreach (var host in contestedHosts.Take(Wire.ContestedHostsShown)) w.WriteStringValue(host);
            w.WriteEndArray();

            // Did the per-record file check RUN at all? Outside the `created` budget: a cut must not remove it.
            w.WriteBoolean("verify_ran", o.Created.Any(c => c.VerifyAttempted));
            // The stronger fact beside it: how many created records the file does not contain, and which.
            // `records_absent` counts CREATED RECORDS; the two `_total` numbers count DISTINCT FormIDs.
            var notLanded = o.Created.Where(c => c.AbsentFromFile || c.ParentAbsentFromFile).ToList();
            w.WriteNumber("records_absent", notLanded.Count);
            if (notLanded.Count > 0)
            {
                // Created ids and PARENT ids are kept apart: a parent's FormID is not one this call created.
                var absentIds = notLanded.Where(c => c.AbsentFromFile).Select(c => FormIdToken.Of(c.FormKey))
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var absentParents = notLanded.Where(c => c.ParentAbsentFromFile).Select(c => FormIdToken.Of(c.ParentKey!.Value))
                                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                w.WriteStartArray("record_absent_formids");
                foreach (var id in absentIds.Take(WriteSentences.AbsentRecordsShown)) w.WriteStringValue(id);
                w.WriteEndArray();
                // Each array is bounded; its total is not, so a consumer can always tell it was cut.
                w.WriteNumber("record_absent_formids_total", absentIds.Count);
                w.WriteStartArray("parent_absent_formids");
                foreach (var id in absentParents.Take(WriteSentences.AbsentRecordsShown)) w.WriteStringValue(id);
                w.WriteEndArray();
                w.WriteNumber("parent_absent_formids_total", absentParents.Count);
            }

            // WHICH loose roots the coverage checks could not read — once at the document root, because both read
            // ONE asset build, and cut by the same rule the text lane's lines are. ABOVE the rows, like verify_ran:
            // written after them it is the one member nothing charges, and the rows are what should pay for it.
            WriteRootFailuresCut(w, WriteTools.CreateRootFailures(o), cap);

            w.WriteNumber("total_created", o.Created.Count);
            w.WriteStartArray("created");
            int rendered = 0;
            bool truncated = false;
            foreach (var c in o.Created)
            {
                if (Over(w, ms, cap)) { truncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", FormIdToken.Of(c.FormKey));
                w.WriteString("record_type", c.RecordType);
                w.WriteString("editorid", c.EditorId);
                // A replace is never silent (the CreatedRecord contract), as a flag rather than brackets.
                w.WriteBoolean("replaced_existing", c.ReplacedExisting);
                // The parent override this nested create hosted the child in, and whose version was copied.
                WriteNullable(w, "parent_host", c.ParentHost);
                w.WriteBoolean("parent_contested", c.ParentContested);
                // The file's verdict as flags: `absent_from_file` says the create is not in the file, `verified` says
                // the walk REACHED this record or completed, which reaches every record it did not find — not a
                // claim about the ops one by one; the gate it puts on `landed_source` is in
                // docs/architecture/json-wire.md.
                w.WriteBoolean("verified", c.VerifyAttempted);
                w.WriteBoolean("absent_from_file", c.AbsentFromFile);
                if (c.ParentKey is { } pk) w.WriteString("parent_formid", FormIdToken.Of(pk));
                w.WriteBoolean("parent_absent_from_file", c.ParentAbsentFromFile);
                w.WriteStartArray("ops");
                foreach (var op in c.Ops)
                {
                    w.WriteStartObject();
                    w.WriteString("label", op.Label);
                    w.WriteBoolean("applied", op.Applied);
                    WriteNullable(w, "error", op.Error);
                    WriteNullable(w, "after", op.After);
                    // The leaf as the WRITTEN FILE holds it; identical keys to the apply lane (#683, #763).
                    WriteNullable(w, "after_on_disk", op.AfterOnDisk);
                    if (op.AfterOnDisk is not null && op.SupersededInCall)
                        w.WriteBoolean("after_on_disk_is_final_leaf", true);
                    // The value came off the file and NOTHING parsed it: an opaque blob, with its length (#529).
                    if (op.AfterOnDiskBytes is { } ob) w.WriteNumber("after_on_disk_opaque_bytes", ob);
                    WriteNullable(w, "landed_on_disk", op.LandedOnDisk);
                    w.WriteString("landed_source",
                        op.RecordAbsentFromFile ? "record_absent"
                        : op.SupersededInCall ? "superseded"
                        : op.LandedOnDisk is not null ? "written_file"
                        : op.VerifyAttempted ? "no_answer" : "not_checked");
                    // `after` is a SENTENCE about what the write did, not a field reading: a CK-parity fill.
                    if (op.AfterIsNote) w.WriteBoolean("after_is_note", true);
                    WriteNullable(w, "apply_note", op.ApplyNote);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_created", rendered);

            // The three post-write reports are INSIDE the budget, like the text twin's.
            WriteVoiceReport(w, o.Voice, ms, cap, ref truncated);
            WriteScriptBindingReport(w, o.ScriptBinding, ms, cap, ref truncated);
            WriteCellShellReport(w, o.CellShell, ms, cap, ref truncated);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, false, readback, ref truncated);

            WriteNullable(w, "warning", o.Warning);
            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // NOT the sibling renders' "raise max_chars to see the rest": a repeated CREATE allocates again.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; "
                    + WriteSentences.CreateRowsCutRemedy(WriteTools.ReadBackCall(o, Path.GetFileName(o.OutputPath)),
                                                        notLanded.Count > 0) + ".");
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_write_seq ------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="SeqTools.Render"/>: the no-SGE-quests no-op, the null epoch
    /// with its reason, and the unchanged destination, all typed. <c>written</c> means THIS call wrote the file.</summary>
    public static string RenderSeqOutcome(SeqOutcome o, int maxChars, string? outputNote = null)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteNull("epoch");
            w.WriteString("epoch_note", WriteSentences.Twins.SeqNoEpoch);
            if (!o.Success)
            {
                WriteNullable(w, "error", o.Error);
                WriteNullable(w, "lane_note", outputNote);   // an ignored lane stays stated on a refusal too
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — an unflushed refusal renders EMPTY. See RenderPatchOutcome.
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
            w.WriteBoolean("user_chose_out_path", o.UserChoseOutput);
            WriteNullable(w, "deploy_warning", o.DeployWarning);
            WriteNullable(w, "lane_note", outputNote);
            w.WriteNumber("quest_count", o.Quests.Count);
            if (o.Quests.Count == 0)
                w.WriteString("note", "no start-game-enabled quests in this plugin — " + WriteSentences.Twins.SeqNoQuests + "."
                    // The lane was acknowledged but never resolved on this path — say which of the two it is.
                    + (o.UserChoseOutput ? " out_path= was not resolved or checked either — no destination was touched, so an unusable one would not have been reported here." : ""));

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
            // Not "raise max_chars to see the rest": widening the ceiling means re-issuing a WRITE.
            if (truncated)
                w.WriteString("truncated_note",
                    $"the render hit max_chars={cap} and dropped trailing quest rows — " + WriteSentences.Twins.SeqListCutRemedy + ".");
            w.WriteString("standing_limit", WriteSentences.Twins.SeqStandingLimit);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_remove ---------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderRemoval"/>, on
    /// <see cref="RenderPatchOutcome"/>'s contract; <c>remaining_records:0</c> is the inert-shell fact.</summary>
    public static string RenderRemovalOutcome(WritePatchBuilder.RemovalOutcome o, int maxChars, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteString("lane", lane);
            WriteEpoch(w, o.Stamp);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — an unflushed refusal renders EMPTY. See RenderPatchOutcome.
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
                w.WriteString("formid", FormIdToken.Of(r.Target));
                w.WriteString("record_type", r.RecordType);
                WriteNullable(w, "editorid", r.EditorId);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_removed", rendered);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Same remedy as the text twin: a repeated remove is REFUSED, so "raise max_chars" would name a failure.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(false, "removed")} — "
                    + WriteTools.RemovedRowsRemedy + ".");
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_forward --------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderForward"/>, on
    /// <see cref="RenderPatchOutcome"/>'s contract; the bracketed per-record facts are flags here.</summary>
    public static string RenderForwardOutcome(WritePatchBuilder.ForwardOutcome o, int maxChars, bool readback, string lane)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            w.WriteBoolean("needs_acknowledge", o.NeedsAcknowledge);
            w.WriteBoolean("dry_run", o.DryRun);
            w.WriteString("lane", lane);
            WriteEpoch(w, o.Stamp);
            if (!o.Success)
            {
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — an unflushed refusal renders EMPTY. See RenderPatchOutcome.
                return Finish(ms);
            }

            w.WriteString("path", o.OutputPath);
            w.WriteString("file", Path.GetFileName(o.OutputPath));
            w.WriteNumber("bytes", o.Bytes);
            WriteStringArray(w, "masters", o.Masters.ToList());

            // The text twin's `source:` disclosure; `source_read` names WHICH copy on disk an off-order read opened.
            w.WriteBoolean("source_in_order", o.OffOrderSource is null);
            if (o.OffOrderSource is { } oo)
            {
                w.WriteStartObject("source_read");
                w.WriteString("source", oo.Plugin);
                w.WriteString("path", oo.Path);
                w.WriteString("where", oo.Where);
                // Non-null ⇒ the order's copy of a plugin EXCLUDED as unparseable, reached by PATH. Never silent.
                WriteNullable(w, "excluded_from_index", oo.ExcludedReason);
                // The stamp fingerprints the ACTIVE order, and this file's content sits outside it.
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
                w.WriteString("formid", FormIdToken.Of(f.Target));
                w.WriteString("record_type", f.RecordType);
                WriteNullable(w, "editorid", f.EditorId);
                w.WriteString("source", f.FromPlugin);
                w.WriteBoolean("replaced_existing", f.ReplacedExisting);
                // How many records nested under the replaced one were carried across.
                w.WriteNumber("preserved_children", f.PreservedChildren);
                w.WriteBoolean("was_already_winner", f.WasAlreadyWinner);
                WriteNullable(w, "prior_winner", f.PriorWinner);
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_forwarded", rendered);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, o.DryRun, readback, ref truncated);

            WriteNullable(w, "warning", o.Warning);
            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // Lane-aware, same rule and helper as the text twin: a re-issue cuts a second patch on the DEFAULT lane.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "forwarded")} — "
                    + WriteTools.ForwardAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_asset_status (S2 read) -----------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="AssetWire"/>'s render: the build-level caveats, one row per
    /// queried path with its winner and provider chain, and the §2.1 accounting in-band. A provider is
    /// <c>{name, kind}</c> — the NAME, never the display token. Pinned in <c>AssetStatusJsonLaneTests</c> by
    /// <c>TheJsonLaneCarriesTheWinnerAndProviderChainAsData</c> and <c>TheJsonLaneCarriesTheSameAccountingTheTextLaneStates</c>.</summary>
    public static string RenderAssetStatus(AssetStatusData d, int maxChars)
        => RenderAssetStatus(d, maxChars, null, out _);

    /// <param name="spill">this call's artifact disposition, priced into the tail reserve so it lands inside max_chars.</param>
    /// <param name="truncated">whether max_chars cut paths out of the window — what the caller auto-spills on.</param>
    public static string RenderAssetStatus(AssetStatusData d, int maxChars, SpillState? spill, out bool truncated)
    {
        int cap = Cap(maxChars);
        // The accounting and the advice after it are priced INSIDE max_chars, as the text twin prices its line.
        int budget = Math.Max(cap - AssetTailReserve(d, cap, spill), 1);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("profile", d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)");
            // The caveats lead, as in the text render: an ABSENT below is authoritative only when both are empty.
            w.WriteBoolean("read_incomplete", d.ReadIncomplete);
            int caveatsOmitted = WriteCappedStringArray(w, ms, "bsa_failures", d.BsaFailures, budget)
                               + RootFailuresJson(w, ms, d, budget)
                               + WriteCappedStringArray(w, ms, "warnings", d.Warnings, budget);
            if (d.SelectorNotes is null) { w.WriteNull("selector_notes"); w.WriteNumber("selector_notes_omitted", 0); }
            else caveatsOmitted += WriteCappedStringArray(w, ms, "selector_notes", d.SelectorNotes, budget);

            w.WriteStartArray("results");
            int rendered = 0;
            foreach (var r in d.Results)
            {
                // rendered > 0: the FIRST row always renders its core answer, as BatchRender does on the text lane.
                if (rendered > 0 && Over(w, ms, budget)) break;
                WriteAssetRow(w, r, d.BsaFailures.Count > 0, d.Warnings.Count > 0, d.RootFailures.Count > 0);
                rendered++;
            }
            w.WriteEndArray();

            var counts = AssetWire.Tally(d, rendered);
            TransportAccounting.WriteJson(w, counts);
            // The document's own flag, so a consumer branching on it re-calls when ANYTHING was dropped.
            w.WriteBoolean("truncated", counts.Truncated > 0 || caveatsOmitted > 0);
            // The out parameter is the ROWS' cut alone: an artifact holds rows.
            truncated = counts.Truncated > 0;
            WriteAssetAdvice(w, counts, cap, caveatsOmitted > 0);
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The <c>counts_only=</c> twin of <see cref="RenderAssetStatus"/>: no path rows, and the layer table is the shared axis.</summary>
    public static string RenderAssetCensus(AssetStatusData d, int maxChars, int limit)
    {
        int cap = Cap(maxChars);
        var c = AssetCensus.Tally(d);
        // What this document writes whatever the budget says comes out of max_chars before the caveats.
        int budget = Math.Max(cap - AssetCensusFixedReserve(d, c), 1);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("profile", d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)");
            // The caveats lead here too: an absent= count is authoritative only where both are empty.
            w.WriteBoolean("read_incomplete", d.ReadIncomplete);
            int omitted = WriteCappedStringArray(w, ms, "bsa_failures", d.BsaFailures, budget)
                        + RootFailuresJson(w, ms, d, budget)
                        + WriteCappedStringArray(w, ms, "warnings", d.Warnings, budget);
            if (d.SelectorNotes is null) { w.WriteNull("selector_notes"); w.WriteNumber("selector_notes_omitted", 0); }
            else omitted += WriteCappedStringArray(w, ms, "selector_notes", d.SelectorNotes, budget);

            WriteCensusCounters(w, c);
            // The layer table through the shared axis: the frame is reserved before its rows are offered to it.
            var body = new BoundedBody(acct: null, budget: budget, () => Size(w, ms));
            WriteHistogramAxes(w, body, AssetCensus.RowLimit(limit), AssetCensus.Axis(c));
            w.WriteBoolean("truncated", omitted > 0 || body.Stopped(SweepSubject.AssetWinnerRows));
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The census's six counters, in one place so the reserve measures what the render writes.</summary>
    static void WriteCensusCounters(Utf8JsonWriter w, AssetCensus.Counts c)
    {
        w.WriteNumber("counted", c.Selected);
        w.WriteNumber("present", c.Present);
        w.WriteNumber("absent", c.Absent);
        w.WriteNumber("errors", c.Errors);
        w.WriteNumber("loose", c.Loose);
        w.WriteNumber("bsa", c.Bsa);
    }

    /// <summary>What the census document carries whatever the budget says: the counters, the axis frame and the
    /// trailing flag, measured under the response's own writer options at the depth they are written.</summary>
    static int AssetCensusFixedReserve(AssetStatusData d, AssetCensus.Counts c)
    {
        int frame;
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("before", "");   // the tail is never a document's first member, so it pays the separator it owes
            // The three caveat counters, written AFTER their array has spent the budget; each omits at most its own.
            w.WriteNumber("bsa_failures_omitted", d.BsaFailures.Count);
            if (d.RootFailures.Count > 0) w.WriteNumber("root_failures_omitted", d.RootFailures.Count);
            w.WriteNumber("warnings_omitted", d.Warnings.Count);
            w.WriteNumber("selector_notes_omitted", d.SelectorNotes?.Count ?? 0);
            WriteCensusCounters(w, c);
            // Read at the same depth the render writes the axis at, so the two measure one object.
            frame = HistogramFrameCostFor(AssetCensus.Axis(c), new JsonUnitDepths(w.CurrentDepth).AxisFrame);
            w.WriteBoolean("truncated", true);
            w.WriteEndObject();
        }
        return Chars(ms) + frame;
    }

    /// <summary>The <c>to_file=</c> twin of <see cref="RenderAssetStatus"/>: the caveats an ABSENT row in the FILE
    /// depends on, and the spilled marker. No rows, because the rows ARE the file.</summary>
    public static string RenderAssetStatusManifestOnly(AssetStatusData d, SpillInfo spill, int maxChars)
    {
        int cap = Cap(maxChars);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("profile", d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)");
            w.WriteBoolean("read_incomplete", d.ReadIncomplete);
            int omitted = WriteCappedStringArray(w, ms, "bsa_failures", d.BsaFailures, cap)
                        + RootFailuresJson(w, ms, d, cap)
                        + WriteCappedStringArray(w, ms, "warnings", d.Warnings, cap);
            if (d.SelectorNotes is null) { w.WriteNull("selector_notes"); w.WriteNumber("selector_notes_omitted", 0); }
            else omitted += WriteCappedStringArray(w, ms, "selector_notes", d.SelectorNotes, cap);
            w.WriteBoolean("truncated", omitted > 0);
            Artifacts.WriteSpillJson(w, spill);
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The three conditional sentences the text accounting composes, on the same conditions: the next page,
    /// an offset past the end, and the max_chars cut. Siblings of the <c>accounting</c> object, not members.</summary>
    /// <param name="everySentence">write them all — the widest case the reserve measures.</param>
    static void WriteAssetAdvice(Utf8JsonWriter w, TransportCounts c, int cap, bool caveatsCut,
                                 bool everySentence = false)
    {
        if (everySentence || c.Remaining > 0)
        {
            w.WriteNumber("next_limit", c.NextLimit);
            w.WriteNumber("next_offset", c.Offset + c.Rendered);
        }
        if (everySentence || (c.Remaining == 0 && c.Total > 0 && c.Offset >= c.Total))
            w.WriteString("offset_note", $"offset={c.Offset} is past the end of the selection ({c.Total} path(s)) — the last page starts before it.");
        if (everySentence || c.Truncated > 0 || caveatsCut)
            w.WriteString("truncated_note",
                $"max_chars={cap} cut content from this document — accounting.truncated names the resolved path(s) " +
                "dropped and each *_omitted counter the caveat entries; raise max_chars, or page with limit=/offset=.");
    }

    /// <summary>The chars held back from max_chars for what this document writes outside the budgeted body, measured
    /// by serializing the WIDEST tail under the response's own writer options.</summary>
    static int AssetTailReserve(AssetStatusData d, int cap, SpillState? spill)
    {
        var widest = AssetWire.Widest(d);
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("before", "");   // the tail is never a document's first member, so it pays the separator one owes
            // Each block can omit at most its own entries, so its own count is the widest number it can write.
            w.WriteNumber("bsa_failures_omitted", d.BsaFailures.Count);
            if (d.RootFailures.Count > 0) w.WriteNumber("root_failures_omitted", d.RootFailures.Count);
            w.WriteNumber("warnings_omitted", d.Warnings.Count);
            w.WriteNumber("selector_notes_omitted", d.SelectorNotes?.Count ?? 0);
            TransportAccounting.WriteJson(w, widest);
            w.WriteBoolean("truncated", true);
            WriteAssetAdvice(w, widest, cap, caveatsCut: true, everySentence: true);
            // The spill block is measured, not estimated: the re-render already has the written artifact's manifest.
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Chars(ms);
    }

    /// <summary>The loose roots that could not be walked, written ONLY when there are some: a build where every
    /// root walked is the document it always was, and the array's own room is charged nowhere it is not written.</summary>
    static int RootFailuresJson(Utf8JsonWriter w, CharCountedStream ms, AssetStatusData d, int budget) =>
        d.RootFailures.Count == 0 ? 0 : WriteCappedStringArray(w, ms, "root_failures", d.RootFailures, budget);

    static void WriteAssetRow(Utf8JsonWriter w, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete,
                              bool rootIncomplete)
    {
        w.WriteStartObject();
        w.WriteString("path", r.RelPath);
        // Only on a row the formids= SELECT derived, so a plain path row is byte-for-byte the document it always was.
        if (r.FormId is not null) w.WriteString("formid", r.FormId);
        if (r.Slot is { } slot) w.WriteString("slot", FaceGenPath.Token(slot));
        if (r.Error is not null)                                  // a rejected path: drive-rooted, or escaping with '..'
        {
            // A per-ROW error, never the document's discriminant: the call succeeded and rendered a row that failed.
            w.WriteString("error", r.Error);
            w.WriteEndObject();
            return;
        }
        var hit = r.Hit!;
        w.WriteNull("error");
        w.WriteBoolean("exists", hit.Exists);
        if (hit.Winner is { } win) WriteAssetProvider(w, "winner", win); else w.WriteNull("winner");
        w.WriteStartArray("providers");
        foreach (var p in hit.Providers) WriteAssetProvider(w, null, p);
        w.WriteEndArray();
        w.WriteBoolean("ambiguous", hit.Ambiguous);
        // On a formids= row only: the OWNER the pair's `differs` verdict is decided on, not the bare OwningMod.
        if (r.FormId is not null)
            WriteNullable(w, "winner_mod", hit.Winner is { } owner ? AssetPathResult.Owner(owner) : null);
        if (!hit.Exists)
        {
            WriteNullableStringArray(w, "prefix_suggestions", r.PrefixSuggestions);
            // The two ways an ABSENT can be wrong, per row: a failed archive read against archives never discovered.
            // Pinned by AssetStatusJsonHedgeTests.TheTwoAbsentHedgesAreStatedApart.
            w.WriteBoolean("absent_may_be_incomplete_read_failure", readIncomplete);
            w.WriteBoolean("absent_may_be_incomplete_undiscovered_archives", discoveryIncomplete);
            // The third hedge, with its own remedy: a loose root that would not walk (the asset could be inside it).
            if (rootIncomplete) w.WriteBoolean("absent_may_be_incomplete_unwalked_root", true);
        }
        // The other half of the FaceGen pair, resolved beside this one; absent on a plain path row.
        if (r.PairPath is not null)
        {
            w.WriteStartObject("pair");
            w.WriteString("path", r.PairPath);
            if (r.Slot is { } s) w.WriteString("slot", FaceGenPath.Token(FaceGenPath.Other(s)));
            if (r.PairHit is { } pair)
            {
                w.WriteBoolean("exists", pair.Exists);
                if (pair.Winner is { } pw) WriteAssetProvider(w, "winner", pw); else w.WriteNull("winner");
                // The OWNER `differs` below is decided on, not the archive name and not the bare OwningMod.
                WriteNullable(w, "winner_mod", pair.Winner is { } pw2 ? AssetPathResult.Owner(pw2) : null);
            }
            else { w.WriteNull("exists"); w.WriteNull("winner"); w.WriteNull("winner_mod"); }
            w.WriteBoolean("differs", r.PairDiffers);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    /// <summary>One provider. <paramref name="name"/> null writes it as an array element.</summary>
    static void WriteAssetProvider(Utf8JsonWriter w, string? name, AssetProvider p)
    {
        if (name is null) w.WriteStartObject(); else w.WriteStartObject(name);
        w.WriteString("name", p.Source);
        w.WriteString("kind", p.Kind == AssetKind.Bsa ? "BSA" : "loose");
        w.WriteEndObject();
    }

    // ---- housecarl_place (S2 write) -----------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="PlaceWire"/>'s render, on the write surface's contract, with
    /// the "this does not win until you enable the mod" instruction in-band as <c>next_step</c>.</summary>
    public static string RenderPlaceOutcome(PlaceOutcome o, int maxChars, IReadOnlySet<string>? poleWithheld = null)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            if (!o.Success)
            {
                w.WriteString("error", o.Error);
                // The same three keys RenderError writes, so a refusal from either door is one shape.
                WriteEpoch(w, null, null);
                w.WriteEndObject();
                w.Flush();          // INSIDE the using — an unflushed refusal renders EMPTY. See RenderPatchOutcome.
                return Finish(ms);
            }

            int placed = 0;
            foreach (var r in o.Results) if (r.Placed) placed++;
            var modFolder = o.ModFolder is null ? null : Path.GetFileName(o.ModFolder);
            WriteNullable(w, "mod_folder", modFolder);
            WriteStringArray(w, "warnings", o.Warnings);

            w.WriteStartArray("results");
            int rendered = 0;
            bool truncated = false;
            foreach (var r in o.Results)
            {
                // rendered > 0: the FIRST row always renders — the only place current_winner is stated.
                if (rendered > 0 && Over(w, ms, cap)) { truncated = true; break; }
                WritePlaceRow(w, r, modFolder, o.FreshFolder, poleWithheld?.Contains(r.AssetPath) == true);
                rendered++;
            }
            w.WriteEndArray();

            // The §2.1 counters through the same composer; this lane pages nothing, so placed/failed are siblings.
            TransportAccounting.WriteJson(w, TransportAccounting.Tally(o.Results.Count, o.Results.Count, rendered,
                                                                       RowWindow.All, 0));
            w.WriteNumber("placed", placed);
            w.WriteNumber("failed", o.Results.Count - placed);
            w.WriteBoolean("truncated", truncated);
            // Not "raise max_chars to see the rest": the bytes are already on disk, so a re-issue would place again.
            if (truncated)
                w.WriteString("truncated_note",
                    $"the render hit max_chars={cap} and dropped trailing destination rows — the WRITE is unaffected; " +
                    "raise max_chars and re-read with " + ToolNames.AssetStatus + " rather than placing again.");
            if (o.LeftoverFolder is not null)
                w.WriteString("leftover_folder_note", PlaceWire.LeftoverNote(o.LeftoverFolder));
            WriteNullable(w, "leftover_folder", o.LeftoverFolder);
            if (placed > 0) w.WriteString("next_step", PlaceWire.EnableAndSort(o, modFolder, rendered));
            WriteCapOverrun(w, ms, cap);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    static void WritePlaceRow(Utf8JsonWriter w, PlaceResult r, string? modFolder, bool freshFolder, bool poleWithheld)
    {
        w.WriteStartObject();
        w.WriteString("path", r.AssetPath);
        w.WriteBoolean("placed", r.Placed);
        WriteNullable(w, "error", r.Error);
        if (r.Placed)
        {
            w.WriteNumber("bytes", r.Bytes);
            WriteNullable(w, "source", r.SourceDesc);
            WriteNullable(w, "current_winner", r.CurrentWinner);
            // The text twin's own line, verbatim: a json caller acts on this string alone.
            w.WriteString("winner_note", PlaceWire.WinnerLine(r, modFolder, freshFolder));
            w.WriteBoolean("winner_is_overwrite", r.WinnerIsOverwrite);
            w.WriteBoolean("winner_is_destination", r.WinnerIsDestination);
            w.WriteBoolean("winner_loses_on_enable", r.WinnerLosesOnEnable);
            // Bytes served out of a mod MO2 does not load are a fact of the SOURCE.
            if (r.SourceOffOrderProvider is { } offOrder)
                w.WriteString("source_off_order_note", WriteSentences.PlaceSourceOffOrder(offOrder, r.SourceOffOrderOwnerEnabled));
            WriteNullable(w, "source_off_order_provider", r.SourceOffOrderProvider);
        }
        // An input the call carried but this destination could not use is SAID on both lanes, not dropped.
        w.WriteBoolean("set_provider_withheld", poleWithheld);
        w.WriteEndObject();
    }

    /// <summary>The voice-coverage report as data: the text render's "[!] WILL BE SILENT" becomes <c>fuz_present:false</c> and a path.</summary>
    static void WriteVoiceReport(Utf8JsonWriter w, VoiceReport? report, CharCountedStream ms, int cap, ref bool truncated)
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
            w.WriteString("info", FormIdToken.Of(l.Info));
            WriteNullable(w, "topic_editorid", l.TopicEditorId);
            w.WriteNumber("response", l.ResponseNumber);
            w.WriteBoolean("fuz_present", l.FuzPresent);
            w.WriteBoolean("lip_present", l.LipPresent);
            WriteNullable(w, "fuz_path", l.FuzPath);
            WriteNullable(w, "lip_path", l.LipPath);
            WriteNullable(w, "fuz_winner", l.FuzWinner);
            w.WriteBoolean("fuz_contended", l.FuzAmbiguous);
            // An "absent" that merely went unscanned is not the same claim as one looked for and not found.
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
            w.WriteString("info", FormIdToken.Of(u.Info));
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

    /// <summary>The per-BLOCK truncation census the three post-write reports carry: without it a cut block renders as
    /// <c>lines: []</c>. Counts ride even when nothing was cut, so <c>rendered == total</c> says the list is complete.</summary>
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
        // Deliberately NOT "raise max_chars": these blocks ride the WRITE renders, so the note stops at the stakes.
        if (cut)
            w.WriteString("truncated_note",
                $"the {blockLabel} block hit max_chars={cap} and its rows were CUT. Why it matters: {stakes}"
                + (cutLoss is null ? "" : $", and {cutLoss}")
                + ". An empty or short array here is a RENDER cut, not a clean bill of health — "
                + WriteSentences.Twins.ReportBlockCut + " (the counts are the total_* / rendered_* members above).");
    }

    /// <summary>The result-script binding report as data; <c>status</c> is the enum the text render prints as
    /// "WILL NOT FIRE".</summary>
    static void WriteScriptBindingReport(Utf8JsonWriter w, ScriptBindingReport? report, CharCountedStream ms, int cap, ref bool truncated)
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
            w.WriteString("info", FormIdToken.Of(f.Info));
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

    /// <summary>The cell-shell report as data — a created cell is valid but EMPTY; <c>must_provide</c> is the work list.</summary>
    static void WriteCellShellReport(Utf8JsonWriter w, CellShellReport? report, CharCountedStream ms, int cap, ref bool truncated)
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
            w.WriteString("cell", FormIdToken.Of(c.Cell));
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
    // ---- unit costs, exposed for the demand pass ---------------------------------------------------
    // A demand must be the SAME number the emission test declares, so the demand pass calls these. Each takes the
    // DEPTH the unit is written at and whether it follows a sibling.

    internal static int PluginHeadCostFor(PluginErrors p, int depth, bool subsequent)
        => PluginHeadCost(p, depth, subsequent);
    internal static int ScriptRecordCostFor(RecordScriptFindings rec, int depth, bool subsequent)
        => ScriptRecordCost(rec, depth, subsequent);
    internal static int ScanErrorRowCostFor(RecordScriptFindings rec, int depth, bool subsequent)
        => ScanErrorRowCost(rec, depth, subsequent);
    internal static int DanglingEntryCostFor(DanglingRef d, int depth, bool subsequent)
        => DanglingEntryCost(d, depth, subsequent);
    internal static int HistogramRowCostFor(SweepCount row, int depth, bool subsequent)
        => HistogramRowCost(row, depth, subsequent);
    internal static int UnreadRowCostFor(PluginErrors p, int depth, bool subsequent)
        => UnreadRowCost(p, depth, subsequent);

    /// <summary>This axis's frame cost, keyed off the SUBJECT so the demand pass and the render name the same axis.</summary>
    internal static int HistogramFrameCostFor(HistogramAxis a, int depth)
        => HistogramFrameCost(AxisJsonName(a.Subject), a.Rows?.Count ?? 0, depth);

    internal static string AxisJsonName(SweepSubject s) => s switch
    {
        SweepSubject.HistogramByTarget => "dangling_by_target_plugin",
        SweepSubject.HistogramBySource => "dangling_by_source_plugin",
        SweepSubject.HistogramByProperty => "unbound_by_property",
        SweepSubject.FaceGenClassRows => "facegen_by_class",
        SweepSubject.FaceGenModRows => "facegen_by_owning_mod",
        SweepSubject.AssetWinnerRows => "winners_by_layer",
        _ => s.ToString(),
    };
}
