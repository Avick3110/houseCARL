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

    /// <summary>The max_chars a whole pass renders at: no reserve bites and no unit crosses it.</summary>
    internal const int Whole = int.MaxValue / 2;

    /// <summary>The complete answer when it fits <paramref name="n"/>, else null, from one pass bounded by n.</summary>
    internal static string? WholeAt(int n, Func<int, WholePass?, string> at)
    {
        var pass = new WholePass(n);
        var w = at(Whole, pass);
        return !pass.Stopped && w.Length <= n ? w : null;
    }

    /// <summary>How many times <see cref="Hold"/> re-renders at the length the last render came back at.</summary>
    internal const int GrowRounds = 8;

    /// <summary>A text render that stops early: whole first by a bounded pass, else the render at the cap, held.</summary>
    /// <param name="at">the same call's render at a max_chars, or the whole pass when handed one; it never reads.</param>
    /// <param name="nextCall">how much wider the same call can print next time, added before the cap is measured.</param>
    public static string Capped(int cap, Func<int, WholePass?, string> at, Func<string>? alsoTry = null,
                                string epochLine = "", int nextCall = 0)
        => WholeAt(cap, at) ?? Hold(at(cap, null), cap, n => at(n, null), out _, alsoTry, epochLine,
                                    n => WholeAt(n, at), nextCall);

    /// <summary>A text render that cannot stop early: its whole answer laid once per call and reused, else held.</summary>
    public static string CappedOnce(int cap, Func<int, string> at, Func<string>? alsoTry = null, string epochLine = "")
    {
        var whole = at(Whole);
        return whole.Length <= cap
            ? whole
            : Hold(at(cap), cap, at, out _, alsoTry, epochLine, n => whole.Length <= n ? whole : null);
    }

    /// <summary>Whether the same call is served at <paramref name="cap"/>: its render at the cap or its whole answer fits.</summary>
    public static bool Serves(int cap, Func<int, WholePass?, string> at)
        => at(cap, null).Length <= cap || WholeAt(cap, at) is not null;

    /// <summary>The floor check: a render over its cap is refused, naming a cap found by growing only.</summary>
    /// <param name="renderAt">the same call at another max_chars, as the next call would render it.</param>
    /// <param name="alsoTry">a second way out, appended to the remedy; asked only of a refused call.</param>
    /// <param name="epochLine">the lane's epoch stamp, where its post-capture refusals carry one.</param>
    /// <param name="wholeAt">the complete answer when it fits a given max_chars, else null.</param>
    /// <param name="nextCall">room left free in the cap named, for the next call printing wider.</param>
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
            int at = renderAt(n).Length + nextCall;
            if (at <= n) return TooSmall(cap, n, also, epochLine);
            n = at;
        }
        return FloorLead + cap + " is too small for this response, and it did not settle on a max_chars it fits: " +
               "re-rendered " + GrowRounds + " times at the length it came back at, it still ran past " + n +
               ", so no max_chars can be named for it: narrow the call" + also + "." + epochLine;
    }

    /// <summary>The floor refusal: the cap the call was given, and the cap it was measured to fit, named as the value to pass.</summary>
    internal static string TooSmall(int cap, int fits, string alsoTry = "", string epochLine = "") =>
        FloorLead + cap + " is too small for what this response carries whatever the budget (its header and the " +
        "notices it owes): this call, as measured, fits max_chars=" + fits + ", so pass max_chars=" + fits +
        alsoTry + "." + epochLine;

    const string FloorLead = "error: max_chars=";   // Wire.RefusalPrefix, then the knob

    /// <summary>How much wider a records or asset_status call can print next time: a timing 3 digits wider, a -NN spill counter.</summary>
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

/// <summary>One whole pass bounded by a cap: the render loop that stops past <see cref="Bound"/> records it here.</summary>
internal sealed class WholePass(int bound)
{
    /// <summary>The cap the pass asks about.</summary>
    internal int Bound { get; } = bound;

    /// <summary>Whether a render loop stopped laying units past the bound, so the render is not the whole answer.</summary>
    internal bool Stopped { get; private set; }

    /// <summary>Whether <paramref name="laid"/> chars are past the bound; true stops the pass and is recorded.</summary>
    internal bool Past(int laid) => Stopped |= laid > Bound;
}
