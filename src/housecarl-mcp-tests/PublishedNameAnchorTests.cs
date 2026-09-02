using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The rename oracle for the published tool names.
///
/// <para>Since the tool-name registry landed (ADR 0004), shipped code and almost every test name a tool by
/// referencing a <c>ToolNames</c> constant. That is what makes deleting a tool a compile error — and it also
/// means a typo in a constant's VALUE moves the declared set, the registered set and the published set
/// together, so every set-equality cell stays green while the surface renames itself.</para>
///
/// <para>The 1.9 capture anchors the names 1.9 published as literals, which covers 39 of the 46. The seven
/// 2.0-era names had no literal anchor outside the guard harness that is being retired. This holds the
/// published set against a checked-in capture of literals, in both directions.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedNameAnchorTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public PublishedNameAnchorTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    static string CapturePath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "data", "tools-list-2.0.json");

    static string[] Captured()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CapturePath));
        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
                       .Select(t => t.GetString()!)
                       .OrderBy(n => n, StringComparer.Ordinal)
                       .ToArray();

        // Vacuity canary: an emptied or reshaped capture would satisfy both claims below it.
        Assert.True(names.Length > 0,
            $"'{CapturePath}' lists no tools, so both anchor claims are vacuous. The capture is the only " +
            "place these names are spelled as literals; an empty one is a broken oracle, not an empty surface.");
        return names;
    }

    /// <summary>Every name in the capture is a tool a caller can still reach — a theory row each, so a rename
    /// names the tool that moved rather than reporting a set difference.</summary>
    public static IEnumerable<object[]> CapturedNames() => Captured().Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(CapturedNames))]
    public void ACapturedNameIsStillPublished_ATypoInItsConstantsValueGoesRedHere(string name)
    {
        Assert.Contains(name, _s.PublishedNames);
    }

    [Fact]
    public void NothingIsPublishedThatTheCaptureDoesNotName_ANewToolAddsItsLiteralHere()
    {
        var captured = Captured().ToHashSet(StringComparer.Ordinal);
        var unanchored = _s.PublishedNames.Where(n => !captured.Contains(n))
                                          .OrderBy(n => n, StringComparer.Ordinal)
                                          .ToArray();

        _out.WriteLine($"published {_s.PublishedNames.Count} · captured {captured.Count}");

        Assert.True(unanchored.Length == 0,
            "These tools are published and their names are spelled nowhere as a literal: " +
            string.Join(", ", unanchored) +
            $". Add each to '{CapturePath}' in the same commit as its ToolNames constant. Without a literal, " +
            "every assertion about the name reads the constant, so a typo in the constant's value renames the " +
            "tool with the whole suite green.");
    }
}
