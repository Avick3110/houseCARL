using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The standing context — the instructions string the server publishes on <c>initialize</c>, which every session is
/// handed whether or not a skill loads. Held on the SERVED string, the way
/// <see cref="PublishedDescriptionBoundTests"/> holds the descriptions <c>tools/list</c> publishes, not on the source
/// literal in <c>Program.cs</c>.
///
/// <para>Its length is MEASURED here and written to the test output, because the fold ledger states a length and a
/// stated length nobody measures is a guess. There is no client cut to hold it under — the cut is on tool
/// descriptions — so the number is recorded rather than bounded. Measured 3,173 characters: the ledger's 2,875 plus
/// 121 for the write-into-an-existing-mod clause and 137 for the generator-input exception, both from review, less 2
/// for the CID clause dropped from the routing sentence, plus 42 for the SPID form list restored to that sentence on
/// review of PR #649.</para>
///
/// <para>The three cross-skill facts folded in are held by their lead phrase and held to ONE occurrence: the point of
/// folding them here was that they are stated once, in the one place a session always sees.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class ServerInstructionsTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public ServerInstructionsTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    [Fact]
    public void TheStandingInstructionsAreServedAndMeasured()
    {
        var text = _s.PublishedInstructions;
        _out.WriteLine($"ServerInstructions: {text.Length} characters");

        Assert.False(string.IsNullOrWhiteSpace(text),
            "initialize published no instructions — the standing context is what a session gets before any skill " +
            "loads, so an empty string is a silent loss, not a smaller payload.");
    }

    [Theory]
    [InlineData("RUNTIME DISTRIBUTION LAYERS")]
    [InlineData("NOTHING houseCARL WRITES WINS UNTIL IT IS ENABLED")]
    [InlineData("NEVER COPY GENERATED OUTPUT")]
    public void EachFoldedFactIsStatedOnce(string lead)
    {
        var text = _s.PublishedInstructions;
        var occurrences = text.Split(lead).Length - 1;

        Assert.True(occurrences == 1,
            $"'{lead}' appears {occurrences} time(s) in the published instructions; it is one of the three " +
            "cross-skill facts the standing context carries, and it belongs there exactly once.");
    }
}
