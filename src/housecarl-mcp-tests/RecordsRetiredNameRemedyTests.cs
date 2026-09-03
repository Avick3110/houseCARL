using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The chain lane's refusals and its render header named tools the cut deleted, and one of them was a REMEDY
/// naming a call — <c>cross_plugin_query references=</c> — that a caller following it can no longer make.
///
/// <para>Every arm here DRIVES the sentence rather than reading it: the refusal text asserted below is what
/// <c>housecarl_records</c> actually emits, and the remedy each one names is called separately and asserted
/// served. The population of retired names comes from <see cref="AliasTable.AllRetiredTools"/>, so a name
/// retired later is covered without editing this file.</para>
///
/// <para>What is NOT covered here, stated rather than implied: the unscannable-record note
/// (<c>LoadOrderService</c> and <c>EffectChain</c>, "Inspect one with …") fires only when a record's body
/// THROWS on fetch, which needs a deliberately malformed plugin this test project has no world for. Its
/// remedy shape is driven below; the note's own text is not.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsRetiredNameRemedyTests : IDisposable
{
    readonly RecordsWorld _w = new();
    public void Dispose() => _w.Dispose();

    string Weapon => RecordsWorld.Fid(_w.Weapons[0]);
    string Mgef => RecordsWorld.Fid(_w.MgefA);

    static RecordsTools.RecordsProject Chain => new() { form = "chain" };
    static RecordsTools.RecordsWalk Reverse => new() { direction = "reverse" };

    /// <summary>Every name the redirect table calls retired. Derived, so this arm grows with the table.</summary>
    static IEnumerable<string> RetiredNames() => AliasTable.AllRetiredTools.Select(r => r.Old);

    /// <summary>The spellings this arm can decide, derived from each retired name's own shape rather than from
    /// a list somebody kept.
    ///
    /// <para>Every retired name is checked in its full <c>housecarl_</c> spelling. The BARE spelling — which is
    /// what all six repaired sentences actually carried, and what the retired vocabulary arm never matched — is
    /// checked only where it contains an underscore, because a single-word bare name is a word of ordinary
    /// English: <c>resolve</c> collides with "resolves to a Weapon" in this very refusal, and <c>remove</c>,
    /// <c>forward</c>, <c>create</c> and <c>apply</c> collide with prose everywhere. Stated rather than papered
    /// over: a sentence writing "use resolve" is outside this arm, and only its prefixed spelling is caught.</para></summary>
    static IEnumerable<string> DecidableSpellings()
    {
        foreach (var full in RetiredNames())
        {
            yield return full;
            var bare = full.StartsWith("housecarl_", StringComparison.Ordinal) ? full["housecarl_".Length..] : full;
            if (bare.Contains('_')) yield return bare;
        }
    }

    static void AssertNamesNoRetiredTool(string rendered)
    {
        foreach (var spelling in DecidableSpellings())
            Assert.False(rendered.Contains(spelling, StringComparison.Ordinal),
                         $"names the retired tool '{spelling}': {rendered.Split('\n')[0]}");
    }

    // ---- the three chain refusals, driven -----------------------------------------------------------

    [Fact]
    public void ChainOnANonMagicEffect_RefusesWithoutNamingARetiredTool()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { Weapon },
                                     project: Chain, walk: Reverse);

        Assert.Contains("error:", r);          // the reverse walk reports per SEED, inside the envelope
        Assert.Contains("not a MagicEffect", r);
        AssertNamesNoRetiredTool(r);
    }

    [Fact]
    public void ChainOnAnUnknownFormId_RefusesWithoutNamingARetiredTool()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { "0ABC12:" + _w.MasterName },
                                     project: Chain, walk: Reverse);

        Assert.Contains("error:", r);
        Assert.Contains("no record with FormID", r);
        AssertNamesNoRetiredTool(r);
    }

    [Fact]
    public void ChainWithANonEffectBearingType_RefusesWithoutNamingARetiredTool()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { Mgef },
                                     types: new[] { "WEAP" }, project: Chain, walk: Reverse);

        Assert.Contains("error:", r);
        Assert.Contains("not effect-bearing", r);
        AssertNamesNoRetiredTool(r);
    }

    /// <summary>The served render's own header. It opened <c>"effect_chain for …"</c> — a deleted tool's name
    /// printed to every caller of a lane that survives.</summary>
    [Fact]
    public void TheServedChainRender_DoesNotOpenWithARetiredToolName()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { Mgef },
                                     project: Chain, walk: Reverse);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("0 error(s)", r);      // served, so the header this arm is about is the subject
        AssertNamesNoRetiredTool(r);
    }

    // ---- the remedies those sentences name, FOLLOWED ------------------------------------------------

    /// <summary>The non-MagicEffect refusal sends the caller to <c>records references=[…] with types= or
    /// plugins=</c>. Made, and served.</summary>
    [Fact]
    public void TheRemedyTheChainRefusalNames_BoundedReferences_IsServed()
    {
        var r = RecordsTools.Records(_w.Svc, references: new[] { Weapon },
                                     types: new[] { "WEAP" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>The other branch, and the reason the sentence says "with types= or plugins=" rather than just
    /// naming <c>references=</c>: unbounded, the same call is refused. Without this arm the clause could be
    /// dropped and nothing would notice.</summary>
    [Fact]
    public void TheSameRemedyUnbounded_IsRefused_SoTheBoundingClauseIsLoadBearing()
    {
        var r = RecordsTools.Records(_w.Svc, references: new[] { Weapon });

        Assert.StartsWith("error:", r);
        Assert.Contains("must be combined with", r);
    }

    /// <summary>The unscannable-record note sends the caller to <c>records formids=[the FormID]</c>. Made, and
    /// served — this is the half of that sentence a test can reach.</summary>
    [Fact]
    public void TheRemedyTheUnscannableNoteNames_AFormidsRead_IsServed()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { Weapon });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }
}
