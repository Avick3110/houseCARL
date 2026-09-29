using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The Nexus description renderer never splits a surrogate pair when it clamps, and decodes <c>&amp;amp;</c> after
/// every other entity. Migrated from the <c>render-clamp-guard</c> probe; each test carries the probe arm's wording.
/// Pure string transforms through <c>Render.OneLine</c> and <c>Render.StripMarkup</c>, no network.</summary>
[Trait("tier", "unit")]
public sealed class NexusRenderClampTests
{
    // U+1F600, one astral char: two UTF-16 code units.
    static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    // Probe arm 1: "OneLine: emoji at the clamp boundary leaves no lone surrogate".
    [Fact]
    public void OneLineEmojiAtTheClampBoundaryLeavesNoLoneSurrogate()
    {
        var output = Render.OneLine(new string('a', 39) + Emoji, 40);
        Assert.False(HasLoneSurrogate(output));
        // The clamp ran and dropped the whole pair: no lone half, and not the unclamped emoji either.
        Assert.Equal(new string('a', 39) + "…", output);
    }

    // Probe arm 2: "StripMarkup cap path: astral char at the cut leaves no lone surrogate".
    [Fact]
    public void StripMarkupCapPathAstralCharAtTheCutLeavesNoLoneSurrogate()
    {
        // No space in the last 200 chars, so the word-boundary backup is skipped and the char clamp is the cut.
        var output = Render.StripMarkup(new string('a', 399) + Emoji, 400);
        Assert.False(HasLoneSurrogate(output));
        Assert.Contains("truncated", output);
        Assert.Equal(new string('a', 399), output[..output.IndexOf('\n')]);
    }

    // Probe arm 3: "entity order: &amp;lt; decodes to &lt;".
    [Fact]
    public void EntityOrderDoubleEncodedLtDecodesToTheLiteralEntity() =>
        Assert.Equal("&lt;", Render.StripMarkup("&amp;lt;", 400));

    // Probe arm 4: "entity regression: single &amp; still renders &".
    [Fact]
    public void EntityRegressionSingleAmpStillRendersAmpersand() =>
        Assert.Equal("Mod A & Mod B", Render.StripMarkup("Mod A &amp; Mod B", 400));

    // Not a probe arm: the ellipsis follows the clamped text with no space left before it.
    [Fact]
    public void OneLineTrimsTheClampedTextBeforeTheEllipsis() =>
        Assert.Equal("aaa…", Render.OneLine("aaa bbb", 4));

    // Not a probe arm: HTML tags are stripped before the entities decode, so a decoded "&lt;" survives as text.
    [Fact]
    public void StripMarkupStripsHtmlTagsAndDecodesLt() =>
        Assert.Equal("bold <3", Render.StripMarkup("<b>bold</b> &lt;3", 400));

    // Not a probe arm: OneLine folds line breaks and space runs into single spaces.
    [Fact]
    public void OneLineFoldsLineBreaksAndSpaceRuns() =>
        Assert.Equal("a b c", Render.OneLine("a\r\nb   c", 50));

    // True if the string holds an unpaired surrogate: a high not followed by a low, or a stray low.
    static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }
}
