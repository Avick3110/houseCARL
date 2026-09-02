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
/// </summary>
[Trait("tier", "unit")]
public sealed class CiWorkflowGuardStepTests
{
    readonly ITestOutputHelper _out;
    public CiWorkflowGuardStepTests(ITestOutputHelper output) => _out = output;

    /// <summary>The one verb that is not a guard: it is the runner the roster runs inside.</summary>
    const string Runner = "ci-all";

    static string WorkflowPath => Path.Combine(HarnessPaths.RepoRoot, ".github", "workflows", "ci.yml");

    static readonly Regex GeneratorInvocation =
        new(@"housecarl-generator\.dll\s+([A-Za-z0-9_\-]+)", RegexOptions.Compiled);

    /// <summary>Every generator verb the workflow invokes, in the order it invokes them.</summary>
    static string[] InvokedVerbs()
    {
        Assert.True(File.Exists(WorkflowPath),
            $"'{WorkflowPath}' is not there, so what CI runs cannot be read. Nothing below can be checked " +
            "against a workflow that is missing.");

        return GeneratorInvocation.Matches(File.ReadAllText(WorkflowPath))
                                  .Select(m => m.Groups[1].Value)
                                  .Distinct(StringComparer.Ordinal)
                                  .ToArray();
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
