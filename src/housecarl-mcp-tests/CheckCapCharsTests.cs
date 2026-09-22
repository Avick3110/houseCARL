using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Xunit;

using static HousecarlMcpTests.CheckErrorsFixtures;

namespace HousecarlMcpTests;

/// <summary>The check family's cap counts CHARACTERS, the unit <c>max_chars</c> and the overrun notice are stated
/// in. It used to count UTF-8 bytes, and the two agreed only because the json writer escaped every non-ASCII
/// character to <c>\uXXXX</c>; the moment a name rode unescaped, a response's notice reported a length that was
/// not its own (#754). Every fact here is asked of a response with non-ASCII in it, which is the only kind that
/// can tell the two units apart.</summary>
public class CheckCapCharsTests
{
    const string AccentedPlugin = "Épée dAcier.esp";
    const string JapaneseEditorId = "鋼の剣_ダングリング";

    static ErrorCheckResult NonAsciiResult()
    {
        var report = new PluginErrors(
            AccentedPlugin,
            new[] { new DanglingRef(FormKey.Factory("000800:HcA.esp"), "Weapon", JapaneseEditorId,
                                    FormKey.Factory("0E0E0E:Skyrim.esm")) },
            Array.Empty<string>(), 0, Array.Empty<string>(), null);
        return Result(reports: new[] { report }, scanned: 1, totalDangling: 1,
                      bySource: new[] { new SweepCount(AccentedPlugin, 1) });
    }

    static int Stated(string notice, string marker) =>
        int.Parse(Regex.Match(notice, marker + @"(\d+)").Groups[1].Value);

    /// <summary>The fixture bites: the response really does carry the characters as themselves, so its character
    /// count and its UTF-8 byte count differ. Without this the facts below pass on an all-ASCII document that
    /// could never tell the old accounting from the new one.</summary>
    [Fact]
    public void TheResponseCarriesTheNonAsciiNamesUnescapedSoCharsAndBytesDiffer()
    {
        var json = Json(NonAsciiResult(), 20_000);

        Assert.Contains(AccentedPlugin, json, StringComparison.Ordinal);
        Assert.Contains(JapaneseEditorId, json, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(json) > json.Length,
                    "the document is all ASCII — nothing here could tell bytes from characters");
    }

    /// <summary>Wherever the overrun notice fires on a non-ASCII response, the length it states is the response's
    /// OWN length in characters, and the cap it names clears the notice in one step. Where it does not fire, the
    /// response is inside the cap it was given. This is the probe arm MATRIX-REMEDY-CLEARS-IN-ONE-STEP asks of
    /// every shape, asked here of the one shape the probe's fixtures have no non-ASCII for.</summary>
    [Fact]
    public void TheOverrunNoticeStatesItsOwnLengthAndClearsInOneStep()
    {
        var r = NonAsciiResult();
        int notices = 0, inside = 0;
        int whole = Json(r, 20_000).Length;

        for (int cap = 1; cap <= whole + 40; cap++)
        {
            var json = Json(r, cap);
            var root = JsonDocument.Parse(json).RootElement;

            if (!root.TryGetProperty("max_chars_overrun", out var n))
            {
                inside++;
                Assert.True(json.Length <= cap,
                            $"max_chars={cap}: a {json.Length}-char document with no overrun notice");
                continue;
            }

            notices++;
            var notice = n.GetString()!;
            Assert.Equal(json.Length, Stated(notice, "This response is "));

            int raiseTo = Stated(notice, "raise max_chars to at least ");
            var cleared = Json(r, raiseTo);
            Assert.False(JsonDocument.Parse(cleared).RootElement.TryGetProperty("max_chars_overrun", out _),
                         $"max_chars={cap} named {raiseTo}, which did not clear the notice");
            Assert.True(cleared.Length <= raiseTo, $"the cap {raiseTo} it named still does not hold it");
        }

        Assert.True(notices > 0 && inside > 0, $"the band did not straddle: notices={notices} inside={inside}");
    }

    /// <summary>The bound, pinned: the encoder widens the Basic Multilingual Plane only, so a character above it
    /// still rides as its <c>\uXXXX\uXXXX</c> surrogate pair — and the accounting is right on that side of the plane
    /// too, because an escape is ASCII and is counted as the characters it is. Here so the bound is a fact the suite
    /// states rather than a sentence in a comment.</summary>
    [Fact]
    public void AnAstralCharacterStillRidesAsEscapesAndIsCountedAsWritten()
    {
        var report = new PluginErrors("Épée 🗡.esp",
            new[] { new DanglingRef(FormKey.Factory("000800:HcA.esp"), "Weapon", "🗡",
                                    FormKey.Factory("0E0E0E:Skyrim.esm")) },
            Array.Empty<string>(), 0, Array.Empty<string>(), null);
        var r = Result(reports: new[] { report }, scanned: 1, totalDangling: 1);

        var json = Json(r, 20_000);
        Assert.DoesNotContain("🗡", json, StringComparison.Ordinal);
        Assert.Contains("ud83d", json, StringComparison.OrdinalIgnoreCase);   // the high half of the pair
        Assert.Equal("🗡", JsonDocument.Parse(json).RootElement
                               .GetProperty("families").GetProperty("errors")
                               .GetProperty("plugins")[0].GetProperty("dangling")[0]
                               .GetProperty("source_editorid").GetString());

        var notice = JsonDocument.Parse(Json(r, 200)).RootElement.GetProperty("max_chars_overrun").GetString()!;
        Assert.Equal(Json(r, 200).Length, Stated(notice, "This response is "));
    }

    /// <summary>The merged check's notice is read by the SHARED matcher too, which is what "one member, one grammar"
    /// buys a consumer: this document capitalises its opening where <c>RenderCap.Overran</c> does not, so the shared
    /// reader has to be case-insensitive, and this arm is what fails if it stops being. The RETRY number stays this
    /// class's own business — check adds the growth term, so one-step clearing is the arm above.</summary>
    [Fact]
    public void TheSharedReaderOfTheOverrunMemberReachesTheCheckDocumentToo()
    {
        var json = Json(NonAsciiResult(), 200);

        JsonOverrun.StatesItsLengthAndCap(json, 200);
    }

    /// <summary>The text lane says the same length about the same sweep — it counts its StringBuilder, which was
    /// always characters, so the two transports agreeing is what "one cap, one unit" means.</summary>
    [Fact]
    public void TheTextLaneStatesItsOwnLengthOnTheSameSweep()
    {
        var text = Text(NonAsciiResult(), 200);

        Assert.Contains(AccentedPlugin, text, StringComparison.Ordinal);
        Assert.Equal(text.Length, Stated(text, "This response is "));
    }
}
