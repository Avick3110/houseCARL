using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// What a <see cref="RecordsWorld"/> leaves behind when it disposes.
///
/// <para>A world repoints <c>CorpusRulebook.CorpusPath</c> — a process-global — at its own generated corpus,
/// and its Dispose deletes the directory that corpus lives in. If the static is not put back, everything
/// after the last private world resolves types against a path that no longer exists. The retired harness
/// carried this protection (a captured prior value restored in a finally); the conversion dropped it, and
/// what hid the loss was which collection xUnit happened to run first.</para>
///
/// <para>Deliberately NOT in the "records" collection: that collection's fixture repoints the static when it
/// is constructed, which is precisely the accident that made the missing restore invisible.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsWorldLifecycleTests
{
    [Fact]
    public void ADisposedWorldPutsTheGlobalCorpusPathBack_AndItStillNamesSomethingThatExists()
    {
        // A prior value this test owns, so the claim does not depend on what ran before it. It is a real
        // file: "restored" has to mean usable, not merely equal to a string.
        var sentinelDir = Path.Combine(Path.GetTempPath(), "hc-corpuspath-prior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sentinelDir);
        var prior = Path.Combine(sentinelDir, "corpus.json");
        File.WriteAllText(prior, "{}");

        var outermost = CorpusRulebook.CorpusPath;
        try
        {
            CorpusRulebook.CorpusPath = prior;

            string worldCorpus;
            using (var w = new RecordsWorld())
            {
                worldCorpus = CorpusRulebook.CorpusPath;

                // Vacuity canary: if a world stopped repointing the static there would be nothing to restore
                // and every claim below would pass for the wrong reason.
                Assert.NotEqual(prior, worldCorpus);
                Assert.StartsWith(w.Root, worldCorpus, StringComparison.OrdinalIgnoreCase);
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
