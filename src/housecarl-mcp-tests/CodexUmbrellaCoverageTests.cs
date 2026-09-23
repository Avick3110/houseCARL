using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The Codex umbrella skill (plugin/codex/housecarl/SKILL.md) is a hand-kept router: every
/// housecarl_* name it writes must be a live tool, and every bundled skill folder must be routed. Migrated
/// from the codex-umbrella-coverage-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class CodexUmbrellaCoverageTests
{
    /// <summary>Deliberate exceptions for both checks. Empty by design; add one only with a one-line reason.</summary>
    static readonly HashSet<string> Allow = new(StringComparer.Ordinal);

    static string UmbrellaPath => Path.Combine(HarnessPaths.RepoRoot, "plugin", "codex", "housecarl", "SKILL.md");

    static string Umbrella()
    {
        Assert.True(File.Exists(UmbrellaPath), $"'{UmbrellaPath}' not found; the umbrella router must resolve");
        return File.ReadAllText(UmbrellaPath);
    }

    static HashSet<string> ToolNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in HousecarlMcp.ToolSurface.Assembly.GetTypes())
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name is { Length: > 0 } n) names.Add(n);
        Assert.NotEmpty(names);
        return names;
    }

    static List<string> SkillSlugs()
    {
        var dir = Path.Combine(HarnessPaths.RepoRoot, ".claude", "skills");
        var slugs = Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!).Where(s => s.Length > 0)
                       .OrderBy(s => s, StringComparer.Ordinal).ToList()
            : new List<string>();
        Assert.NotEmpty(slugs);
        return slugs;
    }

    /// <summary>The generator's shared whole-identifier matcher, reached by reflection so these tests vouch
    /// for the one every consumer uses rather than for a copy.</summary>
    static readonly MethodInfo Boundary =
        typeof(HousecarlGenerator.RoslynLiteralReader).Assembly
            .GetType("HousecarlGenerator.ToolNameMatch", throwOnError: true)!
            .GetMethod("ReferencedAtBoundary", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

    static bool ReferencedAtBoundary(string text, string name) => (bool)Boundary.Invoke(null, new object[] { text, name })!;

    static List<string> MissingRefs(string umbrella, IEnumerable<string> required, ISet<string> allow) =>
        required.Where(r => !allow.Contains(r) && !ReferencedAtBoundary(umbrella, r))
                .OrderBy(r => r, StringComparer.Ordinal).ToList();

    static List<string> DeadToolNames(string umbrella, ISet<string> live, ISet<string> allow) =>
        Regex.Matches(umbrella, "housecarl_[a-z0-9]+(?:_[a-z0-9]+)*", RegexOptions.IgnoreCase)
             .Select(m => m.Value).Distinct(StringComparer.Ordinal)
             .Where(n => !live.Contains(n) && !allow.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    static HashSet<string> Empty() => new(StringComparer.Ordinal);

    // GUARD-SELF: the boundary matcher tells apart every colliding pair of real tool and skill names.
    [Fact]
    public void TheBoundaryMatcherTellsApartEveryCollidingNamePairOnTheSurface()
    {
        var required = ToolNames().Concat(SkillSlugs()).ToList();
        var falsePasses = (from s in required
                           from l in required
                           where l != s && l.Contains(s, StringComparison.Ordinal)
                           where ReferencedAtBoundary($"- `{l}` mentioned alone", s)
                           select $"'{s}' is satisfied by a text naming only '{l}'").ToList();
        Assert.True(falsePasses.Count == 0, string.Join("\n", falsePasses));
    }

    // INV1-GREEN: every housecarl_* name the umbrella writes is a live MCP tool.
    [Fact]
    public void EveryToolNameTheUmbrellaWritesIsALiveTool()
    {
        var dead = DeadToolNames(Umbrella(), ToolNames(), Allow);
        Assert.True(dead.Count == 0, "the Codex umbrella names tools that do not exist: " + string.Join(", ", dead));
    }

    // INV2-GREEN: every bundled skill is referenced in the umbrella router.
    [Fact]
    public void EveryBundledSkillIsRoutedByTheUmbrella()
    {
        var missing = MissingRefs(Umbrella(), SkillSlugs(), Allow);
        Assert.True(missing.Count == 0, "skills not routed by the Codex umbrella: " + string.Join(", ", missing));
    }

    // INV1-RED: a retired tool name in the router is caught.
    [Fact]
    public void ARetiredToolNameInTheRouterIsReportedDead() =>
        Assert.Contains("housecarl_read_record", DeadToolNames("read the winner with `housecarl_read_record` first", ToolNames(), Empty()));

    // INV1-GREEN-SELF: a live tool name is not reported dead.
    [Fact]
    public void ALiveToolNameIsNotReportedDead() =>
        Assert.Empty(DeadToolNames("read the winner with `housecarl_records` first", ToolNames(), Empty()));

    // ALLOW-1: an allow-listed non-tool name is not reported dead.
    [Fact]
    public void AnAllowListedNonToolNameIsNotReportedDead() =>
        Assert.Empty(DeadToolNames("the manifest's first key is `housecarl_artifact`", ToolNames(),
            new HashSet<string>(StringComparer.Ordinal) { "housecarl_artifact" }));

    // INV2-RED: a missing skill reference is caught.
    [Fact]
    public void AMissingSkillReferenceIsReported() =>
        Assert.Contains("facegen-diagnostics", MissingRefs("router text that mentions no skills", new[] { "facegen-diagnostics" }, Empty()));

    // PREFIX-RED: a name mentioned only as another name's prefix is still reported missing.
    [Fact]
    public void ANameMentionedOnlyAsALongerNamesPrefixIsStillMissing() =>
        Assert.Contains("housecarl_create", MissingRefs("- `housecarl_create_record` — the 1.x tool", new[] { "housecarl_create" }, Empty()));

    // PREFIX-GREEN: a genuinely referenced name is not reported missing.
    [Fact]
    public void AGenuinelyReferencedNameIsNotMissing() =>
        Assert.Empty(MissingRefs("- `housecarl_create` — the 2.0 tool", new[] { "housecarl_create" }, Empty()));

    // SUFFIX-RED: a name mentioned only as another name's suffix is still reported missing.
    [Fact]
    public void ANameMentionedOnlyAsALongerNamesSuffixIsStillMissing() =>
        Assert.Contains("record-jobs", MissingRefs("- `bulk-record-jobs` (catalogues, audits, link graphs)", new[] { "record-jobs" }, Empty()));

    // ALLOW-2: an allow-listed name is not reported missing.
    [Fact]
    public void AnAllowListedNameIsNotReportedMissing() =>
        Assert.Empty(MissingRefs("router text that mentions no tools", new[] { "housecarl_read_record" },
            new HashSet<string>(StringComparer.Ordinal) { "housecarl_read_record" }));
}
