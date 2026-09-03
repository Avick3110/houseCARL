using System.Text.RegularExpressions;
using HousecarlGenerator;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The guards CI runs as their own workflow step, held against the attribute that declares them.
///
/// The roster is reflected off [CiProbe], so `ci-all` cannot lose a guard. What it cannot see is a guard
/// wired straight into the workflow as its own step — the shape freshness-capture-guard uses, and the shape
/// the residue countdown could otherwise reach zero over while CI was still running guards.
///
/// So the population comes off the workflow file itself: every generator verb ci.yml invokes is either the
/// runner or an attributed standalone guard, and every standalone guard has a step. Neither side is a list
/// anybody maintains.
///
/// The population is the workflow's OWN LINES, not a list of invocation spellings. A line that names the
/// generator and that <see cref="VerbOn"/> cannot resolve to a verb is RED naming the line, so a spelling
/// nobody thought of fails loud instead of dropping out of the population — which is how a hand-spelled
/// matcher goes quiet over exactly the step it was written to catch. A third arm reads the file's TOKENS
/// rather than its syntax, so a roster verb given a step is caught however the step spells the call.
/// </summary>
[Trait("tier", "unit")]
public sealed class CiWorkflowGuardStepTests
{
    readonly ITestOutputHelper _out;
    public CiWorkflowGuardStepTests(ITestOutputHelper output) => _out = output;

    /// <summary>The one verb that is not a guard: it is the runner the roster runs inside.</summary>
    const string Runner = "ci-all";

    static string WorkflowPath => Path.Combine(HarnessPaths.RepoRoot, ".github", "workflows", "ci.yml");

    /// <summary>Every line naming the generator, 1-based with its text. This is the population, taken off the
    /// file rather than off a pattern that says what an invocation looks like.</summary>
    static (int Number, string Text)[] GeneratorLines()
    {
        Assert.True(File.Exists(WorkflowPath),
            $"'{WorkflowPath}' is not there, so what CI runs cannot be read. Nothing below can be checked " +
            "against a workflow that is missing.");

        return File.ReadAllLines(WorkflowPath)
                   .Select((t, i) => (Number: i + 1, Text: t))
                   .Where(l => l.Text.Contains("housecarl-generator", StringComparison.Ordinal))
                   .ToArray();
    }

    /// <summary>A generator-naming line that runs no guard verb: a comment, or one of the dotnet CLI's own
    /// build-side commands. This one IS a short list — and it is short in the safe direction: a command
    /// missing from it makes its line unreadable, which the arm below reports as RED rather than dropping.</summary>
    static readonly Regex RunsNoVerb =
        new(@"^\s*#|\bdotnet\s+(?:build|restore|publish|pack|clean|test|format|nuget)\b", RegexOptions.Compiled);

    /// <summary>The built-output spelling: <c>… housecarl-generator.dll|.exe &lt;verb&gt;</c>.</summary>
    static readonly Regex ByBuildOutput =
        new(@"housecarl-generator\.(?:dll|exe)\s+([A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    /// <summary>The project spelling the probe files' own "Run:" lines use:
    /// <c>dotnet run --project &lt;…housecarl-generator…&gt; [options] [--] &lt;verb&gt;</c>.</summary>
    static readonly Regex ByProject =
        new(@"--project\s+\S*housecarl-generator\S*\s+(?<rest>.*)$", RegexOptions.Compiled);

    /// <summary>
    /// The verb one workflow line invokes, or null when this reader cannot tell. Null is a FAILURE, reported
    /// by <see cref="EveryLineNamingTheGeneratorResolvesToAVerb_AnUnreadInvocationIsNotASkip"/> — never a skip.
    /// </summary>
    static string? VerbOn(string line)
    {
        if (ByBuildOutput.Match(line) is { Success: true } built) return built.Groups[1].Value;

        if (ByProject.Match(line) is { Success: true } viaProject)
        {
            var tokens = viaProject.Groups["rest"].Value
                                   .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "--") return i + 1 < tokens.Length ? tokens[i + 1] : null;
                if (tokens[i].StartsWith('-')) { i++; continue; }   // an option and the value it carries
                return tokens[i];
            }
        }

        return null;
    }

    /// <summary>Every generator verb the workflow invokes.</summary>
    static string[] InvokedVerbs() =>
        GeneratorLines().Where(l => !RunsNoVerb.IsMatch(l.Text))
                        .Select(l => VerbOn(l.Text))
                        .Where(v => v is not null)
                        .Select(v => v!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

    [Fact]
    public void EveryLineNamingTheGeneratorResolvesToAVerb_AnUnreadInvocationIsNotASkip()
    {
        var lines = GeneratorLines();

        // Vacuity canary: the workflow does invoke the generator, so an empty population is this read
        // breaking rather than CI stopping.
        Assert.True(lines.Length > 0,
            $"No line in '{WorkflowPath}' names housecarl-generator, so every claim below about what CI " +
            "invokes is vacuous. The workflow does invoke it; this read is broken, not the workflow.");

        var unread = lines.Where(l => !RunsNoVerb.IsMatch(l.Text) && VerbOn(l.Text) is null)
                          .Select(l => $"ci.yml:{l.Number}: {l.Text.Trim()}")
                          .ToArray();

        _out.WriteLine($"lines naming the generator: {lines.Length} · verbs read: " +
                       string.Join(", ", InvokedVerbs()));

        Assert.True(unread.Length == 0,
            "These workflow lines name housecarl-generator and no verb can be read out of them:\n  " +
            string.Join("\n  ", unread) +
            "\nAn invocation this guard cannot read is a CI step that nothing holds against the roster — the " +
            "same hole the arms below close, one spelling along. Teach VerbOn the spelling, or add the " +
            "command to RunsNoVerb if the line runs no guard.");
    }

    [Fact]
    public void NoRosterVerbIsNamedAnywhereInTheWorkflow_ARosterVerbWithItsOwnStepWouldRunTwice()
    {
        var tokens = Regex.Split(File.ReadAllText(WorkflowPath), @"[^A-Za-z0-9_\-]+")
                          .ToHashSet(StringComparer.Ordinal);

        // Vacuity canary: the workflow names the runner, so a tokenisation that lost it lost everything.
        Assert.Contains(Runner, tokens);

        var named = CiAll.ProbeNames.Where(tokens.Contains)
                                    .OrderBy(s => s, StringComparer.Ordinal)
                                    .ToArray();

        Assert.True(named.Length == 0,
            "These ci-all roster verbs are named in .github/workflows/ci.yml: " + string.Join(", ", named) +
            ". ci-all runs them already, so a step naming one runs it a second time. This arm reads the " +
            "file's tokens rather than its invocation syntax, so it holds however the step spells the call: " +
            "flag the guard [CiProbe(…, Standalone = true)] if it needs its own process, drop the step, or " +
            "reword prose that happens to spell a verb.");
    }

    [Fact]
    public void EveryGeneratorVerbTheWorkflowInvokesIsTheRunnerOrAnAttributedStandaloneGuard()
    {
        var invoked = InvokedVerbs();
        _out.WriteLine($"ci.yml invokes: {string.Join(", ", invoked)}");

        // Vacuity canary: the workflow certainly runs the runner, so a parse that cannot find it has stopped
        // reading the file rather than found a workflow with no guards in it.
        Assert.Contains(Runner, invoked);

        var standalone = CiAll.StandaloneProbeNames.ToHashSet(StringComparer.Ordinal);
        var roster = CiAll.ProbeNames.ToHashSet(StringComparer.Ordinal);

        var wrong = invoked.Where(v => v != Runner && !standalone.Contains(v))
                           .Select(v => roster.Contains(v)
                               ? $"{v} — in the ci-all roster, so its own step runs it a second time. Flag it " +
                                 "[CiProbe(…, Standalone = true)] if it needs a cold process, or drop the step."
                               : $"{v} — carries no [CiProbe] attribute at all, so the residue countdown cannot " +
                                 "see it and nothing holds it against the roster. Attribute it, or take the " +
                                 "step out.")
                           .OrderBy(s => s, StringComparer.Ordinal)
                           .ToArray();

        Assert.True(wrong.Length == 0,
            "These verbs are invoked by .github/workflows/ci.yml as their own steps and should not be:\n  " +
            string.Join("\n  ", wrong));
    }

    [Fact]
    public void EveryStandaloneGuardHasItsOwnWorkflowStep_OtherwiseItIsOutOfCiAllAndRunByNothing()
    {
        var invoked = InvokedVerbs().ToHashSet(StringComparer.Ordinal);

        var orphaned = CiAll.StandaloneProbeNames.Where(n => !invoked.Contains(n))
                                                 .OrderBy(s => s, StringComparer.Ordinal)
                                                 .ToArray();

        _out.WriteLine($"standalone guards: {string.Join(", ", CiAll.StandaloneProbeNames)}");

        Assert.True(orphaned.Length == 0,
            "These guards are flagged Standalone — so ci-all deliberately does not run them — and " +
            ".github/workflows/ci.yml has no step for them either: " + string.Join(", ", orphaned) +
            ". A guard nothing runs is a guard that cannot fail, which is worse than not having it.");
    }
}
