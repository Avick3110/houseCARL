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

    /// <summary>The floor check every capped text read render closes on (#986). A body is laid inside what the cap
    /// leaves, so a finished render over its cap is its floor alone: the header and the notices it owes. Only here is
    /// that floor exact, since what a render holds back before its body is the widest those notices can be. Such a
    /// call is refused, naming the least max_chars the same call fits: the floor grows with the max_chars it prints
    /// back and the caveat share it grants, so the render is re-measured at the floor until it fits there, and then
    /// the least cap between the refused one and that is searched for.</summary>
    /// <param name="renderAt">the same call at another max_chars (a spilling lane's through
    /// <see cref="Artifacts.AtCap"/>); null for a to_file= manifest, whose file already landed, so it is settled with
    /// the overrun notice rather than refused.</param>
    /// <param name="nextCall">how much wider the same call can print next time, added to the cap it names.</param>
    /// <param name="alsoTry">a second way out, appended to the remedy.</param>
    public static string Hold(string response, int cap, Func<int, string>? renderAt, int nextCall = 0, string alsoTry = "")
    {
        // A render measured for the floor answers raw: its own over-cap arm is the question being asked.
        if (response.Length <= cap || _measuring || IsFloorRefusal(response)) return response;
        if (renderAt is null) return Settle(response, cap);
        int floor = response.Length, least;
        _measuring = true;
        try
        {
            bool fits = false;
            for (int i = 0; i < 16 && !fits; i++)
            {
                int at = renderAt(floor).Length;
                if (at <= floor) fits = true;
                else floor = at;
            }
            // A floor that never settled names no cap: the call is refused saying so, never shipped over the cap.
            if (!fits)
                return FloorLead + cap + " is too small for this response, and no max_chars up to " + floor +
                       " was found that it fits: raise max_chars past " + floor + alsoTry + ".";
            // The least cap that fits lies between the refused cap and the floor; the high end always fits.
            int lo = cap;
            least = floor;
            while (least - lo > 1)
            {
                int mid = lo + (least - lo) / 2;
                if (renderAt(mid).Length <= mid) least = mid;
                else lo = mid;
            }
        }
        finally { _measuring = false; }
        return FloorLead + cap + " is too small for this response, which carries its header and the notices it owes " +
               "whatever the budget: raise max_chars to at least " + (least + nextCall) + alsoTry + ".";
    }

    /// <summary>How much wider a records or asset_status call can print on the next call: its read timing (three more
    /// digits of milliseconds) and a spill file's name taking a -NN counter where it is printed, at most three places.
    /// An allowance, not a bound: a next call four digits slower, or a counter past 99, is wider still.</summary>
    internal const int NextCallGrowth = 3 + 3 * 3;

    [ThreadStatic] static bool _measuring;


    const string FloorLead = "error: max_chars=";   // Wire.RefusalPrefix, then the knob

    /// <summary>What a filtered render adds to its refusal's remedy: the filter's own lines are part of the floor.</summary>
    public const string OmitFilter = ", or omit filter=, whose own lines are part of what it carries";

    /// <summary>Whether a render handed back <see cref="Hold"/>'s refusal, so a caller passes it on bare, with no
    /// trailer appended. A refused render still reports its cut, so a spilling lane spills and renders again: that
    /// refusal names the floor with the spill block in it, and the spill it would have named is removed.</summary>
    public static bool IsFloorRefusal(string rendered) => rendered.StartsWith(FloorLead, StringComparison.Ordinal);

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
