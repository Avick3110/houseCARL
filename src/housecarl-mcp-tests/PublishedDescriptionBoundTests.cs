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
/// <para>Nothing is exempt: every tool the capture names is held to the bound. A description that goes over is
/// brought back under it — the tail moves onto the parameters, which the client delivers whole — rather than
/// listed here as an exception.</para>
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

    public static IEnumerable<object[]> EveryPublishedTool() =>
        PublishedNameAnchorTests.Captured().Select(n => new object[] { n });

    string Description(string tool)
    {
        Assert.True(_s.PublishedTools.TryGetValue(tool, out var t),
            $"'{tool}' is in the published-name capture but the server does not publish it — it was renamed or " +
            $"retired. Update its line in '{PublishedNameAnchorTests.CapturePath}'.");
        return t.GetProperty("description").GetString()!;
    }

    [Theory]
    [MemberData(nameof(EveryPublishedTool))]
    public void EveryPublishedDescriptionFitsTheClientsCut(string tool)
    {
        var length = Description(tool).Length;
        _out.WriteLine($"{tool}: {length} characters (bound {Bound})");

        Assert.True(length <= Bound,
            $"{tool}'s published description is {length} characters; Claude Code shows the first {Bound} and " +
            "drops the rest. Move the tail into the parameter descriptions it belongs to — those arrive whole.");
    }
}
