using System.Text;
using System.Text.Json;

namespace HousecarlMcp;

/// <summary>The TRANSPORT paging window (SPEC 2.1). <paramref name="Spent"/> is the third state
/// <c>Offset</c> and <c>Limit</c> cannot express: a limit that WAS asked for and is now used up.</summary>
internal readonly record struct RowWindow(int Offset, int Limit, bool Spent = false)
{
    internal static readonly RowWindow All = new(0, 0);

    internal IReadOnlyList<T> Apply<T>(IReadOnlyList<T> rows)
    {
        if (Spent) return Array.Empty<T>();
        if (Offset <= 0 && Limit <= 0) return rows;
        var q = rows.Skip(Offset);
        if (Limit > 0) q = q.Take(Limit);
        return q.ToList();
    }

    /// <summary>The window over a SECOND list that continues the first, so a limit the first list exhausted carries
    /// over as spent rather than as no limit.</summary>
    internal RowWindow After(int consumed, int taken) =>
        new(Math.Max(Offset - consumed, 0),
            Limit <= 0 ? 0 : Math.Max(Limit - taken, 0),
            Spent || (Limit > 0 && taken >= Limit));

    /// <summary>The window's own refusal, or null when both values are legal, naming both knobs in one sentence.</summary>
    internal string? Error =>
        Offset < 0 || Limit < 0
            ? $"error: limit={Limit} offset={Offset} — neither can be negative. Pass limit=0 for no limit and offset=0 to start at the beginning of the selection."
            : null;
}

/// <summary>How many DISTINCT rows a render put on the page — a set, so overlapping sections count once.</summary>
internal sealed class RowTally
{
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    internal void Mark(string key) => _seen.Add(key);

    internal int Count => _seen.Count;
}

/// <summary>The numbers one in-band accounting block states, so every spelling of it has ONE composer.</summary>
internal readonly record struct TransportCounts(int Total, int Rendered, int Skipped, int Capped, int Truncated,
                                                int Offset, int Remaining, int Notes, int NextLimit);

/// <summary>The one in-band accounting block, shared by every surface that pages a row list. Its four omissions
/// have four distinct causes, each counted once, so <c>skipped + rendered + truncated + capped == total</c>;
/// <c>remaining</c> and the next page are measured off what was RENDERED, not off the window.</summary>
internal static class TransportAccounting
{
    /// <summary>The window the next-page advice names when the caller passed none, so a caller following it does
    /// not call back with limit=0 and resolve the whole remainder.</summary>
    internal const int DefaultPageLimit = 200;

    /// <summary>What this response actually did: rows the window handed the render, and rows that reached the page.</summary>
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

    /// <summary>The chars held back so the accounting block is affordable, measured off its WIDEST spelling.</summary>
    internal static int Reserve(int total, int windowed, RowWindow w, int notes, string rowNoun)
        => Compose(Widest(total, windowed, w, notes), rowNoun, everySentence: true).Length;

    /// <summary>The widest line this response could produce: every count at its largest, every sentence present.</summary>
    internal static TransportCounts Widest(int total, int windowed, RowWindow w, int notes)
    {
        int most = Math.Max(total, windowed);
        return new TransportCounts(most, windowed, most, most, windowed, w.Offset, most, notes,
                                   Math.Max(w.Limit, DefaultPageLimit));
    }

    /// <summary>The one machine-readable accounting line, closing the render body. <paramref name="rowNoun"/> names
    /// what the counts count, e.g. "path(s)" or "DLL(s)".</summary>
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
        // Only what is still AHEAD of what was rendered earns a next page, at the first row this response did not
        // show, and the advice carries limit= so the next call does not resolve the whole remainder.
        if (everySentence || (c.Remaining > 0 && c.Rendered > 0))
            sb.Append("\nthe selection is longer than this window: re-call with limit=").Append(c.NextLimit)
              .Append(" offset=").Append(c.Offset + c.Rendered).Append(" for the next page.");
        // Where one row is wider than the whole budget, offset= is the only knob that moves, so the offset past
        // that row is named — and named as a SKIP, since it is a row the caller has not seen.
        if (everySentence || (c.Remaining > 0 && c.Rendered == 0 && c.Truncated > 0))
            sb.Append("\nno ").Append(rowNoun).Append(" fitted this window: raise max_chars for the one at offset=")
              .Append(c.Offset).Append(", or skip it with offset=").Append(c.Offset + 1)
              .Append(" to reach the rest of the selection.");
        // An offset past the end would otherwise be told to re-call at the offset it already used.
        if (everySentence || (c.Remaining == 0 && c.Total > 0 && c.Offset >= c.Total))
            sb.Append("\noffset=").Append(c.Offset).Append(" is past the end of the selection (")
              .Append(c.Total).Append(' ').Append(rowNoun).Append(") — the last page starts before it.");
        if (everySentence || c.Truncated > 0)
            sb.Append("\nmax_chars cut ").Append(c.Truncated).Append(' ').Append(rowNoun)
              .Append(" from the render: raise max_chars, or page with limit=/offset=.");
        return sb.ToString();
    }

    /// <summary>The JSON twin of <see cref="Compose"/>, field names matching the text spelling exactly.</summary>
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
