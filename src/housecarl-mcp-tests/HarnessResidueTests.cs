using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The residue countdown and the one-way-conversion guard (RUN_ORDER amendment 2026-09-02 (7),
/// residue items (ii) and (iv), and the sizing pass's "no family in both harnesses").
///
/// Both subjects are DERIVED from the source tree every run. Nothing here is a maintained list of what
/// is left: the old harness is measured where it lives, and the only checked-in number is the baseline
/// the measurement is compared against.
/// </summary>
[Trait("tier", "unit")]
public sealed class HarnessResidueTests
{
    readonly ITestOutputHelper _out;
    public HarnessResidueTests(ITestOutputHelper output) => _out = output;

    /// <summary>The one file a conversion PR edits, and the file every failure message below names.</summary>
    public static string BaselinePath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "harness-residue-baseline.json");

    // ---- the derivation, stated once so a later session can reproduce it -------------------------------
    //
    // probeFiles — every *Probe*.cs under src/housecarl-generator (recursively, bin/ and obj/ excluded, so
    //              a probe cannot hide in a subfolder).
    // probeLines — the total line count of those same files (File.ReadAllLines().Length: a last line with
    //              no trailing newline still counts, which is why this may differ by a few from `wc -l`).
    // ciAllRows  — the ("name", XProbe.RunGuard) rows in CiAll.cs, i.e. what `ci-all` will actually run.
    //              Cross-check available on any run: ci-all's own summary prints "N/N passed" and N is this.

    static string[] ProbeFiles() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-generator"),
                                 "*Probe*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToArray();

    static int ProbeLines() => ProbeFiles().Sum(f => File.ReadAllLines(f).Length);

    static int CiAllRows() =>
        Regex.Matches(
            File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-generator", "CiAll.cs")),
            // [ \t\r]* before the anchor, not [ \t]*: the file is checked out CRLF on Windows and a regex
            // that forgets the \r counts zero rows while looking perfectly correct.
            @"^[ \t]*\(""[a-z0-9-]+"",[ \t]*[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\),?[ \t\r]*$",
            RegexOptions.Multiline).Count;

    static Baseline Committed()
    {
        var json = File.ReadAllText(BaselinePath);
        var b = JsonSerializer.Deserialize<Baseline>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        });
        Assert.True(b != null, $"'{BaselinePath}' did not parse into a baseline.");
        return b!;
    }

    sealed class Baseline
    {
        public int ProbeFiles { get; set; } = -1;
        public int ProbeLines { get; set; } = -1;
        public int CiAllRows { get; set; } = -1;
    }

    // ---- the countdown ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("probeFiles")]
    [InlineData("probeLines")]
    [InlineData("ciAllRows")]
    public void TheOldHarnessResidueMatchesItsCommittedBaseline_AndTheOnlyLegalDirectionIsDown(string measure)
    {
        var b = Committed();
        var (actual, committed) = measure switch
        {
            "probeFiles" => (ProbeFiles().Length, b.ProbeFiles),
            "probeLines" => (ProbeLines(), b.ProbeLines),
            "ciAllRows" => (CiAllRows(), b.CiAllRows),
            _ => throw new ArgumentOutOfRangeException(nameof(measure), measure, null),
        };

        _out.WriteLine($"residue {measure}: {actual} (baseline {committed})");

        // Two directions, two sentences, because they are two different mistakes.
        Assert.False(actual > committed,
            $"The old probe harness GREW: {measure} is {actual}, baseline {committed}. New guards go in " +
            $"src/housecarl-mcp-tests ({nameof(HarnessResidueTests)}'s own two-harness rule); nothing is added " +
            $"to src/housecarl-generator. If this growth is deliberate and ruled, raise the number in " +
            $"'{BaselinePath}' in the same commit and say why there — the countdown is only honest if going " +
            "backwards is a visible, argued edit rather than a silent one.");

        Assert.False(actual < committed,
            $"You removed residue and the countdown does not know: {measure} is {actual}, baseline {committed}. " +
            $"Lower it in '{BaselinePath}' in this PR. A baseline left above the real figure stops being a " +
            "countdown and becomes headroom.");
    }

    // ---- one-way conversion ----------------------------------------------------------------------------
    //
    // The literal form the kickoff proposed — "no probe file drives a tool the test project also tests" —
    // is RED at birth and cannot be otherwise during the ruled sequence: WriteSurfaceGuardProbe.cs drives
    // RecordsTools.Records (two call sites) and does not convert until its own conversion PR, which the
    // ruling places AFTER the cut. Measured on this branch, not assumed.
    //
    // So the one-way property is enforced where it is actually decidable: a test file that claims a probe's
    // assertions carries an explicit `Converted-from: <ProbeName>` marker, and that probe's source file must
    // not exist. Restoring a converted probe is then RED, and the claim is derived from the test project's
    // own markers rather than from a list of what has been converted so far.

    static (string File, string Probe)[] ConversionMarkers() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests"),
                                 "*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .SelectMany(p => Regex.Matches(File.ReadAllText(p), @"Converted-from:[ \t]*([A-Za-z0-9_]+)")
                                       .Select(m => (File: Path.GetFileName(p), Probe: m.Groups[1].Value)))
                 .ToArray();

    [Fact]
    public void EveryConversionMarkerNamesAProbeThatIsGone_NoFamilyLivesInBothHarnesses()
    {
        var markers = ConversionMarkers();

        // The vacuity canary. A regex that stops matching — a reformat, a renamed marker — would otherwise
        // leave this test green over an empty set, which is the guard failing toward green.
        Assert.True(markers.Length > 0,
            "No `Converted-from:` markers found anywhere in src/housecarl-mcp-tests. Either the marker " +
            "convention was renamed without updating this guard, or a conversion landed without claiming " +
            "its source. Both are the guard's subject, not a reason for it to pass.");

        var survivors = markers
            .Where(m => Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src"),
                                                 m.Probe + ".cs", SearchOption.AllDirectories)
                                 .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            .Select(m => $"{m.File} claims {m.Probe}, which still has a source file")
            .Distinct()
            .ToArray();

        Assert.True(survivors.Length == 0,
            "A converted family is living in both harnesses:\n  " + string.Join("\n  ", survivors) +
            "\nA conversion deletes its probe in the same commit; if the probe is back on purpose, the " +
            "conversion marker is the thing to remove.");
    }
}
