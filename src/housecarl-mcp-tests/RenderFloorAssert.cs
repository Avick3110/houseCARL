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
    /// fits it; returns that render. <paramref name="drift"/> is for a lane whose floor carries its own timing or a
    /// spill file's name, which a second call can print a few chars wider: there a re-refusal within that many chars
    /// of the named cap is followed once.</summary>
    public static string RefusesAndTheNamedCapFits(string text, int cap, Func<int, string> renderAt, int drift = 0)
    {
        Assert.True(IsFloorRefusal(text), $"at max_chars={cap} expected a floor refusal, got {text.Length} chars: {Head(text)}");
        Assert.StartsWith(Lead + cap + " ", text);
        Assert.DoesNotContain("\n", text);   // one sentence, not a reply with a note
        int named = Named(text);
        Assert.True(named > cap, $"the refusal at max_chars={cap} names {named}");
        var at = renderAt(named);
        if (drift > 0 && IsFloorRefusal(at) && Named(at) - named <= drift) at = renderAt(named = Named(at));
        Assert.False(IsFloorRefusal(at), $"the render at the named max_chars={named} refused: {Head(at)}");
        Assert.True(at.Length <= named, $"the render at the named max_chars={named} is {at.Length} chars");
        return at;
    }

    /// <summary>Either arm: inside the cap, or refused with a cap that fits.</summary>
    public static void FitsOrRefuses(string text, int cap, Func<int, string> renderAt, int drift = 0)
    {
        if (IsFloorRefusal(text)) RefusesAndTheNamedCapFits(text, cap, renderAt, drift);
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

    static string Head(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
