namespace HousecarlMcp;

/// <summary>
/// What a scan's RENDER may cost before the call refuses instead of going silent (#582).
///
/// <para>The scan terms (types=/plugins=/where=) bound the scan. Nothing bounded the render, and the render is the
/// other half: every rendered row reads a body. A 66,856-row fields projection therefore ran for about 45 minutes
/// with no progress, past the client's 30-minute idle timeout, and the caller learned the cost by waiting.</para>
///
/// <para>So the cost is stated up front. Rows times the per-row cost is what the call will spend rendering; past the
/// bound it refuses and names the shapes that fit. What each call ACTUALLY spent comes back in the accounting as
/// <c>render_ms</c>, which is how the estimates here are checked against a real order rather than trusted.</para>
///
/// <para>TWO costs, because there are two lanes and they are two orders of magnitude apart. A row that reads NAMED
/// field paths off a body costs a field read; a row of <c>form='everything'</c> materialises every field of the
/// record, measured at ~30 ms a row against ~0.013 ms for a three-field projection on the same world. One number
/// over both would either wave the expensive lane through or refuse the cheap one for nothing.</para>
/// </summary>
internal static class RenderBudget
{
    /// <summary>The render ceiling one call is given: ten minutes — a third of the 30-minute idle timeout a Claude
    /// Code client gives a call, leaving the scan itself, the artifact write and a retry inside the same
    /// window.</summary>
    internal const double CeilingMillis = 600_000;

    /// <summary>The declared cost of rendering one detail row: reading the NAMED field paths off the match's body.
    /// Deliberately pessimistic. Measured at 0.013–0.06 ms a row on a synthetic order; the reported cost on the ARR
    /// order before the per-row whole-plugin seek was removed was ~40 ms a row, and removing it measured a 30x–200x
    /// reduction, so 2 ms leaves better than an order of magnitude over the extrapolated real-order figure.</summary>
    internal const double MillisPerRow = 2.0;

    /// <summary>The declared cost of one <c>form='everything'</c> row: the whole record materialised, every field.
    /// Measured at 29.9–32 ms a row on a 2,000-weapon synthetic scan — the body seek is not what costs here, the
    /// field materialisation is, so gathering bodies per plugin does not move this number. 40 ms carries the
    /// measurement plus a margin for a record type fatter than a weapon.</summary>
    internal const double MillisPerWholeRecordRow = 40.0;

    /// <summary>THE BOUND for a named-fields render: ten minutes at <see cref="MillisPerRow"/>.</summary>
    internal const int DefaultMaxRenderRows = 300_000;

    /// <summary>THE BOUND for <c>form='everything'</c>: ten minutes at <see cref="MillisPerWholeRecordRow"/>.</summary>
    internal const int DefaultMaxWholeRecordRows = 15_000;

    /// <summary>The bounds in force. Settable so a test can drive the seam over a world of a few records instead of
    /// building 300,000 — the same reason <see cref="Artifacts.WriteCrossQuery"/> takes a row cap. Production never
    /// assigns them.</summary>
    internal static int MaxRenderRows { get; set; } = DefaultMaxRenderRows;

    /// <inheritdoc cref="MaxRenderRows"/>
    internal static int MaxWholeRecordRows { get; set; } = DefaultMaxWholeRecordRows;

    /// <summary>The chars a text render holds back from <c>max_chars</c> for the accounting line it appends after
    /// its rows. Held back for the same reason the owned-child clause is: a line the response is going to state is
    /// spoken for. Wide enough for either line at its longest values, which a test holds it to.</summary>
    internal const int AccountingReserve = 64;

    /// <summary>The accounting line itself: the rows this render produced and what they cost.</summary>
    internal static string AccountingLine(int rows, long ms) =>
        $"rendered {rows}{(rows == 1 ? " row in " : " rows in ")}{ms} ms\n";

    /// <summary>The batch lane's twin: its bodies are READ before the render, so the count is what was read rather
    /// than what a max_chars cut left showing.</summary>
    internal static string BodiesLine(int rows, long ms) =>
        $"read {rows}{(rows == 1 ? " record body in " : " record bodies in ")}{ms} ms\n";

    /// <summary>The projected render for <paramref name="rows"/> at the lane's own per-row cost.</summary>
    internal static string Projected(int rows, bool wholeRecord)
    {
        var ms = rows * (wholeRecord ? MillisPerWholeRecordRow : MillisPerRow);
        return ms >= 60_000 ? $"about {ms / 60_000:F0} minutes" : $"about {ms / 1000:F0} seconds";
    }

    /// <summary>The refusal for a render over its lane's bound, or null when it fits. One sentence for the cost, one
    /// for the shapes that fit, each carrying the caveat that decides between them. <paramref name="wholeRecord"/>
    /// is the <c>form='everything'</c> lane, whose row is a whole record and whose bound is therefore its own —
    /// and whose first remedy is naming the fields, since that is what moves it between the two.
    /// <para><paramref name="listSelection"/> is the <c>formids=</c> lane, whose remedy differs in kind: it has no
    /// scan terms to narrow, and it reads a body for every id it was handed BEFORE the render window applies, so
    /// paging the same call does not lower its cost.</para></summary>
    internal static string? Refuse(int rows, bool wholeRecord, bool listSelection = false)
    {
        int bound = wholeRecord ? MaxWholeRecordRows : MaxRenderRows;
        if (rows <= bound) return null;
        var lead = wholeRecord
            ? $"error: this call renders {rows:N0} rows and each one reads a WHOLE record body — {Projected(rows, true)} " +
              $"of render, past the {bound:N0}-row bound form='everything' is given (a client stops waiting at 30 " +
              $"minutes). Name the fields you need instead — project.form='fields' with fields=[…] reads a body per " +
              $"row too but costs a fraction of a whole-record read, and is bounded at {MaxRenderRows:N0} rows. Or "
            : $"error: this call renders {rows:N0} rows and each one reads a record body — {Projected(rows, false)} of " +
              $"render, past the {bound:N0}-row bound one call is given (a client stops waiting at 30 minutes). ";
        return lead + (listSelection
            ? (wholeRecord ? "pass" : "Pass") + " fewer formids= entries: this lane reads a body for EVERY id in the " +
              "list before limit= and offset= window the render, so paging the same list does not lower what it costs. " +
              "Re-enter a big artifact a slice at a time, or narrow the selection that wrote it."
            : (wholeRecord ? "narrow" : "Narrow") + " the scan terms (types=, plugins=, where=), or take the selection " +
              "in windows with limit= and offset= — offset= re-scans the selection from the start, so a window costs " +
              "more the further in it is. to_file= captures the COMPLETE selection rather than a window, so it renders " +
              "every row and does not combine with offset=: narrow the scan until the whole set fits, then write it in " +
              "one call.");
    }
}
