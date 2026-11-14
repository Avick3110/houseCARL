using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>SPID's lines read as SPID 7.3 reads them (research.md answers 2, 3 and 5): the split, the key kinds and
/// sanitize, then what each form position names and what SPID does where a name finds nothing, and each first-wins
/// line's place. Each test names the dev line (D:/Modlists/ReSkyrimed) that reaches its rule, where one does.</summary>
[Trait("tier", "unit")]
public sealed class SpidLinesTests
{
    static SpidGrammar G => RuntimeFormats.Spid.Lines!;

    static readonly ModKey Mod = ModKey.FromFileName("HcSpid.esp");
    static FormKey K(uint id) => new(Mod, id);
    static readonly FormKey Aranea = K(0x810), DeerRace = K(0x811), Perk1 = K(0x812), BanditFaction = K(0x813), Kw = K(0x814),
        OutfitA = K(0x815), Skald = K(0x816), Cobj = K(0x817), Spell1 = K(0x818), Elsewhere = FormKey.Factory("000800:HcOther.esp");

    sealed class World
    {
        public readonly SkyPatcherOverlayHarness.StubResolver R = new();
        public readonly Dictionary<FormKey, Type> Classes = new();

        public World()
        {
            R.Plugins.Add(Mod.FileName);
            R.Plugins.Add("HcOther.esp");
            R.RuntimeBases[Mod.FileName] = 0x01000000;
            R.RuntimeBases["HcOther.esp"] = 0x02000000;
            Add(Aranea, "HcAranea", typeof(Npc));
            Add(DeerRace, "HcDeerRace", typeof(Race));
            Add(Perk1, "HcPerk", typeof(Perk));
            Add(BanditFaction, "HcBanditFaction", typeof(Faction));
            Add(Kw, "HcKeyword", typeof(Keyword));
            Add(OutfitA, "HcOutfit", typeof(Outfit));
            Add(Skald, "HcSkaldNpc", typeof(Npc));
            Add(Cobj, "HcRecipe", typeof(ConstructibleObject));
            Add(Spell1, "HcSpell", typeof(Spell));
            Add(Elsewhere, "HcElsewhere", typeof(Npc), 0x02000800);
        }

        void Add(FormKey fk, string eid, Type cls, uint? runtime = null)
        {
            R.EditorIds[eid] = fk;
            R.EditorIdTypes[eid] = cls.Name;
            R.Winners[fk] = fk.ModKey.FileName;
            R.RuntimeForms[runtime ?? 0x01000000 + fk.ID] = fk;
            Classes[fk] = cls;
        }

        public SpidLines.Context Ctx => new(R, fk => Classes.GetValueOrDefault(fk), RuntimeFormats.Spid);

        public SpidLines.LineRead Read(string line) => SpidLines.Read(One(line), Ctx)!;
    }

    static SpidLines.Line One(string text) => Assert.Single(SpidLines.Parse(text, G));

    // ---- the split ---------------------------------------------------------------------------

    // Answer 3: SimpleIni's comment leads are ';' and '#' at a line's start.
    [Fact]
    public void ALineStartingWithASemicolonOrAHashIsAComment()
        => Assert.Empty(SpidLines.Parse("; a\n  # b\nSpell=X", G).Where(l => l.Key != "Spell"));

    // Answer 3 (SimpleIni 4.25 :1734-1746): no comment after a value, so the text after ';' is part of it.
    [Fact]
    public void ATrailingCommentIsPartOfTheValue()
        => Assert.Equal("HcPerk ; note", One("Perk = HcPerk ; note").Tokens.Single().Raw);

    // Answer 3 (LookupConfigs.cpp:117): only the unnamed section is read.
    [Fact]
    public void ALineUnderASectionHeaderIsNotRead()
    {
        var lines = SpidLines.Parse("Perk = HcPerk\n[Extra]\nPerk = HcPerk\n", G);
        Assert.Null(lines[0].Skipped);
        Assert.Contains("sits under [Extra]", lines[1].Skipped);
    }

    // Answer 3 (LookupConfigs.h:61-74): the type names are case-sensitive.
    [Fact]
    public void AKeyInAnotherCaseIsSkipped()
        => Assert.Contains("is not one SPID takes", One("spell = HcSpell").Skipped);

    // Answer 3 (Parser.h:67-72): more sections than the kind takes skips the line.
    [Fact]
    public void TooManySectionsSkipTheLine()
    {
        Assert.Contains("more than the 7 a regular line takes", One("Spell = A|b|c|d|e|f|g|h").Skipped);
        Assert.Contains("more than the 4 a Linked line takes", One("LinkedSpell = A|B|1|2|3").Skipped);
        Assert.Null(One("Spell = A|b|c|d|e|f|g").Skipped);
    }

    // Answer 2, new for the build (dev: Mysticism Restoration_DISTR.ini:77): NONE unsets only a whole section; inside a list it is a name.
    [Fact]
    public void NoneUnsetsOnlyAWholeSection()
    {
        var l = One("Spell = HcSpell|NONE|NONE,HcDeerRace");
        Assert.DoesNotContain(l.Tokens, t => t.Field == SpidField.Strings);
        Assert.Contains(l.Tokens, t => t.Field == SpidField.Forms && t.Raw == "NONE");
    }

    // Answer 2, new (dev: Mysticism Restoration_DISTR.ini:77): the NONE token is a name that finds nothing and is skipped; the line stays.
    [Fact]
    public void ANoneListTokenIsSkippedAndTheLineStays()
    {
        var r = new World().Read("Spell = HcSpell|NONE|NONE,HcDeerRace");
        Assert.Null(r.Dropped);
        Assert.Contains(r.Notes, n => n.StartsWith("form filter 'NONE' finds no record") && n.EndsWith("SPID skips it, so the line reaches more NPCs"));
    }

    // Census rule: an empty filter term throws in SPID, so the line is skipped.
    [Fact]
    public void AnEmptyFilterTermSkipsTheLine()
        => Assert.Contains("has an empty form filter term", One("Spell = HcSpell||HcDeerRace,,HcBanditFaction").Skipped);

    // Answer 3 (LookupConfigs.cpp:13-53): sanitize, and the line says SPID writes it back.
    [Fact]
    public void SanitizeRewritesTheValueAndTheLineSaysSo()
    {
        var l = One("Spell = 0000812 - HcSpid.esp | NONE | 00811 , HcBanditFaction");
        Assert.Equal("0x812~HcSpid.esp", l.Tokens[0].Raw);
        Assert.Contains(l.Tokens, t => t.Raw == "0x811");
        Assert.Contains(l.Notes, n => n.Contains("writes that back into this file"));
        Assert.Empty(One("Spell = 0x812~HcSpid.esp").Notes);
    }

    // Answer 3 (LookupConfigs.h:343-346): Final is an Outfit prefix; on another type SPID drops it.
    [Fact]
    public void FinalOnAnotherTypeIsDropped()
    {
        var spell = One("FinalSpell = HcSpell");
        Assert.False(spell.Final);
        Assert.Equal("Spell", spell.Type);
        Assert.Contains(spell.Notes, n => n.Contains("so SPID drops it"));
        Assert.True(One("FinalOutfit = HcOutfit").Final);
    }

    // Pick SPID Q3 (a): every line kind is read.
    [Fact]
    public void EveryLineKindIsRead()
    {
        Assert.Equal(SpidLineKind.Death, One("DeathSpell = HcSpell").Kind);
        var linked = One("GlobalLinkedFinalDeathOutfit = HcOutfit|HcSpell");
        Assert.Equal((SpidLineKind.Linked, "Outfit", true, true), (linked.Kind, linked.Type, linked.Final, linked.Global));
        Assert.Contains(linked.Tokens, t => t.Field == SpidField.Parents && t.Raw == "HcSpell");
        var group = One("ExclusiveGroup = Mine|HcKeyword,-HcPerk");
        Assert.Equal(SpidLineKind.ExclusiveGroup, group.Kind);
        Assert.Contains(group.Tokens, t => t.Field == SpidField.Members && t.List == SpidLines.ListKind.Not && t.Raw == "HcPerk");
        Assert.Contains("is not one SPID takes", One("GlobalSpell = HcSpell").Skipped);
    }

    // ---- what a line names -------------------------------------------------------------------

    // Pick SPID Q1 (a) (dev: Adamant_DISTR.ini:3 value; Animals Swim_DISTR.ini:1 filter; Mysticism Destruction_DISTR.ini:2 target):
    // a directly named NPC is target, an excluded one and every other filter form filter, the handed-out form value.
    [Fact]
    public void ANamedNpcIsTargetOtherFilterFormsFilterAndTheHandedOutFormValue()
    {
        var r = new World().Read("Perk = HcPerk||0x1000810,HcDeerRace,-HcSkaldNpc");
        Assert.Contains(r.Forms, f => f.Form == Perk1 && f.Place == SkyPatcherOverlay.FormPlace.Value && f.ByEditorId);
        Assert.Contains(r.Forms, f => f.Form == Aranea && f.Place == SkyPatcherOverlay.FormPlace.Target && !f.ByEditorId);
        Assert.Contains(r.Forms, f => f.Form == DeerRace && f.Place == SkyPatcherOverlay.FormPlace.Filter);
        Assert.Contains(r.Forms, f => f.Form == Skald && f.Place == SkyPatcherOverlay.FormPlace.Filter && f.Where == "form filter -");
    }

    // Pick SPID Q2 (a) (dev: MCE_DISTR.ini:2 Onmund; Divine Wardrobe - Integration_DISTR.ini:7 Skald): a string token
    // equal to an NPC's or a keyword's EditorID names it; any other is marked as matched by name in the running game.
    [Fact]
    public void AStringTokenNamesAFormOnlyAsAnNpcOrKeywordEditorId()
    {
        var r = new World().Read("Outfit = HcOutfit|HcSkaldNpc,HcKeyword,Skald,*ald,-HcAranea");
        Assert.Contains(r.Forms, f => f.Form == Skald && f.Place == SkyPatcherOverlay.FormPlace.Target && f.Where == "string filter");
        Assert.Contains(r.Forms, f => f.Form == Kw && f.Place == SkyPatcherOverlay.FormPlace.Filter);
        Assert.Contains(r.Forms, f => f.Form == Aranea && f.Place == SkyPatcherOverlay.FormPlace.Filter);
        Assert.Contains(r.Notes, n => n.StartsWith("string filter 'Skald' is no NPC or keyword EditorID") && n.Contains("display name"));
        Assert.Contains(r.Notes, n => n.StartsWith("string filter '*ald' matches part of a name"));
        // A string token equal to another type's EditorID is no form: SPID matches it as a name.
        Assert.DoesNotContain(new World().Read("Outfit = HcOutfit|HcDeerRace").Forms, f => f.Form == DeerRace);
    }

    // Answer 3 correction: a name no record holds creates a keyword only on a Keyword or Form value.
    [Theory]
    [InlineData("Keyword", null, "SPID creates a keyword of that name and hands it out")]
    [InlineData("Form", null, "SPID creates a keyword of that name and hands it out")]
    [InlineData("Spell", "which a Spell line does not hand out: the line hands nothing out", null)]
    [InlineData("Package", "which a Package line does not hand out", null)]
    [InlineData("Perk", "so SPID drops the line", null)]       // dev: Adamant_DISTR.ini:13
    [InlineData("Faction", "so SPID drops the line", null)]    // dev: Skyrim Extended Cut - Saints and Seducers_DISTR.ini:20
    [InlineData("Outfit", "so SPID drops the line", null)]
    public void AValueNameNoRecordHoldsIsReadByTheLinesType(string type, string? dropped, string? note)
    {
        var r = new World().Read($"{type} = HcNoSuchName");
        if (dropped is null) Assert.Null(r.Dropped); else Assert.Contains(dropped, r.Dropped);
        if (note is not null) Assert.Contains(r.Notes, n => n.EndsWith(note));
    }

    // Answer 3 correction: a filter never creates a keyword, so a keyword line whose only filter misses is dropped.
    [Fact]
    public void AFilterNameNoRecordHoldsCreatesNothing()
    {
        var r = new World().Read("Keyword = HcKeyword||HcNoSuchRace");
        Assert.Contains("every form filter of the line names nothing SPID can use", r.Dropped);
        Assert.DoesNotContain(r.Notes, n => n.Contains("creates"));
    }

    // A keyword the value creates on a line its filters drop is handed to no one.
    [Fact]
    public void AKeywordCreatedOnADroppedLineIsHandedToNoOne()
        => Assert.Contains(new World().Read("Keyword = HcNoSuchName||HcNoSuchRace").Notes, n => n.EndsWith("which the dropped line hands to no one"));

    // Answer 2, new (dev: MCE_DISTR.ini:1-13): a FormID naming a plugin not in the order drops the line.
    [Fact]
    public void AValueOnAPluginNotInTheOrderDropsTheLine()
        => Assert.Contains("names plugin 'Mage Clothing Expansion.esp', which is not in the active order",
            new World().Read("Outfit = 0x013~Mage Clothing Expansion.esp|BrelynaMaryon").Dropped);

    // Answer 3 (FormData.h:357, :613) (dev: Unaggressive Dragon Priests Fix_DISTR.ini:7): one MATCH token failing is
    // skipped and widens the line; every one failing drops it.
    [Fact]
    public void AFailingMatchTokenWidensAndAllFailingDrops()
    {
        var one = new World().Read("Spell = HcSpell||HcDeerRace,HcNoSuchRace");
        Assert.Null(one.Dropped);
        Assert.Contains(one.Notes, n => n.StartsWith("form filter 'HcNoSuchRace'") && n.EndsWith("reaches more NPCs"));
        Assert.Contains("every form filter of the line names nothing", new World().Read("Spell = HcSpell||HcNoSuchRace").Dropped);
    }

    // Answer 3 correction: every NOT token failing drops the line; it is not widened.
    [Fact]
    public void EveryNotTokenFailingDropsTheLine()
    {
        var one = new World().Read("Spell = HcSpell||-HcDeerRace,-HcNoSuchRace");
        Assert.Null(one.Dropped);
        Assert.Contains(one.Notes, n => n.StartsWith("form filter - 'HcNoSuchRace'") && n.EndsWith("excludes fewer NPCs"));
        Assert.Contains("every '-' form filter of the line names nothing", new World().Read("Spell = HcSpell||-HcNoSuchRace").Dropped);
    }

    // Answer 3: an unresolved '+' part drops the line; '+' terms skip the filter-type check, MATCH and NOT do not
    // (dev: Skyrim Extended Cut - Saints and Seducers_DISTR.ini:5 names a COBJ as its only filter).
    [Fact]
    public void APlusGroupNeedsEveryPartAndSkipsTheTypeCheck()
    {
        Assert.Contains("a '+' group needs every part", new World().Read("Spell = HcSpell||HcDeerRace+HcNoSuchRace").Dropped);
        Assert.Null(new World().Read("Spell = HcSpell||HcDeerRace+HcKeyword").Dropped);
        var kw = new World().Read("Spell = HcSpell||HcKeyword");
        Assert.Contains("a Keyword record, which SPID does not take as a form filter", kw.Dropped);
        Assert.Contains("a ConstructibleObject record", new World().Read("Keyword = HcKeyword||0x1000817").Dropped);
    }

    // Pick (12)(ii) (X1): a FormID written with a load index is added as SPID adds it, and the line notes the index.
    [Fact]
    public void ALoadIndexWrittenIntoAFormIdIsAddedAndNoted()
    {
        var r = new World().Read("Spell = HcSpell||0x1000800~HcSpid.esp");
        Assert.Contains(r.Forms, f => f.Form == Elsewhere);
        Assert.Contains(r.Notes, n => n.StartsWith("form filter '0x1000800~HcSpid.esp' is written with a load index"));
    }

    // Pick (12)(iii): a plugin term is a term of its own, which touches its plugin; one the order lacks names nothing.
    [Fact]
    public void APluginTermIsKeptAsThePluginItNames()
    {
        var r = new World().Read("Spell = HcSpell||HcOther.esp");
        Assert.Equal(new[] { "HcOther.esp" }, r.PluginTerms);
        Assert.Null(r.Dropped);
        Assert.Contains("which is not in the active order", new World().Read("Spell = HcSpell||HcAbsent.esp").Dropped);
    }

    // Linked parents and group members are forms the line names, labelled filter; a miss is said, its outcome not claimed.
    [Fact]
    public void LinkedParentsAndGroupMembersAreFilterForms()
    {
        var linked = new World().Read("LinkedSpell = HcSpell|HcPerk,HcNoSuchPerk");
        Assert.Contains(linked.Forms, f => f.Form == Perk1 && f.Place == SkyPatcherOverlay.FormPlace.Filter && f.Where == "linked parents");
        Assert.Contains(linked.Notes, n => n.Contains("'HcNoSuchPerk'") && n.EndsWith("is not read here"));
        Assert.Contains(new World().Read("ExclusiveGroup = G|HcKeyword,-HcPerk").Forms, f => f.Form == Perk1 && f.Where == "group members -");
    }

    // ---- first wins ---------------------------------------------------------------------------

    // Pick SPID Q4 (a) (dev: the 171 Outfit lines): each first-wins line's place, keys before lines within a file, Form
    // lines after every typed line of every file, dropped lines taking none.
    [Fact]
    public void FirstWinsLinesArePlacedInSpidsOrder()
    {
        var w = new World();
        var a = SpidLines.Parse("Outfit = HcOutfit\nForm = HcOutfit\nFinalOutfit = HcOutfit\nOutfit = 0x013~Absent.esp\nPerk = HcPerk", G);
        var b = SpidLines.Parse("Outfit = HcOutfit", G);
        var files = new[] { a, b }.Select(l => ((IReadOnlyList<SpidLines.Line>)l, SpidLines.ReadAll(l, w.Ctx))).ToList();
        var places = SpidLines.Places(files, G);
        Assert.Equal(1, places[(0, 2)].Index);     // FinalOutfit sorts before Outfit in its file
        Assert.Equal(2, places[(0, 0)].Index);
        Assert.Equal(3, places[(1, 0)].Index);     // the next file's typed line
        Assert.Equal(4, places[(0, 1)].Index);     // the Form line, after every typed line
        Assert.Equal(4, places[(0, 1)].Total);
        Assert.False(places.ContainsKey((0, 3)));  // dropped
        Assert.False(places.ContainsKey((0, 4)));  // a Perk line has no first-wins place
    }
}
