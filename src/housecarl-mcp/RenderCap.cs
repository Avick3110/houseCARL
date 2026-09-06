using System.Text;

namespace HousecarlMcp;

/// <summary>The two numbers every <c>max_chars</c>-bounded render carries, together, so neither can be used where the
/// other belongs: <see cref="Cap"/> is the max_chars the caller passed — the number a cut notice must name and the
/// ceiling the finished response may not exceed — and <see cref="Budget"/> is the room content actually has once
/// everything written after it is charged (the trailer the caller appends, and the cut notice itself).
///
/// <para>The bound holds by LAYOUT, not by trimming: a unit is written only when it fits the budget whole, so no
/// rendered string is ever cut mid-token. What did not fit is counted in the notice, never dropped in silence.</para>
/// </summary>
internal readonly record struct RenderCap(int Cap, int Budget)
{
    /// <summary>The budget left after charging <paramref name="trailer"/> characters of tail. Never below zero: a
    /// budget the fixed part already exceeds renders no units at all, which is the honest answer at that cap.</summary>
    public static RenderCap For(int cap, int trailer) => new(cap, Math.Max(cap - trailer, 0));

    /// <summary>Room for <paramref name="length"/> more characters in <paramref name="sb"/>.</summary>
    public bool Fits(StringBuilder sb, int length) => sb.Length + length <= Budget;

    /// <summary>Appends <paramref name="unit"/> only if it fits whole. False means nothing was written.</summary>
    public bool TryAppend(StringBuilder sb, string unit)
    {
        if (!Fits(sb, unit.Length)) return false;
        sb.Append(unit);
        return true;
    }

    /// <summary>Charges a further <paramref name="trailer"/> characters against the same cap — for a render whose
    /// tail is discovered in pieces.</summary>
    public RenderCap Less(int trailer) => new(Cap, Math.Max(Budget - trailer, 0));

    /// <summary>The one arm a bounded render may still exceed its cap on, and it says so: a max_chars smaller than
    /// what the response must carry whatever the budget — its header, its alarms, its accounting — leaves no answer
    /// inside the ceiling, so the answer ships and the overrun is named with the number that clears it in one step.
    /// The same shape the check sweep's <c>max_chars_overrun</c> has carried since #537; nothing is ever trimmed
    /// mid-token to fake the bound. The sentence is part of the response whose length it states, so it settles to a
    /// fixed point rather than quoting a number the notice itself then invalidates.</summary>
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

    static string Say(int length, int cap) =>
        "\n[!] this response is " + length + " chars, over the max_chars=" + cap + " it was given: what it must carry " +
        "whatever the budget — its header, the notices it owes, its accounting — does not fit in that many, so raise " +
        "max_chars to at least " + length + ".";
}
