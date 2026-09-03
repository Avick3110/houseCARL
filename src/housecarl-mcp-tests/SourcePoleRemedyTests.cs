using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The <c>source=</c> family of remedies, each one FOLLOWED rather than read.
///
/// <para>Seven sentences this branch wrote or rewrote spelled the successor call as
/// <c>housecarl_records source="…"</c> with no select term. <c>source=</c> names WHICH VERSION to read; it is not
/// a selection, and the tool refuses a call carrying only a source pole. Every one of those sentences therefore
/// told a caller to make a call that comes back refused — the same defect the chain lane's remedies had, in a
/// family the chain sweep did not reach.</para>
///
/// <para><b>The population here is HAND-DRAWN, and that is the honest description of it.</b> It is the ten sites
/// two independent reviewers derived and drove, not a set computed from the surface. The derived form — every
/// shipped prose site that names a call, held against whether that call is refused — is chartered work, not this
/// PR's: the oracle it needs is per-sentence reachability, which no containment net gives. Filed as #483, with
/// these ten sites as its first cells. Until it exists, a sentence added tomorrow with the same shape is caught
/// by nobody.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class SourcePoleRemedyTests : RecordsTestBase
{
    public SourcePoleRemedyTests(RecordsFixture f) : base(f) { }

    static void Refused(string r, string mustName)
    {
        Assert.StartsWith("error:", r);
        Assert.Contains(mustName, r);
    }

    // ---- the control: what every one of these sentences used to tell the caller to do -----------------

    /// <summary>The whole reason the seven sentences changed. Without this arm the select terms could be
    /// dropped again and nothing would notice.</summary>
    [Theory]
    [InlineData("plugin")]
    [InlineData("overlay")]
    public void ASourcePoleWithNoSelection_IsRefused(string pole)
    {
        var r = pole == "plugin"
            ? RecordsTools.Records(Svc, source: Plugin(W.OldName))
            : RecordsTools.Records(Svc, source: Overlay("post"));

        Refused(r, "select something");
    }

    // ---- the calls the repaired sentences name, MADE ------------------------------------------------

    /// <summary>`LoadOrderService`'s unticked-plugin advisory: "…use housecarl_records with source="X.esp" and
    /// something to select — types=[…] to scan the file".</summary>
    [Fact]
    public void TheUntickedPluginAdvisorysCall_TypesOverANamedPlugin_IsServed() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "WEAP" }));

    /// <summary>The same advisory's other spelling: "…or formids=[…] for named records".</summary>
    [Fact]
    public void TheUntickedPluginAdvisorysOtherCall_FormidsOverANamedPlugin_IsServed() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), formids: new[] { Fid(W.Weapons[0]) }));

    /// <summary>`housecarl_skypatcher_layer`'s own tool DESCRIPTION, and its no-op-scan note: "For ONE record's
    /// computed post-SkyPatcher state use housecarl_records formids=["&lt;FormID&gt;"] source={overlay…}".</summary>
    [Fact]
    public void TheSkyPatcherDescriptionsCall_FormidsOverTheOverlayPole_IsServed() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Overlay("post")));

    /// <summary>The copy read-back sentences: "read its records back with housecarl_records source="&lt;the
    /// patch&gt;.esp" types=["NPC_"]". Driven against a plugin that HAS no NPC_ record, which is the point — an
    /// empty result is served, not refused; only the missing selection was ever the refusal.</summary>
    [Fact]
    public void TheCopyReadBackCall_TypesOverTheWrittenPatch_IsServed() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "NPC_" }));

    // ---- the sentence that could NOT be made followable, and says so --------------------------------

    /// <summary>The copy's masters claim. `housecarl_records` renders no plugin's master list in any form or
    /// transport, so no select term makes that remedy answer its own question; the sentences now state the bound
    /// and name the weaker check that IS available. This arm holds the weaker check followable, and the bound
    /// stated — a sentence that promises nothing must not quietly start promising again.</summary>
    [Fact]
    public void TheWeakerMastersCheckTheCopySentencesName_IsServed()
    {
        var r = CheckTools.CheckTool(Svc, plugins: new[] { W.OldName }, findings: new[] { "missing_masters" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("missing master", r);
    }

    [Fact]
    public void TheCopySentencesStateTheMastersBoundRatherThanPromisingIt()
    {
        Assert.Contains("no houseCARL tool", WriteSentences.CopyReadBackUnverified);
        Assert.DoesNotContain("that the donor is absent from the masters",
                              WriteSentences.CopyReadBackUnverified);
    }
}
