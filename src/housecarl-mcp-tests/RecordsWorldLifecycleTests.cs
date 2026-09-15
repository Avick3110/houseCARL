using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a synthetic world leaves behind when it disposes: a world repoints two process-globals —
/// <c>CorpusRulebook.CorpusPath</c> at its own generated corpus and <c>ResultsStore.OverrideDirForTests</c> at its
/// own auto-spill directory — and deletes both directories on Dispose, so an unrestored static leaves everything
/// after it resolving types, or spilling artifacts, into a path that no longer exists.
/// Deliberately NOT in the "records" collection — that collection's fixture repoints the statics itself, which
/// would hide a missing restore. Every world that repoints them gets an arm here; the suite otherwise
/// passes only on the order the collections happen to run in.</summary>
[Trait("tier", "integration")]
public sealed class RecordsWorldLifecycleTests
{
    [Fact]
    public void ADisposedWorldPutsTheProcessGlobalsBack_AndTheyStillNameSomethingThatExists() =>
        AssertRestoresTheProcessGlobals(() => { var w = new RecordsWorld(); return (w, w.Root); });

    [Fact]
    public void ADisposedOwnedChildWorldPutsTheProcessGlobalsBack() =>
        AssertRestoresTheProcessGlobals(() => { var w = new OwnedChildWorld(); return (w, w.Root); });

    /// <summary>Build one world, dispose it, and require both statics to name what they named before — a real
    /// file and a real directory, since "restored" has to mean usable rather than merely equal to a string.</summary>
    static void AssertRestoresTheProcessGlobals(Func<(IDisposable World, string Root)> build)
    {
        // A prior value this test owns, so the claim does not depend on what ran before it.
        var sentinelDir = Path.Combine(Path.GetTempPath(), "hc-corpuspath-prior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sentinelDir);
        var prior = Path.Combine(sentinelDir, "corpus.json");
        File.WriteAllText(prior, "{}");

        var priorResults = Path.Combine(sentinelDir, "results");
        Directory.CreateDirectory(priorResults);

        var outermost = CorpusRulebook.CorpusPath;
        var outermostResults = ResultsStore.OverrideDirForTests;
        try
        {
            CorpusRulebook.CorpusPath = prior;
            ResultsStore.OverrideDirForTests = priorResults;

            string worldCorpus;
            string worldResults;
            var (world, root) = build();
            using (world)
            {
                worldCorpus = CorpusRulebook.CorpusPath;
                worldResults = ResultsStore.Dir;

                // If a world stopped repointing a static there would be nothing to restore, and every claim
                // below would pass for the wrong reason.
                Assert.NotEqual(prior, worldCorpus);
                Assert.StartsWith(root, worldCorpus, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(priorResults, worldResults);
                Assert.StartsWith(root, worldResults, StringComparison.OrdinalIgnoreCase);
            }

            // The world's own corpus and results directory are gone with its root — which is exactly why the
            // statics may not still be naming them.
            Assert.False(File.Exists(worldCorpus),
                         "the disposed world did not delete its root, so this arm never reached its case");
            Assert.False(Directory.Exists(worldResults),
                         "the disposed world did not delete its root, so this arm never reached its case");

            Assert.Equal(prior, CorpusRulebook.CorpusPath);
            Assert.True(File.Exists(CorpusRulebook.CorpusPath),
                        $"CorpusRulebook.CorpusPath names '{CorpusRulebook.CorpusPath}', which does not exist. " +
                        "A disposed world left the process-global pointing into its deleted temp directory.");

            Assert.Equal(priorResults, ResultsStore.OverrideDirForTests);
            Assert.True(Directory.Exists(ResultsStore.Dir),
                        $"ResultsStore.Dir names '{ResultsStore.Dir}', which does not exist. " +
                        "A disposed world left the process-global pointing into its deleted temp directory.");
        }
        finally
        {
            CorpusRulebook.CorpusPath = outermost;
            ResultsStore.OverrideDirForTests = outermostResults;
            try { Directory.Delete(sentinelDir, true); } catch { /* temp cleanup best-effort */ }
        }
    }
}
