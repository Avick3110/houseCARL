using System.Text;
using System.Text.Json;
using HousecarlCore;

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
    /// <summary>Render the bulk name-resolution result as JSON: <c>{count, resolved:[…]}</c> — one
    /// <c>{formid,type,editorid,name,winner}</c> row per resolvable input, or <c>{formid,error}</c> for a
    /// bad/absent one (per-item, the batch survives — Q3). Rows are one small object each; the input count bounds
    /// the document, so no row truncation is needed here.</summary>
    public static string RenderResolve(IReadOnlyList<ResolvedRef> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Opts))
        {
            w.WriteStartObject();
            w.WriteNumber("count", rows.Count);
            w.WriteStartArray("resolved");
            foreach (var r in rows) WriteResolvedRow(w, r);
            w.WriteEndArray();
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
}
