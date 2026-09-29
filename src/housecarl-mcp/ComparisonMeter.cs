using System.Diagnostics;

namespace HousecarlMcp;

/// <summary>The delta/tree meter (#932); contract in docs/architecture/render-budget.md.</summary>
internal sealed class ComparisonMeter
{
    /// <summary>The share of the budget the "rows that fit" figure is named at.</summary>
    internal const double FitsMargin = 0.9;

    /// <summary>The meter's clock in Stopwatch ticks for the calling flow; null in production, a test sets it.</summary>
    internal static readonly AsyncLocal<Func<long>?> TestClock = new();

    /// <summary>The clock a call's meter reads.</summary>
    internal static Func<long> Clock => TestClock.Value ?? Stopwatch.GetTimestamp;

    readonly string _form;
    readonly string _lever;
    readonly bool _wholeSelection;
    readonly Func<long> _clock;
    readonly long _origin;
    readonly bool _unmetered;
    long _start;
    int _chunks;
    int _lastRead;

    /// <summary>A meter for one call; <paramref name="origin"/> is the clock's reading when the call began.</summary>
    internal ComparisonMeter(string form, string lever, bool wholeSelection, Func<long> clock, long origin)
    {
        _form = form;
        _lever = lever;
        _wholeSelection = wholeSelection;
        _clock = clock;
        _origin = origin;
    }

    ComparisonMeter() { _form = _lever = ""; _clock = () => 0; _unmetered = true; }

    /// <summary>A meter that never refuses, for a caller that is not a tool call.</summary>
    internal static ComparisonMeter Unmetered() => new();

    /// <summary>Marks the first chunk's start, after each pole's one-time work.</summary>
    internal void Start() => _start = _clock();

    /// <summary>The refusal after a chunk when time spent plus the projection passes the budget, else null.</summary>
    internal string? Check(int rowsRead, int totalRows)
    {
        if (rowsRead > _lastRead) { _chunks++; _lastRead = rowsRead; }
        // Never on the first chunk that read, and never with one chunk left: that work is kept.
        if (_unmetered || _chunks < 2 || totalRows - rowsRead <= RecordReads.ComparisonChunkRows) return null;
        long now = _clock();
        double spent = Millis(now - _origin);
        double perRow = Millis(now - _start) / rowsRead;
        double projected = spent + perRow * (totalRows - rowsRead);
        if (projected <= RenderBudget.CeilingMillis) return null;
        double before = Millis(_start - _origin);
        var head = $"this {_form} measured {RenderBudget.PerRowText(perRow)} a row over its first {rowsRead:N0} records, " +
                   $"about {Minutes(projected)} minutes for all {totalRows:N0}, past the ten-minute budget";
        int fits = perRow <= 0 ? 0 : (int)Math.Floor((FitsMargin * RenderBudget.CeilingMillis - before) / perRow);
        if (fits <= 0)
            return head + $"; the selection and index build alone took about {Minutes(before)} minutes before the first " +
                   "record was compared, so narrow the selection itself (types=, plugins=, where=, or fewer formids=).";
        var figure = _wholeSelection ? $"narrow the selection to about {fits:N0} matches" : $"about {fits:N0} rows fit at that rate";
        return head + $" ({figure}); {_lever}";
    }

    static double Millis(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Minutes to a tenth, rounded up, so a projection past the budget never prints as ten.</summary>
    static string Minutes(double ms) => (Math.Ceiling(ms / 6_000) / 10).ToString("0.0");
}
