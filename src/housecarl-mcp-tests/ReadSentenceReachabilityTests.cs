using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using HousecarlMcp;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// REACHABILITY — the separate claim that a catalogue sentence actually arrives at a caller, asserted by
/// the constant's IDENTITY and never by its wording.
///
/// <para>Wording is proven once, at the catalogue (<see cref="ReadSentenceWordingTests"/>). What is left
/// over is "does this sentence reach a response at all", and a constant reference answers it whole-line by
/// construction — there is no prefix to pin, so the prefix-pinning failure cannot occur, and the arm is
/// allowed to be exactly this thin.</para>
///
/// <para><b>The population is DERIVED, not swept.</b> Every (member, render file) pair is read off
/// <c>src/housecarl-mcp</c> by a Roslyn syntax walk over member-access expressions — the same posture #483
/// and #498 take on their own guards. A new render site appears in the population the moment it is written,
/// and it must then be either covered by an arm or recorded in the countdown below. It cannot be neither.
/// </para>
///
/// <para><b>What the countdown records.</b> This class holds ONE fixture — the owned-child world — so the
/// pairs it can drive are the records lane's. Every other pair is a key in
/// <c>read-sentence-reachability-unreached.json</c> with the fixture that would reach it. Those fixtures
/// EXIST (the check-errors, dialogue, scripts and epoch worlds); each lives in its own xUnit collection
/// because the worlds share process-wide statics, so reaching them means one reachability class per
/// collection — which is a following engagement's work, not an invented fixture. The countdown is what
/// makes that remainder a visible, gated number instead of silence.</para>
///
/// <para><b>Two shapes are excluded, by a derived rule rather than a list.</b> A COMPOSER has no constant to
/// find in a response, so its reachability is claimed through the constants it composes; a member whose type
/// is not <c>string</c> is a cap, not a sentence. Both exclusions come off
/// <see cref="SentenceCatalogue.Members"/>, so neither can quietly grow.</para>
///
/// <para><b>Known limit, stated.</b> An arm proves the member reached A response, not that it reached it
/// from the specific file the pair names. A member with two render sites therefore has two arms proving one
/// thing twice. The population is still keyed per site so that a NEW site forces a decision rather than
/// riding an existing member's arm.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class ReadSentenceReachabilityTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    readonly ITestOutputHelper _out;

    public ReadSentenceReachabilityTests(OwnedChildFixture f, ITestOutputHelper output)
    {
        _w = f.W;
        _out = output;
    }

    /// <summary>The one file this class's countdown lives in — named by every failure below.</summary>
    public static string UnreachedPath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests",
                     "read-sentence-reachability-unreached.json");

    // ---- the derived population ------------------------------------------------------------------------

    /// <summary>One render site: a catalogue member named in a product source file.</summary>
    public readonly record struct Site(string Member, string File)
    {
        public string Key => Member + "|" + File;
    }

    static readonly object SitesLock = new();
    static IReadOnlyList<Site>? _sites;

    /// <summary>
    /// Every (string-valued catalogue member, product file) pair, off a Roslyn syntax walk of
    /// <c>src/housecarl-mcp</c>. Member-access expressions only, so a <c>&lt;see cref=…&gt;</c> in a doc
    /// comment — which parses as a cref, not an expression — is not a render site.
    /// </summary>
    public static IReadOnlyList<Site> Sites()
    {
        lock (SitesLock)
        {
            return _sites ??= Derive();
        }
    }

    static IReadOnlyList<Site> Derive()
    {
        var sentences = SentenceCatalogue.Members(typeof(ReadSentences))
            .Where(m => m.Kind == SentenceCatalogue.Shape.Value && m.Type == typeof(string))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var found = new HashSet<Site>();

        foreach (var path in ProductFiles())
        {
            var name = Path.GetFileName(path);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            foreach (var access in tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                if (access.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(ReadSentences) }
                 && sentences.Contains(access.Name.Identifier.ValueText))
                    found.Add(new Site(access.Name.Identifier.ValueText, name));
        }

        // A sentence the product never names DIRECTLY still reaches a caller when a COMPOSER with a render
        // site puts it there — NotRead, DeclaredBy, CarriedBy, NoDeclarers and CouldNotRead are all of that
        // kind, and a population that stopped at direct references would leave the whole precise tier with no
        // reachability claim at all. So a composer that has a render site lends it to every sentence its own
        // body names, read off the catalogue's source by the same walk. Two useful consequences: the
        // constants come back into the population, and a composer with BRANCHES needs the harvest to exercise
        // each of them, because each branch's sentence gets its own arm.
        foreach (var composer in ComposersWithRenderSites(found))
            foreach (var named in SentencesNamedInside(composer, sentences))
                found.Add(new Site(named, composer + "()"));

        return found.OrderBy(s => s.Member, StringComparer.Ordinal)
                    .ThenBy(s => s.File, StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>The catalogue's composers that the product actually calls somewhere under src/housecarl-mcp.</summary>
    static IReadOnlyList<string> ComposersWithRenderSites(IEnumerable<Site> direct)
    {
        var called = new HashSet<string>(StringComparer.Ordinal);
        var composers = SentenceCatalogue.Members(typeof(ReadSentences))
                                         .Where(m => m.Kind == SentenceCatalogue.Shape.Composer)
                                         .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var path in ProductFiles())
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            foreach (var access in tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                if (access.Expression is IdentifierNameSyntax { Identifier.ValueText: nameof(ReadSentences) }
                 && composers.Contains(access.Name.Identifier.ValueText))
                    called.Add(access.Name.Identifier.ValueText);
        }
        return called.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every catalogue sentence named inside one composer's own body.</summary>
    static IReadOnlyList<string> SentencesNamedInside(string composer, IReadOnlySet<string> sentences)
    {
        var catalogue = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "ReadSentences.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(catalogue));

        var body = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                       .SingleOrDefault(m => m.Identifier.ValueText == composer);

        Assert.True(body is not null,
            $"ReadSentences declares a composer '{composer}' the product calls, and its declaration is not in " +
            "ReadSentences.cs. The reachability population reads composer bodies off that file; a composer " +
            "declared elsewhere takes its sentences out of the population silently.");

        return body!.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Select(i => i.Identifier.ValueText)
                    .Where(sentences.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
    }

    static IEnumerable<string> ProductFiles() =>
        Directory.EnumerateFiles(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp"),
                                 "*.cs", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                          && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                          && !string.Equals(Path.GetFileName(p), "ReadSentences.cs", StringComparison.Ordinal));

    static IReadOnlyDictionary<string, string> Unreached()
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(UnreachedPath),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });

        Assert.True(doc is not null, $"'{UnreachedPath}' did not parse.");
        return doc!.Where(kv => !kv.Key.StartsWith('_'))
                   .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The pairs an arm below must actually drive — the derived population minus the countdown.</summary>
    public static TheoryData<string, string> Covered()
    {
        var recorded = Unreached();
        var data = new TheoryData<string, string>();
        foreach (var s in Sites().Where(s => !recorded.ContainsKey(s.Key))) data.Add(s.Member, s.File);
        return data;
    }

    // ---- the gate --------------------------------------------------------------------------------------

    [Fact]
    public void TheRenderSitePopulationIsDerived_AndEveryPairIsEitherDrivenOrRecorded()
    {
        var sites = Sites();
        var recorded = Unreached();

        // Vacuity canary. A rename, a moved catalogue, or a parser that stops finding anything would leave
        // every claim below true over an empty population — the guard failing toward green.
        Assert.True(sites.Count > 0,
            "The render-site walk over src/housecarl-mcp found no ReadSentences references at all, so every " +
            "reachability claim here is vacuous. Either the catalogue moved or the walk broke; both are this " +
            "test's subject, not a reason to pass.");

        var keys = sites.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var stale = recorded.Keys.Where(k => !keys.Contains(k))
                            .OrderBy(k => k, StringComparer.Ordinal).ToArray();

        _out.WriteLine($"render sites: {sites.Count} · recorded unreached: {recorded.Count} · " +
                       $"driven: {sites.Count - recorded.Count}");

        Assert.True(stale.Length == 0,
            "These entries name render sites that no longer exist:\n  " + string.Join("\n  ", stale) +
            $"\nDelete each from '{UnreachedPath}' in this PR. A countdown left above the real figure stops " +
            "being a countdown and becomes headroom.");
    }

    [Theory, MemberData(nameof(Covered))]
    public void TheSentenceReachesAResponse(string member, string file)
    {
        var sentence = (string)SentenceCatalogue.Value(typeof(ReadSentences), member)!;

        Assert.True(Harvest.Length > 0, "The harvest is empty, so this arm cannot say anything.");

        Assert.True(Identifiable(sentence),
            $"ReadSentences.{member} carries no literal run long enough to identify it in a response — its " +
            $"longest segment between format holes is {LongestRun(sentence)} non-space character(s), and an " +
            "arm over that would be satisfied by almost any text. A sentence that cannot be found by its own " +
            $"words is not reachable BY IDENTITY: record '{member}|{file}' in '{UnreachedPath}' saying so, and " +
            "let a fact about the response carry the claim instead.");
        try
        {
            Facts.States(Harvest, sentence);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"ReadSentences.{member} is rendered at {file} and no response this class drives states it.\n" +
                $"Either drive a call that reaches that site, or record '{member}|{file}' in " +
                $"'{UnreachedPath}' with the fixture that would reach it.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// The other direction: a pair recorded as unreached that the harvest DOES state is headroom, and the
    /// entry comes out. Without this the countdown only ever grows.
    /// </summary>
    [Fact]
    public void NoRecordedPairIsActuallyReachedByThisClass_TheCountdownOnlyGoesDown()
    {
        var reached = new List<string>();

        foreach (var (key, _) in Unreached())
        {
            var member = key.Split('|')[0];
            var sentence = SentenceCatalogue.Value(typeof(ReadSentences), member) as string;
            if (sentence is null || !Identifiable(sentence)) continue;

            try { Facts.States(Harvest, sentence); reached.Add(key); }
            catch { /* still unreached, which is what the entry says */ }
        }

        Assert.True(reached.Count == 0,
            "These pairs are recorded as unreached and this class's own harvest states them:\n  " +
            string.Join("\n  ", reached.OrderBy(x => x, StringComparer.Ordinal)) +
            $"\nDelete each key from '{UnreachedPath}' — the arm covers it now.");
    }

    /// <summary>
    /// The least a sentence must carry to be findable by its own words. Below it, an identity arm is
    /// vacuous: <c>SweepClose</c> is "]", and <c>SweepFamilySectionHead</c>'s only literal runs are a
    /// bracket and a space — every response contains those, so the arm would pass over a surface that had
    /// stopped emitting the sentence entirely.
    /// </summary>
    const int IdentifiableRun = 8;

    static int LongestRun(string sentence) =>
        System.Text.RegularExpressions.Regex.Split(sentence, @"\{\d+\}")
              .Select(seg => seg.Replace("{{", "{").Replace("}}", "}").Count(c => !char.IsWhiteSpace(c)))
              .DefaultIfEmpty(0)
              .Max();

    static bool Identifiable(string sentence) => LongestRun(sentence) >= IdentifiableRun;

    // ---- the harvest -----------------------------------------------------------------------------------
    //
    // Every response this class can drive from the owned-child world, concatenated. Kept as one string on
    // purpose: reachability is "does this sentence arrive at a caller at all", and which of these calls
    // produced it is the render site's own question, which the population already keys on.

    string? _harvest;

    string Harvest => _harvest ??= string.Join("\n\n----\n\n", new[]
    {
        Read(_w.CellA),                                        // the cheap tier, on a false-empty collection
        Read(_w.CellA, Tree),                                   // the precise tier: who declares, by name
        Read(_w.CellF, Tree),                                   // two lower declarers, and two fields nobody declares
        Read(_w.CellC, Tree),                                   // a single toucher
        Read(_w.Worldspace, Tree),                              // the SINGULAR shape (TopCell), counted not named
        Read(_w.Topic, Tree),                                   // a topic's INFO responses
        Read(_w.Weapon, Tree),                                  // a 3-toucher with no child-bearing field
        ReadBoth(_w.CellA, _w.CellF, Tree),                     // row 2+, which carries the short header
        Read(_w.CellF, Tree, maxChars: 900),                    // a cut, so the overflow remedies render
        ReadJson(_w.CellA),                                     // the json transport's own clause
        ReadJson(_w.CellF),
    });

    static RecordsTools.RecordsProject Tree => new() { form = "tree" };

    string Read(Mutagen.Bethesda.Plugins.FormKey fk, RecordsTools.RecordsProject? project = null, int maxChars = 0) =>
        RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(fk) },
                             project: project ?? new RecordsTools.RecordsProject { form = "everything" },
                             max_chars: maxChars);

    string ReadJson(Mutagen.Bethesda.Plugins.FormKey fk) =>
        RecordsTools.Records(_w.Svc, formids: new[] { OwnedChildWorld.Fid(fk) }, format: "json",
                             project: new RecordsTools.RecordsProject { form = "everything" });

    string ReadBoth(Mutagen.Bethesda.Plugins.FormKey a, Mutagen.Bethesda.Plugins.FormKey b,
                    RecordsTools.RecordsProject project) =>
        RecordsTools.Records(_w.Svc,
                             formids: new[] { OwnedChildWorld.Fid(a), OwnedChildWorld.Fid(b) }, project: project);
}
