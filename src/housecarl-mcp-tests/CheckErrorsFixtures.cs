using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The shapes and readers the errors family's DTO-level tests share. Why these facts are driven at the
/// render rather than end to end, and what each reader is for, is in
/// <c>docs/architecture/check-family-tests.md</c>.</summary>
internal static class CheckErrorsFixtures
{
    /// <summary>The epoch every hand-shaped result carries, so a stamp is never the thing that varies.</summary>
    internal const string Epoch = "hcfixture0";

    /// <summary>A family selection off the product's own parser — the tokens a caller spells in <c>findings=</c>.</summary>
    internal static SweepFamilySelection Sel(params string[] tokens)
    {
        Assert.True(SweepFamilySelection.TryParse(tokens.Length == 0 ? new[] { "errors" } : tokens,
                                                  out var selection, out var error), error);
        return selection!;
    }

    /// <summary>An errors result. Every parameter a fact below varies is named; the rest are the quiet defaults.</summary>
    internal static ErrorCheckResult Result(
        IReadOnlyList<PluginErrors>? reports = null,
        int scanned = 4,
        int totalDangling = 0,
        int totalMissingMasters = 0,
        int totalUnscannable = 0,
        IReadOnlyDictionary<string, string>? excluded = null,
        IReadOnlyList<SweepCount>? byTarget = null,
        IReadOnlyList<SweepCount>? bySource = null,
        bool countsOnly = false,
        ErrorFindingClass classes = ErrorFindingClass.All,
        IReadOnlyList<string>? baseMastersSwept = null,
        int baselineDangling = 0,
        bool nonBaseInScope = true,
        string? filterNote = null,
        int limit = 0) =>
        new(reports ?? Array.Empty<PluginErrors>(), scanned, totalDangling, totalMissingMasters, totalUnscannable,
            excluded ?? new Dictionary<string, string>(), null, null, filterNote, classes, byTarget, countsOnly,
            Epoch, bySource, baselineDangling, baseMastersSwept ?? Array.Empty<string>(), nonBaseInScope, limit);

    /// <summary>The text render of one errors result — through the merged renderer a surviving tool calls, with its
    /// <see cref="CheckSweep"/> wrapper.</summary>
    internal static string Text(ErrorCheckResult r, int maxChars, params string[] tokens) =>
        Wire.RenderCheck(new CheckSweep(Sel(tokens), Errors: r), maxChars);

    /// <summary>The text render with an explicit histogram row budget — the <c>limit=</c> the axes are cut by.</summary>
    internal static string Text(ErrorCheckResult r, int maxChars, int histogramLimit, params string[] tokens) =>
        Wire.RenderCheck(new CheckSweep(Sel(tokens), Errors: r), maxChars, histogramLimit);

    /// <summary>The json render of the same.</summary>
    internal static string Json(ErrorCheckResult r, int maxChars, params string[] tokens) =>
        JsonWire.RenderCheck(new CheckSweep(Sel(tokens), Errors: r), maxChars);

    internal static string Json(ErrorCheckResult r, int maxChars, int histogramLimit, params string[] tokens) =>
        JsonWire.RenderCheck(new CheckSweep(Sel(tokens), Errors: r), maxChars, histogramLimit);

    /// <summary>The errors family's own object in a merged json document.</summary>
    internal static JsonElement ErrorsFamily(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("families")
                    .GetProperty(SweepFamilySelection.Token(SweepFamily.Errors));

    /// <summary>One plugin's <c>[ERROR]</c> section: its header plus the indented lines under it. Read
    /// section-scoped — the composer never emits two of a section's lines adjacent, so a whole-response search
    /// for a span across them cannot fail.</summary>
    internal static string PluginSection(string response, string plugin)
    {
        var lines = response.Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith("[ERROR] " + plugin, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no [ERROR] section for {plugin}: " + Head(response));
        int end = start + 1;
        while (end < lines.Length && lines[end].StartsWith("  ", StringComparison.Ordinal)) end++;
        return string.Join("\n", lines[start..end]);
    }

    /// <summary>The dangling entry lines a text response actually emitted.</summary>
    internal static string[] EntryLines(string response) =>
        response.Split('\n').Select(l => l.Trim())
                .Where(l => l.Contains("[target not defined by any active plugin]", StringComparison.Ordinal))
                .ToArray();

    /// <summary>The first two lines of a response — enough to read a failure by without printing a sweep.</summary>
    internal static string Head(string response)
    {
        var lines = response.Split('\n');
        return lines.Length > 1 ? lines[0] + " | " + lines[1] : lines[0];
    }
}
