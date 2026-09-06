namespace HousecarlMcp;

/// <summary>
/// What a scan's RENDER may cost before the call refuses instead of going silent (#582).
///
/// <para>The scan terms (types=/plugins=/where=) bound the scan. Nothing bounded the render, and the render is the
/// other half: every rendered row reads a body. A 66,856-row fields projection therefore ran for about 45 minutes
/// with no progress, past the client's 30-minute idle timeout, and the caller learned the cost by waiting.</para>
///
/// <para>So the cost is stated up front. Rows times <see cref="MillisPerRow"/> is what the call will spend rendering;
/// past <see cref="MaxRenderRows"/> it refuses and names the shapes that fit. What each call ACTUALLY spent comes
/// back in the accounting as <c>render_ms</c>, which is how the estimate here is checked against a real order rather
/// than trusted.</para>
/// </summary>
internal static class RenderBudget
{
    /// <summary>The declared cost of rendering one detail row: reading the named fields off the match's body.
    /// Deliberately pessimistic. Measured at 0.03–0.06 ms a row on a synthetic order; the reported cost on the ARR
    /// order before the per-row whole-overlay seek was removed was ~40 ms a row, and removing it measured a 30x–200x
    /// reduction, so 2 ms leaves better than an order of magnitude over the extrapolated real-order figure.</summary>
    internal const double MillisPerRow = 2.0;

    /// <summary>THE BOUND: the most rows one call renders. Ten minutes of render at
    /// <see cref="MillisPerRow"/> — a third of the 30-minute idle timeout a Claude Code client gives a call, leaving
    /// the scan itself, the artifact write and a retry inside the same window.</summary>
    internal const int DefaultMaxRenderRows = 300_000;

    /// <summary>The bound in force. Settable so a test can drive the seam over a world of a few records instead of
    /// building 300,000 — the same reason <see cref="Artifacts.WriteCrossQuery"/> takes a row cap. Production never
    /// assigns it.</summary>
    internal static int MaxRenderRows { get; set; } = DefaultMaxRenderRows;

    /// <summary>The projected render, in whole minutes, for <paramref name="rows"/>.</summary>
    internal static string Projected(int rows)
    {
        var ms = rows * MillisPerRow;
        return ms >= 60_000 ? $"about {ms / 60_000:F0} minutes" : $"about {ms / 1000:F0} seconds";
    }

    /// <summary>The refusal for a render over the bound, or null when it fits. One sentence for the cost, one for
    /// the shapes that fit, each carrying the caveat that decides between them.</summary>
    internal static string? Refuse(int rows) =>
        rows <= MaxRenderRows
            ? null
            : $"error: this call renders {rows:N0} rows and each one reads a record body — {Projected(rows)} of render, " +
              $"past the {MaxRenderRows:N0}-row bound one call is given (a client stops waiting at 30 minutes). " +
              "Narrow the scan terms (types=, plugins=, where=), or take the selection in windows with limit= and " +
              "offset= — offset= re-scans the selection from the start, so a window costs more the further in it is. " +
              "to_file= captures the COMPLETE selection rather than a window, so it renders every row and does not " +
              "combine with offset=: narrow the scan until the whole set fits, then write it in one call.";
}
