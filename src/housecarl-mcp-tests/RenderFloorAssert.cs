using System.Text.RegularExpressions;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a capped text render owes at any max_chars: it fits the cap, or it refuses naming a max_chars at which
/// the same render fits (#986). The floor's exact number is not pinned: it moves with every notice a render owes.</summary>
static class RenderFloorAssert
{
    const string Lead = "error: max_chars=";

    public static bool IsFloorRefusal(string text) => text.StartsWith(Lead, StringComparison.Ordinal);

    /// <summary>The max_chars a floor refusal names.</summary>
    public static int Named(string text) =>
        int.Parse(Regex.Match(text, @"raise max_chars to at least (\d+)").Groups[1].Value);

    /// <summary>The refusal at <paramref name="cap"/> is one sentence naming a larger cap, and the render at that cap
    /// fits it; returns that render.</summary>
    public static string RefusesAndTheNamedCapFits(string text, int cap, Func<int, string> renderAt)
    {
        Assert.True(IsFloorRefusal(text), $"at max_chars={cap} expected a floor refusal, got {text.Length} chars: {Head(text)}");
        Assert.StartsWith(Lead + cap + " ", text);
        Assert.DoesNotContain("\n", text);   // one sentence, not a reply with a note
        int named = Named(text);
        Assert.True(named > cap, $"the refusal at max_chars={cap} names {named}");
        var at = renderAt(named);
        Assert.False(IsFloorRefusal(at), $"the render at the named max_chars={named} refused: {Head(at)}");
        Assert.True(at.Length <= named, $"the render at the named max_chars={named} is {at.Length} chars");
        return at;
    }

    /// <summary>Either arm: inside the cap, or refused with a cap that fits.</summary>
    public static void FitsOrRefuses(string text, int cap, Func<int, string> renderAt)
    {
        if (IsFloorRefusal(text)) RefusesAndTheNamedCapFits(text, cap, renderAt);
        else Assert.True(text.Length <= cap, $"at max_chars={cap} the render is {text.Length} chars: {Head(text)}");
    }

    /// <summary>The ladder at the floor's edge: <paramref name="tooSmall"/> refuses, one below the named cap refuses,
    /// and the named cap serves inside it.</summary>
    public static void RefusesBelowAndServesAt(Func<int, string> renderAt, int tooSmall)
    {
        var refused = renderAt(tooSmall);
        RefusesAndTheNamedCapFits(refused, tooSmall, renderAt);
        int floor = Named(refused);
        var below = renderAt(floor - 1);
        Assert.True(IsFloorRefusal(below), $"one below the named cap, max_chars={floor - 1}, did not refuse: {Head(below)}");
    }

    /// <summary>The first cap from <paramref name="from"/> up the call serves inside it with <paramref name="cut"/>
    /// true: a cut a user can get, found rather than pinned, since the band a fixture cuts in moves with its width.</summary>
    public static (int Cap, string Text) ServedCut(Func<int, string> call, Func<string, bool> cut, int from = 100, int to = 10_000, int step = 10)
    {
        for (int cap = from; cap <= to;)
        {
            var r = call(cap);
            // A refusal names the least cap the call fits on this machine: the search resumes there.
            if (IsFloorRefusal(r)) { cap = Math.Max(cap + step, Named(r)); continue; }
            if (r.Length <= cap && cut(r)) return (cap, r);
            cap += step;
        }
        Assert.Fail($"no cap in {from}..{to} serves this call cut");
        return (0, "");
    }

    static string Head(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
