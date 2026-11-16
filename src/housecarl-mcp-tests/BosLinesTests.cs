using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>BOS's lines read as BOS 3.4.0 and 3.5.0 read them (bos-lines research.md answers 3, 4 and 5, with the
/// top-up's corrections): SimpleIni's split, the section kinds, the fields, then what each field names and what BOS does
/// where a name finds nothing, and BOS's last-wins order. Each test names the dev line (D:/Modlists/ReSkyrimed) or ARR
/// line that reaches its rule, where one does.</summary>
[Trait("tier", "unit")]
public sealed class BosLinesTests
{
    static DistributorFormat Bos => RuntimeFormats.Bos;

    static readonly ModKey Mod = ModKey.FromFileName("HcBos.esp");
    static FormKey K(uint id) => new(Mod, id);
    static readonly FormKey Rock = K(0x800), Mossy = K(0x801), Snowy = K(0x802), Town = K(0x803), TownKw = K(0x804),
        Ref = K(0x805);

    sealed class World
    {
        public readonly SkyPatcherOverlayHarness.StubResolver R = new();
        readonly Dictionary<FormKey, Type> _classes = new();

        public World()
        {
            R.Plugins.Add(Mod.FileName);
            R.Plugins.Add("Skyrim.esm");
            R.RuntimeBases[Mod.FileName] = 0x01000000;
            R.RuntimeBases["Skyrim.esm"] = 0x00000000;
            Add(Rock, "HcRock", typeof(Static));
            Add(Mossy, "HcRockMossy", typeof(Static));
            Add(Snowy, "HcRockSnowy", typeof(Static));
            Add(Town, "HcTownLocation", typeof(Location));
            Add(TownKw, "HcTownKw", typeof(Keyword));
            Add(Ref, "HcRockRef", typeof(PlacedObject));
        }

        void Add(FormKey fk, string eid, Type cls)
        {
            R.EditorIds[eid] = fk;
            R.EditorIdTypes[eid] = cls.Name;
            R.Winners[fk] = fk.ModKey.FileName;
            R.RuntimeForms[0x01000000 + fk.ID] = fk;
            _classes[fk] = cls;
        }

        public BosLines.Context Ctx => new(R, fk => _classes.GetValueOrDefault(fk), Bos);

        public BosLines.LineRead Read(string text) => ReadAll(text).Single(r => r is not null)!;

        public IReadOnlyList<BosLines.LineRead?> ReadAll(string text) => BosLines.ReadAll(BosLines.Parse(text, Bos, "Hc_SWAP.ini"), Ctx);
    }

    static IReadOnlyList<BosLines.Line> Parse(string text) => BosLines.Parse(text, Bos, "Hc_SWAP.ini");

    static BosLines.Line One(string text) => Assert.Single(Parse(text));

    static string Notes(BosLines.LineRead r) => string.Join("\n", r.Notes);

    // ---- the split ---------------------------------------------------------------------------

    // Answer 4 (SimpleIni 4.22 :1252-1253): ';' and '#' lead a comment at a line's start, after any space.
    [Fact]
    public void ALineStartingWithASemicolonOrAHashIsAComment()
        => Assert.Equal("HcRock|HcRockMossy", One("; a\n  # b\n[Forms]\nHcRock|HcRockMossy").Key);

    // Answer 4 (Manager.cpp:81-95): 'Forms' is form swaps, 'Transforms' and 'Properties' property overrides, any other
    // name and none reference swaps; dev COTN Falkreath Lite_SWAP.ini holds [References] and [Forms].
    [Theory]
    [InlineData("[Forms]", BosKind.Forms)]
    [InlineData("[Transforms]", BosKind.Properties)]
    [InlineData("[Properties]", BosKind.Properties)]
    [InlineData("[References]", BosKind.References)]
    [InlineData("[Reference]", BosKind.References)]
    [InlineData("", BosKind.References)]
    [InlineData("[forms]", BosKind.References)]
    [InlineData("[transforms]", BosKind.References)]
    public void TheSectionNamePicksTheMapExactly(string header, BosKind kind)
        => Assert.Equal(kind, One(header + "\nHcRock|HcRockMossy").Section.Kind);

    // Answer 4 (SimpleIni :2458-2483): sections merge in any case and keep their first spelling, which picks the map;
    // a header's tail after ']' is dropped and its name trimmed.
    [Fact]
    public void ASectionRepeatedInAnyCaseJoinsTheFirstAndKeepsItsSpelling()
    {
        var lines = Parse("[forms]\nHcRock|HcRockMossy\n[ Forms ] tail\nHcRockMossy|HcRock");
        Assert.All(lines, l => Assert.Equal("forms", l.Section.Name));
        Assert.All(lines, l => Assert.Equal(BosKind.References, l.Section.Kind));
        Assert.Equal(0, lines[1].Section.Order);
    }

    // Answer 3: a line repeated in its section is read once; dev Simple Snow Improvements - Skyrim_SWAP.ini:790 = :787.
    [Fact]
    public void ALineRepeatedInItsSectionInAnyCaseIsReadOnce()
    {
        var lines = Parse("[Forms]\nHcRock|HcRockMossy\n[Transforms]\nHcRock|HcRockMossy\n[Forms]\nhcrock|hcrockmossy");
        Assert.Null(lines[1].Skipped);
        Assert.StartsWith("repeats line :2 of its section (in any case)", lines[2].Skipped);
    }

    // Answer 4 (SimpleIni :1630-1702): the key runs to '=', and BOS reads only it.
    [Fact]
    public void BosReadsOnlyTheKeyBeforeAnEquals()
    {
        var l = One("[Forms]\nHcRock|HcRockMossy = HcRockSnowy");
        Assert.Equal("HcRock|HcRockMossy", l.Key);
        Assert.Contains("holds '=', and BOS reads only the key before it, 'HcRock|HcRockMossy'", l.Notes);
        Assert.StartsWith("has no key before '='", One("[Forms]\n=HcRock").Skipped);
    }

    // SwapData.cpp:22, :73: a line with no '|' has no second field, which BOS indexes anyway.
    [Fact]
    public void ALineWithNoBarIsNotClaimed()
        => Assert.Contains("which is undefined, so what BOS does with it is not claimed", One("[Forms]\nHcRock").Skipped);

    // SwapData.cpp:73-75: past the fourth field BOS reads nothing.
    [Fact]
    public void FieldsPastTheLastAreSaid()
        => Assert.Contains("has 5 '|' fields; BOS reads the first 4 and ignores the rest", One("[Forms]\nHcRock|HcRockMossy|NONE|100|x").Notes);

    // Answer 4 (Manager.cpp:44-71): a '|' header with head 'Forms' is conditional form swaps, any other head
    // conditional property overrides; its conditions are between the first and second '|'.
    [Fact]
    public void AConditionalHeaderCarriesItsConditionsAndItsKey()
    {
        var s = One("[Forms|HcTownLocation,-HcTownKw]\nHcRock|HcRockMossy").Section;
        Assert.Equal(BosKind.ConditionalForms, s.Kind);
        Assert.Equal(new[] { new BosLines.Condition(false, "HcTownLocation"), new BosLines.Condition(true, "HcTownKw") }, s.Conditions);
        Assert.Equal("Hc_SWAP.ini|HcTownLocation,-HcTownKw", s.ConditionId);
        Assert.Equal(BosKind.ConditionalProperties, One("[Transforms|HcTownLocation]\nHcRock|pos(1,2,3)").Section.Kind);
        // The head is compared whole: 'FormsX' is not 'Forms'.
        Assert.Equal(BosKind.ConditionalProperties, One("[FormsX|HcTownLocation]\nHcRock|pos(1,2,3)").Section.Kind);
    }

    // ---- what a line names -------------------------------------------------------------------

    // Answer 6's labels: the base is the target, each swap a value, each condition of the header a filter (dev
    // Publican's Perch - A Modular Bar and Counter Replacer_SWAP.ini:106 names a '-' condition).
    [Fact]
    public void TheBaseIsTheTargetASwapAValueAndAConditionAFilter()
    {
        var r = new World().Read("[Forms|HcTownLocation,-HcTownKw]\nHcRock|HcRockMossy");
        Assert.Null(r.Dropped);
        Assert.Equal(new[] { (SkyPatcherOverlay.FormPlace.Filter, "conditions", Town), (SkyPatcherOverlay.FormPlace.Filter, "conditions -", TownKw),
                             (SkyPatcherOverlay.FormPlace.Target, "base", Rock), (SkyPatcherOverlay.FormPlace.Value, "swap", Mossy) },
            r.Forms.Select(f => (f.Place, f.Where, f.Form)));
    }

    // Util.cpp:15-38: HEX~Plugin, bare 0x hex and an EditorID each name a form; a term with two '~' is a name.
    [Fact]
    public void EachSpellingNamesAForm()
    {
        var r = new World().Read("[Forms]\n0x801~HcBos.esp|0x01000802");
        Assert.Equal(new[] { Mossy, Snowy }, r.Forms.Select(f => f.Form));
        Assert.StartsWith("swap '0x1~HcBos.esp~x' finds no record by that EditorID",
            new World().Read("[Forms]\nHcRock|0x1~HcBos.esp~x").Dropped);
    }

    // SwapData.cpp:130-132: a base no record holds by that name drops the line, logged.
    [Fact]
    public void ABaseNamingNothingDropsTheLine()
        => Assert.Equal("base 'HcNoRock' finds no record by that EditorID in the active order; BOS logs 'fail : [HcNoRock|HcRock] (BASE formID not found)' and drops the line",
            new World().Read("[Forms]\nHcNoRock|HcRock").Dropped);

    // Answer 5, dev sosPlaques_SWAP.ini:26: a plugin not in the order names nothing, so the line is dropped.
    [Fact]
    public void AnAbsentPluginNamesNothing()
        => Assert.StartsWith("base '0x14EC94~Midwood Isle.esp' names plugin 'Midwood Isle.esp', which is not in the active order",
            new World().Read("[References]\n0x14EC94~Midwood Isle.esp|HcRock").Dropped);

    // SwapData.cpp:92-94: a single swap naming nothing drops the line.
    [Fact]
    public void ASwapNamingNothingDropsTheLine()
        => Assert.EndsWith("(SWAP formID not found)' and drops the line", new World().Read("[Forms]\nHcRock|HcNoRock").Dropped);

    // Answer 3 and the top-up T5 (ARR Samples of Stools … _SWAP.ini:3): a set member naming nothing leaves the set, the rest stand.
    [Fact]
    public void ASwapSetMemberNamingNothingLeavesTheSet()
    {
        var r = new World().Read("[Forms]\nHcRock|HcRockMossy,HcNoRock,HcRockSnowy");
        Assert.Null(r.Dropped);
        Assert.Equal(new[] { Mossy, Snowy }, r.Forms.Where(f => f.Place == SkyPatcherOverlay.FormPlace.Value).Select(f => f.Form));
        Assert.Contains("swap member 'HcNoRock' finds no record by that EditorID in the active order; BOS logs 'failed to process' and leaves it out of the swap set", r.Notes);
    }

    // Answer 3, dev Lumbermill Large Logs_SWAP.ini:24: a transform in the swap field is a swap set naming nothing.
    [Fact]
    public void ATransformInTheSwapFieldDropsTheLine()
        => Assert.StartsWith("no member of its swap set 'rotr(147.9,355.9,82.7),scale(0.90)' names a form (the field holds a transform;",
            new World().Read("[References]\n00099C13~Skyrim.esm|rotr(147.9,355.9,82.7),scale(0.90)").Dropped);

    // SwapData.cpp:83-86, dev Diverse Fences_SWAP.ini:11: a swap naming its own base with no properties is refused; with properties it stands.
    [Fact]
    public void ASwapNamingItsOwnBaseIsRefusedUnlessItSetsProperties()
    {
        Assert.EndsWith("(BASE formID == SWAP formID)' and drops the line", new World().Read("[Forms]\nHcRock|HcRock").Dropped);
        Assert.EndsWith("(BASE formID == SWAP formID)' and drops the line", new World().Read("[Forms]\nHcRock|0x800~HcBos.esp|NONE").Dropped);
        Assert.Null(new World().Read("[Forms]\nHcRock|HcRock|scale(2)").Dropped);
    }

    // DECISIONS 2026-11-13 (12)(ii), dev ICRFixes_SWAP.ini:169: a load index is added as BOS adds it, and the line says so.
    [Fact]
    public void ALoadIndexIsAddedAndNoted()
    {
        var w = new World();
        w.R.RuntimeForms[0x07019D1B] = Ref;
        var r = w.Read("[References]\n0x7019D1B~Skyrim.esm|HcRock");
        Assert.Contains(r.Forms, f => f.Form == Ref && f.Place == SkyPatcherOverlay.FormPlace.Target);
        Assert.Contains("base '0x7019D1B~Skyrim.esm' is written with a load index ('7019D1B' is more than an object id), which BOS adds to 'Skyrim.esm's own index instead of masking off, so it names 07019D1B", r.Notes);
    }

    // Answer 3 (TESDataHandler.cpp:42-54, SwapData.cpp:40-47), dev Snowy Carts for Snowy Regions_SWAP.ini:8: an address
    // naming no record is kept, so the base applies to nothing and a swap falls through.
    [Fact]
    public void AnAddressNamingNoRecordIsKept()
    {
        var b = new World().Read("[Forms]\n0x5010FB2~HcBos.esp|HcRock");
        Assert.Null(b.Dropped);
        Assert.Contains("BOS keeps the line under that FormID, and it applies to no reference", Notes(b));
        var s = new World().Read("[Forms]\nHcRock|0x900~HcBos.esp");
        Assert.Null(s.Dropped);
        Assert.Contains("at a reference it finds no object there and tries an earlier line", Notes(s));
    }

    // ConditionalData.cpp:9-21: a condition naming no form is kept as text and matched in game; a FormID naming no record
    // is kept as that FormID; an absent plugin names no form, so it is text.
    [Fact]
    public void AConditionNamingNothingIsKeptAsTextOrAsItsFormId()
    {
        var r = new World().Read("[Forms|WhiterunLocation,-0x1~Gone.esp,0x01000999]\nHcRock|HcRockMossy");
        Assert.Null(r.Dropped);
        Assert.Contains("conditions 'WhiterunLocation', '-0x1~Gone.esp' name no record, so BOS keeps them as text", Notes(r));
        Assert.Contains("conditions '0x01000999' names 01000999, which no plugin in the active order defines, so it names nothing; BOS keeps it as that FormID, which matches no reference", Notes(r));
    }

    // ConditionalData.cpp:33-70: BOS matches a form condition only against a Location, Region, Keyword, Cell or
    // WorldSpace, so one naming another type never matches, said on every line under it; ARR BlendedShrooms_SWAP.ini:4
    // names a HeadPart, '01', among 67 conditions.
    [Fact]
    public void AConditionOfATypeBosNeverMatchesIsSaid()
    {
        var w = new World();
        var reads = w.ReadAll("[Forms|HcRock,HcTownLocation,-HcTownKw]\nHcRockMossy|HcRockSnowy\nHcRockSnowy|HcRockMossy");
        const string Never = "conditions 'HcRock' names 000800:HcBos.esp, a record of type Static, and BOS matches a condition only against a record of type Location, Region, Keyword, Cell or Worldspace, so this one never matches";
        Assert.All(reads, r => Assert.Contains(Never, r!.Notes));
        Assert.All(reads, r => Assert.Single(r!.Notes));
        Assert.Contains(reads[0]!.Forms, f => f.Form == Rock && f.Place == SkyPatcherOverlay.FormPlace.Filter);
    }

    // ConditionalData.cpp (3.4.0 :99-109): a '-' entry excludes only when it is valid, so one of a type BOS never
    // matches excludes nothing and the line applies as if it were absent.
    [Fact]
    public void ANegatedConditionOfATypeBosNeverMatchesExcludesNothing()
        => Assert.Equal("conditions - 'HcRock' names 000800:HcBos.esp, a record of type Static, and BOS matches a condition only against a record of type Location, Region, Keyword, Cell or Worldspace, so this one never matches and excludes no reference: BOS reads the line as if the condition were absent",
            Assert.Single(new World().Read("[Forms|-HcRock]\nHcRockMossy|HcRockSnowy").Notes));

    // Answer 3, dev Sleipnir Beds - BOS Windhelm Only_SWAP.ini:1: only the text between the first and second '|' is conditions.
    [Fact]
    public void OnlyTheFirstConditionPartIsRead()
    {
        var r = new World().Read("[Forms|HcTownLocation|HcTownKw]\nHcRock|HcRockMossy");
        Assert.DoesNotContain(r.Forms, f => f.Form == TownKw);
        Assert.Contains("its section's header has 3 '|' parts; BOS reads the conditions between the first and second only", r.Notes);
    }

    // Answer 3, ARR Z AnimatedShips LessShips BOS_SWAP.ini:3: a property line naming no property never applies.
    [Fact]
    public void APropertyLineNamingNoPropertyNeverApplies()
    {
        Assert.Contains("its properties field '0x3B~Skyrim.esm' names none of pos, rot, scale or flags", Notes(new World().Read("[Transforms]\nHcRock|0x3B~Skyrim.esm|posA(1,2,3)")));
        Assert.Empty(new World().Read("[Transforms]\nHcRock|posA(1,2,3)").Notes);
    }

    // SwapData.cpp:96-128: a base with ',' naming nothing whole is a base set; its swap set must keep 1 or at least as many.
    [Fact]
    public void ABaseSetFilesEveryBaseOrRefusesASmallSwapSet()
    {
        var r = new World().Read("[Forms]\nHcRock,HcRockMossy|HcRockSnowy");
        Assert.Null(r.Dropped);
        Assert.Equal(new[] { 0x01000800u, 0x01000801u }, r.Bases.Select(b => b.Id));
        Assert.Contains("SWAP formIDSet.size() must be 1 OR equal/greater than BASE formIDSet.size()",
            new World().Read("[Forms]\nHcRock,HcRockMossy,HcRockSnowy|HcRock,HcRockMossy").Dropped);
    }

    // The top-up T4 (3): a FormID side stoul cannot read throws out of BOS's loader with no handler.
    [Fact]
    public void AnUnreadableFormIdIsSaidToStopBossLoad()
        => Assert.Contains("throws on it and BOS catches nothing, so its load stops there", new World().Read("[Forms]\nzz~HcBos.esp|HcRock").Dropped);

    // The same throw from a header's condition and from a swap set's member (ConditionalData.cpp:9-21, Util.cpp:40-76
    // read each through the same stoul).
    [Fact]
    public void AnUnreadableConditionOrSwapSetMemberIsSaidToStopBossLoad()
    {
        Assert.StartsWith("conditions 'zz~HcBos.esp' ", new World().Read("[Forms|zz~HcBos.esp]\nHcRock|HcRockMossy").Dropped);
        Assert.StartsWith("swap member 'zz~HcBos.esp' ", new World().Read("[Forms]\nHcRock|HcRockMossy,zz~HcBos.esp").Dropped);
        Assert.Contains("BOS catches nothing, so its load stops there", new World().Read("[Forms]\nHcRock|HcRockMossy,zz~HcBos.esp").Dropped);
    }

    // CLibUtil string.hpp:224-228: BOS's split trims nothing, so a token with a space beside '|' or ',' is that text,
    // which names no record.
    [Fact]
    public void ASpaceBesideABarOrACommaIsPartOfTheToken()
    {
        Assert.StartsWith("base 'HcRock ' finds no record by that EditorID", new World().Read("[Forms]\nHcRock | HcRockMossy").Dropped);
        var r = new World().Read("[Forms]\nHcRock|HcRockMossy, HcRockSnowy");
        Assert.Null(r.Dropped);
        Assert.Contains("swap member ' HcRockSnowy' finds no record by that EditorID in the active order; BOS logs 'failed to process' and leaves it out of the swap set", r.Notes);
    }

    // SwapData.cpp:96-128: a base set's swap set of two or more gives each base a member at random; one swap is shared.
    [Fact]
    public void ABaseSetWithASwapSetPicksAtRandom()
    {
        const string Random = "BOS gives each base of the set a different member of the swap set, picked at random once at load";
        Assert.Contains(Random, new World().Read("[Forms]\nHcRock,HcRockMossy|HcRockSnowy,HcRock").Notes);
        Assert.DoesNotContain(Random, new World().Read("[Forms]\nHcRock,HcRockMossy|HcRockSnowy").Notes);
    }

    // ---- BOS's order ---------------------------------------------------------------------------

    // Answer 4 (Manager.cpp:204-216): per base and map, lines file in file order, then section order then line; BOS tries the last first.
    [Fact]
    public void LinesFilePerBaseAndMapInBosOrder()
    {
        var w = new World();
        var a = BosLines.Parse("[Forms]\nHcRock|HcRockMossy\n[References]\nHcRock|HcRockSnowy\n[Forms]\nHcRock|HcRockSnowy", Bos, "A_SWAP.ini");
        var b = BosLines.Parse("[Forms]\nHcRock|HcRockMossy\nHcNoRock|HcRock", Bos, "B_SWAP.ini");
        var places = BosLines.Places(new[] { (a, BosLines.ReadAll(a, w.Ctx)), (b, BosLines.ReadAll(b, w.Ctx)) });
        Assert.Equal((1, 3), (places[(0, 0)][0].Index, places[(0, 0)][0].Total));
        Assert.Equal((2, 3), (places[(0, 2)][0].Index, places[(0, 2)][0].Total));
        Assert.Equal((3, 3), (places[(1, 0)][0].Index, places[(1, 0)][0].Total));
        Assert.Equal((BosKind.References, 1, 1), (places[(0, 1)][0].Kind, places[(0, 1)][0].Index, places[(0, 1)][0].Total));
        Assert.False(places.ContainsKey((1, 1)));   // dropped: no place
    }

    // SimpleIni :2458-2483: a section's lines file before a later section's, even where a repeated header brings more
    // of its lines after that section's ([References] and [Reference] are two sections of one map).
    [Fact]
    public void ARepeatedHeadersLinesFileWithItsFirstAppearance()
    {
        var w = new World();
        var a = BosLines.Parse("[References]\nHcRock|HcRockMossy\n[Reference]\nHcRock|HcRockSnowy\n[References]\nHcRock|HcTownKw", Bos, "A_SWAP.ini");
        var places = BosLines.Places(new[] { (a, BosLines.ReadAll(a, w.Ctx)) });
        Assert.Equal(2, places[(0, 2)][0].Index);
        Assert.Equal(3, places[(0, 1)][0].Index);
    }

    // Manager.cpp:50, :161: conditional lines file per base under 'file|conditions', the sets tried from the last key back.
    [Fact]
    public void ConditionalLinesFileUnderTheirSet()
    {
        var w = new World();
        var a = BosLines.Parse("[Forms|HcTownLocation]\nHcRock|HcRockMossy\n[Forms|-HcTownKw]\nHcRock|HcRockSnowy", Bos, "A_SWAP.ini");
        var places = BosLines.Places(new[] { (a, BosLines.ReadAll(a, w.Ctx)) });
        var first = places[(0, 0)][0];
        Assert.Equal(("A_SWAP.ini|HcTownLocation", 2, 2), (first.ConditionId, first.SetIndex, first.Sets));
        Assert.Equal(1, places[(0, 1)][0].SetIndex);
    }
}
