using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Migrated from the emit-match-guard probe (#351): the committed mutagen-reference shards ship in the plugin, so a
/// classifier or emitter change that lands without a regeneration must fail here. The emit is deterministic and the
/// shards are pinned eol=lf, so a byte compare is sound.
/// </summary>
[Trait("tier", "integration")]
public sealed class EmitMatchGuardTests
{
    const string Remedy = "regenerate and commit the result: dotnet run --project src/housecarl-generator -c Release";

    static Dictionary<string, string> Index(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => p, StringComparer.Ordinal);

    // the committed shards match a fresh emit byte for byte, with no missing or extra file
    [Fact]
    public void TheCommittedReferenceShardsMatchAFreshEmit()
    {
        var committedDir = Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills", "mutagen-reference", "references");
        Assert.True(Directory.Exists(committedDir), $"no committed reference tree at {committedDir}");

        var root = Path.Combine(Path.GetTempPath(), "hc-emit-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            var freshDir = Path.Combine(root, "refs");
            CorpusGenerator.GenerateAll(Path.Combine(root, "generated"), freshDir);

            var committed = Index(committedDir);
            var fresh = Index(freshDir);
            Assert.NotEmpty(fresh);

            var notEmitted = committed.Keys.Except(fresh.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(notEmitted.Count == 0, $"committed but not emitted: {string.Join(", ", notEmitted)}. {Remedy}");

            var notCommitted = fresh.Keys.Except(committed.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(notCommitted.Count == 0, $"emitted but not committed: {string.Join(", ", notCommitted)}. {Remedy}");

            var stale = committed.Keys
                .Where(k => !File.ReadAllBytes(committed[k]).AsSpan().SequenceEqual(File.ReadAllBytes(fresh[k])))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(stale.Count == 0, $"committed content differs from a fresh emit: {string.Join(", ", stale)}. {Remedy}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
