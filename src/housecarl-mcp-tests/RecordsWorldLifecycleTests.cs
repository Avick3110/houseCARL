using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a synthetic world leaves behind when it disposes: a world repoints the process-global
/// <c>CorpusRulebook.CorpusPath</c> at its own generated corpus and deletes that directory on Dispose, so an
/// unrestored static leaves everything after it resolving types against a path that no longer exists.
/// Deliberately NOT in the "records" collection — that collection's fixture repoints the static itself, which
/// would hide a missing restore. Every world that repoints the static gets an arm here; the suite otherwise
/// passes only on the order the collections happen to run in.</summary>
[Trait("tier", "integration")]
public sealed class RecordsWorldLifecycleTests
{
    [Fact]
    public void ADisposedWorldPutsTheGlobalCorpusPathBack_AndItStillNamesSomethingThatExists() =>
        AssertRestoresTheGlobalCorpusPath(() => { var w = new RecordsWorld(); return (w, w.Root); });

    [Fact]
    public void ADisposedOwnedChildWorldPutsTheGlobalCorpusPathBack() =>
        AssertRestoresTheGlobalCorpusPath(() => { var w = new OwnedChildWorld(); return (w, w.Root); });

    /// <summary>Build one world, dispose it, and require the static to name what it named before — a real file,
    /// since "restored" has to mean usable rather than merely equal to a string.</summary>
    static void AssertRestoresTheGlobalCorpusPath(Func<(IDisposable World, string Root)> build)
    {
        // A prior value this test owns, so the claim does not depend on what ran before it.
        var sentinelDir = Path.Combine(Path.GetTempPath(), "hc-corpuspath-prior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sentinelDir);
        var prior = Path.Combine(sentinelDir, "corpus.json");
        File.WriteAllText(prior, "{}");

        var outermost = CorpusRulebook.CorpusPath;
        try
        {
            CorpusRulebook.CorpusPath = prior;

            string worldCorpus;
            var (world, root) = build();
            using (world)
            {
                worldCorpus = CorpusRulebook.CorpusPath;

                // If a world stopped repointing the static there would be nothing to restore, and every claim
                // below would pass for the wrong reason.
                Assert.NotEqual(prior, worldCorpus);
                Assert.StartsWith(root, worldCorpus, StringComparison.OrdinalIgnoreCase);
            }

            // The world's own corpus is gone with its root — which is exactly why the static may not still
            // be naming it.
            Assert.False(File.Exists(worldCorpus),
                         "the disposed world did not delete its root, so this arm never reached its case");

            Assert.Equal(prior, CorpusRulebook.CorpusPath);
            Assert.True(File.Exists(CorpusRulebook.CorpusPath),
                        $"CorpusRulebook.CorpusPath names '{CorpusRulebook.CorpusPath}', which does not exist. " +
                        "A disposed world left the process-global pointing into its deleted temp directory.");
        }
        finally
        {
            CorpusRulebook.CorpusPath = outermost;
            try { Directory.Delete(sentinelDir, true); } catch { /* temp cleanup best-effort */ }
        }
    }
}
