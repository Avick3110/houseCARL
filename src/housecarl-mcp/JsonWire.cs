using System.Text;
using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The machine-readable (format="json") twin of the text <see cref="Wire"/> renderer: one serializer per
/// read tool, each consuming the SAME outcome objects the text Wire consumes, so text and JSON can differ only in
/// formatting, never in data. Field values are the same wire tokens the text mode emits, so a token read out of
/// JSON is still a value a write can reuse verbatim.
///
/// <para>Truncation drops trailing ROWS and flags it (<c>truncated:true</c> + <c>rendered</c>) — never a cut of
/// the serialized string at a byte budget the way the text render cuts its StringBuilder, which would emit
/// malformed JSON. The accounting rides inside the document, so JSON is never a silently degraded mode.</para></summary>
static class JsonWire
{
    static readonly JsonWriterOptions Opts = new() { Indented = true };

    /// <summary>The options every json response is written under, exposed so <see cref="CheckAccounting"/> measures
    /// its reserve against the same encoding it will be written in — measuring unindented what is written indented
    /// under-reserves by the whole indentation.</summary>
    internal static JsonWriterOptions WriterOptions => Opts;

    static string Finish(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray());

    static void WriteNullable(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }

    /// <summary>The ONE way a json document writes its epoch stamp, so the marker that rides beside it cannot be
    /// remembered on one lane and forgotten on the next. <c>epoch</c> exactly as before, then — and only when the
    /// build that answered had LOST plugins to a load failure — <c>order_degraded:true</c> and one sentence saying
    /// how many, which ones, that this is a failure rather than a change the caller made, and where the reason is
    /// (#353). Silent on a healthy order, so the normal case gains nothing.
    ///
    /// <para>A sibling of the stamp, never inside it: the epoch is opaque and compared only for equality, so folding
    /// health into the string would leave two builds that differ only in health comparing as merely "different" —
    /// today's ambiguity re-spelled.</para></summary>
    static void WriteEpoch(Utf8JsonWriter w, OrderStamp? stamp) =>
        WriteEpoch(w, stamp?.Epoch, stamp?.ExcludedPlugins);

    /// <summary>The same writer for a lane that holds the epoch and the excluded roster as two carried values (the
    /// sweep results, which have both on the result) rather than as one stamp.</summary>
    static void WriteEpoch(Utf8JsonWriter w, string? epoch, IReadOnlyCollection<string>? excluded)
    {
        WriteNullable(w, "epoch", epoch);
        WriteOrderDegraded(w, excluded);
    }

    /// <summary>The marker on its own, for a document that states it at the ROOT rather than beside an epoch — the
    /// merged check, whose dialogue family carries no epoch to hang it off. Silent on a healthy order.</summary>
    static void WriteOrderDegraded(Utf8JsonWriter w, IReadOnlyCollection<string>? excluded)
    {
        if (excluded is not { Count: > 0 }) return;
        w.WriteBoolean("order_degraded", true);
        w.WriteString("order_degraded_note", OrderDegraded.Sentence(excluded));
    }

    /// <summary>A record's runtime address on the json lanes: <c>runtime_formid</c> always present (null when the
    /// order gives the record none), plus <c>runtime_formid_note</c> written ONLY when there is a reason to state —
    /// a consumer reading the form gets a token or a null, never a sentence where a FormID belongs.</summary>
    static void WriteRuntime(Utf8JsonWriter w, string? runtime, string? note)
    {
        WriteNullable(w, "runtime_formid", runtime);
        if (note is not null) w.WriteString("runtime_formid_note", note);
    }

    /// <summary>The array twin of <see cref="WriteNullable"/>, for a member whose null carries meaning: null says
    /// the value was NOT COMPUTED, an empty array says it was computed and came back empty. Writing <c>[]</c> for
    /// both leaves the consumer nothing to tell them apart.</summary>
    static void WriteNullableStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string>? items)
    {
        if (items is null) w.WriteNull(name); else WriteStringArray(w, name, items);
    }

    /// <summary>The ONE way a json document declares itself a refusal: <c>ok:false</c> followed by the message.
    /// Every whole-call refusal on the read surface writes its discriminant through here so the shape cannot be
    /// stated one way in one renderer and another in the next.
    ///
    /// <para><b>Document-level only.</b> A per-ROW <c>error</c> field — a malformed FormID in a batch, a seed that
    /// did not resolve — is NOT a refusal: the call succeeded and rendered a row that failed. Those sites keep a
    /// bare <c>error</c> and must never gain <c>ok</c>, or a consumer branching on the discriminant would read a
    /// served document as a refused one.</para>
    ///
    /// <para>Epoch is left to the call site: some refusals stamp the build they consulted, some state it as null,
    /// and pre-capture validation refusals omit it because they consulted no build. That three-way split is a
    /// live contract, so this helper writes only the discriminant and the message.</para></summary>
    internal static void WriteRefusal(Utf8JsonWriter w, string? error)
    {
        w.WriteBoolean("ok", false);
        // `error` is nullable because a caller can pass an optional DTO field straight in; that site's refusal
        // document carries a json null there.
        WriteNullable(w, "error", error);
    }

    // ---- housecarl_resolve ---------------------------------------------------------------------------
    /// <summary>Render the bulk name-resolution result as JSON: <c>{count, resolved:[…], rendered, truncated}</c> —
    /// one <c>{formid,type,editorid,name,winner}</c> row per resolvable input, or <c>{formid,error}</c> for a
    /// bad/absent one, so the batch survives. Over max_chars it drops trailing rows and flags <c>truncated</c>,
    /// keeping the document valid JSON with an exact <c>count</c>.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch)
        => RenderResolve(rows, maxChars, epoch, null, out _);

    /// <summary>Optional response-envelope pairs (form=, the resolved source arm, …) written as top-level string
    /// fields at the START of a json document, so a json consumer sees in-band the same call context the text
    /// header line states.
    /// Envelope keys must not collide with any renderer's own top-level keys (count/epoch/records/rendered/
    /// truncated/total/…): Utf8JsonWriter does not dedupe, so a collision emits a duplicate-key document. The
    /// current set is disjoint; keep it that way when adding pairs.</summary>
    static void WriteEnvelope(Utf8JsonWriter w, IReadOnlyList<KeyValuePair<string, string>>? envelope)
    {
        if (envelope is null) return;
        foreach (var kv in envelope) w.WriteString(kv.Key, kv.Value);
    }

    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows, int maxChars, OrderStamp epoch, SpillState? spill, out bool truncated,
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
            WriteEpoch(w, epoch);   // the ONE captured build the whole batch resolved against
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

    /// <summary>A whole-call refusal document: <c>{ok:false, error, epoch}</c>, for tool-layer refusals that have
    /// no outcome object to render. The epoch stamp rides when the refusal consulted a build.
    ///
    /// <para>On the read surface <c>ok</c> marks refusals ONLY — a served read document carries no <c>ok</c>, and
    /// its absence means the call was answered. Deliberate asymmetry with the write surface, which writes the flag
    /// on both outcomes.</para></summary>
    internal static string RenderError(string error, OrderStamp? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteRefusal(w, error);
            WriteEpoch(w, epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>Is the document already at its char ceiling? The writer BUFFERS, so <c>ms.Length</c> lags what has
    /// been written — every row loop that budgets by stream length must flush first, as this does.</summary>
    static bool Over(Utf8JsonWriter w, MemoryStream ms, int cap)
    {
        w.Flush();
        return ms.Length >= cap;
    }

    /// <summary>The post-write read-back block, one construction for all three write documents (apply / create /
    /// forward). <c>readback_source</c> names the WRITTEN FILE's content, or a dry run's in-memory would-be
    /// content — never load-order truth.
    /// <para><c>readback_full</c> describes THIS DOCUMENT: the json renders emit every field of every row, so a
    /// present read-back is always the full one, and it must not be made to carry the caller's ask (which the
    /// in-place lanes override). <c>readback_requested</c> is where the ask lives.</para>
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

    /// <summary>The json twin of <see cref="BatchRender.AppendLines"/>: a caveat list bounded by the SAME budget the
    /// row loop is bounded by. An unbounded list here is a list max_chars does not reach — one under= of thousands of
    /// selectors writes megabytes before the row loop takes its first budget reading, and the rows the caller asked
    /// about are then all cut.
    /// <para>The cut is a sibling <c>&lt;name&gt;_omitted</c> count, not a prose element inside the array — the
    /// capped-list-plus-count shape <see cref="Wire.ContestedHostsShown"/> already uses. A marker element would be
    /// handed to a consumer iterating the array as if it were one of the entries, and the array length would stop
    /// matching the count the accounting states.</para>
    /// <para>Returns how many entries were omitted, so the caller can say so at the document root.</para></summary>
    static int WriteCappedStringArray(Utf8JsonWriter w, MemoryStream ms, string name, IReadOnlyList<string> items,
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
    /// <summary>Serialize the fields array. Each leaf is <c>{path, value}</c> for a round-trippable leaf (value = the
    /// SAME wire token the text mode emits) or <c>{path, note}</c> for a no-value leaf; the display-only <c>display</c>
    /// (biped slots) and the resolve_names <c>link</c> sibling ride alongside, never in place of the token.
    /// A fat record is field-truncated the same way the text render caps field lines — a sentinel field names the
    /// cut and the array closes, so the document stays valid JSON and never sits silently over budget.</summary>
    /// <param name="annotated">The owned-child fields this outcome annotated, if any.</param>
    /// <param name="emitted">Collects the annotated paths this array ACTUALLY carried — the response-level clause is
    /// stated over these, so a field the truncation above dropped states nothing.</param>
    static void WriteFieldsArray(Utf8JsonWriter w, RecordFields r, MemoryStream ms, int cap,
                                 IReadOnlyDictionary<string, ChildUnion?>? annotated = null, ICollection<string>? emitted = null,
                                 LeverNames? levers = null)
    {
        var lv = levers ?? LeverNames.Legacy;
        w.WriteStartArray("fields");
        for (int i = 0; i < r.Fields.Count; i++)
        {
            w.Flush();
            if (ms.Length >= cap)
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
                // A folded row (the 'rows' form): the line's own text is prose, so the leaves it folded ride here
                // with their tokens, links and counts intact — a consumer reads the row, never parses it.
                w.WriteStartArray("cells");
                foreach (var c in cells) { w.WriteStartObject(); WriteLeaf(w, c); w.WriteEndObject(); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            if (annotated is not null && emitted is not null && annotated.ContainsKey(f.Path)) emitted.Add(f.Path);
        }
        w.WriteEndArray();
    }

    /// <summary>One leaf's members, into the object the caller opened: its path, its round-trip value or its
    /// no-value note, and the display-only annotations. A folded row writes no note — its text is prose and its
    /// leaves follow in <c>cells</c>.</summary>
    static void WriteLeaf(Utf8JsonWriter w, FieldValue f)
    {
        w.WriteString("path", f.Path);
        if (f.HasValue) w.WriteString("value", f.Token);   // round-trip parity: identical token to the text render
        else if (f.Cells is null) WriteNullable(w, "note", f.Note);
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
    }

    /// <summary>The additive union of a child-bearing field, as structure rather than only as the prose on
    /// <c>display</c> — the FormIDs are the answer to "what is in this cell", and a caller reads them back through
    /// the same <c>formids=</c> door it came in by. <c>members</c> is capped at
    /// <see cref="ChildUnionMemberCap"/> with <c>members_omitted</c> naming the rest, because a worldspace's union
    /// runs to tens of thousands and a field annotation is not a listing surface.</summary>
    static void WriteChildUnion(Utf8JsonWriter w, ChildUnion u, MemoryStream ms, int cap)
    {
        w.WriteStartObject("owned_child_union");
        w.WriteString("shape", u.Shape.ToString());
        w.WriteNumber("total", u.Total);
        w.WriteNumber("own", u.OwnCount);
        // Stated only where it changes what `own`/`total` mean against the field's `value`: on a nested field
        // (Worldspace.SubCells) the value counts the CONTAINERS and these count the records under them, so a
        // consumer comparing the two numbers without this key is comparing different units.
        if (u.Nested) w.WriteBoolean("nested", true);
        // A SINGULAR field's declarers override one record, so one of them IS live. A COLLECTION is additive —
        // every declarer's children are live — so naming one plugin there would be the #342 misreading this exists
        // to remove; it is named as what it is, the highest plugin that declares anything.
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
        // The member array is the one part of a field object that can run to kilobytes, so it is bounded by BOTH
        // the flat cap and what is left of max_chars: a field object that silently doubled the response would be
        // invisible to the `truncated` flag the auto-spill trigger reads. Whatever is not listed is counted.
        w.Flush();
        int room = (int)Math.Max(0, cap - ms.Length) / ChildUnionMemberBytes;
        int listed = Math.Min(u.Members.Count, Math.Min(ChildUnionMemberCap, room));
        w.WriteStartArray("members");
        for (int i = 0; i < listed; i++) w.WriteStringValue(u.Members[i].ToString());
        w.WriteEndArray();
        if (u.Members.Count > listed) w.WriteNumber("members_omitted", u.Members.Count - listed);
        w.WriteEndObject();
    }

    /// <summary>The budget one listed member costs — a FormKey string, its quotes, its comma and the writer's
    /// indentation. Deliberately generous: it decides how many members fit in what is LEFT of max_chars, and
    /// under-counting is what lets a field object overrun the cap.</summary>
    const int ChildUnionMemberBytes = 40;

    /// <summary>How many union members one field object lists. A cell's union is hundreds and a worldspace's is
    /// tens of thousands, so the array is a sample with its remainder counted, never the whole set.</summary>
    internal const int ChildUnionMemberCap = 100;

    /// <summary>Serialize a resolved record: identity + winner/override_depth/source + the fields array. Shared by
    /// read_record, batch_record_detail, and the cross_plugin_query detail path (one shape, no drift). <paramref
    /// name="matches"/> carries the multi-target references= un-merge when present.</summary>
    /// <param name="childFields">Collects the annotated field paths this record's array carried, for the
    /// response-level clause. Batch/query lanes pass ONE set across their rows and state the clause after the
    /// records array; the single read passes its own and states it here.</param>
    /// <param name="stateChildNote">Single-read lane only: this record object IS the response, so the clause
    /// belongs on it — written AFTER <c>fields</c> and only over what <c>fields</c> carried.</param>
    internal static void WriteReadRecord(Utf8JsonWriter w, ReadOutcome o, MemoryStream ms, int cap, string? matches = null,
                                         OrderStamp? epoch = null, ICollection<string>? childFields = null, bool stateChildNote = false,
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
        if (stateChildNote && childFields is IReadOnlyCollection<string> { Count: > 0 } stated) WriteOwnedChildNote(w, stated, o.OwnedChildUnioned);
        w.WriteEndObject();
    }

    // ---- housecarl_read_record ----------------------------------------------------------------------
    /// <summary>The owned-child clause on the json lane, written ONCE per response over the annotated fields the
    /// document actually CARRIES — the same source the text lane states, so the two transports cannot drift.
    /// Gated on the paths that were written, never on the prose.
    ///
    /// <para>json only ever states the cheap tier's clause: <c>conflict_tree=true</c> is refused in json mode, so
    /// the lane that has the bodies to name declarers does not exist here.</para></summary>
    static void WriteOwnedChildNote(Utf8JsonWriter w, IReadOnlyCollection<string> fields, bool unioned)
    {
        if (fields.Count > 0) w.WriteString("owned_child_note", ReadSentences.OwnedChildClause(fields, unioned));
    }

    // ---- housecarl_batch_record_detail --------------------------------------------------------------
    /// <summary>batch_record_detail as JSON: <c>{count, records:[…], rendered, truncated}</c>. A bad/absent formid is
    /// a per-item <c>{formid,error}</c> so the batch survives. Truncation drops trailing records and flags it — the
    /// document stays valid JSON, and count is exact.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars)
        => RenderBatch(outcomes, maxChars, null, out _);

    /// <summary><paramref name="levers"/>: the CALLER's lever vocabulary for the remedy sentences the row bodies
    /// compose — this renderer is shared by both tool generations. Omitted means the 1.x spelling.</summary>
    public static string RenderBatch(IReadOnlyList<ReadOutcome> outcomes, int maxChars, SpillState? spill, out bool truncated,
                                     IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null)
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
            WriteEpoch(w, outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false;
            var childFields = new SortedSet<string>(StringComparer.Ordinal);   // the annotated fields the rows RENDERED carried
            foreach (var o in outcomes)
            {
                if (manifestOnly) break;   // to_file: the rows are the FILE
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", o.FormKey.ToString()); w.WriteString("error", o.Error); w.WriteEndObject(); }
                else WriteReadRecord(w, o, ms, cap, childFields: childFields, levers: levers);
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            w.WriteBoolean("truncated", rowsTruncated);
            // Over the annotated fields this document actually carries — never the input list, and never a field
            // some row's own truncation dropped. A manifest-only (to_file) or truncated response carries none, and
            // states none.
            WriteOwnedChildNote(w, childFields, outcomes.Any(o => o.OwnedChildUnioned));
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records --------------------------------------------------------------------------

    /// <summary>records counts_only on the list lane: the census document, no rows.
    ///
    /// <para>The resolved count is named <c>resolved</c>, never <c>ok</c>: this is a SERVED answer, and <c>ok</c>
    /// is the refusal grammar's boolean discriminant, so an integer under that key would read as a refusal to one
    /// consumer and fail to parse for another. The TEXT twin keeps <c>ok=</c> — prose has no discriminant to
    /// collide with.</para></summary>
    public static string RenderCounts(IReadOnlyList<KeyValuePair<string, string>> envelope, int count, int ok, int errors, OrderStamp? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            w.WriteNumber("count", count);
            w.WriteNumber("resolved", ok);
            w.WriteNumber("errors", errors);
            WriteEpoch(w, epoch);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records counts_only for forms whose census has named counters (delta: differing/identical;
    /// tree: contested) — the envelope plus the counters, no rows.</summary>
    public static string RenderNamedCounts(IReadOnlyList<KeyValuePair<string, string>> envelope,
                                           IReadOnlyList<KeyValuePair<string, int>> counts, OrderStamp? epoch)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            foreach (var c in counts) w.WriteNumber(c.Key, c.Value);
            WriteEpoch(w, epoch);
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
            WriteEpoch(w, outcomes.FirstOrDefault(o => o.Stamp is not null)?.Stamp);
            w.WriteStartArray("records");
            int rendered = 0; bool rowsTruncated = false;   // summary rows carry no fields, so no owned-child annotation
            foreach (var o in outcomes)
            {
                if (manifestOnly) break;
                w.Flush();
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                w.WriteStartObject();
                w.WriteString("formid", o.FormKey.ToString());
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
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>records form=aggregate on the list lane: the count table over resolved rows, per-item errors
    /// counted apart so they are never silently dropped from a census.</summary>
    public static string RenderListAggregate(string groupBy, IReadOnlyList<KeyValuePair<string, int>> rows,
                                             int count, int errors, OrderStamp? epoch,
                                             IReadOnlyList<KeyValuePair<string, string>>? envelope = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);   // form + the resolved source arm + coverage qualifiers
            w.WriteString("group_by", groupBy);
            w.WriteNumber("count", count);
            if (errors > 0) w.WriteNumber("errors", errors);
            WriteEpoch(w, epoch);
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

    // ---- housecarl_records: the delta / tree comparison forms ---------------------------------------

    /// <summary>One delta row — shared verbatim by the json render and the artifact writer, so the two cannot
    /// drift. A per-item refusal is <c>{formid, error, stack_above?}</c>; a compared row carries both poles, the
    /// stack-above fact when the subject sits mid-stack, and the same delta strings the text render emits.</summary>
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
    /// The identical count only counts COMPLETE comparisons — a truncated deep read is neither, and its row says
    /// so via <c>complete:false</c>.</summary>
    public static string RenderDelta(IReadOnlyList<LoadOrderService.DeltaRow> rows, int maxChars, OrderStamp? epoch,
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
            // The census covers the COMPLETE list: rows may be a WINDOW, so the caller computes the counters over
            // everything and hands them in. Recomputing here would report the window as the world.
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);
            WriteEpoch(w, epoch);
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

    /// <summary>One tree row — the provider stack with per-node deltas against the row's reference pole. Shared
    /// by the json render and the artifact writer: having a row form is what makes trees spillable.</summary>
    /// <returns>true if any part of the row (child declarers or nodes) hit <paramref name="cap"/> and was cut short.
    /// The caller must merge this into the response's own <c>truncated</c> flag — a row-internal cut is otherwise
    /// invisible above this method.</returns>
    internal static bool WriteTreeRow(Utf8JsonWriter w, LoadOrderService.TreeRow row, MemoryStream ms, int cap,
                                      LeverNames? levers = null)
    {
        // The notice vocabulary comes from the carrier like every other remedy here, not a literal. Both callers
        // pass Records explicitly, so the Legacy default is unreached; it is kept so this seam obeys the same
        // "default is 1.x" rule as every other one.
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
        // The precise owned-child answer — the same per-field decision the text lane renders, from the same
        // TreeRow, so json and the spilled artifact carry it without a second composition to drift from.
        if (row.ChildDeclarers.Count > 0)
        {
            w.WriteStartArray("child_declarers");
            foreach (var cd in row.ChildDeclarers)
            {
                w.Flush();
                if (ms.Length >= cap)
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
            if (ms.Length >= cap)
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
        using var ms = new MemoryStream();
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
                if (ms.Length >= cap) { rowsTruncated = true; break; }
                if (WriteTreeRow(w, row, ms, cap, levers)) rowsTruncated = true;
                if (row.ChildDeclarers.Count > 0) anyDeclarers = true;
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered", rendered);
            // The framing line is stated once per response, and its reserve MUST be checked before `truncated` is
            // written: a Utf8JsonWriter cannot un-write a property once appended. The reserve therefore covers
            // every byte still to land — the note, `truncated` itself (written between this check and the note),
            // and the root close.
            w.Flush();
            bool leadOverCap = anyDeclarers
                && ms.Length + TruncatedPropertyReserve + DeclarersLeadReserve + Framing.RootClose >= cap;
            if (leadOverCap) rowsTruncated = true;
            w.WriteBoolean("truncated", rowsTruncated);
            truncated = rowsTruncated;
            if (anyDeclarers && !leadOverCap) w.WriteString("child_declarers_note", ReadSentences.DeclarersLead);
            if (spill is not null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_records: the chain form (walk=) --------------------------------------------------

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
    public static string RenderChain(IReadOnlyList<LoadOrderService.WalkSeedResult> rows, int maxChars, OrderStamp? epoch,
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
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
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
                                            IReadOnlyList<KeyValuePair<string, int>> counts, OrderStamp? epoch,
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
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
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

    // ---- housecarl_records: the info_order form -----------------------------------------------------

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
    public static string RenderInfoOrder(IReadOnlyList<LoadOrderService.InfoOrderRow> rows, int maxChars, OrderStamp? epoch,
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
            foreach (var (k, v) in counts.Select(c => (c.Key, c.Value))) w.WriteNumber(k, v);   // the counters cover the complete list, not this window
            WriteEpoch(w, epoch);
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

    // ---- housecarl_cross_plugin_query ---------------------------------------------------------------
    /// <summary>cross_plugin_query as JSON — three shapes matching the text render: group_by count table
    /// (<c>{group_by, total, groups:[…]}</c>), detail rows (full record objects with fields), or summary rows
    /// (<c>{formid,type,editorid,winner,override_depth}</c>). The accounting (total/capped/notes/truncated) rides
    /// in-band. The detail path threads resolve_names through the SAME ResolveRead the text render uses, so the two
    /// modes read one path.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth = 1)
        => RenderCrossQuery(svc, q, fields, maxChars, resolveNames, winnerFields, depth, null, out _);

    /// <summary>The spill-aware render: <paramref name="spill"/> rides IN the document, since a marker outside the
    /// json body would be invisible to a json consumer, and <paramref name="truncated"/> is the auto-spill trigger
    /// handed back to the tool layer.</summary>
    public static string RenderCrossQuery(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields, int depth,
                                          SpillState? spill, out bool truncated,
                                          IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            WriteEnvelope(w, envelope);
            // Post-capture refusals are epoch-stamped; pre-capture validation refusals carry null and render bare,
            // same as the text twin.
            if (q.Error is not null) { WriteRefusal(w, q.Error); if (q.Stamp is not null) WriteEpoch(w, q.Stamp); }
            else if (q.Groups is not null)                                   // group_by= → count table
            {
                WriteNullable(w, "group_by", q.GroupBy);
                w.WriteNumber("total", q.Total);
                WriteEpoch(w, q.Stamp);
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
                bool anyScoped = AnyScopedFieldRow(q, fields);   // the shared test: any row read from a scoped body
                string? p5 = anyScoped ? ScopedFieldsNote(winnerFields, q.WhereWinner, levers) : null;
                w.WriteNumber("total", q.Total);
                w.WriteBoolean("capped", q.Capped);
                WriteEpoch(w, q.Stamp);                         // offset= windows tile ONLY within one epoch
                if (q.Offset > 0) w.WriteNumber("offset", q.Offset);        // the window's start, in-band
                if (q.ScopeLabel is not null) w.WriteString("scope", q.ScopeLabel);
                WriteNotes(w, q, p5);
                var linkMemo = resolveNames && detail ? new LoadOrderService.LinkMemo() : null;
                w.WriteStartArray("matches");
                int rendered = 0; bool rowsTruncated = false;
                var childFields = new SortedSet<string>(StringComparer.Ordinal);   // the clause once, over the fields the rows carried
                bool childUnioned = false;   // which TIER those rows stated — the scan lanes annotate index-only
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
                        // Pinned to the scan's build — the document's epoch names ONE build.
                        var o = svc.ResolveReadOn(q, fk, winnerFields ? null : (q.Sources is { } src ? src[i] : null), fields, false, depth, resolveNames: resolveNames, linkMemo: linkMemo,
                                                  containerHint: (levers ?? LeverNames.Legacy).ContainerHint);   // a collapsed cell names the caller's own expansion knob
                        if (o.Error is not null) { w.WriteStartObject(); w.WriteString("formid", fk.ToString()); w.WriteString("error", o.Error); if (matches is not null) w.WriteString("matches", matches); w.WriteEndObject(); }
                        else { WriteReadRecord(w, o, ms, cap, matches, childFields: childFields, levers: levers); childUnioned |= o.OwnedChildUnioned; }
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
                WriteOwnedChildNote(w, childFields, childUnioned);
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>Is the scoped-vs-winner field-source note owed at all? True when a <c>fields=</c> detail render
    /// actually carried at least one row read from a scoped plugin's OWN body. Every render that states the note —
    /// the two json lanes, the inline text render, and the scan artifact's manifest — asks HERE, so the four agreeing
    /// is structural rather than four copies of one line that happen to match today. A <c>group_by=</c> outcome
    /// answers false outright: it renders counts, not rows, and fills no per-key sources.</summary>
    internal static bool AnyScopedFieldRow(CrossQueryOutcome q, IReadOnlyList<string>? fields)
        => q.Groups is null && fields is { Count: > 0 }
           && q.Sources is { } sources && sources.Take(q.Keys.Count).Any(s => s is not null);

    /// <summary>The scoped-vs-winner field-source note, one of a 4-way matrix over (winner_fields=, where_source=).
    /// <paramref name="whereWinner"/> is true when the MATCH decided on the live winner, and the note must then not
    /// claim the match was selected on the scoped body. Shared by the text, json, and dense renders so the note
    /// cannot drift across the three.</summary>
    internal static string ScopedFieldsNote(bool winnerFields, bool whereWinner, LeverNames? levers = null)
    {
        // Two of these four arms are REMEDIES that predict a later call, so they are only true for a caller that
        // HAS that lever; the other two are labels echoing what was passed. Both carry the caller's own token.
        // where_source= is spelled identically by both generations, so it stays a literal.
        var wf = (levers ?? LeverNames.Legacy).WinnerFields;
        if (whereWinner)
            return winnerFields
                ? $"the MATCH and the field values are both the load-order WINNER's (where_source=winner, {wf})."
                : $"the MATCH was selected on the load-order WINNER (where_source=winner), but the field values shown are each match's SCOPED plugin's OWN version — pass {wf} to display the winner too.";
        return winnerFields
            ? $"field values are the load-order WINNER's ({wf}); each match was SELECTED on its scoped plugin's body."
            : $"field values are each match's SCOPED plugin's OWN version, NOT the live load-order winner — pass {wf} for load-order truth.";
    }

    // ---- housecarl_cross_plugin_query format=dense --------------------------------------------------
    /// <summary>The columnar render: a <c>columns</c> array once, then ONE positional row array per match —
    /// <c>[formid, editorid, field values…]</c> under fields= (plus a <c>source</c> column under a plugins= scope,
    /// naming the body each row's values were read from), <c>[formid, type, editorid, winner, override_depth]</c>
    /// for summaries. Far cheaper in context than the per-field {path,value} envelopes format=json writes. Reads the
    /// SAME path as the other renders, and cells use the SAME display vocabulary as the text render: the round-trip
    /// token, else the parenthetical note (an absent field is "(absent)", never a silent hole), with
    /// Display/resolve_names annotations appended. The accounting rides in-band; a row whose read FAILS lands in a
    /// separate <c>errors</c> array, never a silently missing row. group_by= never reaches here — the tool renders
    /// its count table via <see cref="RenderCrossQuery"/>.</summary>
    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields)
        => RenderCrossQueryDense(svc, q, fields, maxChars, resolveNames, winnerFields, null, out _);

    public static string RenderCrossQueryDense(LoadOrderService svc, CrossQueryOutcome q, IReadOnlyList<string>? fields, int maxChars, bool resolveNames, bool winnerFields,
                                               SpillState? spill, out bool truncated,
                                               IReadOnlyList<KeyValuePair<string, string>>? envelope = null, LeverNames? levers = null)
    {
        truncated = false;
        int cap = Cap(maxChars);
        bool manifestOnly = spill?.ManifestOnly ?? false;
        using var ms = new MemoryStream();
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
                    foreach (var f in fields!) w.WriteStringValue(f);         // cells align positionally: ReadFields returns exactly one value per requested path, in order
                    // Under a plugins= scope each row's values are SOME scoped plugin's own body, and with 2+ scoped
                    // plugins the caller cannot reconstruct WHICH from the row alone — a defining esp's stale value
                    // then reads as live truth. Carry the provenance per row, exactly like text ("fields (from X):")
                    // and json ("source") do; the renders must not drift.
                    if (anyScoped) w.WriteStringValue("source");
                }
                else
                    foreach (var c in new[] { "formid", "runtime_formid", "type", "editorid", "winner", "override_depth" }) w.WriteStringValue(c);
                if (hasMatches) w.WriteStringValue("matches");
                w.WriteEndArray();

                var linkMemo = resolveNames && detail ? new LoadOrderService.LinkMemo() : null;
                List<(string Formid, string Error)>? errors = null;
                int rendered = 0; bool rowsTruncated = false;
                var childFields = new SortedSet<string>(StringComparer.Ordinal);   // the clause once, over the cells the rows carried
                bool childUnioned = false;   // which TIER those rows stated — the scan lanes annotate index-only
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
                                                  resolveNames: resolveNames, linkMemo: linkMemo, containerHint: (levers ?? LeverNames.Legacy).DenseContainerHint);   // dense refuses depth>1, so the hint names the format hop; pinned to the scan's build
                        if (o.Error is not null) { (errors ??= new()).Add((fk.ToString(), o.Error)); rendered++; continue; }
                        var r = o.Record!;
                        w.WriteStartArray();
                        w.WriteStringValue(r.FormKey);
                        WriteCell(w, RuntimeCell(o.RuntimeFormId, o.RuntimeFormIdNote));
                        WriteCell(w, r.EditorId);
                        foreach (var f in r.Fields)
                        {
                            WriteCell(w, DenseCell(f));
                            // Registered at EMISSION, like every other lane, so the clause is earned by what the
                            // document carries rather than by the outcome's intent.
                            if (o.OwnedChildFields?.ContainsKey(f.Path) == true) { childFields.Add(f.Path); childUnioned |= o.OwnedChildUnioned; }
                        }
                        if (anyScoped) WriteCell(w, o.SourcePlugin);          // the body this row's values were read from (winner_fields=true → the winner)
                        if (hasMatches) WriteCell(w, matches);
                        w.WriteEndArray();
                    }
                    else
                    {
                        var m = q.Prefilled is not null ? q.Prefilled[i] : svc.ResolveSummaryOn(q, fk);   // pinned to the scan's build
                        if (m.Error is not null) { (errors ??= new()).Add((m.FormKey.ToString(), m.Error)); rendered++; continue; }
                        w.WriteStartArray();
                        w.WriteStringValue(m.FormKey.ToString());
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
                w.WriteNumber("rendered", rendered);
                w.WriteBoolean("truncated", rowsTruncated);
                WriteOwnedChildNote(w, childFields, childUnioned);
                truncated = rowsTruncated;
            }
            if (spill is not null && q.Error is null) Artifacts.WriteSpillStateJson(w, spill);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>One dense cell: the round-trip token, else the leaf's parenthetical note ("(absent)", "(no field …)")
    /// so a no-value field is VISIBLE in its cell rather than a silent hole — with the Display and resolve_names
    /// annotations appended in the text render's exact vocabulary.</summary>
    static string? DenseCell(HousecarlCore.FieldValue f)
    {
        var s = f.HasValue ? f.Token : f.Note;
        if (f.Display is not null) s = $"{s}   ({f.Display})";
        if (f.Link is not null) s = $"{s}   ({Wire.LinkText(f.Link)})";
        return s;
    }

    /// <summary>The runtime-FormID cell in a dense row: the eight-hex form, else the reason in the parenthetical
    /// vocabulary every other dense cell uses for a value that is not a round-trip token, else null.</summary>
    static string? RuntimeCell(string? runtime, string? note) => runtime ?? (note is null ? null : $"({note})");

    static void WriteCell(Utf8JsonWriter w, string? v)
    {
        if (v is null) w.WriteNullValue(); else w.WriteStringValue(v);
    }

    internal static void WriteSummaryRow(Utf8JsonWriter w, RecordSummary m, string? matches)
    {
        w.WriteStartObject();
        w.WriteString("formid", m.FormKey.ToString());
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

    /// <summary>Accounting notes (where= predicate note, unscannable-record note) carried IN the JSON document, so
    /// json is never a silently degraded mode next to text. Omitted when there are none.</summary>
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
    /// <summary>The errors family's own head members, written into whatever object is open. Everything above the
    /// first thing a budget can refuse; the response's own title/scanned framing is the caller's, because the
    /// merged surface writes these into a per-family object where the single-family tool writes them flat.</summary>
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

        // The baseline split as DATA. base_masters names the set that was counted, because Mutagen's base set is
        // not the same as the engine's force-loaded implicit set (Creation Club plugins are in the latter, not the
        // former). base_masters_swept distinguishes "the baseline came back clean" from "no baseline was swept";
        // a consumer reading baseline_dangling==0 without it draws the wrong conclusion on a scoped sweep.
        if (didDangling)
        {
            w.WriteNumber("baseline_dangling", r.BaselineDangling);
            w.WriteNumber("non_baseline_dangling", r.TotalDangling - r.BaselineDangling);
        }
        else { w.WriteNull("baseline_dangling"); w.WriteNull("non_baseline_dangling"); }
        // base_masters_swept = the ones this sweep actually opened (empty = none); base_masters = what houseCARL
        // counts as baseline at all. Both, deliberately: the first is a fact about THIS sweep, the second a
        // definition a consumer needs in order to know that Creation Club plugins are not in it. The definition
        // is written even when the walk did not run, because it is true either way.
        WriteStringArray(w, "base_masters_swept", r.BaseMastersSwept ?? Array.Empty<string>());
        WriteStringArray(w, "base_masters", HousecarlCore.ErrorCheck.BaseMasters);
    }

    /// <summary>The errors family's BODY — everything a cap can refuse. It writes no excluded roster, no accounting
    /// and no boundary: those are the RESPONSE's, and a section writer that emitted them could not be called twice
    /// in one document.</summary>
    static void WriteErrorsSection(Utf8JsonWriter w, ErrorCheckResult r, BoundedBody body, int histogramLimit)
    {
        // The depths every unit in this section is measured at, anchored on the object this family is writing into
        // — 1 in the ancestor's root document, 3 in a merged one. Read off the writer rather than passed in, so it
        // cannot be told the wrong answer.
        var depths = new JsonUnitDepths(w.CurrentDepth);
        if (r.CountsOnly)
        {
            // Both axes handed over together, so both frames are reserved before either writes — the json twin
            // of the text lane's two-pass reserve.
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
                // A section is whole or absent, and its cost is MEASURED rather than assumed small: the plugin
                // object's fixed part carries a scan-error string and up to three unscannable-record exception
                // messages, all unbounded, so a bare size test before writing it can overshoot the cap by
                // thousands of chars. A Utf8JsonWriter can only be measured by writing, so the head is written
                // once into a scratch buffer at the same nesting depth.
                var head = p;
                bool opened = body.Emit(SweepSubject.PluginSections,
                                        PluginHeadCost(p, depths.PluginSections, sections > 0),
                                        () => WritePluginHead(w, head));
                if (!opened) break;
                sections++;
                int entries = 0;
                foreach (var d in p.Dangling)
                {
                    // Per ENTRY: one plugin's array can be thousands of rows, and a check taken only at the plugin
                    // boundary lets all of them out at once. Its cost is measured like everything else the
                    // allocation divides room by, so its demand and its emission test read the same number.
                    var entry = d;
                    if (!body.Emit(SweepSubject.DanglingEntries,
                                   DanglingEntryCost(d, depths.DanglingEntries, entries > 0),
                                   () => WriteDanglingEntry(w, entry), p.Plugin)) break;
                    entries++;
                }
                // The section's own closing brackets FINISH a unit already admitted, so they are charged to the
                // subject that opened it — PluginHeadCost measures them as part of the same unit. As an
                // unattributed fixed part they would scale with how many sections rendered, which the skeleton
                // pass cannot see.
                body.Complete(SweepSubject.PluginSections, () => { w.WriteEndArray(); w.WriteEndObject(); });
            }
            w.WriteEndArray();
        }
    }

    /// <summary>One plugin object's FIXED head, opening the <c>dangling</c> array its entries are written into. The
    /// ONE spelling of it: the cost helper writes this same method into its scratch document, so what was measured
    /// and what was written cannot be two different things.</summary>
    static void WritePluginHead(Utf8JsonWriter w, PluginErrors p)
    {
        w.WriteStartObject();
        w.WriteString("plugin", p.Plugin);
        WriteNullable(w, "scan_error", p.ScanError);
        WriteStringArray(w, "missing_masters", p.MissingMasters);
        // The install-vs-enable split as DATA: a json caller reading the union list alone could not pick the
        // remedy that would work. A SUBSET of the array above, so no count and no list moves; null where the
        // split was not made, matching the text lane's fallback to its union sentence.
        WriteNullableStringArray(w, "installed_but_inactive_masters", p.InstalledButInactiveMasters);
        // The unscannable fields sit before the dangling array so that once the ENTRY loop breaks
        // mid-plugin, all that follows is three fixed closing brackets.
        w.WriteNumber("unscannable_records", p.UnscannableRecords);
        WriteStringArray(w, "unscannable_samples", p.UnscannableSamples);
        w.WriteStartArray("dangling");
    }

    /// <summary>ONE dangling entry. Shared by the write and its measurement so the two cannot differ.</summary>
    static void WriteDanglingEntry(Utf8JsonWriter w, DanglingRef d)
    {
        w.WriteStartObject();
        w.WriteString("source", d.Source.ToString());
        w.WriteString("source_type", d.SourceType);
        WriteNullable(w, "source_editorid", d.SourceEditorId);
        w.WriteString("target", d.Target.ToString());
        w.WriteEndObject();
    }

    /// <summary>What ONE plugin object costs the document — its head AND the brackets that close it once its
    /// entries have been written, because those are one unit and one subject pays for both.</summary>
    static int PluginHeadCost(PluginErrors p, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, (w, size) =>
        {
            int before = size();
            WritePluginHead(w, p);
            int head = size() - before;
            // A non-empty array closes on a line of its own; an empty one closes with a single bracket. Which it
            // will be is known from the data here, so one throwaway entry — written between the two measured spans
            // and counted by neither — buys the right close rather than a hopeful one.
            if (p.Dangling.Count > 0) WriteDanglingEntry(w, p.Dangling[0]);
            before = size();
            w.WriteEndArray();
            w.WriteEndObject();
            return head + (size() - before);
        });

    /// <summary>What ONE dangling entry costs, at its own depth and sibling position.</summary>
    static int DanglingEntryCost(DanglingRef d, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteDanglingEntry(w, d));

    /// <summary>What THIS writer's own framing costs, measured against the writer rather than kept by hand: opening
    /// the root object, closing it, and the separator a property pays for not being the first one inside it.
    ///
    /// <para>Hand-kept constants get this wrong: an indented writer closes the root with a newline and a brace,
    /// and the newline is a CR/LF pair here, so the close costs three characters and not two. Every site that
    /// needs these numbers reads them from this one measurement, so they cannot drift apart.</para></summary>
    static readonly WriterFraming Framing = MeasureFraming();

    /// <summary>The three costs <see cref="MeasureFraming"/> reads off the writer, in bytes of the encoded
    /// document.</summary>
    readonly record struct WriterFraming(int Open, int RootClose, int Separator);

    /// <summary>Write two BYTE-IDENTICAL properties into a root object and take the deltas: what the first cost
    /// above the bare <c>{</c> is one property, what the second cost above the first is that same property PLUS the
    /// separator, and what the finished document holds above the still-open one is the root close.</summary>
    static WriterFraming MeasureFraming()
    {
        using var ms = new MemoryStream();
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
        return new WriterFraming(Open: opened, RootClose: (int)ms.Length - second,
                                 Separator: (second - first) - (first - opened));
    }

    /// <summary>What the overrun notice costs the document, encoded as the response will encode it — json escapes
    /// what a raw char count would miss, and the notice's own length is part of the length it reports. The scratch
    /// document is a root object of its own, so what the REAL document pays is the scratch less the wrapper it
    /// already has, plus the separator the notice owes for following the properties written before it.</summary>
    static int OverrunNoticeCost(string notice)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("max_chars_overrun", notice);
            w.WriteEndObject();
        }
        return (int)ms.Length - (Framing.Open + Framing.RootClose) + Framing.Separator;
    }

    /// <summary>The document's size so far, without flushing: committed bytes plus what the writer still holds.
    /// Same number a flush per entry would give, at a price a per-entry budget test can afford.</summary>
    static int Size(Utf8JsonWriter w, MemoryStream ms) => (int)(ms.Length + w.BytesPending);

    /// <summary>What <c>child_declarers_note</c> costs the document — <see cref="ReadSentences.DeclarersLead"/>'s
    /// own json-escaped bytes plus the property's separator, measured the same way <see cref="Framing"/> measures
    /// the writer's own punctuation rather than hand-counted. Reserved by <see cref="RenderTree"/>, and measured
    /// into an object that already has a property so it pays the same separator the real write does.</summary>
    static readonly int DeclarersLeadReserve =
        MeasureRootProperty(w => w.WriteString("child_declarers_note", ReadSentences.DeclarersLead));

    /// <summary>What the <c>truncated</c> boolean costs the document, measured the same way. <see cref="RenderTree"/>
    /// writes it BETWEEN the reserve check and <c>child_declarers_note</c>, so its bytes are part of what the note
    /// has to fit behind. Measured against <c>false</c>, the wider of the two spellings, so the reserve is never
    /// short.</summary>
    static readonly int TruncatedPropertyReserve = MeasureRootProperty(w => w.WriteBoolean("truncated", false));

    /// <summary>One property's cost in the root object, from the writer itself rather than a hand count — the
    /// measurement both reserves above share, so a reserve cannot drift from the write it stands for. Written into
    /// an object that already has a property, because the real writes do, so the measurement pays the same
    /// separator.</summary>
    static int MeasureRootProperty(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
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

    /// <summary>The document written SO FAR, as text, without closing it — for the one caller that has to READ what
    /// it has produced rather than measure it: the overrun remedy counts how many times this response prints back
    /// the cap it was given. Flushes first, so nothing pending is missed.</summary>
    static string SoFar(Utf8JsonWriter w, MemoryStream ms)
    {
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Where each json unit sits, from one anchor: the depth of the object a family writes its own members
    /// into (1 in an ancestor's root document, 3 in a merged one — root, <c>families</c>, the family).
    ///
    /// <para>Depth is load-bearing, not cosmetic: the response is written INDENTED, so every nesting level costs
    /// two spaces on every line of every unit inside it, and a cost measured shallower than the unit is written
    /// under-measures by more the bigger the unit gets — which puts the response over its own cap. The offsets are
    /// stated ONCE here and read by both the demand pass and the write, so the two cannot drift.</para></summary>
    /// <param name="Section">the depth of the object holding a family's members.</param>
    internal readonly record struct JsonUnitDepths(int Section)
    {
        /// <summary>Elements of <c>plugins</c>.</summary>
        internal int PluginSections => Section + 1;
        /// <summary>Elements of a plugin object's <c>dangling</c>.</summary>
        internal int DanglingEntries => Section + 3;
        /// <summary>Elements of <c>records</c>.</summary>
        internal int ScriptRecords => Section + 1;
        /// <summary>Elements of a histogram axis's <c>rows</c>, and of the two wrapped honesty layers'
        /// rows — <c>unread.rows</c> and <c>scan_errors.rows</c>.</summary>
        internal int HistogramRows => Section + 2;
        /// <summary>A histogram axis OBJECT, written as a member of the family object itself.</summary>
        internal int AxisFrame => Section;
        /// <summary>Elements of <c>seeds</c> and of <c>seeds_unreachable</c>.</summary>
        internal int DialogueSeeds => Section + 1;
        /// <summary>Elements of a seed's <c>topics</c>.</summary>
        internal int DialogueTopics => Section + 3;
    }

    /// <summary>What ONE UNIT costs the finished document, measured where it will land.
    ///
    /// <para>A <see cref="Utf8JsonWriter"/> cannot be asked what something would cost without writing it, so the
    /// unit is written into a throwaway document positioned exactly as the live one will be: the same
    /// <see cref="Opts"/>, the same nesting <paramref name="depth"/> (indentation is per line and per level), and
    /// the same sibling position — an array element after another pays a one-character separator the first element
    /// does not.</para>
    ///
    /// <para>Returns a DELTA — what the unit itself appended — not the scratch document's length. The allocation
    /// divides room by this number, so an over-count is room handed to a subject that will not spend it.</para></summary>
    /// <param name="depth">the live writer's <c>CurrentDepth</c> where the unit is written — for an array element,
    /// the depth of the array.</param>
    /// <param name="subsequent">is something already in that array? A later element pays a separator.</param>
    /// <param name="measure">writes the unit and returns its cost, reading the scratch length through the delegate
    /// it is handed — so a unit written in TWO spans, an opening and the brackets that close it after its children,
    /// can measure both and leave the children it wrote in between uncounted.</param>
    internal static int MeasureUnit(int depth, bool subsequent, Func<Utf8JsonWriter, Func<int>, int> measure)
    {
        using var ms = new MemoryStream();
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

    /// <summary>The same measurement for a NAMED MEMBER rather than an array element — a histogram axis's own
    /// object, which is a property of the family object and not a row of anything.</summary>
    /// <param name="depth">the depth of the object the member is written into.</param>
    static int MeasureMember(int depth, Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms, Opts);
        w.WriteStartObject();
        for (int i = 1; i < depth; i++) w.WriteStartObject("n");
        w.WriteString("before", "");   // the member is never the first thing in a family object, so it pays a separator
        int before = Size(w, ms);
        write(w);
        return Size(w, ms) - before;
    }

    /// <summary>A family's stamp and coverage as data: <c>epoch_covers_all_inputs</c> is false when off-order files
    /// were swept beside the index, since their content is outside the fingerprint and <c>off_order_scanned</c> names
    /// them — or when the family also reports verdicts read off another substrate, which <paramref name="uncovered"/>
    /// names in <c>epoch_uncovered</c>. Every family that stamps writes it through here.
    /// Success path only; a refusal swept nothing and carries the bare stamp.</summary>
    /// <param name="excludedCount">how many plugins the build this family read had lost to a load failure — a count,
    /// because a family whose own result carries no roster still reports the same fact off the response's stamp.</param>
    internal static void WriteSweepEpoch(Utf8JsonWriter w, string? epoch, int excludedCount,
                                         IReadOnlyList<string>? offOrderScanned,
                                         IReadOnlyList<string>? uncovered = null)
    {
        if (epoch is null) return;
        WriteNullable(w, "epoch", epoch);
        // The flag and the count, not the sentence: a family head only exists inside the merged check document,
        // whose ROOT states the sentence once for the whole response over the same build (a call whose families
        // read a different one refuses instead). Telling it again per family would put the same ~250 characters in
        // one document three times, beside a roster that already names those plugins WITH their reasons — and the
        // fixed part comes out of the budget the findings are listed from. The count is what the family's TEXT
        // head says beside epoch=, so the two transports state the same thing here.
        if (excludedCount > 0)
        {
            w.WriteBoolean("order_degraded", true);
            w.WriteNumber("order_degraded_plugins", excludedCount);
        }
        w.WriteBoolean("epoch_covers_all_inputs", offOrderScanned is not { Count: > 0 } && uncovered is not { Count: > 0 });
        // Named only where there are classes to name, so the key is never a caveat over a stamp that covers everything.
        if (uncovered is { Count: > 0 }) WriteStringArray(w, "epoch_uncovered", uncovered);
    }

    /// <summary>A swept family's off-order roster AND the coverage caveat that makes it readable — the same tail
    /// the text render prints in brackets. The roster alone leaves a json consumer with a file name and, on the
    /// scripts family, an unverifiable count with nothing tying the two together; null when nothing was swept
    /// off-order, so the key is never a caveat over a lane that did not run.</summary>
    static void WriteOffOrder(Utf8JsonWriter w, IReadOnlyList<string>? offOrderScanned, string coverage)
    {
        WriteStringArray(w, "off_order_scanned", offOrderScanned ?? Array.Empty<string>());
        WriteNullable(w, "off_order_coverage", offOrderScanned is { Count: > 0 } ? coverage : null);
    }

    // ---- housecarl_check — the merged, multi-family document ----------------------------------------
    /// <summary>The merged sweep as json: the scope facts flat at the top, then a <c>families</c> object keyed by
    /// family token, each carrying exactly what that family's single-family tool writes as a whole document — its
    /// head, its body, its own accounting and its own boundary. A single-family call is therefore not shaped
    /// differently from a multi-family one, so a family can be added without changing any consumer's parse.
    ///
    /// <para>The excluded-plugin roster and the overrun notice are RESPONSE-level and written once: the first is a
    /// fact about the scope every family shares, the second a fact about the document as a whole.</para></summary>
    public static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit = 1000)
        => RenderCheck(s, maxChars, histogramLimit, out _);

    /// <summary>The same render, handing back the allocation it built, for the tests. An internal seam: the public
    /// render is the one every caller uses.</summary>
    internal static string RenderCheck(CheckSweep s, int maxChars, int histogramLimit, out BoundedBody? measured)
    {
        measured = null;
        // WHAT THIS RESPONSE ACTUALLY DID, composed ONCE and handed to everything below — see the text lane.
        var o = CheckOutcome.For(s);
        int cap = Cap(maxChars);
        var sections = o.Sections;
        var accts = o.Accountings(cap);
        // One accounting + boundary reserve per family, and ONE entry slack for the response: the body stops the
        // moment a unit crosses the budget, so at most one unit can land over however many families rendered.
        int reserve = CheckAccounting.JsonEntrySlack;
        foreach (var a in accts) reserve += a.JsonAccountingReserve;
        int budget = Math.Max(0, cap - reserve);

        if (o.Error is not null)
        {
            using var ems = new MemoryStream();
            using (var ew = new Utf8JsonWriter(ems, Opts))
            {
                ew.WriteStartObject();
                WriteRefusal(ew, o.Error);
                WriteEpoch(ew, o.Epoch, o.OrderExcluded);
                ew.WriteEndObject();
                ew.Flush();
            }
            return Finish(ems);
        }

        // A family's members are written into the family object, which sits under `families`, which sits in the
        // root — so a unit's depth is anchored two levels below the root object this render opens. The section
        // writers read the same anchor off the live writer.
        var depths = new JsonUnitDepths(FamilySectionDepth);
        // WHAT EACH SUBJECT WANTS, measured before anything is written, so the allocation can water-fill
        // over it rather than discover shortfalls at render time (SweepDemand, BodyAllocation).
        var demand = SweepDemand.ForJson(o, budget, histogramLimit, depths);
        // AND WHAT THE DOCUMENT OWES WHATEVER THE BUDGET SAYS: composed with no units in it and measured, never
        // assembled from a roster of write sites. See the text lane for why the row budget has to exclude the
        // whole of it.
        int fixedPart;
        {
            using var sms = new MemoryStream();
            var skeletonAccts = o.Accountings(cap);
            BoundedBody skeletonBody;
            using (var sw = new Utf8JsonWriter(sms, Opts))
            {
                skeletonBody = BoundedBody.Skeleton(skeletonAccts, () => Size(sw, sms));
                sw.WriteStartObject();
                Compose(sw, o, sections, skeletonAccts, skeletonBody, histogramLimit);
                sw.WriteEndObject();
            }
            fixedPart = (int)sms.Length - skeletonBody.ReservedWritten - skeletonBody.BodyTotal;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            var body = BoundedBody.ForFamilies(accts, budget, () => Size(w, ms), o.Plan(),
                                               demand.Demand, demand.Reserved + fixedPart, o.ResponseSubjects,
                                               demand.Reserved);
            measured = body;
            Compose(w, o, sections, accts, body, histogramLimit);

            int closed = Size(w, ms) + Framing.RootClose;
            int needed = body.FixedPart(closed);
            var overrun = accts.Count > 0 ? accts[0] : null;
            // How many times this document prints the cap back, COUNTED in the document itself rather than taken
            // from how many families it has: the remedy has to name a cap that already covers what those numbers
            // gain when they widen. Read here, before the notice is written, because a site inside the notice is
            // one the raise is paying to remove.
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

    /// <summary>The depth a family writes its own members at: the root object, <c>families</c>, the family. Named
    /// once because the demand pass has no writer to read it off — the section writers take theirs from the live
    /// <c>CurrentDepth</c>, and the two must agree.</summary>
    internal const int FamilySectionDepth = 3;

    /// <summary>The whole merged document bar the root braces and the overrun notice, composed through one
    /// <paramref name="body"/>. Run twice per render: once with a <see cref="BoundedBody.Skeleton"/>, whose refusal
    /// of every unit leaves exactly the fixed part behind to be measured, and once for real.</summary>
    static void Compose(Utf8JsonWriter w, CheckOutcome o, IReadOnlyList<SweepFamily> sections,
                        IReadOnlyList<CheckAccounting> accts, BoundedBody body, int histogramLimit)
    {
        var s = o.Sweep;
        // The scope facts, as data and as the sentence; the sentence is the same string the text lane prints.
        // THREE LISTS, because a family can be in three states and two lists could only say two of them.
        // `families_ran` is what ANSWERED, taken off the outcome rather than the selection — filled from the
        // selection it would name a family whose whole section was a refusal, an undetectable false negative for
        // a consumer reading it as "these have findings". `families_refused` is the middle state, with the ground
        // beside each; the last list is what was never asked.
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
        // A response-level fact, like the roster below: the order this whole call answered from was short of
        // plugins. Stated here rather than per family so a dialogue-only check says it too (#353).
        WriteOrderDegraded(w, o.OrderExcluded);

        // Above `families` for the reason the text lane states: an accounting reports what has been emitted,
        // and every family's accounting is written inside the loop below.
        WriteExcluded(w, o.ExcludedPlugins, body);

        w.WriteStartObject("families");
        for (int i = 0; i < sections.Count; i++)
        {
            var f = sections[i];
            w.WriteStartObject(SweepFamilySelection.Token(f));
            // A family that refused says so HERE, rather than the refusal becoming the whole call's error.
            if (o.Refusal(f) is { } refusal)
            {
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
            else
            {
                DialogueSweepRender.WriteHead(w, o);
                DialogueSweepRender.WriteSection(w, o, body);
            }
            // This family's accounting and boundary, out of the room held for them rather than out of the rows
            // the next family still has to render.
            var acct = accts[i];
            body.Reserved(() => { acct.WriteJson(w); w.WriteString("boundary", acct.Boundary); });
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    // ---- housecarl_validate_scripts -----------------------------------------------------------------
    /// <summary>The scripts family's own head members, written into whatever object is open. A finding CLASS the
    /// caller excluded is emitted as <c>null</c>, NOT as 0 — the json counterpart of the text render's NOT CHECKED —
    /// so a class nobody looked for cannot be parsed as one that came back clean. <c>unverifiable</c> is never
    /// null: it cannot be filtered out.</summary>
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
        // The property filter rides as DATA, not just prose in filter_note: `unbound` / `bound_but_null` count only
        // matching findings, while `records_with_scripts` and `unverifiable` are plugin-wide regardless of it, and
        // a consumer has to be able to read that asymmetry off the document.
        WriteNullable(w, "property_contains", r.PropertyContains);
        WriteNullable(w, "filter_note", r.FilterNote);
        WriteOffOrder(w, r.OffOrderScanned, ReadSentences.SweepOffOrderScriptsCoverage);
        w.WriteNumber("unverifiable_collapsed", r.UnverifiableCollapsed);
        w.WriteBoolean("read_incomplete", r.ReadIncomplete);
        w.WriteBoolean("counts_only", r.CountsOnly);
    }

    /// <summary>The scripts family's BODY — everything a cap can refuse. It writes no excluded roster, no accounting
    /// and no boundary: those are the RESPONSE's, and they are also where <c>capped</c>, <c>rendered</c> and
    /// <c>truncated</c> now come from, because <see cref="CheckAccounting.JsonAccountingReserve"/> measures that writer and a
    /// field written anywhere else is a field outside the reserve.</summary>
    static void WriteScriptsSection(Utf8JsonWriter w, ScriptCheckResult r, BoundedBody body, int histogramLimit)
    {
        var depths = new JsonUnitDepths(w.CurrentDepth);
        if (r.CountsOnly)
        {
            WriteHistograms(w, body, histogramLimit, depths,
                ("unbound_by_property", SweepSubject.HistogramByProperty, r.Histogram));
            // The honesty layer, on its own subject and its own bound — a silently short list of what could NOT be
            // read is the boundary of the answer going missing, not a finding inside it.
            //
            // Wrapped in {total, rows, rendered, truncated}, the shape housecarl_validate_scripts publishes here and
            // the shape the `unread` layer uses one family over. A bare array that was cut cannot say so, and a
            // consumer iterating it believes it holds the complete set of what went unchecked.
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
            // A record object is whole or absent, and its cost is MEASURED rather than assumed small: a record
            // carries an unbounded EditorID, an unbounded set of property names and an unbounded "could not verify"
            // reason per script, so a post-check alone would let one whole record land past the budget.
            var row = rec;
            if (!body.Emit(SweepSubject.ScriptRecords,
                           ScriptRecordCost(row, depths.ScriptRecords, records > 0),
                           () => WriteScriptRecord(w, row))) break;
            records++;
        }
        w.WriteEndArray();
    }

    /// <summary>ONE <c>scan_errors</c> row — the counts_only honesty layer's unit, shared by the write and the
    /// measurement.</summary>
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
        w.WriteString("formid", rec.Record.ToString());
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

    /// <summary>What ONE record object costs, encoded exactly as the response will encode it — at its own nesting
    /// depth and sibling position, and returned as the DELTA the unit appended rather than a scratch document's
    /// whole length (see <see cref="MeasureUnit"/>).</summary>
    static int ScriptRecordCost(RecordScriptFindings rec, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteScriptRecord(w, rec));

    /// <summary>What one <c>scan_errors</c> row costs, same construction, same depth.</summary>
    static int ScanErrorRowCost(RecordScriptFindings rec, int depth, bool subsequent)
        => MeasureUnit(depth, subsequent, w => WriteScanErrorRow(w, rec));
    // ---- shared sweep writers ----------------------------------------------------------------------
    /// <summary>Reserve every axis's OBJECT FRAME out of the body budget, then write the axes. The frame — the
    /// <c>distinct</c>/<c>rendered</c>/<c>cut_by</c> members around the rows — is this transport's whole disclosure
    /// that the axis exists and how much of it is here, so it is written unconditionally and its room comes out of
    /// <c>max_chars</c> first, exactly as the text lane reserves its closing line.
    ///
    /// <para>The reserve covers the frame's leading members as well as its trailing ones, and the leading ones are
    /// already written by the time this axis's own rows are tested, so an axis over-reserves against itself by that
    /// much. Over-reserving costs characters; under-reserving is what this exists to stop, so the simpler
    /// arithmetic is deliberately taken in the safe direction.</para></summary>
    static void WriteHistograms(Utf8JsonWriter w, BoundedBody? body, int rowLimit, JsonUnitDepths depths,
                                params (string Name, SweepSubject Subject, IReadOnlyList<SweepCount>? Rows)[] axes)
    {
        if (body is not null)
            foreach (var a in axes)
                if (a.Rows is not null)
                    body.Reserve(a.Subject, HistogramFrameCost(a.Name, a.Rows.Count, depths.AxisFrame));
        foreach (var a in axes) WriteHistogram(w, a.Name, a.Subject, a.Rows, rowLimit, body, depths);
    }
    /// <summary>What ONE axis object costs with no rows in it, encoded exactly as the response will encode it — the
    /// same construction as <see cref="PluginHeadCost"/>, at this object's own nesting depth, with every member at
    /// its widest: <c>rendered</c> can print as many digits as <c>distinct</c>, and <c>cut_by</c> carries the longer
    /// of the two knob names.</summary>
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

    /// <summary>A counts_only histogram: <c>{distinct, rows:[{key,count}], rendered, cut_by}</c>. Absent when the mode
    /// was not requested; PRESENT with an empty <c>rows</c> when the sweep genuinely found nothing — the two must not
    /// look alike.</summary>
    /// <param name="body">the ONE bounded emission path, or null for validate_scripts, which passes no budget —
    /// its response layer is not this branch's.</param>
    /// <param name="subject">this axis's OWN emission subject — two axes sharing one would let the first to stop
    /// stop the second.</param>
    static void WriteHistogram(Utf8JsonWriter w, string name, SweepSubject subject, IReadOnlyList<SweepCount>? rows,
                               int rowLimit, BoundedBody? body, JsonUnitDepths depths)
    {
        if (rows is null) { body?.Release(subject); return; }
        // The object's own fixed members do not grow with the findings, so they are part of the fixed part; the ROWS
        // are what the budget gates, and `rendered` is written from what the gate let through. The frame goes
        // through the body so its cost is MEASURED into the fixed part rather than inferred from its reserve.
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
            // The row's cost is MEASURED like every other unit the allocation divides room by, so its demand and
            // its emission test read the same number.
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
            // WHICH knob stopped it, from the same computation the text lane renders as a sentence: distinct vs
            // rendered says an axis is short but not which parameter a consumer would have to change. Null where
            // the axis is whole, so "complete" is read rather than inferred from two numbers.
            if (HistogramCut.For(rows.Count, rendered, cutByBudget) is { } cut) w.WriteString("cut_by", cut.Knob);
            else w.WriteNull("cut_by");
            w.WriteEndObject();
        });
        // The frame is written and charged, so whatever room is still held for it goes back — holding it any longer
        // would charge the unread and excluded rows below for a disclosure already on the document.
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

    /// <summary>Write part of an axis object's own FRAME: unconditional, never refused, and measured into the
    /// response's fixed part when there is a body to measure it with. validate_scripts passes none — its response
    /// layer is not this branch's — and then this is a plain write.</summary>
    static void Unconditional(BoundedBody? body, SweepSubject subject, Action commit)
    {
        if (body is null) commit();
        else body.Fixed(subject, commit);
    }

    /// <summary>Under counts_only, check_errors' reports carry the honesty layer only — plugins whose records could
    /// not be read. Emitted so a counts-only answer still names what it could not check.
    /// <para>Wrapped in <c>{total, rows, rendered, truncated}</c> rather than a bare array, because a budget cut
    /// that drops trailing rows with no flag leaves a consumer iterating the array believing it holds the complete
    /// set of what went unchecked.</para></summary>
    static void WriteUnreadPlugins(Utf8JsonWriter w, IReadOnlyList<PluginErrors> reports, BoundedBody body,
                                   JsonUnitDepths depths)
    {
        w.WriteStartObject("unread");
        w.WriteNumber("total", reports.Count);
        w.WriteStartArray("rows");
        int rendered = 0;
        foreach (var p in reports)
        {
            // The exact sibling of the plugin head: the row carries a scan error and its unscannable samples, all
            // unbounded, so its cost is measured rather than left to a post-check.
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

    /// <summary>The excluded-plugin roster. <paramref name="body"/> is the bounded emission path; null is
    /// validate_scripts, which passes no budget here — its response layer is not this branch's.</summary>
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
            // Measured rather than left to a post-check, for the same reason as the other units: `reason` is a
            // Mutagen parse-failure message with no length of its own, so a post-check lets a whole row land over
            // budget.
            if (!body.Emit(SweepSubject.ExcludedRows, ExcludedRowCost(row, depth, rendered > 0), Write)) break;
            rendered++;
        }
        w.WriteEndArray();
    }

    /// <summary>What one roster row costs, for the demand pass. The roster is a member of the ROOT object in every
    /// render that has one, so its depth is the same constant everywhere — unlike a family's units, which sit two
    /// levels deeper in a merged document than in a single-family one.</summary>
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
    /// <summary>The machine-readable twin of <see cref="WriteTools.Render"/>: ONE write outcome, the SAME data the
    /// text render states. Everything the text lane treats as prose is a typed field here — the lane the call named,
    /// whether it was a dry run, the epoch of the build the winners resolved from, per-op results, and the read-back.
    /// A REFUSAL is a document too (<c>ok:false</c> with the reason), not an empty body: a json caller must never
    /// have to parse "error: …" out of a string to learn the call failed. The first-touch in-place consent prompt is
    /// its own flag — a required confirmation, not a failure. Truncation drops trailing ROWS and says so, so the
    /// document is always valid JSON rather than a string cut mid-token.
    /// <para><paramref name="lane"/> is passed in, NOT derived from the outcome: <c>Fail</c> and <c>NeedsAck</c>
    /// construct their outcome with <c>InPlace</c>/<c>Extended</c> at their defaults, so deriving it from those
    /// flags would report "patch" for a refusal on an <c>into=</c> call and for the first-touch in-place consent
    /// prompt. The tool layer knows which lane the call named.</para></summary>
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
            WriteEpoch(w, o.Stamp);
            if (!o.Success)
            {
                // NeedsAcknowledge carries its prompt in Error — labelled as a prompt, never as an error string.
                WriteNullable(w, o.NeedsAcknowledge ? "confirmation" : "error", o.Error);
                w.WriteEndObject();
                // Flush BEFORE reading the stream: this return is INSIDE the writer's using-block, so without it the
                // buffered document is still unwritten and the caller gets an EMPTY string. The success path below
                // returns after disposal.
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
                // What the write DID that the file cannot say afterwards — today only the duplicate Add (the list
                // already carried this element). Its own key, not folded into `landed`, which is compared against
                // `landed_on_disk`.
                WriteNullable(w, "apply_note", op.ApplyNote);
                // The twin of the text render's file-vs-memory split, and it REPORTS rather than judges. `landed` is
                // the applied edit's own read (in memory, before the serialize); `landed_on_disk` is the same
                // descriptor re-derived from the WRITTEN FILE, null when the file could not answer for this op.
                WriteNullable(w, "landed_on_disk", op.LandedOnDisk);
                // WHERE the clause came from, as a word rather than a verdict:
                //   "written_file"  the file answered for this op — `landed_on_disk` is its reading
                //   "superseded"    a later op in this call wrote the same field, so the file's final state is that
                //                   op's result and cannot speak for this one
                //   "no_answer"     the file was re-opened and did not yield this op's leaf (or the read failed)
                //   "not_checked"   this op was never asked — a lane that runs no per-op file check (patch, dry run),
                //                   or an op appended after the resolved edits (the SNAM topic-marker sync)
                // Deliberately NOT a judgement about whether the write "landed": a real difference cannot be told
                // reliably from a representational one (a byte-quantised Percent, an overlay's type name), and the
                // attempt tells callers to re-issue writes that did land. Both readings are here; the caller decides.
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
            // Lane-aware, shared with forward: this document budgets the `ops` array, and a re-issue to widen it is
            // safe on into=/dry-run but cuts a second patch on the default lane and re-serializes the caller's own
            // file on in_place.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "applied")} — "
                    + WriteTools.ApplyAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_create ---------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderCreate"/> — the SAME data the text render
    /// states, on <see cref="RenderPatchOutcome"/>'s contract: a refusal is a document (<c>ok:false</c> with the
    /// reason), the first-touch consent prompt is its own flag rather than an error, the epoch rides on every
    /// response, and truncation drops trailing ROWS so the document stays valid JSON.
    /// <para>The three post-write reports ride as data, not prose: a silent line, an inert result script and an
    /// empty cell are hazards a json consumer has to be able to see.</para></summary>
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

            // Hoisted ABOVE the budgeted `created` array: a statement that this artifact will out-rank a mod on a
            // parent record it only meant to host a child in must survive a max_chars cut. One entry per distinct
            // contested parent; empty when every host was uncontested. Bounded on the SAME constant as the text
            // twin, because hoisting an unbounded block above the budget only moves the overflow.
            // `total_contested_parent_hosts` carries the full distinct count regardless, so the cut is stated;
            // each host past the cap is still named on its own record's `parent_host`.
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
                // The parent override this nested create hosted the child in, and whose version was copied.
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
                    WriteNullable(w, "apply_note", op.ApplyNote);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                rendered++;
            }
            w.WriteEndArray();
            w.WriteNumber("rendered_created", rendered);

            // The three post-write reports are INSIDE the budget, like the text twin's: unguarded they take the
            // document past max_chars and still close it with truncated:false.
            WriteVoiceReport(w, o.Voice, ms, cap, ref truncated);
            WriteScriptBindingReport(w, o.ScriptBinding, ms, cap, ref truncated);
            WriteCellShellReport(w, o.CellShell, ms, cap, ref truncated);

            if (o.ReadBack is { } rb) WriteReadbackBlock(w, ms, cap, rb, false, readback, ref truncated);

            WriteNullable(w, "note", o.Note);
            w.WriteBoolean("truncated", truncated);
            // NOT the sibling renders' "raise max_chars to see the rest". That remedy is safe on remove/forward/
            // apply — a repeated remove is refused, a repeated forward re-copies identical bodies — but a repeated
            // CREATE allocates the records again.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; "
                    + WriteSentences.CreateRowsCutRemedy(WriteTools.ReadBackCall(o, Path.GetFileName(o.OutputPath))) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_write_seq ------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="SeqTools.Render"/>. Three facts the text render states in
    /// prose are typed here: <c>written:false</c> with <c>quest_count:0</c> is the "no SGE quests, so no .seq is
    /// needed" no-op (never a silent empty file); <c>epoch:null</c> carries its own reason, since this call consults
    /// no load-order build; and <c>written:false</c> with <c>unchanged:true</c> and a non-null <c>seq_path</c> is
    /// "the destination already held exactly these bytes". <c>written</c> therefore means "this call wrote the
    /// file", never merely "a path exists".</summary>
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
            w.WriteBoolean("user_chose_output_dir", o.UserChoseOutput);
            WriteNullable(w, "deploy_warning", o.DeployWarning);
            WriteNullable(w, "lane_note", outputNote);
            w.WriteNumber("quest_count", o.Quests.Count);
            if (o.Quests.Count == 0)
                w.WriteString("note", "no start-game-enabled quests in this plugin — " + WriteSentences.Twins.SeqNoQuests + "."
                    // The lane was acknowledged but never resolved on this path: user_chose_output_dir true, no
                    // seq_path, no deploy_warning. Say which of the two it is.
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
            // Not "raise max_chars to see the rest": widening the ceiling means re-issuing a WRITE. Same wording
            // as SeqTools.Render's.
            if (truncated)
                w.WriteString("truncated_note",
                    $"the render hit max_chars={cap} and dropped trailing quest rows — " + WriteSentences.Twins.SeqListCutRemedy + ".");
            w.WriteString("standing_limit", WriteSentences.Twins.SeqStandingLimit);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_remove ---------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderRemoval"/> — the SAME data, on
    /// <see cref="RenderPatchOutcome"/>'s contract: a refusal is a document, the consent prompt is its own flag,
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
            // Same remedy as the text twin, from the same constant: a repeated remove is REFUSED, so "raise
            // max_chars" would name the one call guaranteed to fail.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(false, "removed")} — "
                    + WriteTools.RemovedRowsRemedy + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_forward --------------------------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="WriteTools.RenderForward"/> — the SAME data, on
    /// <see cref="RenderPatchOutcome"/>'s contract. The two per-record facts the text render puts in brackets are
    /// flags here: <c>replaced_existing</c> (an override this artifact already carried had its FIELDS replaced, with
    /// <c>preserved_children</c> naming how many nested records rode across the replace) and
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
                // How many records nested under the replaced one were carried across — a consumer branching on
                // replaced_existing alone would read the replace as total.
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
            // Lane-aware, same rule and same helper as the text twin: a re-issue is idempotent on in_place=/into=
            // and free on a dry run, but on the DEFAULT lane it cuts a second patch.
            if (truncated)
                w.WriteString("truncated_note",
                    $"{WriteSentences.JsonRowsCut(cap)}; {WriteSentences.RowsCutOperationIntact(o.DryRun, "forwarded")} — "
                    + WriteTools.ForwardAgainRemedy(o, Path.GetFileName(o.OutputPath)) + ".");
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    // ---- housecarl_asset_status (S2 read) -----------------------------------------------------------
    /// <summary>The machine-readable twin of <see cref="AssetWire"/>'s render — the SAME data: the build-level
    /// caveats, one row per queried path with its winner and full provider chain, and the §2.1 accounting in-band.
    /// <para>A provider is <c>{name, kind}</c>, not the printed <c>"Name" (kind)</c> token: the reusable value is
    /// the NAME ALONE, which is what <c>housecarl_place</c>'s <c>source_provider=</c> accepts, so a consumer reads
    /// the field rather than parsing the display token apart.</para>
    /// <para>The eight counters ride under one <c>accounting</c> object, written by the SAME composer family the
    /// text line goes through (<see cref="TransportAccounting"/>), so
    /// <c>skipped + rendered + truncated + capped == total</c> on both lanes and neither can drift. Room for that
    /// object and the advice after it is reserved out of max_chars before anything is written, the way the text
    /// twin reserves its accounting line — so max_chars means the same thing on both lanes. <c>truncated</c> at the
    /// document root is the boolean every json document carries; <c>accounting.truncated</c> is the count.</para></summary>
    public static string RenderAssetStatus(AssetStatusData d, int maxChars)
    {
        int cap = Cap(maxChars);
        // The accounting object and the advice after it are priced INSIDE max_chars, as the text twin prices its
        // accounting line: room for their widest spelling is held back BEFORE the caveats and the rows write, rather
        // than appended past the cap.
        int budget = Math.Max(cap - AssetTailReserve(d, cap), 1);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("profile", d.ProfileName.Length > 0 ? d.ProfileName : "(unconfigured)");
            // The caveats lead, as they do in the text render: an ABSENT below is only authoritative when both are
            // empty, and a document that truncates its rows must not be able to cut the reason away. Each is capped
            // against the same budget its text twin caps against — an under= of thousands of selectors would
            // otherwise write megabytes here before the row loop ever checked the budget.
            w.WriteBoolean("read_incomplete", d.ReadIncomplete);
            int caveatsOmitted = WriteCappedStringArray(w, ms, "bsa_failures", d.BsaFailures, budget)
                               + WriteCappedStringArray(w, ms, "warnings", d.Warnings, budget);
            if (d.SelectorNotes is null) { w.WriteNull("selector_notes"); w.WriteNumber("selector_notes_omitted", 0); }
            else caveatsOmitted += WriteCappedStringArray(w, ms, "selector_notes", d.SelectorNotes, budget);

            w.WriteStartArray("results");
            int rendered = 0;
            foreach (var r in d.Results)
            {
                // rendered > 0: the FIRST row always renders its core answer, even when the caveats alone exhausted
                // the budget — the same rule BatchRender makes on the text lane, so a one-path call is answered.
                if (rendered > 0 && Over(w, ms, budget)) break;
                WriteAssetRow(w, r, d.ReadIncomplete, d.Warnings.Count > 0);
                rendered++;
            }
            w.WriteEndArray();

            var counts = AssetWire.Tally(d, rendered);
            TransportAccounting.WriteJson(w, counts);
            // The document's own flag, so a consumer branching on it re-calls when ANYTHING was dropped: a caveat
            // block the budget cut is a loss the row counters cannot see.
            w.WriteBoolean("truncated", counts.Truncated > 0 || caveatsOmitted > 0);
            WriteAssetAdvice(w, counts, cap, caveatsOmitted > 0);
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    /// <summary>The three conditional sentences the text accounting composes, on the same three conditions: the next
    /// page (measured off what was RENDERED, so a consumer paging by these lands on the first path it has not seen),
    /// an offset past the end, and the max_chars cut. Siblings of the <c>accounting</c> object rather than members of
    /// it, because that object is the eight counters and nothing else.</summary>
    /// <param name="caveatsCut">a caveat block lost entries to the budget — the cut the row counters cannot see.</param>
    /// <param name="everySentence">write them all, whatever the counts say — the widest case the reserve measures.</param>
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

    /// <summary>The chars held back from max_chars for what this document writes outside the budgeted body — the
    /// accounting object, the truncation flag, every advice sentence, and the three caveat <c>_omitted</c> counters,
    /// which are written after their array has already spent the budget. Measured by serializing the WIDEST tail
    /// under the response's own writer options, so no rendering of it can outgrow its own room (measuring unindented
    /// what is written indented under-reserves by the whole indentation).</summary>
    static int AssetTailReserve(AssetStatusData d, int cap)
    {
        var widest = AssetWire.Widest(d);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteString("before", "");   // the tail is never a document's first member, so it pays the separator one owes
            // Each block can omit at most its own entries, so its own count is the widest number it can write.
            w.WriteNumber("bsa_failures_omitted", d.BsaFailures.Count);
            w.WriteNumber("warnings_omitted", d.Warnings.Count);
            w.WriteNumber("selector_notes_omitted", d.SelectorNotes?.Count ?? 0);
            TransportAccounting.WriteJson(w, widest);
            w.WriteBoolean("truncated", true);
            WriteAssetAdvice(w, widest, cap, caveatsCut: true, everySentence: true);
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }

    static void WriteAssetRow(Utf8JsonWriter w, AssetPathResult r, bool readIncomplete, bool discoveryIncomplete)
    {
        w.WriteStartObject();
        w.WriteString("path", r.RelPath);
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
        if (!hit.Exists)
        {
            WriteNullableStringArray(w, "prefix_suggestions", r.PrefixSuggestions);
            // The two ways an ABSENT can be wrong, per row rather than only at the top: the text render hedges the
            // answer where it is read, and a json consumer branching on exists=false needs the same hedge. TWO
            // booleans because they are two distinct hedges with two distinct remedies — an archive that failed to
            // read (the asset could be inside it) against base archives never discovered (they went unscanned) — and
            // one OR of them would leave the consumer re-deriving which applies from the top-level pair.
            w.WriteBoolean("absent_may_be_incomplete_read_failure", readIncomplete);
            w.WriteBoolean("absent_may_be_incomplete_undiscovered_archives", discoveryIncomplete);
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
    /// <summary>The machine-readable twin of <see cref="PlaceWire"/>'s render — the SAME data on the write
    /// surface's contract: <c>ok</c> on both outcomes, a refusal is a document, and the "this does not win until
    /// you enable and sort the mod" instruction rides in-band as <c>next_step</c>. It is written from the same
    /// helper the text lane calls, because a truncated json document that dropped the instruction would be the
    /// silently degraded mode json is not allowed to be.</summary>
    public static string RenderPlaceOutcome(PlaceOutcome o, int maxChars, IReadOnlySet<string>? poleWithheld = null)
    {
        int cap = WriteSentences.Cap(maxChars);   // the WRITE budget rule, shared with the text twin
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", o.Success);
            if (!o.Success)
            {
                w.WriteString("error", o.Error);
                // The same three keys RenderError writes: a refusal from the outcome and a refusal from the tool
                // layer are one shape, so a consumer reading doc["epoch"] on a refusal never throws on one of them.
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
                // rendered > 0: the FIRST row always renders, as it does on the text lane — and on this document it
                // is the only place current_winner, the mod the caller has to sort above, is stated.
                if (rendered > 0 && Over(w, ms, cap)) { truncated = true; break; }
                WritePlaceRow(w, r, modFolder, poleWithheld?.Contains(r.AssetPath) == true);
                rendered++;
            }
            w.WriteEndArray();

            // The §2.1 counters ride under the same `accounting` object housecarl_asset_status writes, through the
            // same composer, so a consumer reads doc.accounting.total on both S2 tools. This lane pages nothing, so
            // the window is the whole list and only max_chars can omit a row. placed/failed are siblings, not
            // members: that object is the eight shared counters and nothing else.
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
            w.WriteEndObject();
        }
        return Finish(ms);
    }

    static void WritePlaceRow(Utf8JsonWriter w, PlaceResult r, string? modFolder, bool poleWithheld)
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
            w.WriteString("winner_note", r.CurrentWinner is not null
                ? $"{r.CurrentWinner} currently wins the VFS — sort the new mod ABOVE it"
                : $"nothing else provides this path — once '{modFolder ?? "(the new folder)"}' is enabled, the placed copy wins");
            // Bytes served out of a mod MO2 does not load are a fact of the SOURCE, and look like any other
            // placement without it.
            if (r.SourceOffOrderProvider is { } offOrder)
                w.WriteString("source_off_order_note", WriteSentences.PlaceSourceOffOrder(offOrder, r.SourceOffOrderOwnerEnabled));
            WriteNullable(w, "source_off_order_provider", r.SourceOffOrderProvider);
        }
        // An input the call carried but this destination could not use is SAID on both lanes, not dropped.
        w.WriteBoolean("set_provider_withheld", poleWithheld);
        w.WriteEndObject();
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

    /// <summary>The per-BLOCK truncation census the three post-write reports carry.
    /// <para>Without it a cut block renders as <c>lines: []</c>, indistinguishable from "nothing to report" — the
    /// exact inversion of what these blocks exist to say, since an empty voice list then reads as "every created
    /// line is voiced". Not an edge case: <see cref="RenderCreateOutcome"/>'s created rows budget against the SAME
    /// <c>cap</c> and run first, so whenever they truncate every block below renders empty. The document-level
    /// <c>truncated</c> flag is a weaker claim — it does not say WHICH block lost rows.</para>
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
        // apply on the default lane auto-suffixes a second patch. The note states the cut and the stakes and stops
        // there — the write is done, and the counts above are what a consumer branches on.
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
    // ---- unit costs, exposed for the demand pass ---------------------------------------------------
    // A demand must be the SAME number the emission test declares, so the demand pass calls these rather than
    // spelling the costs a second time. Each takes the DEPTH the unit is written at and whether it follows a
    // sibling: both change what the unit costs, and neither is knowable from the unit alone.

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

    /// <summary>This axis's frame cost, keyed off the SUBJECT so the demand pass and the render name the same
    /// axis. The json lane carries its own field names for the axes; mapping by subject is what keeps the two
    /// spellings from drifting into measuring different objects.</summary>
    internal static int HistogramFrameCostFor(HistogramAxis a, int depth)
        => HistogramFrameCost(AxisJsonName(a.Subject), a.Rows?.Count ?? 0, depth);

    internal static string AxisJsonName(SweepSubject s) => s switch
    {
        SweepSubject.HistogramByTarget => "dangling_by_target_plugin",
        SweepSubject.HistogramBySource => "dangling_by_source_plugin",
        SweepSubject.HistogramByProperty => "unbound_by_property",
        _ => s.ToString(),
    };
}
