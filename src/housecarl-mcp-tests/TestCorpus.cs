using HousecarlCore;
using HousecarlGenerator;

namespace HousecarlMcpTests;

/// <summary>The one corpus every test in this process reads. The corpus is a pure function of the Mutagen build, so
/// one copy serves every world; <see cref="TestRunSetup"/> generates it before the first test runs and sets
/// <c>CorpusRulebook.CorpusPath</c> to it once, and nothing repoints or deletes it while tests run.</summary>
public static class TestCorpus
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc-test-corpus-" + Guid.NewGuid().ToString("N"));

    static readonly Lazy<string> Generated = new(Generate);
    static readonly Lazy<CorpusRulebook> Book = new(() => CorpusRulebook.Load(Path));

    /// <summary>The generated corpus.json, generated on first use.</summary>
    public static string Path => Generated.Value;

    /// <summary>The rulebook over <see cref="Path"/>, loaded once. It is safe to share: a call that needs link
    /// targets derives its own with <see cref="CorpusRulebook.WithLinkTargets"/>.</summary>
    public static CorpusRulebook Rulebook => Book.Value;

    static string Generate()
    {
        var path = System.IO.Path.Combine(Dir, "gen", "corpus.json");
        try
        {
            CorpusGenerator.GenerateAll(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.Combine(Dir, "ref"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The test corpus could not be generated into {Dir}: {ex.Message}", ex);
        }
        CorpusRulebook.CorpusPath = path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } };
        return path;
    }
}
