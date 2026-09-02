using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The residue countdown and the one-way-conversion guard — the rules in ADR 0003 (docs/decisions/)
/// that keep the two-harness window honest, including "no family lives in both harnesses".
///
/// Both subjects are DERIVED from the source tree every run. Nothing here is a maintained list of what is
/// left: the old harness is measured where it lives, and the checked-in baseline is a PER-FILE map the
/// measurements are compared against, not a set of totals. Per file so a conversion PR deletes its own key
/// rather than editing numbers every other conversion PR also edits.
///
/// Four measures gate with exact equality in both directions — the probe-file key set, each file's ci-all
/// row count, the guard files outside that set, and the standalone CI steps. They move only when a guard
/// actually leaves the old harness. The line count does not gate: it moves in both directions on ordinary
/// in-place guard edits, which the two-harness rule explicitly requires ("if you are editing one, edit it
/// where it already lives"). It is derived, printed per file where it has drifted, and kept in the baseline
/// as information a conversion PR still updates (ADR 0003).
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
    //              a probe cannot hide in a subfolder), keyed by repo-relative forward-slashed path.
    // lines      — File.ReadAllLines().Length per file (a last line with no trailing newline still counts,
    //              which is why this may differ by a few from `wc -l`). REPORT-ONLY.
    // ciAllRows  — the roster verbs whose entry point is declared in that file, off CiAll.ProbeHosts: the
    //              [CiProbe]-attributed methods themselves. Neither a table nor a name convention — three
    //              earlier shapes of this population were a filename glob, a regex over the registry's
    //              source text, and a method-name convention, and each was short of the real set.
    //              Cross-check available on any run: ci-all's own summary prints "N/N passed" and N is the
    //              sum of this column.
    // standaloneCiSteps — the same, for verbs CI runs as their own workflow step rather than inside ci-all.

    sealed class FileEntry
    {
        public int Lines { get; set; }
        public int CiAllRows { get; set; }
        public int StandaloneCiSteps { get; set; }
    }

    sealed class Baseline
    {
        public Dictionary<string, FileEntry> ProbeFiles { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, FileEntry> GuardsOutsideTheCountedFiles { get; set; } = new(StringComparer.Ordinal);
    }

    static Baseline Committed()
    {
        var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(BaselinePath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        });
        Assert.True(b != null, $"'{BaselinePath}' did not parse into a baseline.");

        // Vacuity canary: a renamed or emptied section would satisfy every claim below it.
        Assert.True(b!.ProbeFiles.Count > 0,
            $"'{BaselinePath}' carries no probeFiles entries, so every comparison below is vacuous. Either the " +
            "file's shape changed or the countdown was emptied without reaching zero the honest way.");
        return b;
    }

    /// <summary>Repo-relative, forward-slashed — the spelling the baseline keys use and a reader can paste.</summary>
    static string Rel(string full) => Path.GetRelativePath(HarnessPaths.RepoRoot, full).Replace('\\', '/');

    static bool NotBuildOutput(string p) =>
        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    static string[] ProbeFilesFull() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-generator"),
                                 "*Probe*.cs", SearchOption.AllDirectories)
                 .Where(NotBuildOutput)
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToArray();

    static string[] ProbeFiles() => ProbeFilesFull().Select(Rel).ToArray();

    // ---- resolving a guard to the file that declares it -------------------------------------------------
    //
    // The registry answers "which TYPES host guards". Turning that into "which FILE hosts each" was a
    // proxy for one revision: it assumed the file is named after the type. Both directions of that
    // assumption are wrong, and the false-green one is the case this measure exists for. So the host file
    // is derived from the TYPE — searching that type's own project for the declaration, compared as a full
    // path — and cross-assembly guards are outside the counted set by construction, because probeFiles
    // enumerates src/housecarl-generator and nothing else.
    //
    // Locating a project's source by its assembly name is still a convention, but its failure is loud —
    // zero or many declarations throws and names the type — never a silent pass.

    static readonly Regex TypeDeclaration =
        new(@"\b(?:class|struct|record)\s+(?:(?:class|struct)\s+)?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    static readonly Dictionary<string, Dictionary<string, List<string>>> DeclarationIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Type name → the file(s) declaring it, for one project's source tree. Built once per root.</summary>
    static Dictionary<string, List<string>> DeclarationIndex(string root)
    {
        if (DeclarationIndexes.TryGetValue(root, out var cached)) return cached;

        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
            foreach (Match m in TypeDeclaration.Matches(File.ReadAllText(p)))
            {
                var name = m.Groups[1].Value;
                if (!map.TryGetValue(name, out var files)) map[name] = files = new List<string>();
                if (!files.Contains(p)) files.Add(p);
            }
        return DeclarationIndexes[root] = map;
    }

    /// <summary>
    /// The file that DECLARES <paramref name="t"/>. Exactly one match is required: zero means the declaration
    /// is somewhere this search cannot see, many means it cannot say which — and a wrong answer here is a
    /// guard dropping out of the count, so both are loud.
    /// </summary>
    static string DeclaringFile(Type t)
    {
        var project = t.Assembly.GetName().Name!;
        var root = Path.Combine(HarnessPaths.RepoRoot, "src", project);

        Assert.True(Directory.Exists(root),
            $"The CI guard {t.FullName} is compiled into assembly '{project}', and there is no " +
            $"'src/{project}' to search for its declaration. The residue count cannot say which file hosts it.");

        DeclarationIndex(root).TryGetValue(t.Name, out var hits);
        hits ??= new List<string>();

        Assert.True(hits.Count == 1,
            $"Cannot say which file declares the CI guard {t.FullName}: found {hits.Count} " +
            $"declarations of '{t.Name}' under src/{project}" +
            (hits.Count == 0 ? "." : " — " + string.Join(", ", hits.Select(Rel)) + ".") +
            " The residue count derives each guard's host file from its declaring type, so an unresolved or " +
            "ambiguous declaration is the count going quiet rather than a detail.");

        return hits[0];
    }

    /// <summary>Every guard verb the attribute declares, grouped by the repo-relative file hosting it.</summary>
    static Dictionary<string, (int Rows, int Standalone)> GuardCountsByFile()
    {
        var map = new Dictionary<string, (int Rows, int Standalone)>(StringComparer.Ordinal);

        void Bump(Type host, bool standalone)
        {
            var key = Rel(DeclaringFile(host));
            map.TryGetValue(key, out var c);
            map[key] = standalone ? (c.Rows, c.Standalone + 1) : (c.Rows + 1, c.Standalone);
        }

        foreach (var (_, host) in HousecarlGenerator.CiAll.ProbeHosts) Bump(host, standalone: false);
        foreach (var (_, host) in HousecarlGenerator.CiAll.StandaloneProbeHosts) Bump(host, standalone: true);
        return map;
    }

    /// <summary>The baseline's two maps as one lookup — every file it records a guard count for.</summary>
    static Dictionary<string, FileEntry> CommittedGuardCounts(Baseline b)
    {
        var all = new Dictionary<string, FileEntry>(b.ProbeFiles, StringComparer.Ordinal);
        foreach (var (k, v) in b.GuardsOutsideTheCountedFiles) all[k] = v;
        return all;
    }

    // ---- the countdown ---------------------------------------------------------------------------------

    [Fact]
    public void TheCountedProbeFileSetMatchesTheBaseline_AndTheOnlyLegalDirectionIsDown()
    {
        var b = Committed();
        var actual = ProbeFiles();

        var added = actual.Except(b.ProbeFiles.Keys, StringComparer.Ordinal)
                          .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var gone = b.ProbeFiles.Keys.Except(actual, StringComparer.Ordinal)
                                .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        _out.WriteLine($"residue probeFiles: {actual.Length} (baseline {b.ProbeFiles.Count})");

        // Two directions, two sentences, because they are two different mistakes.
        Assert.False(added.Length > 0,
            "The old probe harness GREW — these files are not in the countdown:\n  " +
            string.Join("\n  ", added) +
            $"\nNew guards go in src/housecarl-mcp-tests; nothing is added to src/housecarl-generator. If this " +
            $"growth is deliberate and ruled, add each file to '{BaselinePath}' in the same commit and say why " +
            "there — the countdown is only honest if going backwards is a visible, argued edit.");

        Assert.False(gone.Length > 0,
            "You removed residue and the countdown does not know — these files are in the baseline and not on " +
            "disk:\n  " + string.Join("\n  ", gone) +
            $"\nDelete each key from '{BaselinePath}' in this PR. A baseline left above the real figure stops " +
            "being a countdown and becomes headroom.");
    }

    [Fact]
    public void EveryFilesCiAllRowCountMatchesTheBaseline_SoARowCannotAppearOrLeaveUnseen()
    {
        var b = Committed();
        var committed = CommittedGuardCounts(b);
        var actual = GuardCountsByFile();

        var wrong = committed.Keys.Union(actual.Keys, StringComparer.Ordinal)
            .Select(k => (File: k,
                          Actual: actual.TryGetValue(k, out var a) ? a.Rows : 0,
                          Committed: committed.TryGetValue(k, out var c) ? c.CiAllRows : 0))
            .Where(x => x.Actual != x.Committed)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .Select(x => $"{x.File}: {x.Actual} row(s), baseline {x.Committed}")
            .ToArray();

        _out.WriteLine($"residue ciAllRows: {HousecarlGenerator.CiAll.ProbeNames.Count} " +
                       $"(baseline {committed.Values.Sum(v => v.CiAllRows)}) — ci-all's own summary prints the same N");

        Assert.True(wrong.Length == 0,
            "The ci-all roster no longer matches the countdown, per file:\n  " + string.Join("\n  ", wrong) +
            $"\nThe roster is reflected off [CiProbe], so a row appears when a guard gains the attribute and " +
            $"leaves when its file goes. Record the change in '{BaselinePath}' in the same commit — a row " +
            "appearing unrecorded is growth, and one leaving unrecorded turns the countdown into headroom.");
    }

    [Fact]
    public void EveryFilesStandaloneCiStepCountMatchesTheBaseline_AGuardWithItsOwnStepIsResidueToo()
    {
        var b = Committed();
        var committed = CommittedGuardCounts(b);
        var actual = GuardCountsByFile();

        var wrong = committed.Keys.Union(actual.Keys, StringComparer.Ordinal)
            .Select(k => (File: k,
                          Actual: actual.TryGetValue(k, out var a) ? a.Standalone : 0,
                          Committed: committed.TryGetValue(k, out var c) ? c.StandaloneCiSteps : 0))
            .Where(x => x.Actual != x.Committed)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .Select(x => $"{x.File}: {x.Actual} standalone step(s), baseline {x.Committed}")
            .ToArray();

        _out.WriteLine($"residue standaloneCiSteps: {HousecarlGenerator.CiAll.StandaloneProbeNames.Count} " +
                       $"(baseline {committed.Values.Sum(v => v.StandaloneCiSteps)}) — " +
                       string.Join(", ", HousecarlGenerator.CiAll.StandaloneProbeNames));

        Assert.True(wrong.Length == 0,
            "The guards CI runs as their own workflow step no longer match the countdown, per file:\n  " +
            string.Join("\n  ", wrong) +
            $"\nA standalone guard is old-harness residue like any other and gates the same way. Record the " +
            $"change in '{BaselinePath}' in the same commit.");
    }

    /// <summary>
    /// The gated set of guards living outside the counted probe files, and WHICH they are. Those files are
    /// deliberately not folded into probeFiles: WriteEngine.cs is 4,000 lines of product code hosting two
    /// guard verbs, and counting it as harness residue would inflate the countdown with code that is not
    /// residue. It gets its own gated measure instead, so it is visible, cannot grow silently, and has to
    /// reach zero along with the rest.
    /// </summary>
    [Fact]
    public void TheGuardFilesOutsideTheCountedSetMatchTheBaseline_TheyCloseW7Too()
    {
        var b = Committed();
        var counted = ProbeFiles().ToHashSet(StringComparer.Ordinal);
        var outside = GuardCountsByFile().Keys.Where(k => !counted.Contains(k))
                                              .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        var added = outside.Except(b.GuardsOutsideTheCountedFiles.Keys, StringComparer.Ordinal).ToArray();
        var gone = b.GuardsOutsideTheCountedFiles.Keys.Except(outside, StringComparer.Ordinal)
                                                      .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        _out.WriteLine($"residue guardFilesOutsideTheCount: {outside.Length} " +
                       $"(baseline {b.GuardsOutsideTheCountedFiles.Count}) — {string.Join(", ", outside)}");

        Assert.False(added.Length > 0,
            "A CI guard now lives outside the files the countdown counts:\n  " + string.Join("\n  ", added) +
            "\nA new guard belongs in src/housecarl-mcp-tests, not in the old harness — and least of all in a " +
            $"file the countdown cannot see. If this is deliberate and ruled, record it in '{BaselinePath}' in " +
            "the same commit and say why there.");

        Assert.False(gone.Length > 0,
            "A guard left the old harness and the countdown does not know:\n  " + string.Join("\n  ", gone) +
            $"\nDelete the key from '{BaselinePath}' in this PR — a baseline above the real figure stops being " +
            "a countdown and becomes headroom.");
    }

    /// <summary>
    /// The four headline numbers, derived from the same map the gates use, printed for the CI countdown step.
    /// The line total is report-only, so the drift it accumulates is printed per file rather than asserted:
    /// a conversion PR refreshes what it touches, and nothing chases a total.
    /// </summary>
    [Fact]
    public void TheDerivedTotalsAreReported_AndTheLineDriftIsNamedRatherThanGated()
    {
        var b = Committed();
        var files = ProbeFilesFull();
        var lines = files.ToDictionary(Rel, f => File.ReadAllLines(f).Length, StringComparer.Ordinal);
        var counts = GuardCountsByFile();

        var probeLines = lines.Values.Sum();
        _out.WriteLine($"probeFiles {files.Length} · probeLines {probeLines} (baseline " +
                       $"{b.ProbeFiles.Values.Sum(v => v.Lines)}, report-only) · ciAllRows " +
                       $"{counts.Values.Sum(c => c.Rows)} · guardFilesOutsideTheCount " +
                       $"{counts.Keys.Count(k => !lines.ContainsKey(k))} · standaloneCiSteps " +
                       $"{counts.Values.Sum(c => c.Standalone)}");

        var drifted = lines.Where(kv => b.ProbeFiles.TryGetValue(kv.Key, out var e) && e.Lines != kv.Value)
                           .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                           .Select(kv => $"{kv.Key}: {kv.Value} lines, baseline {b.ProbeFiles[kv.Key].Lines}")
                           .ToArray();
        _out.WriteLine(drifted.Length == 0
            ? "line counts: no drift"
            : $"line counts drifted in {drifted.Length} file(s) — report-only, refresh what you touch:\n  " +
              string.Join("\n  ", drifted));

        // The one claim here: the countdown is measuring something. Zero is W7's finish line, and it is
        // reached by conversion, never by a derivation that quietly stopped finding anything.
        Assert.True(files.Length > 0 && counts.Count > 0,
            "The residue derivation found no probe files or no guards at all. W7 is not finished; the " +
            "measurement is broken.");
    }

    static bool IsCompilerGenerated(Type t) =>
        t == typeof(HousecarlGenerator.CiAll)
     || t.Name.Contains('<')
     || t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    /// <summary>
    /// Every CI guard resolves to the source file that declares it. This is what stops the measures above
    /// from going quiet: a guard whose host file cannot be named would drop out of the count silently, which
    /// is the same failure one level along.
    /// </summary>
    [Fact]
    public void EveryGuardResolvesToASourceFile_SoNoneCanDropOutOfTheCountUnseen()
    {
        var types = HousecarlGenerator.CiAll.GuardTypes;

        // Vacuity canary: an empty guard set would satisfy every claim below it.
        Assert.True(types.Count > 0,
            "CiAll.GuardTypes is empty, so every residue claim derived from it is vacuous. Either the " +
            "attribute scan found nothing or its shape changed. Both are this guard's subject, not a reason " +
            "to pass.");

        // The attribute sits on a method, so its declaring type is always a real type — but a guard declared
        // on a compiler-generated closure or on CiAll itself is not a file anyone can open, and would have the
        // count measuring the runner's own file instead of the guard's.
        var synthetic = types.Where(IsCompilerGenerated)
                             .Select(t => t.FullName ?? t.Name)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(s => s, StringComparer.Ordinal)
                             .ToArray();

        Assert.True(synthetic.Length == 0,
            "These CI guards do not name a type anyone can open: " + string.Join(", ", synthetic) +
            ". The residue count derives each guard's host FILE from its declaring type, so a guard declared " +
            "anywhere but a real named type takes itself out of the count.");

        // DeclaringFile is loud on zero or on many, so calling it for every type IS the resolution claim.
        foreach (var t in types.Distinct()) DeclaringFile(t);
    }

    // ---- one-way conversion ----------------------------------------------------------------------------
    //
    // The literal form "no probe file drives a tool the test project also tests" is RED at birth and cannot
    // be otherwise during the ruled sequence: WriteSurfaceGuardProbe.cs drives RecordsTools.Records and does
    // not convert until its own conversion PR, which the ruling places after the cut.
    //
    // So the one-way property is enforced where it is decidable: a test file that claims a probe's assertions
    // carries an explicit `Converted-from: <ProbeName>` marker, and that probe's source file must not exist.

    static (string File, string Probe)[] ConversionMarkers() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests"),
                                 "*.cs", SearchOption.AllDirectories)
                 .Where(NotBuildOutput)
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
                                 .Any(NotBuildOutput))
            .Select(m => $"{m.File} claims {m.Probe}, which still has a source file")
            .Distinct()
            .ToArray();

        Assert.True(survivors.Length == 0,
            "A converted family is living in both harnesses:\n  " + string.Join("\n  ", survivors) +
            "\nA conversion deletes its probe in the same commit; if the probe is back on purpose, the " +
            "conversion marker is the thing to remove.");
    }
}
