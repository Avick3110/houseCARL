using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>SkyPatcher key names match without regard to case, and a '#' line is a comment (issue #1035).</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherKeyCaseTests
{
    [Theory]
    [InlineData("filterByNPCs", "filterByNpcs", "")]
    [InlineData("FILTERBYNPCSEXCLUDED", "filterByNpcs", "Excluded")]
    public void AMixedCaseFilterResolvesToTheCanonicalEntry(string key, string canonical, string connective)
    {
        var npc = Catalog.ForSubfolder("npc")!;
        var mixed = Catalog.Classify(npc, key);
        Assert.Equal(SkyPatcherKeyRole.Filter, mixed.Role);
        Assert.Same(npc.Filters.Single(f => f.Name == canonical), mixed.Filter);
        Assert.Equal(connective, mixed.Connective);   // the overlay compares connectives against the catalog's text
    }

    [Fact]
    public void AMixedCaseOperationResolvesToTheCanonicalEntry()
    {
        var npc = Catalog.ForSubfolder("npc")!;
        var mixed = Catalog.Classify(npc, "RACE");
        Assert.Equal(SkyPatcherKeyRole.Operation, mixed.Role);
        Assert.Same(Catalog.Classify(npc, "race").Operation, mixed.Operation);
        Assert.Equal("RACE", mixed.BaseKey);   // a response names the key as the line spells it
    }

    // {me} is the NPC's own address; each spelling reaches a different key lookup (catalog, field-map op, built-in filter, field-map filter)
    [Theory]
    [InlineData("filterByNPCs={me}:race=UBE_AllRace.esp|5A184")]
    [InlineData("filterByNpcs={me}:RACE=UBE_AllRace.esp|5A184")]
    [InlineData("filterByEditorIDContains=HcCase:race=UBE_AllRace.esp|5A184")]
    [InlineData("FILTERBYRACES=Skyrim.esm|13746:race=UBE_AllRace.esp|5A184")]
    public void AMixedCaseNpcLineChangesThePostStateRace(string text)
    {
        var mod = new SkyrimMod(new ModKey("HcSpCase", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcCaseNpc";
        var nord = new FormKey(new ModKey("Skyrim", ModType.Master), 0x13746);
        var ube = new FormKey(new ModKey("UBE_AllRace", ModType.Plugin), 0x5A184);
        npc.Race.SetTo(nord);

        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("OriSeranaUBE.esp.ini", 1, "#Change Serana race to UBE Nord"),
            Line("OriSeranaUBE.esp.ini", 2, text.Replace("{me}", $"HcSpCase.esp|{npc.FormKey.ID:X}")));

        Assert.Equal(ube, npc.Race.FormKey);
        Assert.Equal(0, r.LinesSkippedUnresolvedFilter);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("skipped"));
    }

    // an upper-case Excluded line must exclude, not include, the NPC it names
    [Fact]
    public void AnUpperCaseExcludedLineLeavesTheNamedNpcUnchanged()
    {
        var (npc, nord, _) = CaseNpc();
        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("Case.ini", 1, $"FILTERBYNPCSEXCLUDED=HcSpCase.esp|{npc.FormKey.ID:X}:race=UBE_AllRace.esp|5A184"));

        Assert.Equal(nord, npc.Race.FormKey);
        Assert.Empty(r.Applied);
    }

    // one warning for two spellings of the same filter, and it names the key as the line spells it
    [Fact]
    public void AWarningIsDedupedAcrossSpellingsAndNamesTheLinesKey()
    {
        var (npc, _, _) = CaseNpc();
        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("Case.ini", 1, "FILTERBYKEYWORDSOR=HcNoSuchKeyword:race=UBE_AllRace.esp|5A184"),
            Line("Case.ini", 2, "filterByKeywordsOr=HcNoSuchKeyword:race=UBE_AllRace.esp|5A184"));

        var w = Assert.Single(r.Warnings, w => w.Contains("HcNoSuchKeyword"));
        Assert.Contains("(in a FILTERBYKEYWORDSOR)", w);
    }

    // the conflict and no-op lookups key on the catalog's op name, so two spellings of one op still collide
    [Fact]
    public void TwoSpellingsOfOneOpAreACrossFileConflict()
    {
        static SkyPatcherDiscovery.IniFile Ini(string name, string line) => new(
            RelPath: $"SKSE/Plugins/SkyPatcher/weapon/{name}", Subfolder: "weapon", SortKey: name,
            WinningProvider: "TestMod", LooseFilePath: null, ShadowedProviders: Array.Empty<string>(), GatePlugin: null,
            NotApplied: null, Lines: new[] { SkyPatcherParse.ParseLine(line) });
        var report = SkyPatcherConflicts.Detect(new SkyPatcherDiscovery.FolderScan("weapon", Catalog.ForSubfolder("weapon")!,
                PatchingEnabled: true, Files: new[] { Ini("a.ini", "filterByWeapons=Skyrim.esm|12EB7:ATTACKDAMAGE=40"),
                    Ini("z.ini", "FILTERBYWEAPONS=Skyrim.esm|12EB7:attackDamage=60") }),
            Catalog, FieldMap);

        var c = Assert.Single(report.Conflicts);
        Assert.Equal(new[] { "ATTACKDAMAGE", "attackDamage" }, c.Entries.Select(e => e.Op));
    }

    [Fact]
    public void AnUpperCaseOpWritingTheCurrentValueIsANoOp()
    {
        var (npc, nord, _) = CaseNpc();
        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("Case.ini", 1, $"filterByNpcs=HcSpCase.esp|{npc.FormKey.ID:X}:RACE=Skyrim.esm|13746"));

        var a = Assert.Single(r.Applied);
        Assert.Equal(("RACE", "race"), (a.Op, a.OpName));   // the line's text for display, the catalog's name for lookups
        Assert.True(SkyPatcherConflicts.IsNoOpWrite(a, FieldMap.For("npc", "Npc")));
    }

    static (Npc npc, FormKey nord, FormKey ube) CaseNpc()
    {
        var mod = new SkyrimMod(new ModKey("HcSpCase", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcCaseNpc";
        var nord = new FormKey(new ModKey("Skyrim", ModType.Master), 0x13746);
        npc.Race.SetTo(nord);
        return (npc, nord, new FormKey(new ModKey("UBE_AllRace", ModType.Plugin), 0x5A184));
    }

    [Theory]
    [InlineData("#Change Serana race to UBE Nord")]
    [InlineData("   # indented")]
    public void AHashLineIsAComment(string line)
    {
        var parsed = SkyPatcherParse.ParseLine(line);
        Assert.Equal(SkyPatcherLineKind.Comment, parsed.Kind);
        Assert.Empty(parsed.Segments);
        Assert.Null(parsed.Note);
    }
}
