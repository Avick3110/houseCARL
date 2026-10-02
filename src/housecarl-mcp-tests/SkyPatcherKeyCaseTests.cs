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
    [InlineData("filterByNPCs", "filterByNpcs")]
    [InlineData("FILTERBYNPCSEXCLUDED", "filterByNpcs")]
    public void AMixedCaseFilterResolvesToTheCanonicalEntry(string key, string canonical)
    {
        var npc = Catalog.ForSubfolder("npc")!;
        var mixed = Catalog.Classify(npc, key);
        Assert.Equal(SkyPatcherKeyRole.Filter, mixed.Role);
        Assert.Same(npc.Filters.Single(f => f.Name == canonical), mixed.Filter);
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

    [Fact]
    public void AnNpcLineSpelledFilterByNPCsChangesThePostStateRace()
    {
        var mod = new SkyrimMod(new ModKey("HcSpCase", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcCaseNpc";
        var nord = new FormKey(new ModKey("Skyrim", ModType.Master), 0x13746);
        var ube = new FormKey(new ModKey("UBE_AllRace", ModType.Plugin), 0x5A184);
        npc.Race.SetTo(nord);

        var r = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("OriSeranaUBE.esp.ini", 1, "#Change Serana race to UBE Nord"),
            Line("OriSeranaUBE.esp.ini", 2, $"filterByNPCs=HcSpCase.esp|{npc.FormKey.ID:X}:race=UBE_AllRace.esp|5A184"));

        Assert.Equal(ube, npc.Race.FormKey);
        Assert.Equal(0, r.LinesSkippedUnresolvedFilter);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("skipped"));
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
