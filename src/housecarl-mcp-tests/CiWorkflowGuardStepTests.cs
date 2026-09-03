using System.Text;
using System.Text.RegularExpressions;
using HousecarlGenerator;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The guards CI runs as their own workflow step, held against the attribute that declares them.
///
/// <para>The roster is reflected off [CiProbe], so <c>ci-all</c> cannot lose a guard. What it cannot see is a
/// guard wired straight into the workflow as its own step — the shape freshness-capture-guard uses, and the
/// shape the residue countdown could otherwise reach zero over while CI was still running guards.</para>
///
/// <para>The workflow is DOWNSTREAM of the attribute, not parsed. This class renders the standalone-guard
/// steps from <see cref="CiAll.StandaloneProbeNames"/> and asserts <c>ci.yml</c> carries that block
/// byte-equal. Flag a guard <c>Standalone = true</c> and its step is owed; hand-edit, delete or respell one
/// and this is red naming it. There is nothing to parse and no invocation grammar to keep up with.</para>
///
/// <para><b>What this does NOT claim.</b> A guard invocation hand-added to a workflow outside the generated
/// block is outside the residue countdown's reach. The completeness claim is over the sanctioned path — the
/// generated block in the one workflow file named below — and not over arbitrary shell. Three earlier shapes
/// of this check tried to derive that wider surface by reading the workflow, and each was a hand-enumeration
/// wearing a different hat (the invocation's extension, then the file's name, then a list of dotnet commands
/// that runs no verb); each went green over a real offender. An arm that cannot be made honest is deleted,
/// not strengthened, so they are gone.</para>
/// </summary>
[Trait("tier", "unit")]
public sealed class CiWorkflowGuardStepTests
{
    readonly ITestOutputHelper _out;
    public CiWorkflowGuardStepTests(ITestOutputHelper output) => _out = output;

    /// <summary>The one verb that is not a guard: it is the runner the roster runs inside.</summary>
    const string Runner = "ci-all";

    /// <summary>The single workflow file the generated block lives in. Named, deliberately: the honest
    /// extension to more workflows is to say which file carries the block, never to scan for invocations.</summary>
    static string WorkflowPath => Path.Combine(HarnessPaths.RepoRoot, ".github", "workflows", "ci.yml");

    const string BeginMarker = "      # >>> generated: standalone CI guard steps — do not hand-edit";
    const string EndMarker = "      # <<< generated: standalone CI guard steps";

    /// <summary>The generator build output CI invokes, spelled once, here.</summary>
    const string GeneratorDll = "src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll";

    /// <summary>
    /// The block ci.yml is required to carry, rendered from the attribute. This is the ONLY place the shape of
    /// a standalone guard's CI step is written down; the workflow holds a copy of it.
    /// </summary>
    static string RenderedBlock()
    {
        var verbs = CiAll.StandaloneProbeNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append('\n');
        sb.Append("      # Rendered from CiAll.StandaloneProbeNames. `if: !cancelled()` so one red run still shows whether these\n");
        sb.Append("      # passed — the same \"report every failure\" posture as the ci-all runner itself.\n");
        foreach (var verb in verbs)
        {
            sb.Append("      - name: CI guard (standalone, own cold process) — ").Append(verb).Append('\n');
            sb.Append("        if: ${{ !cancelled() }}\n");
            sb.Append("        run: dotnet ").Append(GeneratorDll).Append(' ').Append(verb).Append('\n');
        }
        sb.Append(EndMarker);
        return sb.ToString();
    }

    /// <summary>The workflow's text with line endings normalised, so the comparison is about content rather
    /// than about whether git checked the file out CRLF.</summary>
    static string WorkflowText()
    {
        Assert.True(File.Exists(WorkflowPath),
            $"'{WorkflowPath}' is not there, so what CI runs cannot be read. Nothing below can be checked " +
            "against a workflow that is missing.");

        return File.ReadAllText(WorkflowPath).Replace("\r\n", "\n");
    }

    [Fact]
    public void TheWorkflowCarriesTheGeneratedStandaloneGuardBlock_ByteEqualToWhatTheAttributeRenders()
    {
        // Vacuity canary: with no standalone guards the block is two markers and a comment, and "the workflow
        // contains it" would be a claim about nothing.
        Assert.True(CiAll.StandaloneProbeNames.Count > 0,
            "No guard carries [CiProbe(…, Standalone = true)], so the generated block is empty and this claim " +
            "is vacuous. If the last standalone guard really has gone, delete the block from ci.yml and this " +
            "arm with it — do not leave an empty block asserting nothing.");

        var text = WorkflowText();
        var block = RenderedBlock();

        _out.WriteLine($"standalone guards: {string.Join(", ", CiAll.StandaloneProbeNames)}");

        var occurrences = 0;
        for (var i = text.IndexOf(block, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(block, i + 1, StringComparison.Ordinal)) occurrences++;

        if (occurrences == 1) return;

        // Name what is actually wrong rather than printing two walls of YAML: which rendered lines the file
        // does not carry, and whether the markers are there at all.
        var missing = block.Split('\n')
                           .Where(l => !text.Contains(l, StringComparison.Ordinal))
                           .ToArray();

        Assert.True(occurrences == 1,
            (occurrences == 0
                ? "'.github/workflows/ci.yml' does not carry the generated standalone-guard block."
                : $"'.github/workflows/ci.yml' carries the generated block {occurrences} times, so CI runs " +
                  "those guards more than once and the countdown counts them once.") +
            "\nThe block is RENDERED from [CiProbe(…, Standalone = true)] — the attribute is the one home, and " +
            "the workflow holds a copy. Paste the block below into ci.yml between the markers, exactly.\n" +
            (missing.Length == 0
                ? "Every rendered line is present, so the block is there but broken up or reordered.\n"
                : "Lines the workflow does not carry:\n  " + string.Join("\n  ", missing) + "\n") +
            "\nExpected block:\n" + block + "\n\n" +
            $"Begin marker present: {text.Contains(BeginMarker, StringComparison.Ordinal)} · " +
            $"end marker present: {text.Contains(EndMarker, StringComparison.Ordinal)}");
    }

    [Fact]
    public void NoRosterVerbIsNamedAnywhereInTheWorkflow_ARosterVerbWithItsOwnStepWouldRunTwice()
    {
        var tokens = Regex.Split(WorkflowText(), @"[^A-Za-z0-9_\-]+").ToHashSet(StringComparer.Ordinal);

        // Vacuity canary: the workflow names the runner, so a tokenisation that lost it lost everything.
        Assert.Contains(Runner, tokens);

        var named = CiAll.ProbeNames.Where(tokens.Contains)
                                    .OrderBy(s => s, StringComparer.Ordinal)
                                    .ToArray();

        Assert.True(named.Length == 0,
            "These ci-all roster verbs are named in .github/workflows/ci.yml: " + string.Join(", ", named) +
            ". ci-all runs them already, so a step naming one runs it a second time. This arm reads the file's " +
            "tokens against a derived population and makes no claim about invocation syntax: flag the guard " +
            "[CiProbe(…, Standalone = true)] if it needs its own process — which puts it in the generated " +
            "block — drop the step, or reword prose that happens to spell a verb.");
    }
}
