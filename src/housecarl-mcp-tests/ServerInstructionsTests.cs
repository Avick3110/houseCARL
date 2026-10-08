using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The standing context — the instructions string the server publishes on <c>initialize</c>, which every session is
/// handed whether or not a skill loads. Held on the SERVED string, the way
/// <see cref="PublishedDescriptionBoundTests"/> holds the descriptions <c>tools/list</c> publishes, not on the source
/// literal in <c>Program.cs</c>.
///
/// <para>Claude Code delivers the first 2,048 characters of the instructions and appends "[truncated]", so the
/// string is bounded there; at 3,173 characters the enable and generated-output rules never reached a session.</para>
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
    public void TheStandingInstructionsAreNotEmpty()
    {
        var text = _s.PublishedInstructions;

        Assert.False(string.IsNullOrWhiteSpace(text),
            "initialize published no instructions — the standing context is what a session gets before any skill " +
            "loads, so an empty string is a silent loss, not a smaller payload.");
    }

    [Fact]
    public void TheInstructionsFitClaudeCodesCutOf2048Characters()
    {
        var length = _s.PublishedInstructions.Length;
        _out.WriteLine($"ServerInstructions: {length} characters (Claude Code cut {PublishedDescriptionBoundTests.Bound})");

        Assert.True(length <= PublishedDescriptionBoundTests.Bound,
            $"The published instructions are {length} characters; Claude Code delivers the first " +
            $"{PublishedDescriptionBoundTests.Bound} and drops the rest. Shorten them, or move a tool-specific line into that " +
            "tool's description.");
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
