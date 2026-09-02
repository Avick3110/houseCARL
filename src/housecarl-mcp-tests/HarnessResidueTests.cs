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
/// Both subjects are DERIVED from the source tree every run. Nothing here is a maintained list of what
/// is left: the old harness is measured where it lives, and the only checked-in numbers are the baseline
/// the measurements are compared against.
///
/// THREE of the four measures gate. probeFiles, ciAllRows and guardFilesOutsideTheCount move only when a
/// guard actually leaves the old harness, so exact equality in both directions is a countdown. probeLines
/// moves in both directions on ordinary in-place guard edits, which the two-harness rule explicitly
/// requires ("if you are editing one, edit it where it already lives") — gating it would punish the
/// correct act. It is derived and printed beside the gated three, and it stays in the baseline file as
/// information a conversion PR still updates (ADR 0003).
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
    //              REPORT-ONLY: derived and printed, never asserted. See the class summary.
    // ciAllRows  — CiAll.ProbeNames.Count: the registry `ci-all` dispatches, read off the compiled generator
    //              rather than off the text of CiAll.cs. A regex over the source was the first shape and it
    //              was short by whatever it did not match — a row carrying a trailing comment counted as
    //              zero while ci-all ran it, so the old harness could gain a guard with the countdown green.
    //              Cross-check available on any run: ci-all's own summary prints "N/N passed" and N is this.

    static string[] ProbeFiles() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-generator"),
                                 "*Probe*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .ToArray();

    static int ProbeLines() => ProbeFiles().Sum(f => File.ReadAllLines(f).Length);

    static int CiAllRows() => HousecarlGenerator.CiAll.ProbeNames.Count;

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
        public int GuardFilesOutsideTheCount { get; set; } = -1;
    }

    // ---- the countdown ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("probeFiles")]
    [InlineData("ciAllRows")]
    public void TheOldHarnessResidueMatchesItsCommittedBaseline_AndTheOnlyLegalDirectionIsDown(string measure)
    {
        var b = Committed();
        var (actual, committed) = measure switch
        {
            "probeFiles" => (ProbeFiles().Length, b.ProbeFiles),
            "ciAllRows" => (CiAllRows(), b.CiAllRows),
            _ => throw new ArgumentOutOfRangeException(nameof(measure), measure, null),
        };

        _out.WriteLine($"residue {measure}: {actual} (baseline {committed})" +
                       $"  ·  probeLines {ProbeLines()} (baseline {b.ProbeLines}, report-only)");

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

    // ---- the guards ci-all runs from files the countdown does NOT count ---------------------------------
    //
    // probeFiles counts the harness's own files by the `*Probe*.cs` convention, and that convention is not the
    // whole of what ci-all runs. Two earlier shapes of this check asked the wrong question: first the glob was
    // trusted on its own, then a companion arm derived "guard" as a method named RunGuard — and NINE of the 132
    // registry rows dispatch something else (ToolBridgeProbe.Run, CompileProbe.Run, BsaProbe.Run,
    // PkcuProbe.RunRegression, PerkRefsProbe.RunDeletedGuard, Mo2InstanceProbe.RunProbe,
    // RemapWave2NestedMechProbe.RunCompactGuard, and WriteEngine's two coerce verbs). WriteEngine is the live
    // one: it is in src/housecarl-core, so probeFiles cannot see it at any level of naming discipline, and the
    // countdown could reach zero — W7's closing condition — with ci-all still running two guards out of it.
    //
    // So the question is asked of the registry itself, which is the only honest answer to "what does ci-all
    // run". Those files are NOT folded into probeFiles: WriteEngine.cs is 4,000 lines of product code hosting
    // two guard verbs, and counting it as harness residue would inflate the countdown with code that is not
    // residue. It gets its own gated measure instead, so it is visible, cannot grow silently, and has to reach
    // zero along with the other two.
    //
    // The registry answers "which TYPES host guards". Turning that into "which FILE hosts each" was a fourth
    // proxy for one revision: it assumed the file is named after the type. Both directions of that assumption
    // are wrong, and the false-green one is the case this measure exists for — a guard type FooProbe hosted at
    // src/housecarl-core/FooProbe.cs read as counted because src/housecarl-generator/FooProbe.cs happened to
    // exist and the comparison was on bare filenames. So the host file is derived from the TYPE now:
    //
    //   * cross-assembly is outside BY CONSTRUCTION, with no lookup at all — probeFiles enumerates
    //     src/housecarl-generator and nothing else, so a type compiled into another project cannot be in it
    //     whatever it is called. `t.Assembly` settles the WriteEngine case and kills the name collision;
    //   * otherwise the file is the one that DECLARES the type, found by searching that type's own project
    //     for the declaration, and compared as a FULL path against probeFiles' full paths.
    //
    // Locating a project's source by its assembly name is still a convention, but it is one whose failure is
    // loud — zero or many declarations throws and names the type — never a silent pass, and the gate above it
    // does not depend on it at all.

    static Assembly GeneratorAssembly { get; } = typeof(HousecarlGenerator.CiAll).Assembly;

    static bool NotBuildOutput(string p) =>
        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

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

    /// <summary>Repo-relative, forward-slashed — the spelling a reader can paste into an editor.</summary>
    static string Rel(string full) => Path.GetRelativePath(HarnessPaths.RepoRoot, full).Replace('\\', '/');

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
            $"The ci-all registry guard {t.FullName} is compiled into assembly '{project}', and there is no " +
            $"'src/{project}' to search for its declaration. The residue count cannot say which file hosts it.");

        DeclarationIndex(root).TryGetValue(t.Name, out var hits);
        hits ??= new List<string>();

        Assert.True(hits.Count == 1,
            $"Cannot say which file declares the ci-all registry guard {t.FullName}: found {hits.Count} " +
            $"declarations of '{t.Name}' under src/{project}" +
            (hits.Count == 0 ? "." : " — " + string.Join(", ", hits.Select(Rel)) + ".") +
            " The residue count derives each guard's host file from its declaring type, so an unresolved or " +
            "ambiguous declaration is the count going quiet rather than a detail.");

        return hits[0];
    }

    static string[] GuardFilesOutsideTheCount()
    {
        var counted = ProbeFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);   // FULL paths, never bare names
        return HousecarlGenerator.CiAll.ProbeTypes
            .Distinct()
            .Where(t => t.Assembly != GeneratorAssembly || !counted.Contains(DeclaringFile(t)))
            .Select(t => $"{t.Name} ({Rel(DeclaringFile(t))})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    static bool IsCompilerGenerated(Type t) =>
        t == typeof(HousecarlGenerator.CiAll)
     || t.Name.Contains('<')
     || t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    /// <summary>
    /// Every ci-all registry guard resolves to the source file that declares it. This is what stops the
    /// measure below from going quiet: a guard whose host file cannot be named would drop out of the count
    /// silently, which is the same failure one level along.
    /// </summary>
    [Fact]
    public void EveryRegistryGuardResolvesToASourceFile_SoNoneCanDropOutOfTheCountUnseen()
    {
        var types = HousecarlGenerator.CiAll.ProbeTypes;

        // Vacuity canary: an empty registry would satisfy every claim below it.
        Assert.True(types.Count > 0,
            "CiAll.ProbeTypes is empty, so every residue claim derived from the registry is vacuous. Either the " +
            "registry did not load or its shape changed. Both are this guard's subject, not a reason to pass.");

        // A row registered as a lambda declares its guard on a compiler-generated closure — or on CiAll itself
        // — which is not a file anyone can open, and would have the count measuring the registry's own file
        // instead of the guard's.
        var synthetic = types.Where(IsCompilerGenerated)
                             .Select(t => t.FullName ?? t.Name)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(s => s, StringComparer.Ordinal)
                             .ToArray();

        Assert.True(synthetic.Length == 0,
            "These ci-all rows do not name a type anyone can open: " + string.Join(", ", synthetic) +
            ". Register a method group, not a lambda — the residue count derives each guard's host FILE from " +
            "its declaring type, and a lambda's declaring type is the registry rather than the guard.");

        // DeclaringFile is loud on zero or on many, so calling it for every type IS the resolution claim.
        foreach (var t in types.Distinct()) DeclaringFile(t);
    }

    /// <summary>
    /// The gated count of registry guards living outside the counted probe files, and WHICH they are. Exact
    /// equality in both directions, like the other two gated measures: a new one is growth, and retiring one
    /// without recording it turns the countdown into headroom.
    /// </summary>
    [Fact]
    public void GuardsHostedOutsideTheCountedFilesMatchTheirBaseline_TheyCloseW7Too()
    {
        var outside = GuardFilesOutsideTheCount();
        var committed = Committed().GuardFilesOutsideTheCount;

        _out.WriteLine($"registry guards outside the counted probe files: {outside.Length} " +
                       $"(baseline {committed}) — {string.Join(", ", outside)}");

        Assert.False(outside.Length > committed,
            $"A ci-all guard now lives outside the files the countdown counts: {string.Join(", ", outside)} " +
            $"({outside.Length}, baseline {committed}). A new guard belongs in src/housecarl-mcp-tests, not in " +
            $"the old harness — and least of all in a file the countdown cannot see. If this is deliberate and " +
            $"ruled, raise the number in '{BaselinePath}' in the same commit and say why there.");

        Assert.False(outside.Length < committed,
            $"A guard left the old harness and the countdown does not know: {string.Join(", ", outside)} " +
            $"({outside.Length}, baseline {committed}). Lower it in '{BaselinePath}' in this PR — a baseline " +
            "above the real figure stops being a countdown and becomes headroom.");
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
