using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The shapes and readers the scripts family's DTO-level arms share — the scripts-family twin of
/// <see cref="CheckErrorsFixtures"/>. Rationale for the DTO-vs-live driving-lane split is in
/// <c>docs/architecture/check-family-tests.md</c>.
/// </summary>
internal static class ScriptsFixtures
{
    /// <summary>The epoch every hand-shaped result carries, so a stamp is never the thing that varies.</summary>
    internal const string Epoch = "hcfixture0";

    /// <summary>A family selection off the product's own parser.</summary>
    internal static SweepFamilySelection Sel(params string[] tokens)
    {
        Assert.True(SweepFamilySelection.TryParse(tokens.Length == 0 ? new[] { "scripts" } : tokens,
                                                  out var selection, out var error), error);
        return selection!;
    }

    /// <summary>A scripts result. Every parameter a fact below varies is named; the rest are the quiet defaults.</summary>
    internal static ScriptCheckResult Result(
        IReadOnlyList<RecordScriptFindings>? reports = null,
        int pluginsScanned = 1,
        int recordsWithScripts = 0,
        int totalUnbound = 0,
        int totalNullObject = 0,
        int totalUnverifiable = 0,
        bool capped = false,
        bool readIncomplete = false,
        IReadOnlyDictionary<string, string>? excludedPlugins = null,
        string? filterNote = null,
        IReadOnlyList<SweepCount>? histogram = null,
        bool countsOnly = false,
        ScriptFindingClass classes = ScriptFindingClass.All,
        int totalUnboundObject = 0,
        int totalUnboundScalar = 0,
        string? propertyContains = null,
        int limit = 0) =>
        new(reports ?? Array.Empty<RecordScriptFindings>(), pluginsScanned, recordsWithScripts, totalUnbound,
            totalNullObject, totalUnverifiable, capped, readIncomplete,
            excludedPlugins ?? new Dictionary<string, string>(), null, filterNote, histogram, countsOnly, classes,
            totalUnboundObject, totalUnboundScalar, propertyContains, Epoch, limit);

    /// <summary>The text render of one scripts result — through the merged renderer a surviving tool calls, with its
    /// <see cref="CheckSweep"/> wrapper.</summary>
    internal static string Text(ScriptCheckResult r, int maxChars, params string[] tokens) =>
        Wire.RenderCheck(new CheckSweep(Sel(tokens), Scripts: r), maxChars);

    /// <summary>The json render of the same.</summary>
    internal static string Json(ScriptCheckResult r, int maxChars, params string[] tokens) =>
        JsonWire.RenderCheck(new CheckSweep(Sel(tokens), Scripts: r), maxChars);

    /// <summary>The scripts family's own object in a merged json document.</summary>
    internal static JsonElement ScriptsFamily(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("families")
                    .GetProperty(SweepFamilySelection.Token(SweepFamily.Scripts));
}
