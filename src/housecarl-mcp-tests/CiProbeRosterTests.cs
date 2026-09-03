using System.Reflection;
using HousecarlGenerator;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The ci-all roster's population, held against a second, independently derived spelling of it.
///
/// The runner finds guards by walking its own assembly's reference closure and keeping the houseCARL
/// assemblies — fast, and correct as long as a guard's project is reachable from the generator. That "as
/// long as" is the part a guard should not take on trust: a project the generator does not reference could
/// carry an attributed guard that nothing runs, and the residue countdown would never see it either.
///
/// So this derives the population the other way, from the repo's own csproj files, loads each project's
/// assembly, and holds everything it finds against what the runner found. The two spellings disagree only
/// if a guard exists that CI does not run.
/// </summary>
[Trait("tier", "unit")]
public sealed class CiProbeRosterTests
{
    readonly ITestOutputHelper _out;
    public CiProbeRosterTests(ITestOutputHelper output) => _out = output;

    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>Every [CiProbe] verb in the repo's own assemblies, with where it was found.</summary>
    static (string Verb, string Assembly, string Member, bool Standalone)[] AttributedVerbsAcrossTheRepo()
    {
        var found = new List<(string, string, string, bool)>();

        foreach (var asm in RepoProjects.AllAssemblies())
        {
            var assemblyName = asm.GetName().Name!;

            foreach (var type in asm.GetTypes())
                foreach (var method in type.GetMethods(Members))
                    if (method.GetCustomAttribute<CiProbeAttribute>(inherit: false) is { } attr)
                        found.Add((attr.Name, assemblyName, $"{type.FullName}.{method.Name}", attr.Standalone));
        }

        return found.OrderBy(f => f.Item1, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void EveryAttributedGuardInTheRepoIsOneTheRunnerFound_NotOneNothingRuns()
    {
        var acrossRepo = AttributedVerbsAcrossTheRepo();
        var runnerKnows = CiAll.ProbeNames.Concat(CiAll.StandaloneProbeNames).ToHashSet(StringComparer.Ordinal);

        _out.WriteLine($"[CiProbe] across the repo: {acrossRepo.Length} · runner: {runnerKnows.Count} " +
                       $"({CiAll.ProbeNames.Count} roster + {CiAll.StandaloneProbeNames.Count} standalone) · " +
                       "assemblies the runner walked: " +
                       string.Join(", ", CiAll.GuardAssemblies.Select(a => a.GetName().Name)));

        // Vacuity canary: a scan that found nothing would satisfy the claim below it.
        Assert.True(acrossRepo.Length > 0,
            "No [CiProbe] attribute was found anywhere in the repo's assemblies. The attribute IS the roster, " +
            "so an empty scan means this measurement is broken, not that there are no guards.");

        var invisible = acrossRepo.Where(v => !runnerKnows.Contains(v.Verb))
                                  .Select(v => $"{v.Verb} — {v.Member} in {v.Assembly}")
                                  .OrderBy(s => s, StringComparer.Ordinal)
                                  .ToArray();

        Assert.True(invisible.Length == 0,
            "These guards carry [CiProbe] and the runner cannot see them, so nothing runs them:\n  " +
            string.Join("\n  ", invisible) +
            "\nThe runner walks its own assembly's houseCARL references, so a guard in a project the generator " +
            "does not reference is invisible to CI and to the residue countdown alike. Reference the project " +
            "from src/housecarl-generator, or move the guard.");
    }

    [Fact]
    public void EveryVerbTheRunnerKnowsWasFoundInTheRepoToo_TheTwoSpellingsAgree()
    {
        var all = AttributedVerbsAcrossTheRepo();

        // CiAll refuses a verb clash for the assemblies IT walks. This scan sees the whole repo, so a clash in
        // a project the runner does not reference reaches here first — and a bare ToDictionary answered it with
        // the BCL's "An item with the same key has already been added", which names neither guard.
        var clashes = all.GroupBy(v => v.Verb, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1)
                         .Select(g => $"{g.Key} — " + string.Join(", ", g.Select(v => $"{v.Member} in {v.Assembly}")))
                         .OrderBy(s => s, StringComparer.Ordinal)
                         .ToArray();

        Assert.True(clashes.Length == 0,
            "Two guards claim the same CI verb, so one of them is unreachable by name:\n  " +
            string.Join("\n  ", clashes) +
            "\nVerb names are the roster's identity and must be unique across the whole repo, not just across " +
            "the assemblies the runner walks.");

        var acrossRepo = all.ToDictionary(v => v.Verb, v => v, StringComparer.Ordinal);

        var missing = CiAll.ProbeNames.Concat(CiAll.StandaloneProbeNames)
                                      .Where(n => !acrossRepo.ContainsKey(n))
                                      .OrderBy(s => s, StringComparer.Ordinal)
                                      .ToArray();

        Assert.True(missing.Length == 0,
            "The runner dispatches these verbs and the repo-wide scan did not find them: " +
            string.Join(", ", missing) +
            ". The two derivations of the same population disagree, so one of them is wrong — most likely a " +
            "project whose source is not under src/ or whose build output is stale.");

        var flagDisagrees = CiAll.StandaloneProbeNames.Where(n => !acrossRepo[n].Standalone)
                                 .Concat(CiAll.ProbeNames.Where(n => acrossRepo[n].Standalone))
                                 .OrderBy(s => s, StringComparer.Ordinal)
                                 .ToArray();

        Assert.True(flagDisagrees.Length == 0,
            "The runner sorts these verbs into roster-or-standalone differently from the attribute that " +
            "declares them: " + string.Join(", ", flagDisagrees) + ".");
    }
}
