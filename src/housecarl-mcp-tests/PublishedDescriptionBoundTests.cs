using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// Claude Code cuts a tool description at 2,048 characters and delivers parameter descriptions whole, so a
/// description longer than that loses its tail on the way to the caller. This holds the bound on the SERVED
/// surface: the description string <c>tools/list</c> actually publishes, not the source literal.
///
/// <para>The subject set is not hand-listed: the theory rows come off the checked-in name capture, and a
/// <see cref="Fact"/> below pins the set driven to <see cref="ServerFixture.PublishedNames"/>, so a tool added
/// to the surface arrives here rather than quietly leaving the sweep short. (MemberData is evaluated at
/// discovery, before the fixture exists, which is why the rows cannot read the running server directly.)</para>
///
/// <para><see cref="StillOversized"/> is the shrinking list of tools whose descriptions have not been brought
/// under the bound yet; the second theory holds each of them STILL over, so an entry cannot go stale.</para>
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
        ToolNames.Apply,
        ToolNames.Check,
        ToolNames.CompactPlugin,
        ToolNames.Copy,
        ToolNames.Create,
        ToolNames.Forward,
        ToolNames.MergePlugins,
        ToolNames.NifInspect,
        ToolNames.NifSet,
        ToolNames.Place,
        ToolNames.Remove,
    };

    static string[] CapturedNames()
    {
        var path = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "data", "tools-list-2.0.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("tools").EnumerateArray()
                  .Select(t => t.GetString()!)
                  .OrderBy(n => n, StringComparer.Ordinal)
                  .ToArray();
    }

    public static IEnumerable<object[]> EveryPublishedTool() =>
        CapturedNames().Select(n => new object[] { n });

    public static IEnumerable<object[]> OversizedTools() =>
        StillOversized.OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    string Description(string tool) => _s.PublishedTools[tool].GetProperty("description").GetString()!;

    [Fact]
    public void TheToolsDrivenAreExactlyTheOnesPublished_SoTheSweepIsNotShort()
    {
        var driven = CapturedNames();
        var published = _s.PublishedNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(driven);
        Assert.Equal(published, driven);
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
    /// or this fails.</summary>
    [Theory]
    [MemberData(nameof(OversizedTools))]
    public void EveryToolNamedOversizedStillIs(string tool)
    {
        var length = Description(tool).Length;
        _out.WriteLine($"{tool}: {length} characters (bound {Bound})");

        Assert.True(length > Bound,
            $"{tool}'s published description is {length} characters, which is inside the {Bound} bound — " +
            "delete its line from StillOversized so the bound holds it from now on.");
    }
}
