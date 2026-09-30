using System.Text;

namespace HousecarlMcp;

/// <summary>The cap a cut notice names and the response may not exceed, beside the room content has once the tail
/// is charged. The bound holds by layout, never by trimming; contract in docs/architecture/render-budget.md.</summary>
internal readonly record struct RenderCap(int Cap, int Budget)
{
    public static RenderCap For(int cap, int trailer) => new(cap, Math.Max(cap - trailer, 0));

    public bool Fits(StringBuilder sb, int length) => sb.Length + length <= Budget;

    /// <summary>Appends <paramref name="unit"/> only if it fits whole. False means nothing was written.</summary>
    public bool TryAppend(StringBuilder sb, string unit)
    {
        if (!Fits(sb, unit.Length)) return false;
        sb.Append(unit);
        return true;
    }

    public RenderCap Less(int trailer) => new(Cap, Math.Max(Budget - trailer, 0));

    /// <summary>The max_chars a whole-first pass renders at: no reserve bites and no unit crosses it, so the render
    /// is the complete answer.</summary>
    internal const int Whole = int.MaxValue / 2;

    /// <summary>The max_chars of a whole-first pass bounded by <paramref name="bound"/>: no reserve bites, and the render
    /// stops laying units once it is past the bound (<see cref="Past"/>), so the pass costs what the bound does.</summary>
    internal static int WholeWithin(int bound) => bound <= int.MaxValue - Whole - 1 ? Whole + 1 + bound : Whole;

    /// <summary>Whether a render at <paramref name="cap"/> is a bounded whole-first pass that has laid past its bound,
    /// so it can stop: what it returns is then wider than the bound, and is read as not fitting.</summary>
    internal static bool Past(int cap, int laid) => cap > Whole && laid > cap - Whole - 1;

    /// <summary>The whole answer when it fits <paramref name="n"/>, else null, from a pass bounded by n.</summary>
    internal static string? WholeAt(int n, Func<int, string> at) => at(WholeWithin(n)) is var w && w.Length <= n ? w : null;

    /// <summary>How many times <see cref="Hold"/> re-renders at the length the last render came back at.</summary>
    internal const int GrowRounds = 8;

    /// <summary>A text render over data already read, whole first (#986): the complete answer when it fits
    /// <paramref name="cap"/>, else the render at the cap, refused by <see cref="Hold"/> when that is over it. Every
    /// pass is bounded by the cap it is asked about, not by the size of the answer.</summary>
    /// <param name="at">the same call's render at a given max_chars; it re-renders only, never reads.</param>
    /// <param name="nextCall">how much wider the same call can print next time, added before the cap is measured.</param>
    public static string Capped(int cap, Func<int, string> at, Func<string>? alsoTry = null, string epochLine = "",
                                int nextCall = 0)
        => WholeAt(cap, at) ?? Hold(at(cap), cap, at, out _, alsoTry, epochLine, n => WholeAt(n, at), nextCall);

    /// <summary>Whether the same call is served at <paramref name="cap"/>: its render at the cap fits, or its whole
    /// answer does. At most two renders, each bounded by the cap, and no floor search.</summary>
    public static bool Serves(int cap, Func<int, string> at) => at(cap).Length <= cap || WholeAt(cap, at) is not null;

    /// <summary>The floor check a capped text render closes on (#986). A body is laid inside what the cap leaves, so a
    /// render over its cap is its header and the notices it owes: the call is refused, naming a max_chars the same call
    /// was measured to fit. That cap is found by growing only: re-render at the length the render came back at, until
    /// it fits the length it was given (sufficient rather than tight, the #546 ruling).</summary>
    /// <param name="renderAt">the same call at another max_chars, as the next call would render it.</param>
    /// <param name="alsoTry">a second way out, appended to the remedy; asked only of a refused call.</param>
    /// <param name="epochLine">the lane's epoch stamp, where its post-capture refusals carry one.</param>
    /// <param name="wholeAt">the complete answer when it fits a given max_chars, else null (<see cref="WholeAt"/>): a
    /// whole-first call serves it at any cap it fits, so once it fits a round the cap named is its own width.</param>
    /// <param name="nextCall">how much wider the same call can print next time (<see cref="NextCallGrowth"/>): the cap
    /// named is one the render was measured to fit with that much room still free.</param>
    public static string Hold(string response, int cap, Func<int, string> renderAt, out bool refused,
                              Func<string>? alsoTry = null, string epochLine = "", Func<int, string?>? wholeAt = null,
                              int nextCall = 0)
    {
        refused = response.Length > cap;
        if (!refused) return response;
        var also = alsoTry?.Invoke() ?? "";
        int n = response.Length + nextCall;
        for (int i = 0; i < GrowRounds; i++)
        {
            if (wholeAt?.Invoke(n) is { } whole) return TooSmall(cap, whole.Length + nextCall, also, epochLine);
            // Fits with the next call's room still free, or grows to the length it came back at plus that room.
            int at = renderAt(n).Length + nextCall;
            if (at <= n) return TooSmall(cap, n, also, epochLine);
            n = at;
        }
        // A render that grows with every cap it is given names no cap: refused saying so, never shipped over the cap.
        return FloorLead + cap + " is too small for this response, and it did not settle on a max_chars it fits: " +
               "re-rendered " + GrowRounds + " times at the length it came back at, it still ran past " + n +
               ", so no max_chars can be named for it: narrow the call" + also + "." + epochLine;
    }

    /// <summary>The floor refusal: the cap the call was given, and the cap it was measured to fit, named as the value to
    /// pass and never as a floor: a render whose caveats take a share of the cap admits a whole caveat line as the cap
    /// grows, so a cap a little above the named one can be refused again (it then names one that serves).</summary>
    internal static string TooSmall(int cap, int fits, string alsoTry = "", string epochLine = "") =>
        FloorLead + cap + " is too small for what this response carries whatever the budget (its header and the " +
        "notices it owes): this call, as measured, fits max_chars=" + fits + ", so pass max_chars=" + fits +
        alsoTry + "." + epochLine;

    const string FloorLead = "error: max_chars=";   // Wire.RefusalPrefix, then the knob

    /// <summary>How much wider a records or asset_status call can print the next time it is made: its read timing three
    /// digits wider, and its spill file's name taking a -NN counter in the three places the spill block prints it. An
    /// allowance, not a bound: a next call four digits slower, or a counter past 99, is wider still.</summary>
    internal const int NextCallGrowth = 3 + 3 * 3;

    /// <summary>What a filtered render adds to its refusal's remedy, when the unfiltered render fits the cap it was given.</summary>
    public const string OmitFilter = ", or omit filter=, whose own lines are part of what it carries";

    /// <summary>The over-cap arm of a render that is not refused, and it says so: a write's report, whose write already
    /// happened, and a to_file= manifest, whose file already landed. The notice settles to a fixed point because it is
    /// part of the response whose length it states (#361).</summary>
    public static string Settle(string response, int cap)
    {
        if (response.Length <= cap) return response;
        var notice = Say(response.Length, cap);
        for (int i = 0; i < 4; i++)
        {
            var next = Say(response.Length + notice.Length, cap);
            bool same = next.Length == notice.Length;
            notice = next;
            if (same) break;
        }
        return response + notice;
    }

    static string Say(int length, int cap) => "\n[!] " + Overran(length, cap);

    /// <summary>The one sentence either transport closes an over-cap response with, so text and json state one
    /// answer: the response's length, the cap it was given, and the cap THIS response would have fitted in. A wider
    /// cap renders more rows, so that number is about this answer and not a promise about the next call; the merged
    /// sweep's own twin adds the growth term for that. The json twin is <c>max_chars_overrun</c>, written by
    /// <c>JsonWire.WriteCapOverrun</c>.</summary>
    internal static string Overran(int length, int cap) =>
        "this response is " + length + " chars, over the max_chars=" + cap + " it was given: what it must carry " +
        "whatever the budget — its header, the notices it owes, its accounting — does not fit in that many, so raise " +
        "max_chars to at least " + length + ".";
}
