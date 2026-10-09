using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Three refusals that stay refused, each naming the call that works, and that call run as served.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsRefusalRouteTests : RecordsTestBase
{
    public RecordsRefusalRouteTests(RecordsFixture f) : base(f) { }

    static readonly string[] Armo = { "ARMO" };
    static readonly string[] Spel = { "SPEL" };

    // ---- fields_source= a plugin ------------------------------------------------------------------

    [Fact]
    public void FieldsSourceAPlugin_NamesSourceWithThatPlugin()
    {
        var r = RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName), fields_source: W.MasterName);
        Refused(r, "is not a value it takes", "'winner'", $"source=\"{W.MasterName}\"", "plugins= still selects");
        Assert.DoesNotContain("origin", r);
    }

    [Fact]
    public void FieldsSourceNotAPlugin_NamesSourceWithAPlaceholder() =>
        Refused(RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName), fields_source: "master"),
                "is not a value it takes", "'winner'", "source=\"<plugin>\"");

    /// <summary>No plugins= on the call: the sentence names no scope it lacks, and its source= route runs.</summary>
    [Fact]
    public void FieldsSourceWithoutPlugins_NamesNoPluginsScope()
    {
        var r = RecordsTools.Records(Svc, types: Armo, fields_source: W.MasterName);
        Refused(r, "is not a value it takes", $"source=\"{W.MasterName}\"");
        Assert.DoesNotContain("plugins=", r);
        Served(RecordsTools.Records(Svc, types: Armo, source: Plugin(W.MasterName)), RecordsWorld.RenamedArmorOldEid);
    }

    /// <summary>On a formids= read the list lane answers: source= names the version, and that read runs.</summary>
    [Fact]
    public void FieldsSourceOnFormids_TheListLaneAnswersWithSource()
    {
        var spell = new[] { Fid(W.SpellA) };
        var r = RecordsTools.Records(Svc, formids: spell, fields_source: W.MasterName);
        Refused(r, "on a formids= read", "source=");
        Assert.DoesNotContain("plugins=", r);
        Served(RecordsTools.Records(Svc, formids: spell, source: Plugin(W.MasterName)), "HcRecSpellA");
    }

    /// <summary>On a walk the walk's sentence answers, never offering 'winner', and source= on the walk runs.</summary>
    [Fact]
    public void FieldsSourceOnAWalk_NamesSourceAndNotWinner()
    {
        var spell = new[] { Fid(W.SpellA) };
        var r = RecordsTools.Records(Svc, formids: spell, walk: new RecordsTools.RecordsWalk(), fields_source: W.MasterName);
        Refused(r, "a walk's reading forms", "source=");
        Assert.DoesNotContain("'winner'", r);
        Served(RecordsTools.Records(Svc, formids: spell, walk: new RecordsTools.RecordsWalk(), source: Plugin(W.MasterName)),
               "HcRecMgefFire");
    }

    /// <summary>On info_order source= means an off-order fold, so the sentence says drop it and offers no source=.</summary>
    [Fact]
    public void FieldsSourceOnInfoOrder_SaysDropItWithoutSource()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, fields_source: W.MasterName, project: Form("info_order"));
        Refused(r, "drop it");
        Assert.DoesNotContain("source=", r.Replace("fields_source=", ""));
    }

    /// <summary>The route the sentence names: the same scope with source= reads the master's copy.</summary>
    [Fact]
    public void FieldsSourceRoute_SourceUnderTheSameScopeReadsThatPluginsVersion() =>
        Served(RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName), source: Plugin(W.MasterName)),
               RecordsWorld.RenamedArmorOldEid);

    // ---- walk + where -----------------------------------------------------------------------------

    [Fact]
    public void WalkPlusWhere_NamesTheSeedRouteAndTheNarrowingRoutes() =>
        Refused(RecordsTools.Records(Svc, types: Spel, where: new[] { "editorid = HcRecSpellA" }, walk: new RecordsTools.RecordsWalk()),
                "to pick the seeds by where=", "walk.through", "walk.exclusions", "to_file=", "formids=[\"@<file>\"]");

    /// <summary>formids= seeds with where=: the seed route (scan with where= and to_file=, walk the file) runs.</summary>
    [Fact]
    public void WalkPlusWhereOnFormidsSeeds_TheSeedRouteRuns()
    {
        var seeds = new[] { Fid(W.SpellA), Fid(W.SpellB) };
        var pick = new[] { "editorid = HcRecSpellB" };
        var r = RecordsTools.Records(Svc, formids: seeds, where: pick, walk: new RecordsTools.RecordsWalk());
        Refused(r, "scan with where= and to_file= and walk formids=[\"@<file>\"]");
        Assert.DoesNotContain("types=", r);
        var path = W.Scratch("results", "walk-seeds.jsonl");
        Served(RecordsTools.Records(Svc, formids: seeds, where: pick, to_file: path));
        var walked = RecordsTools.Records(Svc, formids: new[] { "@" + path }, walk: new RecordsTools.RecordsWalk());
        Served(walked, "OtherMgef");
        Assert.DoesNotContain("HcRecMgefFire", walked);
    }

    /// <summary>A reverse carrier walk with where= gets the reverse walk's references= route, which runs.</summary>
    [Fact]
    public void ReverseCarrierWalkPlusWhere_NamesReferencesAndThatRouteRuns()
    {
        var mgef = new[] { Fid(W.MgefA) };
        var pick = new[] { "editorid = HcRecSpellC" };
        var r = RecordsTools.Records(Svc, formids: mgef, where: pick,
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" });
        Refused(r, "references=");
        Assert.DoesNotContain("walk.through", r);
        var refs = RecordsTools.Records(Svc, types: Spel, references: mgef, where: pick);
        Served(refs, "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellA", refs);
    }

    /// <summary>walk.through narrows the reached set by type: the spells' magic effect is left out.</summary>
    [Fact]
    public void WalkRoute_ThroughNarrowsTheReachedSetByType()
    {
        Served(RecordsTools.Records(Svc, types: Spel, walk: new RecordsTools.RecordsWalk()), "HcRecMgefFire");
        var r = RecordsTools.Records(Svc, types: Spel, walk: new RecordsTools.RecordsWalk { through = new[] { "Spell" } });
        Served(r, "HcRecSpellA");
        Assert.DoesNotContain("HcRecMgefFire", r);
    }

    /// <summary>A stop exclusion keeps the stopped Spell and leaves out the magic effect reached only past it.</summary>
    [Fact]
    public void WalkRoute_ExclusionsStopAtAType()
    {
        var seed = new[] { Fid(W.ListHop) };
        Served(RecordsTools.Records(Svc, formids: seed, walk: new RecordsTools.RecordsWalk()), "HcRecSpellHop", "HcRecMgefHop");
        var r = RecordsTools.Records(Svc, formids: seed, walk: new RecordsTools.RecordsWalk
        {
            exclusions = new[] { new RecordsTools.RecordsWalkExclusion { match = "Spell", severity = "stop" } },
        });
        Served(r, "HcRecSpellHop");
        Assert.DoesNotContain("HcRecMgefHop", r);
    }

    /// <summary>The reached set written with to_file= and re-entered as formids= is narrowed by where=.</summary>
    [Fact]
    public void WalkRoute_ToFileThenReEnterWithWhere()
    {
        var path = W.Scratch("results", "walk-reached.jsonl");
        Served(RecordsTools.Records(Svc, types: Spel, walk: new RecordsTools.RecordsWalk(), to_file: path));
        var r = RecordsTools.Records(Svc, formids: new[] { "@" + path }, where: new[] { "editorid = HcRecMgefFire" });
        Served(r, "HcRecMgefFire");
        Assert.DoesNotContain("HcRecSpellA", r);
    }

    // ---- project.fields with form=chain ------------------------------------------------------------

    static RecordsTools.RecordsWalk Template => new() { follow = "Template" };

    [Fact]
    public void ChainPlusFields_SaysChainReadsNoFieldsAndNamesTheFieldsForm() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.NpcChild) }, walk: Template,
                                     project: new RecordsTools.RecordsProject { form = "chain", fields = new[] { "Configuration.Level" } }),
                "reads no fields", "form='fields' with the same walk=");

    /// <summary>With no walk= the missing walk answers first, so the sentence never names a walk the call lacks.</summary>
    [Fact]
    public void ChainPlusFieldsWithoutWalk_AsksForTheWalk()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.NpcChild) },
                                     project: new RecordsTools.RecordsProject { form = "chain", fields = new[] { "Configuration.Level" } });
        Refused(r, "pass walk=");
        Assert.DoesNotContain("the same walk=", r);
    }

    /// <summary>A walk that is itself refused answers first, so the chain sentence never names it.</summary>
    [Fact]
    public void ChainPlusFieldsWithABadWalk_TheWalkRefusalAnswers()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.NpcChild) }, walk: new RecordsTools.RecordsWalk { direction = "sideways" },
                                     project: new RecordsTools.RecordsProject { form = "chain", fields = new[] { "Configuration.Level" } });
        Refused(r, "walk.direction='sideways'");
        Assert.DoesNotContain("the same walk=", r);
    }

    [Fact]
    public void ChainRoute_TheFieldsFormWithTheSameWalkReadsTheReachedSet() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.NpcChild) }, walk: Template, project: Fields("Configuration.Level")),
               "HcRecNpcParent", "Configuration.Level");

    // ---- the source= description -------------------------------------------------------------------

    [Fact]
    public void SourceDescription_SaysSkyPatcherChangesRecordsAndNamesTheOverlayPole()
    {
        var p = typeof(RecordsTools).GetMethod(nameof(RecordsTools.Records))!.GetParameters().Single(x => x.Name == "source");
        var d = ((System.ComponentModel.DescriptionAttribute)p.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single()).Description;
        Assert.Contains("SkyPatcher INIs can change records at runtime, which no other pole shows: {\"overlay\": \"skypatcher\"", d);
    }
}
