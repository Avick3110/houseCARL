using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The SELECT-a-family half of <c>housecarl_skse</c>: which family <c>findings=</c> picks, what a call that names
/// none of them ran, and the two refusals. All four are pure over their inputs, so they are driven directly rather
/// than through a live MO2 instance — the three family renders themselves are covered by the generator's
/// skse/native-pairing/config probes against synthetic data.
/// </summary>
[Trait("tier", "unit")]
public sealed class SkseFamilySelectionTests
{
    static SkseTools.SkseFamily Parse(string? findings)
    {
        Assert.True(SkseTools.TryParseFamily(findings, out var family, out var error), error);
        Assert.Null(error);
        return family;
    }

    [Fact]
    public void FindingsOmitted_RunsTheInventoryFamily()
    {
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse(null));
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse(""));
        Assert.Equal(SkseTools.SkseFamily.Inventory, Parse("   "));
    }

    [Theory]
    [InlineData("inventory", "Inventory")]
    [InlineData("pairing", "Pairing")]
    [InlineData("config", "Config")]
    [InlineData("  PAIRING  ", "Pairing")]
    [InlineData("Config", "Config")]
    public void EachFamilyTokenSelectsItsFamily_CaseAndPaddingInsensitive(string token, string expected)
        => Assert.Equal(expected, Parse(token).ToString());

    /// <summary>An unknown value is refused naming all three spellings — never quietly defaulted to inventory, which
    /// would answer a question the caller did not ask and call it the answer.</summary>
    [Theory]
    [InlineData("inventories")]
    [InlineData("skse_inventory")]
    [InlineData("errors")]
    [InlineData("inventory,pairing")]
    public void AnUnknownFindingsValueIsRefused_NamingTheThreeFamilies(string token)
    {
        Assert.False(SkseTools.TryParseFamily(token, out _, out var error));
        Assert.NotNull(error);
        Assert.StartsWith("error: ", error);
        Assert.Contains(token.Trim(), error);
        foreach (var family in new[] { "inventory", "pairing", "config" })
            Assert.Contains($"'{family}'", error);
        Assert.Equal(1, OneFamilyPerCallCount(error!));
    }

    /// <summary>How many times the refusal says the one-family rule. It is one rule, so it is said once — a second
    /// telling is the reader's cue that they missed something the first time.</summary>
    static int OneFamilyPerCallCount(string error) =>
        System.Text.RegularExpressions.Regex.Matches(error, "family per call",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

    /// <summary>A list-shaped value carries the housecarl_check habit, where findings= names several families at once.
    /// The refusal names the SHAPE as well as the word, so the caller knows to drop the list rather than hunt for a
    /// spelling that does not exist.</summary>
    [Theory]
    [InlineData("inventory,pairing")]
    [InlineData("[\"inventory\"]")]
    public void AListShapedFindingsValueIsRefusedNamingTheShape(string token)
    {
        Assert.False(SkseTools.TryParseFamily(token, out _, out var error));
        Assert.Contains("takes ONE value, not a list", error);
        Assert.Equal(1, OneFamilyPerCallCount(error!));
    }

    /// <summary>peek= reads one DLL image, which only the inventory family looks at. Passing it with another family is
    /// refused rather than ignored: a silently dropped flag reads as a peek that found nothing.</summary>
    [Fact]
    public void PeekOnANonInventoryFamilyIsRefused_AndOnInventoryIsNot()
    {
        foreach (var family in new[] { SkseTools.SkseFamily.Pairing, SkseTools.SkseFamily.Config })
        {
            var error = SkseTools.PeekFamilyError(peek: true, family);
            Assert.NotNull(error);
            Assert.StartsWith("error: ", error);
            Assert.Contains(family.ToString().ToLowerInvariant(), error);
            Assert.Contains("findings='inventory'", error);
            Assert.Null(SkseTools.PeekFamilyError(peek: false, family));
        }
        Assert.Null(SkseTools.PeekFamilyError(peek: true, SkseTools.SkseFamily.Inventory));
    }

    /// <summary>Every response says which family ran and how to ask for the two it did not — the only thing that keeps
    /// the omitted-findings default from being a silent narrowing.</summary>
    [Theory]
    [InlineData("inventory", "pairing", "config")]
    [InlineData("pairing", "inventory", "config")]
    [InlineData("config", "inventory", "pairing")]
    public void TheFooterNamesTheFamilyThatRanAndTheSpellingOfBothThatDidNot(string mine, string first, string second)
    {
        var footer = SkseTools.FamilyFooter(Parse(mine));

        Assert.Contains($"this call ran findings='{mine}'", footer);
        Assert.Contains("NOT run:", footer);
        Assert.Contains($"findings='{first}'", footer);
        Assert.Contains($"findings='{second}'", footer);

        // The three retired spellings are gone from the surface, so no response may teach one of them.
        foreach (var retired in new[] { "skse_inventory", "native_pairing_audit", "skse_config_audit" })
            Assert.DoesNotContain(retired, footer, StringComparison.Ordinal);
    }

    /// <summary>Every refusal this one tool can return carries the same "error: " prefix, so a caller reading them
    /// cannot take one for an answer because it happened to start with a lowercase word.</summary>
    [Fact]
    public void AllThreeRefusalsCarryTheErrorPrefix()
    {
        SkseTools.TryParseFamily("errors", out _, out var famErr);
        Assert.StartsWith("error: ", famErr);
        Assert.StartsWith("error: ", SkseTools.PeekFamilyError(peek: true, SkseTools.SkseFamily.Config));
        Assert.StartsWith("error: ", SkseInventoryWire.PeekArgError(peek: true, filter: null));
    }

    /// <summary>Records which family render the dispatch called, so the findings= switch and the footer append can be
    /// driven without a live MO2 instance.</summary>
    sealed class RecordingRenders : SkseTools.IFamilyRenders
    {
        public string? Called;
        public int Cap;
        public SkseTools.FamilyCall Call;
        public string Inventory(SkseTools.FamilyCall c) { Called = "inventory"; Cap = c.Cap; Call = c; return "INVENTORY-BODY"; }
        public string Pairing(SkseTools.FamilyCall c) { Called = "pairing"; Cap = c.Cap; Call = c; return "PAIRING-BODY"; }
        public string Config(SkseTools.FamilyCall c) { Called = "config"; Cap = c.Cap; Call = c; return "CONFIG-BODY"; }
    }

    /// <summary>Each findings= value runs its OWN family's render, and the answer ends on that family's footer. Without
    /// this, swapping two arms of the switch changes every response and breaks no test.</summary>
    [Theory]
    [InlineData("inventory", "INVENTORY-BODY")]
    [InlineData("pairing", "PAIRING-BODY")]
    [InlineData("config", "CONFIG-BODY")]
    public void EachFamilyRunsItsOwnRenderAndTheAnswerEndsOnTheFooter(string token, string body)
    {
        var family = Parse(token);
        var renders = new RecordingRenders();

        var text = SkseTools.Dispatch(renders, family, filter: null, peek: false, max_chars: 0);

        Assert.Equal(token, renders.Called);
        Assert.StartsWith(body, text);
        Assert.EndsWith(SkseTools.FamilyFooter(family), text);
        Assert.Contains($"this call ran findings='{token}'", text);
    }

    /// <summary>The footer is paid for out of max_chars rather than appended past it, so the renders are handed a cap
    /// already short by its length — the one part of the bound the tool itself controls.</summary>
    [Fact]
    public void TheFooterLengthIsSubtractedFromTheCapHandedToTheRender()
    {
        var renders = new RecordingRenders();
        SkseTools.Dispatch(renders, SkseTools.SkseFamily.Pairing, filter: null, peek: false, max_chars: 5_000);
        Assert.Equal(5_000 - SkseTools.FamilyFooter(SkseTools.SkseFamily.Pairing).Length, renders.Cap);

        // A cap smaller than the footer never becomes zero or negative: the render still gets a usable bound.
        SkseTools.Dispatch(renders, SkseTools.SkseFamily.Pairing, filter: null, peek: false, max_chars: 1);
        Assert.True(renders.Cap >= 1);
    }

    /// <summary>Every TRANSPORT knob the dispatch takes reaches the render on the call it composes. Without this the
    /// window could be dropped between the published argument and the render and no test would notice: the renders
    /// are driven directly everywhere else, so they would still page correctly over a window nothing handed them.
    /// </summary>
    [Fact]
    public void TheTransportKnobsReachTheRenderOnTheCall()
    {
        var renders = new RecordingRenders();

        SkseTools.Dispatch(renders, SkseTools.SkseFamily.Inventory, filter: "SkyPatcher", peek: true, max_chars: 9_000,
                           json: false, window: new RowWindow(Offset: 4, Limit: 7));

        Assert.Equal("SkyPatcher", renders.Call.Filter);
        Assert.True(renders.Call.Peek);
        Assert.False(renders.Call.Json);
        Assert.Equal(new RowWindow(4, 7), renders.Call.Window);
    }

    /// <summary>format='json' reaches the render as the json flag AND suppresses the text footer — appending the
    /// footer's prose to a document would only break it.</summary>
    [Fact]
    public void AJsonCallSetsTheJsonFlagAndTakesNoFooter()
    {
        var renders = new RecordingRenders();

        var text = SkseTools.Dispatch(renders, SkseTools.SkseFamily.Config, filter: null, peek: false, max_chars: 9_000,
                                      json: true, window: RowWindow.All);

        Assert.True(renders.Call.Json);
        Assert.Equal("CONFIG-BODY", text);
        // The whole cap is the render's, since no footer is paid for out of it.
        Assert.Equal(9_000, renders.Call.Cap);
    }
}
