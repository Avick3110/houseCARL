using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// No refusal or render header on <c>housecarl_records</c>' chain and scan lanes may name a retired tool, and
/// every remedy one of them names must be a call a caller can actually make.
///
/// <para>Each test drives the sentence rather than reading it, and calls the remedy separately to assert it is
/// served. The population of retired names comes from <see cref="AliasTable.AllRetiredTools"/>, so a name
/// retired later is covered without editing this file.</para>
///
/// <para>Not covered: the unscannable-record note (<c>LoadOrderService</c> and <c>EffectChain</c>, "Inspect one
/// with …") fires only when a record's body THROWS on fetch, which needs a deliberately malformed plugin this
/// test project has no world for. Its remedy shape is driven below; the note's own text is not.</para>
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

    /// <summary>Every name the redirect table calls retired, so the population grows with the table.</summary>
    static IEnumerable<string> RetiredNames() => AliasTable.AllRetiredTools.Select(r => r.Old);

    /// <summary>The spellings these tests can decide, derived from each retired name's own shape. Every retired
    /// name is checked in its full <c>housecarl_</c> spelling; the bare spelling only where it contains an
    /// underscore, because a single-word bare name is ordinary English — <c>resolve</c> collides with "resolves
    /// to a Weapon" in these very refusals, and <c>remove</c>, <c>forward</c>, <c>create</c> and <c>apply</c>
    /// collide with prose everywhere. A sentence writing "use resolve" is therefore not caught.</summary>
    static IEnumerable<string> DecidableSpellings()
    {
        foreach (var full in RetiredNames())
        {
            yield return full;
            var bare = full.StartsWith("housecarl_", StringComparison.Ordinal) ? full["housecarl_".Length..] : full;
            if (bare.Contains('_')) yield return bare;
        }
    }

    static string Head(string s) => s.Split('\n')[0];

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

    /// <summary>The served render's own header, which every caller of this lane sees.</summary>
    [Fact]
    public void TheServedChainRender_DoesNotOpenWithARetiredToolName()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { Mgef },
                                     project: Chain, walk: Reverse);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("0 error(s)", r);      // served, so the header is what the check below reads
        AssertNamesNoRetiredTool(r);
    }

    // ---- the SCAN lane's headers, over every transport the tool declares ----------------------------

    /// <summary>The transports, derived from the tool's own format vocabulary rather than typed out, so a
    /// transport added later gets a cell without an edit here.</summary>
    public static IEnumerable<object[]> Transports() =>
        Enum.GetNames<Wire.QueryFormat>().Select(n => new object[] { n.ToLowerInvariant() });

    /// <summary>The scan lane's header, the highest-traffic render on the surface, held over every declared
    /// transport through the same derived retired-name check the chain header uses.</summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public void AServedScanRenderNamesNoRetiredTool(string format)
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: format,
                                     project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + Head(r));
        AssertNamesNoRetiredTool(r);
    }

    /// <summary>The group_by twin of the same header, which is a second call site.</summary>
    [Fact]
    public void AServedAggregateScanRenderNamesNoRetiredTool()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + Head(r));
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

    /// <summary>Why the sentence says "with types= or plugins=" rather than just naming <c>references=</c>:
    /// unbounded, the same call is refused. Without this the clause could be dropped and nothing would
    /// notice.</summary>
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
