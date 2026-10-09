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

    /// <summary>The route the sentence names: the same scope with source= reads the master's copy.</summary>
    [Fact]
    public void FieldsSourceRoute_SourceUnderTheSameScopeReadsThatPluginsVersion() =>
        Served(RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName), source: Plugin(W.MasterName)),
               RecordsWorld.RenamedArmorOldEid);

    // ---- walk + where -----------------------------------------------------------------------------

    [Fact]
    public void WalkPlusWhere_SaysTypesSelectsTheSeedsAndNamesBothRoutes() =>
        Refused(RecordsTools.Records(Svc, types: Spel, where: new[] { "editorid = HcRecSpellA" }, walk: new RecordsTools.RecordsWalk()),
                "types= selects the seeds", "walk.through", "walk.exclusions", "to_file=", "formids=[\"@<file>\"]");

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
