using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The shapes and readers the errors family's DTO-level arms share. Rationale — why these facts are driven at the
/// render rather than end to end, and what each reader is for — is in
/// <c>docs/architecture/check-family-tests.md</c>.
/// </summary>
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

    /// <summary>One dangling ref from <paramref name="plugin"/>, numbered so a rendered entry is identifiable.</summary>
    internal static DanglingRef Ref(int i, string plugin) =>
        new(FormKey.Factory($"{0x000800 + i:X6}:{plugin}"), "Npc", $"HcEd{i}",
            FormKey.Factory("0E0E0E:Skyrim.esm"));

    /// <summary>A plugin report carrying <paramref name="n"/> dangling refs and nothing else.</summary>
    internal static PluginErrors Plugin(string name, int n) =>
        new(name, Enumerable.Range(1, n).Select(i => Ref(i, name)).ToArray(),
            Array.Empty<string>(), 0, Array.Empty<string>(), null);

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

    /// <summary>The errors family's SECTION of a text response — its head down to the next family's head or the
    /// boundary block. Read per family: a whole-response search takes whichever family came first.</summary>
    internal static string ErrorsSection(string response)
    {
        var lines = response.Split('\n');
        int start = Array.FindIndex(lines, l => l.StartsWith("[errors] ", StringComparison.Ordinal));
        Assert.True(start >= 0, "no errors-family section in the response: " + Head(response));
        int end = Array.FindIndex(lines, start + 1,
            l => (l.StartsWith("[", StringComparison.Ordinal) && l.Contains("] ") && !l.StartsWith("[ERROR]", StringComparison.Ordinal)
                  && !l.StartsWith("[accounting:", StringComparison.Ordinal) && !l.StartsWith("[UNREAD]", StringComparison.Ordinal))
                 || l.StartsWith("boundary (", StringComparison.Ordinal));
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    /// <summary>The errors family's accounting line, or null where that lane writes none.</summary>
    internal static string? AccountingLine(string response) =>
        ErrorsSection(response).Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("[accounting:", StringComparison.Ordinal));

    /// <summary>The errors family's accounting line, or a failure naming the section that carried none.</summary>
    internal static string Accounting(string response) =>
        AccountingLine(response) ?? throw new InvalidOperationException(
            "the errors section carries no accounting line: " + ErrorsSection(response));

    /// <summary>The errors family's head line — where the sweep states what it swept and what it found.</summary>
    internal static string HeadLine(string response) =>
        ErrorsSection(response).Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("scanned ", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("no errors head line: " + Head(response));

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
