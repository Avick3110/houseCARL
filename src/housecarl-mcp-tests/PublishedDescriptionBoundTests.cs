using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// Claude Code cuts a tool description at 2,048 characters and delivers parameter descriptions whole, so a
/// description longer than that loses its tail on the way to the caller. This holds the bound on the SERVED
/// surface: the description string <c>tools/list</c> actually publishes, not the source literal.
///
/// <para>The subject set is not hand-listed: the theory rows come off the checked-in name capture, which
/// <see cref="PublishedNameAnchorTests"/> holds equal to the published set in both directions — so a tool added
/// to the surface arrives here rather than quietly leaving the sweep short. (MemberData is evaluated at
/// discovery, before the fixture exists, which is why the rows cannot read the running server directly.)</para>
///
/// <para><see cref="StillOversized"/> is the shrinking list of tools whose descriptions have not been brought
/// under the bound yet; the Fact below holds each of them STILL over, so an entry cannot go stale.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedDescriptionBoundTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public PublishedDescriptionBoundTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    /// <summary>The client's cut.</summary>
    const int Bound = 2048;

    // Tools whose descriptions are still over the bound, one line each. Each description PR deletes its own line.
    static readonly HashSet<string> StillOversized = new(StringComparer.Ordinal)
    {
        ToolNames.CompactPlugin,
        ToolNames.Create,
        ToolNames.Forward,
        ToolNames.NifInspect,
        ToolNames.NifSet,
        ToolNames.Remove,
    };

    public static IEnumerable<object[]> EveryPublishedTool() =>
        PublishedNameAnchorTests.Captured().Select(n => new object[] { n });

    string Description(string tool)
    {
        Assert.True(_s.PublishedTools.TryGetValue(tool, out var t),
            $"'{tool}' is named in this test file but the server does not publish it — it was renamed or " +
            $"retired. Update its line in StillOversized and in '{PublishedNameAnchorTests.CapturePath}'.");
        return t.GetProperty("description").GetString()!;
    }

    [Theory]
    [MemberData(nameof(EveryPublishedTool))]
    public void EveryPublishedDescriptionFitsTheClientsCut(string tool)
    {
        var length = Description(tool).Length;
        _out.WriteLine($"{tool}: {length} characters (bound {Bound})");

        if (StillOversized.Contains(tool)) return;

        Assert.True(length <= Bound,
            $"{tool}'s published description is {length} characters; Claude Code shows the first {Bound} and " +
            "drops the rest. Move the tail into the parameter descriptions it belongs to — those arrive whole.");
    }

    /// <summary>The stale-entry guard: when a tool's description comes under the bound, its line above must go,
    /// or this fails. One Fact over the whole set rather than a row each, so that the last PR of the wave —
    /// which empties the set — leaves a test that passes on nothing rather than an empty-MemberData failure.</summary>
    [Fact]
    public void EveryToolNamedOversizedStillIs()
    {
        foreach (var tool in StillOversized.OrderBy(n => n, StringComparer.Ordinal))
        {
            var length = Description(tool).Length;
            _out.WriteLine($"{tool}: {length} characters (bound {Bound})");

            Assert.True(length > Bound,
                $"{tool}'s published description is {length} characters, which is inside the {Bound} bound — " +
                "delete its line from StillOversized so the bound holds it from now on.");
        }
    }
}
