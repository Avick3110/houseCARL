using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>The TRANSPORT paging window (SPEC §2.1): <c>offset=</c> steps over rows before the window,
/// <c>limit=</c> bounds the window. <c>Limit = 0</c> is no limit, the shape every surface defaults to, so a call
/// that passes neither renders exactly what it rendered before paging existed.</summary>
internal readonly record struct RowWindow(int Offset, int Limit)
{
    /// <summary>The whole list — no offset, no limit.</summary>
    internal static readonly RowWindow All = new(0, 0);

    /// <summary>The window of <paramref name="rows"/> this describes.</summary>
    internal IReadOnlyList<T> Apply<T>(IReadOnlyList<T> rows)
    {
        if (Offset <= 0 && Limit <= 0) return rows;
        var q = rows.Skip(Offset);
        if (Limit > 0) q = q.Take(Limit);
        return q.ToList();
    }

    /// <summary>The window over a SECOND list that continues the first — the shape a family whose row list is two
    /// concatenated populations needs (SKSE inventory: DLLs then configs). <paramref name="consumed"/> is how many
    /// rows the first list held, so the offset lands where the first list stopped and the limit counts what the
    /// first list already spent.</summary>
    internal RowWindow After(int consumed, int taken) =>
        new(Math.Max(Offset - consumed, 0), Limit <= 0 ? 0 : Math.Max(Limit - taken, 0));

    /// <summary>The window's own refusal, or null when both values are legal. One sentence naming both knobs,
    /// because a caller who got one wrong usually typed the other in the same call.</summary>
    internal string? Error =>
        Offset < 0 || Limit < 0
            ? $"error: limit={Limit} offset={Offset} — neither can be negative. Pass limit=0 for no limit and offset=0 to start at the beginning of the selection."
            : null;
}

/// <summary>How many DISTINCT rows a render actually put on the page. A set, not a counter, because a render whose
/// sections overlap — one DLL listed both as version-locked and in the plugin roster — would otherwise count it
/// twice and report more rendered rows than the window held.</summary>
internal sealed class RowTally
{
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record that the row with this key reached the page.</summary>
    internal void Mark(string key) => _seen.Add(key);

    /// <summary>How many distinct rows reached the page.</summary>
    internal int Count => _seen.Count;
}

/// <summary>The numbers one in-band accounting block states. A record so the real line, the JSON twin and the
/// widest-case line the reserve measures all go through ONE composer — a second formatter would be a second
/// spelling, and the reserve would stop bounding what is written.</summary>
internal readonly record struct TransportCounts(int Total, int Rendered, int Skipped, int Capped, int Truncated,
                                                int Offset, int Remaining, int Notes, int NextLimit);

/// <summary>The one in-band accounting block (SPEC §2.1: <c>total / rendered / capped / truncated / notes</c> is
/// required output on every bulk lane), shared by every surface that pages a row list. One composer for the text
/// line, one writer for its JSON twin, and one reserve so the block is paid for INSIDE max_chars rather than
/// appended past it.
///
/// <para>The four omissions have four distinct causes and each is counted once, so
/// <c>skipped + rendered + truncated + capped == total</c>: <c>skipped</c> is what <c>offset=</c> stepped over
/// BEFORE the window, <c>capped</c> what <c>limit=</c> left AFTER it, <c>truncated</c> what <c>max_chars</c> cut
/// out of the window. <c>remaining</c> and the next page are measured off what was RENDERED, not off the window: a
/// caller walking by this block's own advice must land on the first row it has not seen, and rows the cap cut were
/// selected but never shown.</para></summary>
internal static class TransportAccounting
{
    /// <summary>The window the next-page advice names when the caller passed none. Without a limit= in the advice a
    /// caller following it calls back with limit=0, which resolves the WHOLE remainder on every page — the paging is
    /// only cheap if the advice keeps it paged.</summary>
    internal const int DefaultPageLimit = 200;

    /// <summary>What this response actually did. <paramref name="windowed"/> is how many rows the window handed the
    /// render; <paramref name="rendered"/> how many of those it got onto the page.</summary>
    internal static TransportCounts Tally(int total, int windowed, int rendered, RowWindow w, int notes) => new(
        Total: total,
        Rendered: rendered,
        Skipped: Math.Min(w.Offset, total),
        Capped: Math.Max(total - w.Offset - windowed, 0),
        Truncated: Math.Max(windowed - rendered, 0),
        Offset: w.Offset,
        Remaining: Math.Max(total - (w.Offset + rendered), 0),
        Notes: notes,
        NextLimit: w.Limit > 0 ? w.Limit : DefaultPageLimit);

    /// <summary>The chars held back from max_chars so the accounting block is always affordable — measured by
    /// composing the WIDEST line this response could write, so no rendering of it can outgrow its own room.</summary>
    internal static int Reserve(int total, int windowed, RowWindow w, int notes, string rowNoun)
        => Compose(Widest(total, windowed, w, notes), rowNoun, everySentence: true).Length;

    /// <summary>The widest line this response could produce: every count at its largest (so every digit slot is at
    /// its real width) and, with <c>everySentence</c>, every optional sentence present. An upper bound, which is
    /// what a reserve has to be.</summary>
    static TransportCounts Widest(int total, int windowed, RowWindow w, int notes)
    {
        int most = Math.Max(total, windowed);
        return new TransportCounts(most, windowed, most, most, windowed, w.Offset, most, notes,
                                   Math.Max(w.Limit, DefaultPageLimit));
    }

    /// <summary>The one machine-readable accounting line, always last: how many rows the selection named, how many
    /// rendered, how many the paging window stepped over or left behind, and how many max_chars cut. A bulk consumer
    /// checks these numbers instead of counting prose it might miss. <paramref name="rowNoun"/> names what the
    /// counts count, e.g. "path(s)" or "DLL(s)".</summary>
    internal static string Compose(TransportCounts c, string rowNoun, bool everySentence)
    {
        var sb = new StringBuilder("\n\n[accounting] total=").Append(c.Total)
            .Append(" rendered=").Append(c.Rendered)
            .Append(" skipped=").Append(c.Skipped)
            .Append(" capped=").Append(c.Capped)
            .Append(" truncated=").Append(c.Truncated)
            .Append(" offset=").Append(c.Offset)
            .Append(" remaining=").Append(c.Remaining)
            .Append(" notes=").Append(c.Notes);
        // Only what is still AHEAD of what was rendered earns a next page, and the next offset starts at the first
        // row this response did not show — so a caller following the advice sees every row exactly once. The advice
        // carries limit= as well: without it the next call resolves the whole remainder instead of one page.
        if (everySentence || c.Remaining > 0)
            sb.Append("\nthe selection is longer than this window: re-call with limit=").Append(c.NextLimit)
              .Append(" offset=").Append(c.Offset + c.Rendered).Append(" for the next page.");
        // An offset past the end would otherwise be told to re-call at the offset it already used.
        if (everySentence || (c.Remaining == 0 && c.Total > 0 && c.Offset >= c.Total))
            sb.Append("\noffset=").Append(c.Offset).Append(" is past the end of the selection (")
              .Append(c.Total).Append(' ').Append(rowNoun).Append(") — the last page starts before it.");
        if (everySentence || c.Truncated > 0)
            sb.Append("\nmax_chars cut ").Append(c.Truncated).Append(' ').Append(rowNoun)
              .Append(" from the render: raise max_chars, or page with limit=/offset=.");
        return sb.ToString();
    }

    /// <summary>The JSON twin of <see cref="Compose"/>: the same eight numbers, in-band, under one
    /// <c>accounting</c> object — so a json consumer reads the accounting off named fields instead of parsing the
    /// text line. Field names match the text spelling exactly; a name added here is a name added there.</summary>
    internal static void WriteJson(Utf8JsonWriter w, TransportCounts c)
    {
        w.WriteStartObject("accounting");
        w.WriteNumber("total", c.Total);
        w.WriteNumber("rendered", c.Rendered);
        w.WriteNumber("skipped", c.Skipped);
        w.WriteNumber("capped", c.Capped);
        w.WriteNumber("truncated", c.Truncated);
        w.WriteNumber("offset", c.Offset);
        w.WriteNumber("remaining", c.Remaining);
        w.WriteNumber("notes", c.Notes);
        w.WriteEndObject();
    }
}
