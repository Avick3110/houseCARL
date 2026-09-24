using System.Runtime.CompilerServices;
using HousecarlCore;
using HousecarlGenerator;

namespace HousecarlMcpTests;

/// <summary>The one corpus every test in this process reads. The corpus is a pure function of the Mutagen build, so
/// one copy serves every world; it is generated before any test runs, <c>CorpusRulebook.CorpusPath</c> is set to it
/// once, and nothing repoints or deletes it while tests run.</summary>
public static class TestCorpus
{
    static readonly string Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hc-test-corpus-" + Environment.ProcessId);

    /// <summary>The generated corpus.json.</summary>
    public static string Path { get; } = System.IO.Path.Combine(Dir, "gen", "corpus.json");

    /// <summary>A fresh rulebook over <see cref="Path"/>.</summary>
    public static CorpusRulebook Rulebook() => CorpusRulebook.Load(Path);

    [ModuleInitializer]
    internal static void Generate()
    {
        CorpusGenerator.GenerateAll(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.Combine(Dir, "ref"));
        CorpusRulebook.CorpusPath = Path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } };
    }
}
