using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>KID's lines read as KID 4.0.6 and 4.1.0 read them (kid-lines research.md answers 3, 4, 5 and 9, the top-up
/// T3 and T6): the split, the sections, the filter entries and groups, then what each position names and what KID does
/// where a name finds nothing, by section. Each test names the dev line (D:/Modlists/ReSkyrimed) that reaches its rule,
/// where one does.</summary>
[Trait("tier", "unit")]
public sealed class KidLinesTests
{
    static DistributorFormat Kid => RuntimeFormats.Kid;

    static readonly ModKey Mod = ModKey.FromFileName("HcKid.esp");
    static FormKey K(uint id) => new(Mod, id);
    static readonly FormKey Helmet = K(0x800), Kw = K(0x801), Sword = K(0x802), Recipe = K(0x803), List = K(0x804),
        Elsewhere = FormKey.Factory("000800:HcOther.esp");

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
            Add(Helmet, "HcHelmet", typeof(Armor));
            Add(Kw, "HcKeyword", typeof(Keyword));
            Add(Sword, "HcSword", typeof(Weapon));
            Add(Recipe, "HcRecipe", typeof(ConstructibleObject));
            Add(List, "HcList", typeof(FormList));
            Add(Elsewhere, "HcElsewhere", typeof(Armor), 0x02000800);
        }

        void Add(FormKey fk, string eid, Type cls, uint? runtime = null)
        {
            R.EditorIds[eid] = fk;
            R.EditorIdTypes[eid] = cls.Name;
            R.Winners[fk] = fk.ModKey.FileName;
            R.RuntimeForms[runtime ?? 0x01000000 + fk.ID] = fk;
            Classes[fk] = cls;
        }

        public KidLines.Context Ctx => new(R, fk => Classes.GetValueOrDefault(fk), RuntimeFormats.Kid);

        public KidLines.LineRead Read(string line) => ReadAll(line).Single()!;

        public IReadOnlyList<KidLines.LineRead?> ReadAll(string text) => KidLines.ReadAll(KidLines.Parse(text, Kid), Ctx);
    }

    static KidLines.Line One(string text) => Assert.Single(KidLines.Parse(text, Kid));

    // ---- the split ---------------------------------------------------------------------------

    // Answer 4 (SimpleIni 4.25 :1259-1260): ';' and '#' lead a comment at a line's start.
    [Fact]
    public void ALineStartingWithASemicolonOrAHashIsAComment()
        => Assert.Equal("Keyword", One("; a\n  # b\nKeyword = HcTag|Armor").Key);

    // Answer 4 (GetSection("")): only the unnamed section is read; an empty header returns to it.
    [Fact]
    public void ALineUnderASectionHeaderIsNotRead()
    {
        var lines = KidLines.Parse("[Mine]\nKeyword = A|Armor\n[]\nKeyword = B|Armor", Kid);
        Assert.StartsWith("sits under [Mine]", lines[0].Skipped);
        Assert.Null(lines[1].Skipped);
    }

    // Answer 4 (LookupConfigs.cpp:24-32, :102-104): a type not one of the 19, spelt exactly, drops the line with no log;
    // ARR's ABA_KW-OCF_LCTN_KID.ini 'FormList' line is one, and a lower-case type is another.
    [Theory]
    [InlineData("Keyword = HcTag|FormList|HcList", "has type 'FormList', which is not one of KID's types")]
    [InlineData("Keyword = HcTag|armor", "has type 'armor', which is not one of KID's types")]
    [InlineData("Keyword = HcTag", "has no type section")]
    public void ATypeNotOneOfKidsDropsTheLineWithoutALog(string line, string why)
    {
        var skipped = One(line).Skipped!;
        Assert.StartsWith(why, skipped);
        Assert.EndsWith("so KID drops the line without a log", skipped);
    }

    // Answer 4 (LookupConfigs.cpp:20, string::split untrimmed): the sections are not trimmed, so a space beside '|'
    // makes the type no type, and the keyword keeps its space.
    [Fact]
    public void TheSectionsAreNotTrimmed()
    {
        Assert.StartsWith("has type ' Armor', and KID does not trim around '|'", One("Keyword = HcTag | Armor").Skipped);
        var r = new World().Read("Keyword = HcTag |Armor");
        Assert.Contains(r.Notes, n => n.StartsWith("keyword 'HcTag ' is no keyword's EditorID (KID keeps the space"));
    }

    // Answer 9: a trailing ';' comment is part of the value, so in the type it drops the line, in the filters it is a
    // string no item has, and in the chance it is harmless (stof stops at the space).
    [Fact]
    public void ATrailingCommentIsPartOfTheValue()
    {
        Assert.StartsWith("has type 'Armor ; note'", One("Keyword = HcTag|Armor ; note").Skipped);
        var r = new World().Read("Keyword = HcTag|Misc Item|Gold001   ; misc items take no traits");
        Assert.Contains(r.Notes, n => n.StartsWith("filters 'Gold001   ; misc items take no traits' is no record's EditorID"));
        Assert.Null(One("Keyword = HcTag|Weapon|*Iron|E|50     ; 50%").Skipped);
    }

    // Answer 4 (ExclusiveGroups.cpp:7): only the key 'ExclusiveGroup' exactly is a group; any other key is a keyword line,
    // dev's four 'keyword' lines included (Footprints_KID.ini:2).
    [Fact]
    public void OnlyTheExactGroupKeyMakesAGroupLine()
    {
        Assert.True(One("ExclusiveGroup = Mine|HcKeyword").IsGroup);
        Assert.False(One("exclusivegroup = HcTag|Armor").IsGroup);
        Assert.Equal("Armor", One("keyword = 0xE550B~Footprints.esp|Armor|ClothingFeet").Type);
    }

    // Answer 4 (FormData.cpp:8-9, stoul): a term with one '~' whose FormID part is no hex number throws, and KID logs
    // the entry as failed; a term with two '~' is a name; a partial term is never read as a FormID.
    [Fact]
    public void AnUnreadableFormIdFailsTheLineAndTwoTildesAreAName()
    {
        Assert.Equal("names keyword 'zz~HcKid.esp', whose part before '~' is no hex FormID KID can read, so KID fails to parse the entry and logs 'Failed to parse entry'",
            One("Keyword = zz~HcKid.esp|Armor").Skipped);
        Assert.StartsWith("has filter 'zz~HcKid.esp'", One("Keyword = HcTag|Armor|HcHelmet,zz~HcKid.esp").Skipped);
        Assert.Null(One("Keyword = HcTag|Armor|*zz~HcKid.esp").Skipped);
        var r = new World().Read("Keyword = HcTag|Armor|a~b~c");
        Assert.Contains(r.Notes, n => n.StartsWith("filters 'a~b~c' is no record's EditorID"));
    }

    // r1 #13: the parse-time skip reads a term by the row's many-separators rule, as the resolver does: with KID's row
    // flipped to an address at the first '~', a two-'~' term with no hex before it fails the line in each section.
    [Theory]
    [InlineData("Keyword = zz~HcKid.esp~more|Armor", "names keyword 'zz~HcKid.esp~more', whose part before '~' is no hex FormID")]
    [InlineData("Keyword = HcTag|Armor|zz~HcKid.esp~more", "has filter 'zz~HcKid.esp~more', whose part before '~' is no hex FormID")]
    [InlineData("ExclusiveGroup = Mine|HcKeyword,zz~HcKid.esp~more", "has member 'zz~HcKid.esp~more', whose part before '~' is no hex FormID")]
    public void TheParseTimeSkipReadsTwoTildesByTheRowsRule(string text, string skipped)
    {
        var flipped = ShippedRows.Distributor("kid", ("\"manySeparators\": \"name\"", "\"manySeparators\": \"addressAtFirst\""));
        Assert.StartsWith(skipped, Assert.Single(KidLines.Parse(text, flipped)).Skipped);
        Assert.Null(One(text).Skipped);
    }

    // Answer 4 (string.hpp:129, stof): a chance that does not start with a number throws.
    [Fact]
    public void AChanceThatIsNoNumberFailsTheLine()
    {
        Assert.StartsWith("has chance 'half', which is not a number", One("Keyword = HcTag|Armor|||half").Skipped);
        Assert.Null(One("Keyword = HcTag|Armor|||NONE").Skipped);
        Assert.Null(One("Keyword = HcTag|Armor||| .5").Skipped);
    }

    // Answer 4 (BaseFilters.cpp:25-45): ',' entries trimmed and NONE skipped; an entry holding '+' is its own group, and
    // '-' and '*' are read on each member.
    [Fact]
    public void TheFiltersSplitIntoEntriesAndGroups()
    {
        var t = One("Keyword = HcTag|Armor| A , NONE ,B+-*C, D+E ,-*F").Tokens.ToList();
        Assert.Equal(new[] { ("A", false, false, -1), ("B", false, false, 0), ("C", true, true, 0), ("D", false, false, 1), ("E", false, false, 1), ("F", true, true, -1) },
            t.Select(x => (x.Raw, x.Not, x.Partial, x.Group)).ToArray());
    }

    // Answer 4 (LookupConfigs.cpp:20): sections past the fifth are ignored; traits on a type that takes none are ignored
    // (Traits.h:630-657), dev BOOBIES Misc Item lines carry none.
    [Fact]
    public void ExtraSectionsAndTraitsOnATraitlessTypeAreIgnored()
    {
        Assert.Contains("has 6 '|' sections; KID reads the first five and ignores the rest", One("Keyword = HcTag|Armor||||x").Notes);
        Assert.Contains("a Misc Item line takes no traits, so KID ignores 'E'", One("Keyword = HcTag|Misc Item||E").Notes);
        Assert.Empty(One("Keyword = HcTag|Armor||E").Notes);
    }

    // ---- what a line names -------------------------------------------------------------------

    // Pick KID Q1 (a): a filter form of the line's own type is the target (dev GVLT_LifeswornVestige_KID.ini:1), any other
    // filter form a filter (dev Footprints_KID.ini:2 ClothingFeet), the keyword the value (Footprints_KID.ini:2 by FormID,
    // dev Adamant_KID.ini:2 by EditorID); an excluded form of the line's own type is a filter.
    [Fact]
    public void AnOwnTypeFilterFormIsTargetOtherFiltersFilterAndTheKeywordValue()
    {
        var r = new World().Read("Keyword = 0x801~HcKid.esp|Armor|HcHelmet,HcKeyword,-HcElsewhere");
        Assert.Contains(new DistributorLines.Named(SkyPatcherOverlay.FormPlace.Value, "keyword", "0x801~HcKid.esp", Kw, false), r.Forms);
        Assert.Contains(new DistributorLines.Named(SkyPatcherOverlay.FormPlace.Target, "filters", "HcHelmet", Helmet, true), r.Forms);
        Assert.Contains(new DistributorLines.Named(SkyPatcherOverlay.FormPlace.Filter, "filters", "HcKeyword", Kw, true), r.Forms);
        Assert.Contains(new DistributorLines.Named(SkyPatcherOverlay.FormPlace.Filter, "filters -", "HcElsewhere", Elsewhere, true), r.Forms);
        Assert.Null(r.Dropped);
        var w = new World().Read("Keyword = HcKeyword|Weapon|HcHelmet");
        Assert.Equal(SkyPatcherOverlay.FormPlace.Filter, w.Forms.Single(f => f.Form == Helmet).Place);
        Assert.Equal(SkyPatcherOverlay.FormPlace.Value, w.Forms.Single(f => f.Form == Kw).Place);
    }

    // Answer 5 by section, the keyword: a name no keyword holds is created and handed out (dev GVLT_LifeswornVestige_KID.ini:1
    // HT_ArmorVisor); a name only a record of another type holds is created too (top-up T6); a FormID that names nothing or
    // names no keyword drops the line.
    [Fact]
    public void AKeywordNameNoKeywordHoldsIsCreatedAndAFormIdThatNamesNoKeywordDropsTheLine()
    {
        var w = new World();
        Assert.Contains("keyword 'HT_ArmorVisor' is no keyword's EditorID, so KID creates one keyword of that name for all the KID lines naming it, in any case, and this line hands it out",
            w.Read("Keyword = HT_ArmorVisor|Armor|HcHelmet").Notes);
        Assert.Contains("keyword 'HcSword' is no keyword's EditorID, so KID creates one keyword of that name for all the KID lines naming it, in any case, and this line hands it out",
            w.Read("Keyword = HcSword|Armor|HcHelmet").Notes);
        Assert.Equal("keyword '0x802~HcKid.esp' names 000802:HcKid.esp, a Weapon record, not a keyword; KID logs 'FAIL - keyword doesn't exist' and drops the line",
            w.Read("Keyword = 0x802~HcKid.esp|Armor").Dropped);
        Assert.StartsWith("keyword '0x999~HcKid.esp' names 01000999", w.Read("Keyword = 0x999~HcKid.esp|Armor").Dropped);
        Assert.StartsWith("its keyword section is empty", w.Read("Keyword = |Armor").Dropped);
    }

    // r1 #16: a keyword FormID written with a load index owes its note, also on the line it drops.
    [Fact]
    public void AKeywordFormIdWrittenWithALoadIndexIsNoted()
        => Assert.Contains(new World().Read("Keyword = 0x1000800~HcKid.esp|Armor").Notes,
            n => n.StartsWith("keyword '0x1000800~HcKid.esp' is written with a load index"));

    // The keyword is created before any filter is read (LookupForms.cpp:41-47), so a line its filters drop still creates
    // it and hands it to no item.
    [Fact]
    public void AKeywordCreatedOnADroppedLineIsHandedToNoItem()
    {
        var r = new World().Read("Keyword = HcNew|Armor|0x999~HcAbsent.esp");
        Assert.NotNull(r.Dropped);
        Assert.Contains("keyword 'HcNew' is no keyword's EditorID, so KID creates one keyword of that name for all the KID lines naming it, in any case; "
                        + "the line is dropped, but KID still creates the keyword, and this line hands it to no item", r.Notes);
    }

    // r1 #11, r2 #8 (call B (i); Cache.h:5, addedKeywords an IStringMap): KID makes one keyword of a created name for every
    // line naming it in any case, so no line is said to be its creator and every line's note differs only in the name as
    // written (ARR 4thcreatures_KID.ini:7 'BestiaryFlesh colossus', Dragonborns Bestiary_KID.ini:60 'BestiaryFlesh Colossus').
    [Fact]
    public void EveryLineNamingACreatedKeywordInAnyCaseSaysOneKeywordIsMadeForAll()
    {
        var reads = new World().ReadAll("Keyword = NewKw|Armor|HcHelmet\nKeyword = newkw|Weapon|HcSword\nKeyword = NewKw|Book\nKeyword = Spaced Kw |Book");
        foreach (var (i, name) in new[] { (0, "NewKw"), (1, "newkw"), (2, "NewKw") })
            Assert.Contains($"keyword '{name}' is no keyword's EditorID, so KID creates one keyword of that name for all the KID lines naming it, in any case, "
                            + "and this line hands it out", reads[i]!.Notes);
        Assert.Contains(reads[3]!.Notes, n => n.StartsWith("keyword 'Spaced Kw ' is no keyword's EditorID (KID keeps the space")
                                              && n.EndsWith(", so KID creates one keyword of that name for all the KID lines naming it, in any case, and this line hands it out"));
    }

    // Answer 5 by section, the filters: a name no record holds is kept as text (dev BOOBIES_Skyrim_KID.ini ArmorTypeArmlets),
    // unless a KID line creates a keyword of that name, which it then names (dev BOOBIES ArmorTypeLeftHandRing); a filter
    // never creates a keyword.
    [Fact]
    public void AFilterNameIsARecordThenAKeywordKidCreatesThenText()
    {
        var reads = new World().ReadAll("Keyword = HcMade|Book\nKeyword = HcTag|Armor|HcMade\nKeyword = HcTag|Armor|HcLoose");
        Assert.Contains("filters 'HcMade' is no record's EditorID but names the keyword a KID line creates, so it matches the items that keyword is given to", reads[1]!.Notes);
        Assert.Contains(reads[2]!.Notes, n => n.StartsWith("filters 'HcLoose' is no record's EditorID and no keyword a KID line creates, so KID keeps it as text"));
        Assert.DoesNotContain(reads[2]!.Notes, n => n.StartsWith("filters") && n.Contains("creates one keyword of that name"));
    }

    // r1 #15 (Cache.h:5, addedKeywords an IStringMap): a filter finds a created keyword by name in any case.
    [Fact]
    public void AFilterFindsACreatedKeywordInAnyCase()
        => Assert.Contains("filters 'hcmade' is no record's EditorID but names the keyword a KID line creates, so it matches the items that keyword is given to",
            new World().ReadAll("Keyword = HcMade|Book\nKeyword = HcTag|Armor|hcmade")[1]!.Notes);

    // Answer 5 by section, the filters: a FormID or plugin that names nothing, or a record KID does not take as a filter
    // (FormData.cpp:110-148), is skipped and the line widens; every plain filter failing drops it (dev
    // BOOBIES_RotolMoreIcons_KID.ini:3, a plugin not in the order; Aetherium Forge Destroys Items_KID.ini:49, a FormID of one).
    [Fact]
    public void AFailingFilterWidensTheLineAndAllFailingDropIt()
    {
        var w = new World();
        var widened = w.Read("Keyword = HcTag|Armor|HcHelmet,HcRecipe");
        Assert.Null(widened.Dropped);
        Assert.Contains("filters 'HcRecipe' names 000803:HcKid.esp, a ConstructibleObject record, which KID does not take as a filter; KID skips it, so the line reaches more items", widened.Notes);
        Assert.Equal("filters 'DNPCsSkullMasks.esp' names plugin 'DNPCsSkullMasks.esp', which is not in the active order; every plain filter of the line (no '-', '*' or '+') names nothing KID can use, so KID drops the line",
            w.Read("Keyword = BoobiesArmorFullmaskLight|Armor|DNPCsSkullMasks.esp").Dropped);
        Assert.StartsWith("filters '0x967~Andrealletius Reinfused Artifacts.esp' names plugin 'Andrealletius Reinfused Artifacts.esp', which is not in the active order",
            w.Read("Keyword = 0x801~HcKid.esp|Weapon|0x967~Andrealletius Reinfused Artifacts.esp").Dropped);
        // A '*' term stands beside a failing exact term, but is not one: the exact list still fails (BaseFilters.cpp:73-95).
        Assert.NotNull(w.Read("Keyword = HcTag|Armor|*Iron,HcRecipe").Dropped);
    }

    // BaseFilters.cpp:93: every '-' filter failing drops the line too, a '-*' one stands.
    [Fact]
    public void EveryNotFilterFailingDropsTheLine()
    {
        var w = new World();
        Assert.EndsWith("every '-' filter of the line names nothing KID can use, so KID drops the line", w.Read("Keyword = HcTag|Armor|-0x999~HcKid.esp").Dropped);
        var widened = w.Read("Keyword = HcTag|Armor|-0x999~HcKid.esp,-HcHelmet");
        Assert.Null(widened.Dropped);
        Assert.Contains(widened.Notes, n => n.EndsWith("KID skips it, so the line excludes fewer items"));
        Assert.Null(w.Read("Keyword = HcTag|Armor|-0x999~HcKid.esp,-*Iron").Dropped);
    }

    // BaseFilters.cpp:97-122: a '+' group with a part that fails is dropped; the line stands while another group does,
    // and drops when every group did.
    [Fact]
    public void APlusGroupNeedsEveryPartAndTheLineNeedsOneGroup()
    {
        var w = new World();
        var one = w.Read("Keyword = HcTag|Armor|HcHelmet+HcKeyword,HcHelmet+0x999~HcKid.esp");
        Assert.Null(one.Dropped);
        Assert.Contains(one.Notes, n => n.StartsWith("'+' group 'HcHelmet+0x999~HcKid.esp': '0x999~HcKid.esp' names 01000999") && n.EndsWith("so KID drops that group and the line's other groups stand"));
        Assert.EndsWith("every group of the line lost one, so KID drops the line", w.Read("Keyword = HcTag|Armor|HcHelmet+0x999~HcKid.esp").Dropped);
        Assert.Equal(SkyPatcherOverlay.FormPlace.Target, one.Forms.First(f => f.Where == "filters +").Place);
    }

    // A form list a filter names is named itself and not followed to its members (top-up T3: no dev or ARR line names one).
    [Fact]
    public void AFormListFilterNamesTheListOnly()
        => Assert.Equal(new[] { List }, new World().Read("Keyword = HcTag|Armor|HcList").Forms.Where(f => f.Where == "filters").Select(f => f.Form));

    // A plugin term names no form; it is kept apart for plugins= (DECISIONS 2026-11-13 (12)(iii); dev
    // BOOBIES_ImmersiveIcons_KID.ini:15 -ccbgssse050-ba_daedric.esl).
    [Fact]
    public void APluginTermIsKeptApartAndNamesNoForm()
    {
        var r = new World().Read("Keyword = BookTypeMissive|Book|*Bounty,-HcOther.esp");
        Assert.Equal(new[] { "HcOther.esp" }, r.PluginTerms);
        Assert.DoesNotContain(r.Forms, f => f.Where.StartsWith("filters"));
        Assert.Equal("HcSword.ESP", Assert.Single(One("Keyword = HcTag|Armor|HcSword.ESP").Tokens).Raw);
        Assert.Empty(new World().Read("Keyword = HcTag|Armor|HcSword.ESP").PluginTerms);   // the ending is case-sensitive (FormData.h:25)
    }

    // Pick (12)(ii): a FormID written with a load index is added as KID adds it, unmasked, and the line notes the index
    // (ARR Dragonborn Reskin - Wheeler/DBWR_SPEL_KID.ini:7 0X6055666~Inigo.esp).
    [Fact]
    public void ALoadIndexWrittenIntoAFormIdIsAddedAndNoted()
    {
        var r = new World().Read("Keyword = HcTag|Armor|0x1000800~HcKid.esp");
        Assert.Contains(r.Notes, n => n.StartsWith("filters '0x1000800~HcKid.esp' is written with a load index"));
        Assert.Contains(r.Forms, f => f.Form == Elsewhere);
    }

    // A '.nif' term is a model path and is not looked up; a '*' term is a partial string (answer 3, BaseFilters.h:35).
    [Fact]
    public void AModelPathAndAPartialStringAreMatchedInTheRunningGame()
    {
        var r = new World().Read("Keyword = HcTag|Weapon|weapons\\iron\\sword.NIF,*steel,*mace.nif");
        Assert.Contains(r.Notes, n => n.StartsWith("filters 'weapons\\iron\\sword.NIF' ends in '.nif', so KID matches it against an item's whole model path"));
        Assert.Contains("filters '*steel' matches part of an item's EditorID, name or one of its keywords' EditorIDs in the running game, which is not read here", r.Notes);
        Assert.Contains(r.Notes, n => n.StartsWith("filters '*mace.nif' ends in '.nif', so KID matches it against part of an item's model path"));
        Assert.Empty(r.Forms.Where(f => f.Where.StartsWith("filters")));
    }

    // ExclusiveGroups.cpp:30-66: members split untrimmed; a member that is no keyword is left out and logged; a group
    // keeping no keyword is dropped; a keyword another KID line creates may be a member.
    [Fact]
    public void AGroupKeepsItsKeywordMembersOnly()
    {
        var w = new World();
        var reads = w.ReadAll("Keyword = HcMade|Book\nExclusiveGroup = Mine|HcKeyword, HcMade,HcHelmet,HcMade");
        var g = reads[1]!;
        Assert.Null(g.Dropped);
        Assert.Contains(g.Notes, n => n.StartsWith("members ' HcMade' finds no record by that EditorID") && n.Contains("KID keeps the space"));
        Assert.Contains(g.Notes, n => n.StartsWith("members 'HcHelmet' names 000800:HcKid.esp, an Armor record, not a keyword"));
        Assert.Equal(SkyPatcherOverlay.FormPlace.Filter, g.Forms.Single(f => f.Form == Kw).Place);
        Assert.Equal("group 'Mine' keeps no keyword, so KID drops it", w.Read("ExclusiveGroup = Mine|HcHelmet").Dropped);
        Assert.StartsWith("names no members", One("ExclusiveGroup = Mine|NONE").Skipped);
    }

    // r1 #14: a group whose members are all keywords KID lines create keeps them, so KID does not drop it.
    [Fact]
    public void AGroupOfCreatedKeywordsOnlyIsKept()
    {
        var reads = new World().ReadAll("Keyword = HcMade|Book\nKeyword = HcMore|Armor\nExclusiveGroup = Mine|HcMade,HcMore");
        Assert.Null(reads[2]!.Dropped);
        Assert.Empty(reads[2]!.Notes);
    }

    // r1 #17 (ExclusiveGroups.cpp:30, IDs.at(0)): an empty member throws, so KID fails the group line.
    [Fact]
    public void AnEmptyGroupMemberFailsTheGroupLine()
        => Assert.StartsWith("has an empty member, so KID fails to parse the entry", One("ExclusiveGroup = Mine|HcKeyword,,HcMade").Skipped);

    // R39: one row drives the read and the skill text, so a type string edited in the row is read and printed as edited,
    // and the old one is no type for either.
    [Fact]
    public void OneRowDrivesTheReadAndTheSkillText()
    {
        var row = ShippedRows.Distributor("kid", ("\"key\": \"Soul Gem\"", "\"key\": \"SoulGem\""));
        Assert.Equal("SoulGem", Assert.Single(KidLines.Parse("Keyword = HcTag|SoulGem", row)).Type);
        Assert.NotNull(Assert.Single(KidLines.Parse("Keyword = HcTag|Soul Gem", row)).Skipped);
        var text = HousecarlGenerator.RuntimeFormatSkillText.KidRegions(row);
        Assert.Contains("`SoulGem`", text["kid.lines"]);
        Assert.DoesNotContain("`Soul Gem`", text["kid.lines"]);
        Assert.Contains("Soul Gem", HousecarlGenerator.RuntimeFormatSkillText.KidRegions(RuntimeFormats.Kid)["kid.lines"]);
    }

    // The keyword of every line is looked up as a keyword after every filter is looked up among every type, so the
    // call's EditorID table walks every type once before the narrower keyword lookups use it.
    [Fact]
    public void FilterNamesAreLookedUpBeforeKeywordNames()
    {
        var w = new World();
        var order = new List<string?>();
        var spy = new SpyResolver(w.R, order);
        KidLines.ReadAll(KidLines.Parse("Keyword = HcKeyword|Armor|HcHelmet\nKeyword = HcKeyword|Armor|HcSword", Kid),
            new KidLines.Context(spy, fk => w.Classes.GetValueOrDefault(fk), RuntimeFormats.Kid));
        Assert.Equal(RuntimeTargets.EveryTypeScope, order.First());
        Assert.True(order.LastIndexOf(RuntimeTargets.EveryTypeScope) < order.IndexOf("Keyword"));
    }

    sealed class SpyResolver(SkyPatcherOverlay.IFormResolver inner, List<string?> order) : SkyPatcherOverlay.IFormResolver
    {
        public FormKey? ResolveEditorId(string editorId, string? mutagenType) { order.Add(mutagenType); return inner.ResolveEditorId(editorId, mutagenType); }
        public string? ReadWinnerLeaf(FormKey donor, string path) => inner.ReadWinnerLeaf(donor, path);
        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record) => inner.KeywordsOf(record);
        public bool PluginPresent(string pluginName) => inner.PluginPresent(pluginName);
        public (FormKey? Record, string? Why) FromRuntimeFormId(uint value) => inner.FromRuntimeFormId(value);
        public uint? RuntimeBaseOf(string pluginName) => inner.RuntimeBaseOf(pluginName);
        public string? WinnerPluginOf(FormKey record) => inner.WinnerPluginOf(record);
        public string? EditorIdOf(FormKey record) => inner.EditorIdOf(record);
    }
}
