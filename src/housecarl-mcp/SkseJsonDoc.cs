using System.Text;
using System.Text.Json;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>The json skeleton the three <c>housecarl_skse</c> family documents share: the family that ran, the two
/// that did not, the filter and profile, and the small writers the bodies reuse. Each family's own serializer lives
/// beside its text render, because the twin must classify with the SAME judge; see docs/architecture/skse-layer.md.</summary>
static class SkseJsonDoc
{
    /// <summary>Write one family document; <paramref name="body"/> gets the writer and the stream, so a row loop can flush and measure.</summary>
    internal static string Write(SkseTools.SkseFamily family, string? filter, string profile,
                                 Action<Utf8JsonWriter, CharCountedStream> body)
    {
        using var ms = new CharCountedStream();
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

    /// <summary>The build-level caveats every family carries — the same three the text render writes and the accounting counts.</summary>
    internal static void Caveats(Utf8JsonWriter w, bool readIncomplete, IReadOnlyList<string> warnings,
                                 IReadOnlyList<string> bsaFailures)
    {
        w.WriteStartObject("caveats");
        w.WriteBoolean("read_incomplete", readIncomplete);
        Strings(w, "warnings", warnings);
        Strings(w, "archive_read_failures", bsaFailures);
        w.WriteEndObject();
    }

    /// <summary>Is the document already at its char ceiling? Characters, not bytes, and the writer is flushed first because it buffers.</summary>
    internal static bool Over(Utf8JsonWriter w, CharCountedStream ms, int cap)
    {
        w.Flush();
        return JsonWire.Chars(ms) >= cap;
    }

    /// <summary>The chars held back from max_chars for the tail every family document closes on — the json twin of
    /// <see cref="TransportAccounting.Reserve"/>, measured by composing the widest tail so no rendering outgrows it.</summary>
    internal static int TailReserve(bool readIncomplete, IReadOnlyList<string> warnings, IReadOnlyList<string> bsaFailures,
                                    TransportCounts widest, Action<Utf8JsonWriter>? conditional = null)
    {
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            conditional?.Invoke(w);
            Caveats(w, readIncomplete, warnings, bsaFailures);
            TransportAccounting.WriteJson(w, widest);
            w.WriteEndObject();
        }
        return JsonWire.Chars(ms);
    }
}
