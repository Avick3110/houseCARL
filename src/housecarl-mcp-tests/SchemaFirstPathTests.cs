using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#1160: a where= path that names no field is refused from the schema before the scan, on every form; a real path is left alone.</summary>
[Trait("tier", "unit")]
public sealed class SchemaFirstPathTests
{
    // The wrong paths agents wrote in real use (scoping §2), each refused at plan time; the link and union cases hold the walker to its target and arm sets.
    [Theory]
    [InlineData("plugin = Skyrim.esm", "ARMO")]
    [InlineData("Items[*any]->type = Armor", "FLST")]
    [InlineData("WorldModel[*any].File contains x", "ARMA")]
    [InlineData("override_depth > 1", "ARMA")]
    [InlineData("FULL contains Iron", "ARMO")]
    [InlineData("name contains Iron", "ARMO")]
    [InlineData("Race->ArmorRating > 0", "NPC_")]
    [InlineData("Conditions[*any].Data.NoSuchParameter exists", "PERK")]
    [InlineData("Items[*any]->Type = Armor", "FLST")]
    public void ARealUseWrongPathIsRefusedBeforeTheScan(string clause, string types) =>
        Assert.NotNull(SchemaPlan.Of(clause, types.Split(',')));

    // The one with two arrows is refused by the grammar, before the schema.
    [Fact]
    public void ATwoArrowPathIsRefusedByTheGrammar() =>
        Assert.NotNull(FieldPredicateSet.Parse(new[] { "TemplateArmor->Keywords[*any]->editorid = X" }).Error);

    // The real paths from the same transcripts plan clean, with types= and without.
    [Theory]
    [InlineData("Items[*any]->editorid startswith Armor", "FLST")]
    [InlineData("Items[*any]->Type = Arrow", "FLST")]
    [InlineData("Conditions[*any].Data.Parameter1 exists", "PERK")]
    [InlineData("Conditions[*any].Data.RunOnType = Subject", "MGEF")]
    [InlineData("WorldModel[1].File contains female", "ARMA")]
    [InlineData("WorldModel.Female.File exists", "ARMA")]
    [InlineData("ObjectBounds.First.X > 0", "WEAP")]
    [InlineData("VirtualMachineAdapter.Scripts[*any].Name = x", "")]
    [InlineData("ActorEffect[*any] = 000001:Skyrim.esm", "")]
    [InlineData("HeadParts[*count] > 0", "")]
    [InlineData("WorldModel.Female.AlternateTextures[*count] > 0", "")]
    [InlineData("Configuration.Flags has IsCharGenFacePreset", "")]
    [InlineData("*parent.EditorID exists", "REFR")]
    [InlineData("Race.FormKey = 013746:Skyrim.esm", "NPC_")]
    [InlineData("Race.IsNull = false", "NPC_")]
    [InlineData("Keywords[*any].FormKey exists", "ARMO")]
    public void ARealUsePathPlansClean(string clause, string types) =>
        Assert.Null(SchemaPlan.Of(clause, types.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    // The issue's sentence names the case slip on the link's targets.
    [Fact]
    public void TheIssuePathNamesTheCaseSlipOnTheLinkTargets()
    {
        var refusal = SchemaPlan.Of("Items[*any]->type = Armor", "FLST");
        Assert.Contains("any record type 'Items[*any]' can link to", refusal);
        Assert.Contains("'Type'", refusal);
    }

    // A step below a link reads the link's own member (FormKey, IsNull), as main did: one record of each pair matches.
    [Theory]
    [InlineData("Race.FormKey = {0}")]
    [InlineData("Race.IsNull = false")]
    [InlineData("Template.FormKey = {0}")]
    [InlineData("Keywords[*any].FormKey = {0}")]
    [InlineData("Items[*any].FormKey = {0}")]
    [InlineData("Effects[*any].BaseEffect.FormKey = {0}")]
    [InlineData("VirtualMachineAdapter.Scripts[*any].Properties[*any].Object.FormKey = {0}")]
    public void AStepBelowALinkReadsTheLinksOwnMember(string clause)
    {
        var mod = new SkyrimMod(new ModKey("HcLinkMember", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var hit = mod.Keywords.AddNew().FormKey;
        var miss = mod.Keywords.AddNew().FormKey;
        var want = new List<FormKey>();
        foreach (var fk in new[] { hit, miss })
        {
            bool h = fk == hit;
            var n = mod.Npcs.AddNew(); if (h) n.Race.SetTo(fk); n.Template.SetTo(fk);
            var a = mod.Armors.AddNew(); a.Keywords = new() { fk };
            var l = mod.FormLists.AddNew(); l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(fk));
            var s = mod.Spells.AddNew(); var e = new Effect(); e.BaseEffect.SetTo(fk); s.Effects.Add(e);
            var q = mod.Quests.AddNew();
            q.VirtualMachineAdapter = new QuestAdapter { Scripts = { new ScriptEntry { Name = "S", Properties = { new ScriptObjectProperty { Name = "P", Object = new FormLink<ISkyrimMajorRecordGetter>(fk) } } } } };
            if (h) want.AddRange(new[] { n.FormKey, a.FormKey, l.FormKey, s.FormKey, q.FormKey });
        }
        Assert.Null(SchemaPlan.Set(string.Format(clause, $"{hit.ID:X6}:{hit.ModKey.FileName}"), out var set));
        var got = mod.EnumerateMajorRecords().Where(r => set.Matches(r)).Select(r => r.FormKey).ToList();
        Assert.Single(got);
        Assert.Contains(got[0], want);
    }

    // A name the link does not carry is refused, and the hint keeps the quantifier.
    [Fact]
    public void ANameBelowALinkThatItLacksIsToldToStepThroughTheLinkWithItsQuantifier() =>
        Assert.Contains("'Items[*any]->EditorId'", SchemaPlan.Of("Items[*any].EditorId exists", "FLST"));

    // With no types=, a path that is a field on no record type at all is refused.
    [Fact]
    public void WithNoTypesAPathOnNoRecordTypeIsRefused() =>
        Assert.Contains("'Nope' is not a field on any record type", SchemaPlan.Of("Nope.X = 1"));

    // With no types=, a type that lacks the path is not scanned and gets no note of its own while another type carries the path.
    [Fact]
    public void WithNoTypesATypeLackingThePathIsSkippedWithoutANote()
    {
        var mod = new SkyrimMod(new ModKey("HcFirstSight", ModType.Plugin), SkyrimRelease.SkyrimSE);
        mod.Spells.AddNew(); mod.Spells.AddNew();
        var w = mod.Weapons.AddNew();
        w.BasicStats = new WeaponBasicStats { Damage = 10 };
        Assert.Null(SchemaPlan.Set("BasicStats.Damage >= 0", out var set));
        Assert.Equal(new[] { w.FormKey }, mod.EnumerateMajorRecords().Where(r => set.Matches(r)).Select(r => r.FormKey).ToArray());
        Assert.Null(set.AccountingNote());
        Assert.Equal(1, set.Scanned);
    }

    // Several types=: the type lacking the path gets one fixed note; refusal only when none has it.
    [Fact]
    public void SeveralTypesNoteTheLackingOneAndRefuseOnlyWhenNoneHasIt()
    {
        Assert.Null(SchemaPlan.Set("ArmorRating > 1", out var set, "ARMO", "WEAP"));
        Assert.Contains("'ArmorRating' is not a field of Weapon", set.AccountingNote());
        Assert.Contains("no Weapon can match", set.AccountingNote());
        Assert.NotNull(SchemaPlan.Of("ArmorRating > 1", "WEAP", "SPEL"));
    }
}

/// <summary>#1160 end to end: the issue's call refuses on form=fields instead of answering a bare 0.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class SchemaFirstPathRecordsTests : RecordsTestBase
{
    public SchemaFirstPathRecordsTests(RecordsFixture f) : base(f) { }

    [Theory]
    [InlineData("summary")]
    [InlineData("fields")]
    public void TheIssueCallIsRefusedOnEveryForm(string form)
    {
        var r = RecordsTools.Records(Svc, types: new[] { "FLST" }, where: new[] { "Items[*any]->type = Armor" },
            project: new RecordsTools.RecordsProject { form = form, fields = form == "fields" ? new[] { "EditorID" } : null });
        Assert.StartsWith("records", r);
        Assert.Contains("'type' is not a field", r);
        Assert.DoesNotContain("0 matches", r);
    }

    // Main's counts on this world: a step below a link reads the link's member.
    [Theory]
    [InlineData("SPEL", "Effects[*any].BaseEffect.FormKey exists", 4)]
    [InlineData("NPC_", "Template.FormKey exists", 2)]
    public void AStepBelowALinkMatchesWhatMainMatched(string type, string clause, int main) =>
        Assert.Contains($"\"total\":{main},", RecordsTools.Records(Svc, types: new[] { type }, where: new[] { clause }, counts_only: true, format: "json").Replace(" ", ""));

    // With no types=, a whole-plugin scope says nothing per type while one type carries the path...
    [Fact]
    public void APluginScopeWritesNoNotePerTypeLackingThePath()
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.MasterName), where: new[] { "BasicStats.Damage >= 1" }, counts_only: true);
        Served(r);
        Assert.DoesNotContain("note: predicate", r);
    }

    // ...and one loud sentence on every form when no type in scope carries it.
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "json")]
    [InlineData("fields", null)]
    [InlineData("aggregate", null)]
    public void APathNoTypeInScopeCarriesGetsOneSentence(string? form, string? format)
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.MasterName), where: new[] { "Stages[*count] >= 0" }, format: format,
            project: form is null ? null : new RecordsTools.RecordsProject { form = form, fields = form == "fields" ? new[] { "EditorID" } : null, group_by = form == "aggregate" ? "type" : null });
        Assert.Equal(1, CountOf(r, "names no field on any record type in scope"));
        Assert.DoesNotContain("note: predicate", r);
    }
}
