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
    /// call is refused, naming a max_chars the same render fits, found by rendering at the floor until it fits
    /// (the floor grows with the max_chars it prints back and the caveat share it grants).</summary>
    /// <param name="renderAt">the same render at another max_chars; null for a to_file= manifest, whose file already
    /// landed, so it is settled with the overrun notice rather than refused.</param>
    /// <param name="alsoTry">a second way out, appended to the remedy.</param>
    public static string Hold(string response, int cap, Func<int, string>? renderAt, string alsoTry = "")
    {
        // A render measured for the floor answers raw: its own over-cap arm is the question being asked.
        if (response.Length <= cap || _measuring || IsFloorRefusal(response)) return response;
        if (renderAt is null) return Settle(response, cap);
        int floor = response.Length;
        _measuring = true;
        try
        {
            int at = floor;
            for (int i = 0; i < 16 && (at = renderAt(floor).Length) > floor; i++) floor = at;
            // A render that fits there may be narrower than the floor it was measured at (it cut nothing, so it owes
            // no spill): the cap that holds it may be smaller, so it steps down while the smaller cap fits.
            for (int i = 0; i < 16 && at < floor && at > cap; i++)
            {
                int down = renderAt(at).Length;
                if (down > at) break;
                floor = at;
                at = down;
            }
        }
        finally { _measuring = false; }
        return FloorLead + cap + " is below the " + floor + " chars this response carries whatever the budget " +
               "(its header and the notices it owes), so raise max_chars to at least " + floor + alsoTry + ".";
    }

    [ThreadStatic] static bool _measuring;

    /// <summary>Runs <paramref name="call"/> with the floor check off, so a test can read the cut notices a render lays
    /// below its floor. The server never calls it.</summary>
    internal static string Unheld(Func<string> call)
    {
        bool was = _measuring;
        _measuring = true;
        try { return call(); }
        finally { _measuring = was; }
    }

    const string FloorLead = "error: max_chars=";   // Wire.RefusalPrefix, then the knob

    /// <summary>What a filtered render adds to its refusal's remedy: the filter's own lines are part of the floor.</summary>
    public const string OmitFilter = ", or omit filter=, whose own lines are part of that floor";

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
