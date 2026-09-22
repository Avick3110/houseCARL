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
    /// <param name="callerCap">the max_chars the CALLER passed, not the budget left after the tail reserve: a
    /// document that shipped over it closes with <c>max_chars_overrun</c>, as every other json document does.</param>
    internal static string Write(SkseTools.SkseFamily family, string? filter, string profile, int callerCap,
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
            JsonWire.WriteCapOverrun(w, ms, callerCap);
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

    /// <summary>The build-level caveats every family carries — the same ones the text render writes and the accounting counts.</summary>
    internal static void Caveats(Utf8JsonWriter w, bool readIncomplete,
                                 IReadOnlyList<string> warnings, IReadOnlyList<string> bsaFailures,
                                 IReadOnlyList<string> rootFailures, int cap)
    {
        w.WriteStartObject("caveats");
        w.WriteBoolean("read_incomplete", readIncomplete);
        Strings(w, "warnings", warnings);
        Strings(w, "archive_read_failures", bsaFailures);
        // The loose twin, beside the archives: a root that would not read is named, not just hedged — and BOUNDED,
        // because one entry per root per directory asked about is a list a blocked tree can make wider than the whole
        // document. Through the SHARED writer, so this lane's json names the roots its own text lane names: bounding
        // the array against the json stream instead priced an element differently and the two counts drifted apart.
        // Written empty or not, so the array and its sibling count add up to the whole on every document, which is the
        // shape json-wire.md states and asset_status keeps.
        JsonWire.WriteRootFailuresCut(w, rootFailures, cap);
        w.WriteEndObject();
    }

    /// <summary>Does a unit of <paramref name="cost"/> chars still fit inside <paramref name="cap"/>? The json twin of
    /// the text lane's <c>length + cost</c> test: the row is measured through <see cref="JsonWire.MeasureUnit"/> before
    /// it is admitted, so the row that crosses is never written and the document closes inside the cap it was given
    /// (#859). Characters, not bytes, and the writer is flushed first because it buffers.</summary>
    internal static bool Fits(Utf8JsonWriter w, CharCountedStream ms, int cap, int cost)
    {
        w.Flush();
        return JsonWire.Chars(ms) + cost <= cap;
    }

    /// <summary>The chars held back from max_chars for the tail every family document closes on — the json twin of
    /// <see cref="TransportAccounting.Reserve"/>, measured by composing the widest tail so no rendering outgrows it.</summary>
    /// <param name="rowArrays">the family's row lists, by name — required, so a family cannot forget one and
    /// under-reserve. What is written past the last row the budget admitted is each array's CLOSE; composing the array
    /// empty here charges that close plus the open's own width, which the open already paid for in the document, so the
    /// reserve is a floor of tens of chars rather than an exact figure — measured off the writer, never hand-written.</param>
    internal static int TailReserve(bool readIncomplete, IReadOnlyList<string> warnings, IReadOnlyList<string> bsaFailures,
                                    IReadOnlyList<string> rootFailures, int cap, TransportCounts widest,
                                    IReadOnlyList<string> rowArrays, Action<Utf8JsonWriter>? conditional = null)
    {
        using var ms = new CharCountedStream();
        using (var w = new Utf8JsonWriter(ms, JsonWire.WriterOptions))
        {
            w.WriteStartObject();
            conditional?.Invoke(w);
            foreach (var name in rowArrays)
            {
                w.WriteStartArray(name);
                w.WriteEndArray();
            }
            Caveats(w, readIncomplete, warnings, bsaFailures, rootFailures, cap);
            TransportAccounting.WriteJson(w, widest);
            w.WriteEndObject();
        }
        return JsonWire.Chars(ms);
    }
}
