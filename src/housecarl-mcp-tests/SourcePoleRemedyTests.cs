using System.ComponentModel;
using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The <c>source=</c> family of remedies, each one followed rather than read. <c>source=</c> names
/// WHICH VERSION to read, not a selection, so a sentence naming it without a select term sends the caller to a
/// call the tool refuses. The population is hand-drawn: these are named sites, not a set computed from the
/// surface, so a sentence added later with the same shape is not caught here.</summary>
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

    // ---- the control: a source pole on its own -------------------------------------------------------

    /// <summary>Without this the select terms could be dropped back out of those sentences and nothing would
    /// notice.</summary>
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
    /// patch&gt;.esp" types=["NPC_"]". Driven against a plugin with no NPC_ record, which is the point: an empty
    /// result is served, not refused — only a missing selection refuses.</summary>
    [Fact]
    public void TheCopyReadBackCall_TypesOverTheWrittenPatch_IsServed() =>
        Served(RecordsTools.Records(Svc, source: Plugin(W.OldName), types: new[] { "NPC_" }));

    // ---- the sentence that could NOT be made followable, and says so --------------------------------

    /// <summary>The copy's masters claim. `housecarl_records` renders no plugin's master list in any form or
    /// transport, so no select term makes that remedy answer its own question; the sentences state the bound and
    /// name the weaker check that IS available. Held here: that weaker check is followable.</summary>
    [Fact]
    public void TheWeakerMastersCheckTheCopySentencesName_IsServed()
    {
        var r = CheckTools.CheckTool(Svc, plugins: new[] { W.OldName }, findings: new[] { "missing_masters" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("missing master", r);
    }

    // ---- the sentences themselves, pinned to the call above -----------------------------------------
    //
    // The tests above prove the CALL works; they do not prove the SENTENCE still names it. These three pins
    // read the shipped text itself rather than a copy of it.

    static readonly string[] SelectTerms =
        { "formids=", "types=", "plugins=", "where=", "references=", "conflicts_only=" };

    /// <summary>`housecarl_skypatcher_layer`'s PUBLISHED description, read off the same attribute the SDK
    /// builds the schema from. Where it tells a caller to reach for `housecarl_records` with a source pole it
    /// must also name a selection.</summary>
    [Fact]
    public void TheSkyPatcherToolDescriptionNamesASelectionBesideItsSourcePole()
    {
        var m = typeof(SkyPatcherTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is { } a
                      && a.Name == ToolNames.SkypatcherLayer);
        var desc = m.GetCustomAttribute<DescriptionAttribute>()!.Description;

        Assert.Contains(ToolNames.Records + " ", desc);
        var at = desc.IndexOf(ToolNames.Records, StringComparison.Ordinal);
        var sentence = desc.Substring(at, Math.Min(220, desc.Length - at));
        Assert.Contains("source=", sentence);
        Assert.Contains(SelectTerms, t => sentence.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>The on-disk-but-not-listed advisory, rendered by the shipped method rather than read from
    /// source. It names `housecarl_records` with a source pole, so it must name a selection with it. The test
    /// asserts it reached the records-naming branch instead of skipping — an early return here could not fail.
    /// The sibling installed-but-unticked advisory is the same sentence shape and is not pinned: this world has
    /// no such plugin.</summary>
    [Fact]
    public void TheOnDiskNotListedAdvisoryNamesASelectionBesideItsSourcePole()
    {
        var m = typeof(LoadOrderService).GetMethod("ExplainPluginAbsence",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
        var text = (string?)m.Invoke(Svc, new object[] { W.OldName });

        Assert.NotNull(text);
        Assert.Contains(ToolNames.Records, text!);   // never a silent skip
        Assert.Contains("source=", text!);
        Assert.Contains(SelectTerms, t => text!.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>The copy tool's standalone line, against the shipped source. It is rendered inside a write path
    /// this test project has no fixture for, so the pin is on the text rather than on a render.</summary>
    [Fact]
    public void TheCopyToolsStandaloneLineStatesTheBoundAndPromisesNothing()
    {
        var src = File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "NpcCopyTools.cs"));

        Assert.Contains("no houseCARL tool can list a plugin's masters", src);
        Assert.DoesNotContain("that the donor is absent from the masters", src);
    }

    [Fact]
    public void TheCopySentencesStateTheMastersBoundRatherThanPromisingIt()
    {
        Assert.Contains("no houseCARL tool", WriteSentences.CopyReadBackUnverified);
        Assert.DoesNotContain("that the donor is absent from the masters",
                              WriteSentences.CopyReadBackUnverified);
    }
}
