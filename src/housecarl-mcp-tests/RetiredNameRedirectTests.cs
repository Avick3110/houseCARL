using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// #472 — the retired-name redirect, swept against what 1.9 actually PUBLISHED rather than against a
/// maintained list.
///
/// <para>BindingShimProbe's D3 arm takes its subjects from <c>AliasTable.AllRetiredTools</c> minus the
/// running server's registered set. The right-hand side is derived from the wire; the left-hand side is a
/// list somebody maintains. So D3 proves every retired name the table KNOWS ABOUT redirects to a live tool
/// — it cannot prove the table is complete. Delete a tool, forget its row, and D3 sweeps the rows it has,
/// reports clean, and a caller on old docs gets a bare "Unknown tool".</para>
///
/// <para>Here the subject set is every tool houseCARL 1.9.0 published, captured off the shipped 1.9 server
/// (see data/tools-list-1.9.0.json for its provenance). Each one is either still on the surface or must
/// redirect — a deleted tool with no row is a RED cell by construction, not an invisible omission.</para>
///
/// <para>This DUPLICATES D3 for the rows both cover; D3 stays until BindingShimProbe converts whole, which
/// the ruling places at the cut. It is not repointed here — nothing moves out of the old harness except by
/// a conversion PR.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class RetiredNameRedirectTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public RetiredNameRedirectTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    static readonly Regex ToolToken = new("housecarl_[a-z0-9_]+", RegexOptions.Compiled);

    static string FixturePath =>
        Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "data", "tools-list-1.9.0.json");

    /// <summary>What 1.9.0 published — read from the frozen capture, one theory row each.</summary>
    public static IEnumerable<object[]> ToolsPublishedBy19()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        foreach (var t in doc.RootElement.GetProperty("tools").EnumerateArray())
            yield return new object[] { t.GetString()! };
    }

    /// <summary>Names a tool token only where the next character cannot extend it — 'housecarl_create' is a
    /// prefix of 'housecarl_create_record', and three of the 2.0 successors are prefixes of names they
    /// absorbed, so a bare Contains would accept a response that never mentions the successor at all.</summary>
    static bool NamesToolAtBoundary(string text, string tool)
    {
        int from = 0;
        while ((from = text.IndexOf(tool, from, StringComparison.Ordinal)) >= 0)
        {
            int after = from + tool.Length;
            char next = after < text.Length ? text[after] : ' ';
            if (!(char.IsLetterOrDigit(next) || next == '_')) return true;
            from = after;
        }
        return false;
    }

    [Theory]
    [MemberData(nameof(ToolsPublishedBy19))]
    public void EveryToolNineteenPublishedIsStillLiveOrRedirectsToRegisteredSuccessors(string old)
    {
        if (_s.PublishedNames.Contains(old)) return;    // still on the surface: nothing was retired here

        var row = AliasTable.AllRetiredTools.FirstOrDefault(r =>
            string.Equals(r.Old, old, StringComparison.OrdinalIgnoreCase));

        Assert.True(row.Old is not null,
            $"{old} was published by houseCARL 1.9.0 and is not registered now, and AliasTable has no " +
            "retired-tool row for it. A caller on 1.x docs gets a bare 'Unknown tool' — which is the dead " +
            "end the redirect exists to prevent, and the omission a maintained subject set cannot see.");

        // ALL of what the row names, not any of it: housecarl_validate_dialogue was SPLIT and names two
        // successors, and "at least one registered" would let that row pass on one while the other is dead.
        var promises = ToolToken.Matches(row.Successor).Select(m => m.Value)
                                .Where(t => !string.Equals(t, old, StringComparison.Ordinal))
                                .Distinct(StringComparer.Ordinal).ToArray();

        Assert.True(promises.Length > 0, $"{old}'s row names no successor tool at all: {row.Successor}");

        var unregistered = promises.Where(t => !_s.PublishedNames.Contains(t)).ToArray();
        Assert.True(unregistered.Length == 0,
            $"{old}'s row points at [{string.Join(", ", unregistered)}], which tools/list does not publish.");

        // And the LIVE response has to say them — a row nobody reads redirects nobody.
        var r = _s.Call(old, "{}");
        var unspoken = promises.Where(t => !NamesToolAtBoundary(r.Text, t)).ToArray();
        Assert.True(unspoken.Length == 0,
            $"{old}'s row promises [{string.Join(", ", unspoken)}] but the response does not name them: {r.Describe()}");
    }

    /// <summary>
    /// The vacuity canary. If every 1.9 name is still registered, the theory above asserts nothing and
    /// passes 45 times — a broken sweep reported as a clean one.
    /// </summary>
    [Fact]
    public void TheSweepHasSubjects_SomeToolNineteenPublishedIsGoneFromTheSurface()
    {
        var retired = ToolsPublishedBy19().Select(r => (string)r[0])
                                          .Where(n => !_s.PublishedNames.Contains(n))
                                          .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        _out.WriteLine($"1.9 published {ToolsPublishedBy19().Count()}, now registered {_s.PublishedNames.Count}, " +
                       $"retired and gone {retired.Length}: {string.Join(", ", retired)}");

        Assert.NotEmpty(retired);
    }
}
