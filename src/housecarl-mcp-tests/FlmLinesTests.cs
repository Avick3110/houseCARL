using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>FLM's lines read as FLM 1.8.1 reads them (flm-lines research.md answers 3 and 4, with the top-up's
/// corrections T1 and T2): SimpleIni's split of the unnamed section, FLM's Sanitize, the keys and field shapes, the two
/// passes and the names they hold, the filter split and verdict, what each field names and what FLM does where a name
/// finds nothing, and what each kept line adds to which list. Each test names the dev line (D:/Modlists/ReSkyrimed) or
/// ARR line that reaches its rule, where one does.</summary>
[Trait("tier", "unit")]
public sealed class FlmLinesTests
{
    static DistributorFormat Flm => RuntimeFormats.Flm;

    static readonly ModKey Mod = ModKey.FromFileName("HcFlm.esp");
    static FormKey K(uint id) => new(Mod, id);
    static readonly FormKey List = K(0x800), List2 = K(0x801), Coin = K(0x802), Gem = K(0x803), Old = K(0x804), Kw = K(0x805),
        Seed = K(0x806), Bush = K(0x807), Planter = K(0x808), Planted = K(0x809), Holder = K(0x80A), Toys = K(0x80B),
        Boots = K(0x80C);

    sealed class World
    {
        public readonly SkyPatcherOverlayHarness.StubResolver R = new();
        readonly Dictionary<FormKey, Type> _classes = new();
        public readonly Dictionary<FormKey, IReadOnlyList<FormKey>> Items = new();

        public World()
        {
            R.Plugins.Add(Mod.FileName);
            R.Plugins.Add("Skyrim.esm");
            R.RuntimeBases[Mod.FileName] = 0x01000000;
            R.RuntimeBases["Skyrim.esm"] = 0x00000000;
            Add(List, "HcList", typeof(FormList));
            Add(List2, "HcList2", typeof(FormList));
            Add(Coin, "HcCoin", typeof(MiscItem));
            Add(Gem, "HcGem", typeof(MiscItem));
            Add(Old, "HcOld", typeof(MiscItem));
            Add(Kw, "HcKw", typeof(Keyword));
            Add(Seed, "HcSeed", typeof(Ingredient));
            Add(Bush, "HcBush", typeof(Flora));
            Add(Planter, "flPlanterPlantableItem", typeof(FormList));
            Add(Planted, "flPlanterPlantedFlora", typeof(FormList));
            Add(Holder, "HcHolder", typeof(FormList));
            Add(Toys, "BYOHRelationshipAdoptionPlayerGiftChildMale", typeof(FormList));
            Add(Boots, "HcBoots", typeof(Armor));
            Items[List] = new[] { Old };
            Items[Holder] = new[] { Gem, Coin };
        }

        void Add(FormKey fk, string eid, Type cls)
        {
            R.EditorIds[eid] = fk;
            R.EditorIdTypes[eid] = cls.Name;
            R.Winners[fk] = fk.ModKey.FileName;
            R.RuntimeForms[0x01000000 + fk.ID] = fk;
            _classes[fk] = cls;
        }

        public FlmLines.Context Ctx => new(R, fk => _classes.GetValueOrDefault(fk), fk => Items.GetValueOrDefault(fk), Flm);

        /// <summary>Every line of the files, in the order given, read in FLM's passes.</summary>
        public (IReadOnlyList<FlmLines.Line> Lines, IReadOnlyList<FlmLines.LineRead?> Reads) ReadAll(params (string File, string Text)[] files)
        {
            var lines = files.SelectMany(f => FlmLines.Parse(f.Text, Flm, f.File)).ToList();
            return (lines, FlmLines.ReadAll(lines, Ctx));
        }

        public IReadOnlyList<FlmLines.LineRead?> Reads(string text) => ReadAll(("Hc_FLM.ini", text)).Reads;

        public FlmLines.LineRead Read(string text) => Reads(text).Last(r => r is not null)!;

        /// <summary>The add rows of one file's lines, by line index.</summary>
        public IReadOnlyDictionary<int, string> Rows(string text)
        {
            var (lines, reads) = ReadAll(("Hc_FLM.ini", text));
            return FlmLines.Rows(new[] { (lines, reads) }, Ctx).ToDictionary(kv => kv.Key.Line, kv => kv.Value);
        }
    }

    static IReadOnlyList<FlmLines.Line> Parse(string text) => FlmLines.Parse(text, Flm, "Hc_FLM.ini");

    static FlmLines.Line One(string text) => Assert.Single(Parse(text));

    static string Notes(FlmLines.LineRead r) => string.Join("\n", r.Notes);

    static FormKey? Labelled(FlmLines.LineRead r, SkyPatcherOverlay.FormPlace place, string where, FormKey fk)
        => r.Forms.Any(n => n.Place == place && n.Where == where && n.Form == fk) ? fk : null;

    // ---- the split ---------------------------------------------------------------------------

    // T5 (1.8.1 FindEntry): ';' and '#' lead a comment at a line's start, after any space; ARR holds 1,639 ';' lines.
    [Fact]
    public void ALineStartingWithASemicolonOrAHashIsAComment()
        => Assert.Equal("FormList", One("; a\n  # b\nFormList = HcList|HcCoin").Key);

    // Answer 3: a line under a named header is not FLM's, which reads only the unnamed section (Manipulator.hpp:407, :487).
    [Fact]
    public void ALineUnderANamedSectionIsNotRead()
    {
        var lines = Parse("[Mine]\nFormList = HcList|HcCoin\n[]\nFormList = HcList|HcGem");
        Assert.StartsWith("sits under [Mine], and FLM reads only the unnamed section", lines[0].Skipped);
        Assert.Null(lines[1].Skipped);
    }

    // Answer 3: a key with no '=' is dropped (ARR FirstPersonInteractions_FLM.ini:21, a U+FEFF before ';').
    [Fact]
    public void ALineWithNoEqualsIsNotRead()
    {
        Assert.StartsWith("has no '='", One("\uFEFF; looks like a comment").Skipped);
        Assert.StartsWith("has no key before '='", One("= HcList|HcCoin").Skipped);
    }

    // Answer 4 (Manipulator.hpp:417-418, :513-518): keys are lowercased against the table; an unknown key is logged and invalid.
    [Fact]
    public void KeysMatchInAnyCaseAndAnUnknownKeyIsInvalid()
    {
        Assert.Equal("FormList", One("FORMLIST = HcList|HcCoin").HeaderKey);
        Assert.Contains("so FLM logs 'Unknown key FromList!' and counts it invalid", One("FromList = HcList|HcCoin").Skipped);
    }

    // Answer 4 (Utility.hpp:98-123): spaces round '|' and ',' go, leading zeros after '0x' go (ARR 74 lines).
    [Fact]
    public void SanitizeStripsSpacesAndLeadingZeros()
    {
        Assert.Equal(new[] { "HcList", "HcCoin,0x804~HcFlm.esp" }, One("FormList = HcList | HcCoin , 0x000804~HcFlm.esp").Fields);
        Assert.Equal(Coin, new World().Read("FormList = HcList | 0x00000802~HcFlm.esp").Forms.Single(f => f.Where == "items").Form);
    }

    // Answer 4: the Dawnguard and Dragonborn rewrite runs on the whole value, in any case, whatever plugin follows; noted
    // as a load index is (DECISIONS 2026-11-13 (12)(ii)). 0 tokens on dev or ARR.
    [Fact]
    public void AFormIdWithIndex02Or04IsRewrittenToTheDlcAndNoted()
    {
        var l = One("FormList = HcList|0x02000804~HcFlm.esp,0X4000802");
        Assert.Equal("0x000804~Dawnguard.esm~HcFlm.esp,0x000802~Dragonborn.esm", l.Fields[1]);
        Assert.Contains("FLM rewrites '0x02000804' to '0x000804~Dawnguard.esm' before it reads the line", string.Join("\n", l.Notes));
        Assert.Contains("load index 04 as Dragonborn.esm's", string.Join("\n", l.Notes));
    }

    // CLibUtil 191c90a8 string.hpp:225-229: split keeps an empty field (ARR LootingAnimations_FLM.ini:52 ends with ',').
    [Fact]
    public void AnEmptyItemIsLoggedAndTheRestAdded()
    {
        var r = new World().Read("FormList = HcList|HcCoin,");
        Assert.Contains("an empty item (a ',' with nothing after it) names nothing; FLM logs 'Unable to find Form: .' and adds the rest", Notes(r));
        Assert.Single(r.Adds);
    }

    // Manipulator.hpp:746-750: a FormList line takes 2 or 3 fields.
    [Fact]
    public void AWrongFieldCountIsInvalid()
        => Assert.StartsWith("has 1 '|' field(s), and FLM's FormList takes 2 or 3 (list|forms|filter), so FLM logs 'Wrong FormList format'",
            new World().Read("FormList = HcList").Dropped);

    // ---- the filter --------------------------------------------------------------------------

    // Utility.hpp:267-303: ',' separates alternatives, every condition of one must hold; '+' needs the plugin, '-' its absence.
    [Theory]
    [InlineData("+HcFlm.esp", 1)]
    [InlineData("-HcFlm.esp", -1)]
    [InlineData("+HcAbsent.esp", -1)]
    [InlineData("+HcAbsent.esp,+HcFlm.esp", 1)]
    [InlineData("+HcFlm.esp&-Skyrim.esm", -1)]
    [InlineData("HcFlm.esp", 0)]
    [InlineData("nothing", 1)]
    public void AFilterIsTrueFalseOrInvalid(string filter, int verdict)
        => Assert.Equal(verdict, FlmLines.Evaluate(FlmLines.SanitizeFilter(filter), Flm.FlmLines!, new World().R.PluginPresent).Value);

    // T2 (2): 1.8.1 splits as 1.7.0, going on only while another extension kind is ahead, so a later condition of the
    // same extension is never checked (ARR BAA_FL-OCF_Filters_FLM.ini:891 `hasRequiemYesMLU`, 31 conditions).
    [Fact]
    public void ALaterConditionOfTheSameExtensionIsNeverChecked()
    {
        var v = FlmLines.Evaluate("+hcflm.esp&+hcabsent.esp", Flm.FlmLines!, new World().R.PluginPresent);
        Assert.Equal(1, v.Value);
        Assert.Contains("FLM checks '+hcflm.esp' of '+hcflm.esp&+hcabsent.esp' and never '&+hcabsent.esp'", v.Notes.Single());
        Assert.Equal(new[] { "+a.esp", "+b.esm" }, FlmLines.SplitConditions("+a.esp&+b.esm", Flm.FlmLines!.FilterExtensions, out _));
    }

    // (12)(iii): a FormList line's filter is evaluated first; false counts the line filtered out and adds nothing, and
    // the plugin it names is no form the line touches (ARR 287 filtered lines; T1: 219 filtered out, 67 apply, 1 invalid).
    [Fact]
    public void AFalseFilterFiltersTheLineOutAndNamesNoForm()
    {
        var w = new World();
        var off = w.Read("FormList = HcList|HcCoin|-HcFlm.esp");
        Assert.True(off.FilteredOut);
        Assert.Empty(off.Adds);
        Assert.Contains("its filter '-HcFlm.esp' is false: '-hcflm.esp' (hcflm.esp is in the order) fails, so FLM counts the line filtered out", Notes(off));
        Assert.Empty(off.PluginTerms);
        var on = w.Read("FormList = HcList|HcCoin|+HcFlm.esp");
        Assert.False(on.FilteredOut);
        Assert.Single(on.Adds);
        Assert.Contains("its filter '+HcFlm.esp' is true: '+hcflm.esp' holds", Notes(on));
    }

    // Manipulator.hpp:754-756: FLM reads the filter before the list and the items, so a filtered-out line logs no miss
    // and is not invalid for a list it never looks up; its forms are still labelled.
    [Fact]
    public void AFilteredOutLineLooksNothingUp()
    {
        var r = new World().Read("FormList = HcNoList|HcNoSuch,HcCoin|-HcFlm.esp");
        Assert.True(r.FilteredOut);
        Assert.Null(r.Dropped);
        Assert.DoesNotContain("Unable to find", Notes(r));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items", Coin));
    }

    // Manipulator.hpp:1396-1432: '#' names a Filter line's verdict, the name lowercased; ARR BAC_FL-OCF_LIGH_FLM.ini:87
    // ends '|#', an empty name, which is invalid.
    [Fact]
    public void ANamedFilterIsReadByNameAndAnUnknownOneIsInvalid()
    {
        var w = new World();
        var reads = w.Reads("FormList = HcList|HcCoin|#HasMe\nFilter = hasme|+HcFlm.esp\nFormList = HcList|HcGem|#");
        Assert.Contains("its filter '#HasMe' is true: the Filter line at Hc_FLM.ini:2 is true", Notes(reads[0]!));
        Assert.StartsWith("its filter '#' is invalid (no Filter line defines '')", reads[2]!.Dropped);
    }

    // Manipulator.hpp:754-756 with split keeping an empty field: a trailing '|' is an empty filter, which names no
    // section and so is false.
    [Fact]
    public void ATrailingBarIsAnEmptyFilterAndFiltersTheLineOut()
        => Assert.True(new World().Read("FormList = HcList|HcCoin|").FilteredOut);

    // Manipulator.hpp:1154-1160: the first Filter of a name wins, names lowercased.
    [Fact]
    public void AFilterNameDefinedTwiceKeepsTheFirst()
        => Assert.StartsWith("names filter 'hasme', which Hc_FLM.ini:1 defines first",
            new World().Reads("Filter = HasMe|+HcFlm.esp\nFilter = hasme|-HcFlm.esp")[1]!.Dropped);

    // ---- what a line names -------------------------------------------------------------------

    // Answer 6's labels: the list is the target, each item a value (dev Skyrim Revoiced_FLM.ini:2 `VoicesJarl|MaleNordAltFour`).
    [Fact]
    public void TheListIsTheTargetAndEachItemAValue()
    {
        var r = new World().Read("FormList = HcList|HcCoin,HcGem");
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Target, "list", List));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items", Coin));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items", Gem));
    }

    // Manipulator.hpp:775-780 (FindForm<BGSListForm>): a target naming nothing, or a record that is not a list, drops the line.
    [Fact]
    public void ATargetThatIsNoListDropsTheLine()
    {
        var w = new World();
        Assert.Contains("its list 'HcCoin' names 000802:HcFlm.esp, a record of type MiscItem, not a FormList, so FLM logs 'Unable to find FormList: HcCoin.'",
            w.Read("FormList = HcCoin|HcGem").Dropped);
        Assert.Contains("its list 'HcNoList' finds no record", w.Read("FormList = HcNoList|HcGem").Dropped);
    }

    // Manipulator.hpp:1474-1480: an item naming nothing is logged and the rest are added (dev Skyrim Revoiced_FLM.ini:3
    // `FemaleComonerAltTwo`; ARR 715 missing forms).
    [Fact]
    public void AnItemNamingNothingIsSkippedAndTheRestAdded()
    {
        var r = new World().Read("FormList = HcList|HcNoSuch,HcCoin");
        Assert.Null(r.Dropped);
        Assert.Contains("item 'HcNoSuch' finds no record", Notes(r));
        Assert.Contains("FLM logs 'Unable to find Form: HcNoSuch.' and adds the rest", Notes(r));
        Assert.Equal(new[] { Coin }, r.Adds.Single().Forms.Select(f => f.Form));
    }

    // Answer 4 (Utility.hpp:77): '0x' with no '~' is a runtime FormID only in lower case; '0X' is an EditorID.
    [Fact]
    public void BareHexIsAFormIdOnlyWithALowerCaseX()
    {
        var w = new World();
        Assert.Equal(Coin, w.Read("FormList = HcList|0x1000802").Adds.Single().Forms.Single().Form);
        Assert.Contains("item '0X1000802' finds no record by that EditorID", Notes(w.Read("FormList = HcList|0X1000802")));
    }

    // Utility.hpp:57-58: a ten-character FormID side loses characters 2-3 before the add, noted (DECISIONS 2026-11-13
    // (12)(ii)); 0 such sides on dev or ARR.
    [Fact]
    public void ATenCharacterFormIdLosesItsIndexAndIsNoted()
    {
        var r = new World().Read("FormList = HcList|0xFE000802~HcFlm.esp");
        Assert.Equal(Coin, r.Adds.Single().Forms.Single().Form);
        Assert.Contains("item '0xFE000802~HcFlm.esp' has the ten-character FormID '0xFE000802', from which FLM erases 'FE'", Notes(r));
    }

    // DECISIONS 2026-11-13 (14) Q1 (b), Manipulator.hpp:1460-1473: a '*list' item adds the list's own forms, read from
    // its winning record, each labelled through the list (ARR 72 '*' tokens).
    [Fact]
    public void AStarListAddsItsRecordsFormsThroughIt()
    {
        var r = new World().Read("FormList = HcList|*HcHolder");
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items: list", Holder));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items through list", Gem));
        Assert.Equal(new[] { Gem, Coin }, r.Adds.Single().Forms.Select(f => f.Form));
    }

    // Q1 (b), Manipulator.hpp:881-967, :1436-1444: a '#group' item adds the group's forms, labelled through it; groups
    // are read before the file's other lines, so a FormList line above its Group line still finds it.
    [Fact]
    public void AGroupItemAddsItsMembersThroughIt()
    {
        var r = new World().Reads("FormList = HcList2|#Mine\nGroup = Mine|HcCoin,*HcHolder")[0]!;
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items through group", Coin));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "items through group", Gem));
        Assert.Equal(new[] { Coin, Gem, Coin }, r.Adds.Single().Forms.Select(f => f.Form));
    }

    // Answer 4 (pass 2 per file, :469-533): an alias or group counts from its own file on, so a line of an earlier file
    // naming it finds nothing (0 forward uses on dev or ARR).
    [Fact]
    public void AnAliasOrGroupCountsFromItsOwnFileOn()
    {
        var (_, reads) = new World().ReadAll(("A_FLM.ini", "FormList = #Both|HcCoin\nFormList = HcList|#Mine"),
                                             ("B_FLM.ini", "Alias = Both|HcList,HcList2\nGroup = Mine|HcGem\nFormList = #Both|HcGem"));
        Assert.Contains("names alias 'Both', which no Alias line defines in this file or an earlier one", reads[0]!.Dropped);
        Assert.Contains("item '#Mine' names no group or collection FLM holds by then", Notes(reads[1]!));
        Assert.Equal(new[] { List, List2 }, reads[4]!.Adds.Select(a => a.List));
        Assert.NotNull(Labelled(reads[4]!, SkyPatcherOverlay.FormPlace.Target, "list through alias", List2));
    }

    // Manipulator.hpp:826-879: an alias keeps the lists it finds; one naming none, or a name defined before, is omitted.
    [Fact]
    public void AnAliasKeepsTheListsItFinds()
    {
        var reads = new World().Reads("Alias = A|HcList,HcCoin\nAlias = A|HcList2\nAlias = B|HcNoList");
        Assert.Contains("alias list 'HcCoin' names 000802:HcFlm.esp, a record of type MiscItem, not a FormList", Notes(reads[0]!));
        Assert.StartsWith("names alias 'A', which Hc_FLM.ini:1 defines first", reads[1]!.Dropped);
        Assert.StartsWith("names no list FLM finds", reads[2]!.Dropped);
    }

    // Manipulator.hpp:948-964: a group naming no form FLM finds is omitted; one defined twice keeps the first.
    [Fact]
    public void AGroupWithNoFormOrARepeatedNameIsOmitted()
    {
        var reads = new World().Reads("Group = G|HcNoSuch\nGroup = H|HcCoin\nGroup = H|HcGem");
        Assert.StartsWith("holds no form FLM finds", reads[0]!.Dropped);
        Assert.StartsWith("names group 'H', which Hc_FLM.ini:2 defines first", reads[2]!.Dropped);
    }

    // Answer 6, Q1 (b): a Collection's keywords are filters; a '#collection' item shows them, its members not read.
    [Fact]
    public void ACollectionShowsItsKeywordsAsFilters()
    {
        var reads = new World().Reads("FormList = HcList|#Shiny\nCollection = Shiny|Misc|HcKw");
        Assert.NotNull(Labelled(reads[1]!, SkyPatcherOverlay.FormPlace.Filter, "keywords", Kw));
        Assert.NotNull(Labelled(reads[0]!, SkyPatcherOverlay.FormPlace.Filter, "items through collection", Kw));
        Assert.Equal("Shiny", reads[0]!.Adds.Single().Collections.Single());
    }

    // Manipulator.hpp:1020-1026: one keyword naming nothing drops the whole collection (ARR 37 lines, e.g.
    // `OCF_ActiHeatSource_OtherLarge`); a type FLM does not take is invalid; a keyword must be a keyword.
    [Fact]
    public void OneMissingKeywordDropsTheCollection()
    {
        var w = new World();
        Assert.Contains("keyword 'HcNoKw' finds no record", w.Read("Collection = C|Armor|HcKw,HcNoKw").Dropped);
        Assert.Contains("logs 'Keyword HcNoKw do not exist!' and drops the whole collection", w.Read("Collection = C|Armor|HcKw,HcNoKw").Dropped);
        Assert.Contains("keyword 'HcCoin' names 000802:HcFlm.esp, a record of type MiscItem, not a Keyword", w.Read("Collection = C|Armor|HcCoin").Dropped);
        Assert.StartsWith("has type 'Shout', which is not one of FLM's collection types", w.Read("Collection = C|Shout|HcKw").Dropped);
    }

    // Manipulator.hpp:1001-1043: a collection's own filter is read after its keywords are stored, so a false one still
    // leaves the name to be filled at load (said, not shown); no ARR collection has a filter.
    [Fact]
    public void AFilteredOutCollectionMayStillBeFilled()
    {
        var r = new World().Read("Collection = C|Misc|HcKw|-HcFlm.esp");
        Assert.True(r.FilteredOut);
        Assert.Contains("FLM has stored its keywords before it reads the filter, so collection 'C' may still be filled at load", Notes(r));
    }

    // Manipulator.hpp:925, :1036-1041, :1135, :1446: a '#name' naming a collection whose own filter is false or invalid
    // finds it at pass 2 only if its stored keywords gathered a form at load; no dev or ARR collection has a filter.
    [Fact]
    public void ANameOfACollectionItsFilterKeptOutIsKnownOnlyIfFilled()
    {
        var reads = new World().Reads("Collection = C|Misc|HcKw|-HcFlm.esp\nCollection = D|Misc|HcKw|#nosuch\nFormList = HcList|#C\nGroup = G|#D");
        Assert.Contains("item '#C' names collection 'C', whose own filter at Hc_FLM.ini:1 is not true, so FLM holds it only if its stored keywords gathered a form at load (not read here); if none, FLM logs 'Unknown Group/Collection: C.' and skips it", Notes(reads[2]!));
        Assert.Contains("group member '#D' names collection 'D', whose own filter at Hc_FLM.ini:2 is not true", Notes(reads[3]!));
        Assert.Contains("if none, FLM logs 'Unknown Collection: D.' and skips it", Notes(reads[3]!));
        Assert.DoesNotContain("names collection", Notes(new World().Reads("Collection = C|Misc|HcKw|+HcFlm.esp\nFormList = HcList|#C")[1]!));
    }

    // Manipulator.hpp:921-934: a group member '#collection' brings the collection's forms, so a group holding only
    // one is kept only if the collection fills, which is not read.
    [Fact]
    public void AGroupOfACollectionAloneIsKeptOnlyIfItFills()
    {
        var reads = new World().Reads("Group = G|#Shiny\nCollection = Shiny|Misc|HcKw\nGroup = H|#NoSuch");
        Assert.Contains("FLM keeps group 'G' only if collection 'Shiny' holds a form at load, which is not read here", Notes(reads[0]!));
        Assert.NotNull(Labelled(reads[0]!, SkyPatcherOverlay.FormPlace.Filter, "group members through collection", Kw));
        Assert.Contains("group member '#NoSuch' names no collection FLM holds, so FLM logs 'Unknown Collection: NoSuch.'", Notes(reads[2]!));
        Assert.StartsWith("holds no form FLM finds", reads[2]!.Dropped);
    }

    // Manipulator.hpp:284-296, FormListType.hpp: a simplified key's fixed list is the target, marked implied (DECISIONS
    // 2026-11-13 (14) Q3); ARR KabusGourds_FLM.ini `Plant` lines.
    [Fact]
    public void APlantLinesListsAreImpliedTargets()
    {
        var r = new World().Read("Plant = HcSeed|HcBush");
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Target, "implied list", Planter));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Target, "implied list", Planted));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "seed", Seed));
        Assert.NotNull(Labelled(r, SkyPatcherOverlay.FormPlace.Value, "plant", Bush));
    }

    // Manipulator.hpp:1345-1371: a half naming nothing makes the pair invalid; a Plant half of an unexpected type is a warning.
    [Fact]
    public void APairHalfNamingNothingIsInvalidAndAnOddPlantTypeAWarning()
    {
        var w = new World();
        Assert.StartsWith("its plant 'HcNoBush' finds no record", w.Read("Plant = HcSeed|HcNoBush").Dropped);
        Assert.Contains("seed 'HcBoots' is a Armor, not Ingredient, Ingestible or Activator; FLM logs a warning and keeps it", Notes(w.Read("Plant = HcBoots|HcBush")));
    }

    // T2 (4): DragonbornSpiderCrafting is read and never added in 1.8.1.
    [Fact]
    public void SpiderCraftingIsReadAndNeverAdded()
    {
        var r = new World().Read("DragonbornSpiderCrafting = HcCoin|HcGem");
        Assert.Contains("FLM 1.8.1 reads DragonbornSpiderCrafting lines and never adds them", Notes(r));
        Assert.Empty(r.Adds);
    }

    // Manipulator.hpp:1300-1330: a list key adds its items to its one fixed list.
    [Fact]
    public void AListKeyAddsToItsFixedList()
        => Assert.Equal(Toys, new World().Read("BToys = HcCoin").Adds.Single().List);

    // T2 (1): Remove is a no-op counted valid, FormsPair always invalid (1.8.1's handlers); 0 lines on dev or ARR.
    [Fact]
    public void RemoveDoesNothingAndFormsPairIsAlwaysInvalid()
    {
        var w = new World();
        var remove = w.Read("Remove = HcList|HcOld");
        Assert.Null(remove.Dropped);
        Assert.Contains("FLM 1.8.1 counts a Remove line valid and does nothing with it at data load", Notes(remove));
        Assert.StartsWith("FLM 1.8.1's FormsPair parser returns false for every line", w.Read("FormsPair = HcCoin|HcGem").Dropped);
        Assert.Contains("FLM checks a ModEvent line at load (three fields, an event name of letters only, its list and forms) and acts on it when a mod sends the event in the running game; neither is read here", Notes(w.Read("ModEvent = Ev|HcList|HcCoin")));
    }

    // ---- FLM's order -------------------------------------------------------------------------

    // Answer 4 (:413-433, :493-523): pass 1 every file's Filter, then Collection lines; pass 2 file by file, Alias and
    // Group lines first, then the rest; each file key by key, any case (research-topup F1; ARR 5 files differ from file order).
    [Fact]
    public void FlmVisitsFiltersCollectionsThenFileByFileAliasesGroupsThenTheRest()
    {
        var lines = new[] { ("A_FLM.ini", "Plant = HcSeed|HcBush\nGroup = G|HcCoin\nFormList = HcList|HcCoin\nformlist = HcList|HcGem\nFilter = f|+HcFlm.esp"),
                            ("B_FLM.ini", "Collection = C|Misc|HcKw\nAlias = A|HcList") }
            .SelectMany(f => FlmLines.Parse(f.Item2, Flm, f.Item1)).ToList();
        Assert.Equal(new[] { 4, 5, 1, 2, 3, 0, 6 }, FlmLines.Visits(lines));
    }

    // Utility.hpp:188-196: a form already in the list (its record's own, or an earlier add) is skipped, each line saying
    // which (dev Skyrim Revoiced_FLM.ini:1 names MaleCondescendingAlt twice).
    [Fact]
    public void AFormAlreadyInTheListIsSkipped()
    {
        var rows = new World().Rows("FormList = HcList|HcOld,HcCoin,HcCoin\nFormList = HcList|HcCoin,HcGem");
        Assert.Equal("          adds 1 form(s) to HcList (000800:HcFlm.esp), skipping 2 already in it: HcOld (the list's own record), HcCoin (earlier on this line)\n", rows[0]);
        Assert.Equal("          adds 1 form(s) to HcList (000800:HcFlm.esp), skipping 1 already in it: HcCoin (Hc_FLM.ini:1)\n", rows[1]);
    }

    // Manipulator.hpp:284-296: simplified lists fill before FormList adds, so a FormList line naming the planter list
    // skips a seed a later Plant line adds.
    [Fact]
    public void SimplifiedListsFillBeforeFormListAdds()
    {
        var rows = new World().Rows("FormList = flPlanterPlantableItem|HcSeed\nPlant = HcSeed|HcBush\nPlant = HcSeed|HcCoin");
        Assert.Equal("          adds HcSeed to flPlanterPlantableItem and HcBush to flPlanterPlantedFlora\n", rows[1]);
        Assert.Equal("          skips the pair: HcSeed is already in flPlanterPlantableItem (Hc_FLM.ini:2)\n", rows[2]);
        Assert.Contains("skipping 1 already in it: HcSeed (Hc_FLM.ini:2)", rows[0]);
    }

    // A filtered-out or invalid line adds nothing and gets no row.
    [Fact]
    public void ALineThatDoesNotApplyHasNoRow()
        => Assert.Empty(new World().Rows("FormList = HcList|HcCoin|-HcFlm.esp\nFormList = HcNoList|HcCoin"));
}
