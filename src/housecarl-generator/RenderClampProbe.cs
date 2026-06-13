using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Render-clamp guard (2026-06-13 cosmetic sweep, render NOTEs N2 + N3) — locks two correctness
/// properties of the Nexus description renderer (HousecarlMcp.Render.StripMarkup / OneLine), fully
/// self-contained (pure string transforms, no game data, no network).
///
/// Arms:
///   1. SURROGATE-SAFE CLAMP (OneLine) — an astral char (emoji) straddling the clamp boundary must
///      not be split into a lone half-surrogate. OneLine clamps via ClampChars, so its output carries
///      no unpaired surrogate.
///   2. SURROGATE-SAFE CLAMP (StripMarkup cap path) — same property through the description-body
///      truncation, with no space in the last 200 chars so the word-boundary backup is skipped and the
///      raw char clamp is what runs.
///   3. ENTITY DECODE ORDER (&amp; LAST) — a double-encoded "&amp;lt;" (author wanted the visible text
///      "&lt;") must decode to "&lt;", not "<": &amp; is undone after every other entity so a decoded
///      entity's '&' can never re-trigger another replacement.
///   4. ENTITY REGRESSION — the common single-encoded "Mod A &amp; Mod B" still renders "Mod A & Mod B"
///      (passes under both orders; proves the reorder didn't regress the ordinary case).
///
/// Teeth (mutation-RED, verified at authoring): revert ClampChars to a naive s[..n] → arms 1+2 FAIL;
/// move ".Replace(\"&amp;\", \"&\")" back to the FRONT of the entity chain → arm 3 FAILS.
/// </summary>
internal static class RenderClampProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" render-clamp guard — surrogate-safe truncation + &amp;-last decode");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        const string Emoji = "😀"; // U+1F600 GRINNING FACE — one astral char, two UTF-16 code units

        // Arm 1 — OneLine clamps directly via ClampChars. Put the emoji's high half at index n-1 so a naive
        // clamp would orphan it. n=40, input is 39 'a' + emoji (length 41) → clamp at 40 splits the pair.
        var oneLineIn = new string('a', 39) + Emoji;
        var oneLineOut = Render.OneLine(oneLineIn, 40);
        Check(!HasLoneSurrogate(oneLineOut), "1. OneLine: emoji at the clamp boundary leaves no lone surrogate");

        // Arm 2 — StripMarkup's body cap. cap=400, input is 399 'a' + emoji (cleaned length 401 > 400), no space
        // in the last 200 chars so the word-boundary backup is skipped and the char clamp is what truncates.
        var stripIn = new string('a', 399) + Emoji;
        var stripOut = Render.StripMarkup(stripIn, 400);
        Check(!HasLoneSurrogate(stripOut), "2. StripMarkup cap path: astral char at the cut leaves no lone surrogate");

        // Arm 3 — double-encoded "&amp;lt;" must decode to the literal text "&lt;", not "<".
        var doubleEnc = Render.StripMarkup("&amp;lt;", 400);
        Check(doubleEnc == "&lt;", $"3. entity order: \"&amp;lt;\" decodes to \"&lt;\" (got \"{doubleEnc}\")");

        // Arm 4 — ordinary single-encoded ampersand still renders correctly (regression-safety, both orders).
        var single = Render.StripMarkup("Mod A &amp; Mod B", 400);
        Check(single == "Mod A & Mod B", $"4. entity regression: single \"&amp;\" still renders \"&\" (got \"{single}\")");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
        return fail;
    }

    /// <summary>True if the string contains an unpaired surrogate (a high surrogate not followed by a low,
    /// or a stray low surrogate) — i.e. a broken half-glyph.</summary>
    static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++; // valid pair — skip its low half
            }
            else if (char.IsLowSurrogate(s[i])) return true; // a low surrogate with no high before it
        }
        return false;
    }
}
