using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>The rename oracle for the published tool names. Shipped code and almost every test name a tool
/// through a <c>ToolNames</c> constant, so a typo in a constant's VALUE moves the declared, registered and
/// published sets together and every set-equality test stays green while the surface renames itself. The
/// checked-in capture is the only place the names are spelled as literals; it is held against the published
/// set in both directions.</summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedNameAnchorTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public PublishedNameAnchorTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    internal static string CapturePath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "data", "tools-list-2.0.json");

    /// <summary>The captured names, sorted, with the empty-capture guard. Other test classes that drive their rows
    /// off the capture read it through here, so the oracle is parsed in one place.</summary>
    internal static string[] Captured()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(CapturePath));
        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
                       .Select(t => t.GetString()!)
                       .OrderBy(n => n, StringComparer.Ordinal)
                       .ToArray();

        // An emptied or reshaped capture would satisfy both claims below it.
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
