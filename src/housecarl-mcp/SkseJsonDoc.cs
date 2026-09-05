using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>The json skeleton the three <c>housecarl_skse</c> family documents share: the family it ran, the two it
/// did not (the in-band twin of the text footer — a json document cannot carry the footer's prose), the filter and
/// profile it answered under, and the small writers the three bodies reuse. Each family's own serializer lives
/// beside its text render rather than here, because the twin must classify with the SAME judge the text render uses
/// — a copy elsewhere would be free to drift, which is the one thing a twin may not do.</summary>
static class SkseJsonDoc
{
    /// <summary>Write one family document. <paramref name="body"/> gets the writer and the stream behind it, so a
    /// row loop can flush and measure against max_chars the way every other json render does.</summary>
    internal static string Write(SkseTools.SkseFamily family, string? filter, string profile,
                                 Action<Utf8JsonWriter, MemoryStream> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("family", family.ToString().ToLowerInvariant());
            Strings(w, "not_run", SkseTools.NotRun(family));
            Nullable(w, "filter", string.IsNullOrWhiteSpace(filter) ? null : filter.Trim());
            w.WriteString("profile", profile);
            body(w, ms);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>A string member whose null carries meaning — absent value, not empty value.</summary>
    internal static void Nullable(Utf8JsonWriter w, string name, string? v)
    {
        if (v is null) w.WriteNull(name); else w.WriteString(name, v);
    }

    internal static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }

    /// <summary>The full winner-first provider chain, each tagged loose or BSA.</summary>
    internal static void Providers(Utf8JsonWriter w, IReadOnlyList<SkseProvider> providers)
    {
        w.WriteStartArray("providers");
        foreach (var p in providers)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("kind", p.Kind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>The build-level caveats every family carries — the same three the text render's caveat block writes,
    /// and the three the accounting's <c>notes</c> counts.</summary>
    internal static void Caveats(Utf8JsonWriter w, bool readIncomplete, IReadOnlyList<string> warnings,
                                 IReadOnlyList<string> bsaFailures)
    {
        w.WriteStartObject("caveats");
        w.WriteBoolean("read_incomplete", readIncomplete);
        Strings(w, "warnings", warnings);
        Strings(w, "archive_read_failures", bsaFailures);
        w.WriteEndObject();
    }

    /// <summary>Is the document already at its char ceiling? The writer BUFFERS, so the stream length lags what has
    /// been written — a row loop that budgets by stream length must flush first, as this does.</summary>
    internal static bool Over(Utf8JsonWriter w, MemoryStream ms, int cap)
    {
        w.Flush();
        return ms.Length >= cap;
    }

    /// <summary>The chars held back from max_chars for the tail every family document closes on — the caveats object,
    /// the accounting object, and whatever conditional members that family may still write after its row arrays. The
    /// json twin of <see cref="TransportAccounting.Reserve"/>: without it the row loops fill the document to the cap
    /// and the tail is written past it. Measured by composing the widest tail, so no rendering of it can outgrow its
    /// own room.</summary>
    internal static int TailReserve(bool readIncomplete, IReadOnlyList<string> warnings, IReadOnlyList<string> bsaFailures,
                                    TransportCounts widest, Action<Utf8JsonWriter>? conditional = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            conditional?.Invoke(w);
            Caveats(w, readIncomplete, warnings, bsaFailures);
            TransportAccounting.WriteJson(w, widest);
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }
}
