using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.SkyPatcherOverlayHarness;

namespace HousecarlMcpTests;

/// <summary>An NPC's inventory and factions under the struct-entry collection ops, replayed once.</summary>
public sealed class SkyPatcherOverlayNpcEntryReplay
{
    public static readonly FormKey ItemA = new(new ModKey("HcItm", ModType.Plugin), 0x900);
    public static readonly FormKey ItemB = new(new ModKey("HcItm", ModType.Plugin), 0x901);
    public static readonly FormKey ItemC = new(new ModKey("HcItm", ModType.Plugin), 0x902);
    public static readonly FormKey ItemD = new(new ModKey("HcItm", ModType.Plugin), 0x903);
    public static readonly FormKey FacA = new(new ModKey("HcFac", ModType.Plugin), 0xA01);

    public Npc Npc { get; }
    public SkyPatcherOverlay.SkyPatcherOverlayResult Result { get; }
    public List<(FormKey Fk, int Count)> Entries => Npc.Items!.Select(e => (e.Item.Item.FormKey, e.Item.Count)).ToList();

    public SkyPatcherOverlayNpcEntryReplay()
    {
        var mod = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs.AddNew();
        npc.EditorID = "HcTestNpc";
        npc.Items = new()
        {
            new ContainerEntry { Item = new ContainerItem { Item = ItemA.ToLink<IItemGetter>(), Count = 2 } },
            new ContainerEntry { Item = new ContainerItem { Item = ItemD.ToLink<IItemGetter>(), Count = 1 } },
        };
        var me = $"HcSpOv.esp|{npc.FormKey.ID:X}";
        Npc = npc;
        Result = Apply(npc, npc.FormKey, npc.EditorID, "npc", "Npc", new StubResolver(),
            Line("a.ini", 1, $"filterByNpcs={me}:objectsToAdd=HcItm.esp|901=3"),
            Line("a.ini", 2, $"filterByNpcs={me}:factionsToAdd=HcFac.esp|A01=2"),
            Line("a.ini", 3, $"filterByNpcs={me}:addOnceToInventory=HcItm.esp|901~5"),
            Line("z.ini", 1, $"filterByNpcs={me}:removeInventoryObjectsByCount=HcItm.esp|901~1"),
            Line("z.ini", 2, $"filterByNpcs={me}:objectsToReplace=HcItm.esp|900~HcItm.esp|902"),
            Line("z.ini", 3, $"filterByNpcs={me}:objectsToRemove=HcItm.esp|902~5"),
            Line("z.ini", 4, $"filterByNpcs={me}:setEssential=true"),
            Line("z.ini", 5, $"filterByNpcs={me}:removeInventoryObjectsByCount=HcItm.esp|903~2"),
            Line("z.ini", 6, $"filterByNpcs={me}:setFlags=unique"),
            Line("z.ini", 7, $"filterByNpcs={me}:setProtected=none"));
    }
}

/// <summary>Migrated from the skypatcher-overlay-guard probe's NPC entry-op, formList and leveledList arms.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherOverlayEntryOpsTests(SkyPatcherOverlayNpcEntryReplay npc) : IClassFixture<SkyPatcherOverlayNpcEntryReplay>
{
    [Fact] // npc field map present
    public void TheNpcFieldMapIsPresent() => Assert.NotNull(FieldMap.For("npc", "Npc"));

    [Fact] // '='-packed addEntry landed (itemB count 3 -> byCount -> 2)
    public void AnEqualsPackedAddEntryLands() => Assert.Contains((SkyPatcherOverlayNpcEntryReplay.ItemB, 2), npc.Entries);

    [Fact] // addOnce on a present entry is a visible no-op
    public void AddOnceOnAPresentEntryIsAVisibleNoOp()
    {
        Assert.Contains(npc.Result.Applied, a => a.Op == "addOnceToInventory" && a.Note is { } n && n.Contains("no-op"));
        Assert.Single(npc.Entries, e => e.Fk == SkyPatcherOverlayNpcEntryReplay.ItemB);
    }

    [Fact] // replaceEntry retargeted itemA -> itemC, count preserved
    public void ReplaceEntryRetargetsAndKeepsTheCount()
    {
        Assert.DoesNotContain(npc.Entries, e => e.Fk == SkyPatcherOverlayNpcEntryReplay.ItemA);
        Assert.Contains((SkyPatcherOverlayNpcEntryReplay.ItemC, 2), npc.Entries);
    }

    [Fact] // QUALIFIED removal skipped LOUD (itemC still present, warning names the gap)
    public void AQualifiedRemovalIsSkippedLoud()
    {
        Assert.Contains(npc.Entries, e => e.Fk == SkyPatcherOverlayNpcEntryReplay.ItemC);
        Assert.Contains(npc.Result.Warnings, w => w.Contains("objectsToRemove") && w.Contains("NOT applied"));
    }

    [Fact] // factionsToAdd entry with rank sub-field
    public void FactionsToAddCarriesTheRank()
        => Assert.Contains(npc.Npc.Factions, f => f.Faction.FormKey == SkyPatcherOverlayNpcEntryReplay.FacA && f.Rank == 2);

    [Fact] // flagBool set (setEssential=true)
    public void AFlagBoolSets() => Assert.True(npc.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential));

    [Fact] // removeByCount to <=0 REMOVES the entry (engine RemoveAt path)
    public void RemoveByCountToZeroRemovesTheEntry()
        => Assert.DoesNotContain(npc.Entries, e => e.Fk == SkyPatcherOverlayNpcEntryReplay.ItemD);

    [Fact] // flagsSet lands via the engine (setFlags=unique; essential kept)
    public void FlagsSetAddsTheBitAndKeepsTheOthers()
    {
        Assert.True(npc.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique));
        Assert.True(npc.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential));
    }

    [Fact] // 'none' on a flagBool is a VISIBLE leave-unchanged no-op, not a warning
    public void NoneOnAFlagBoolIsAVisibleLeaveUnchanged()
    {
        Assert.False(npc.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Protected));
        Assert.Contains(npc.Result.Applied, a => a.Op == "setProtected" && a.Note is { } n && n.Contains("leave unchanged"));
        Assert.DoesNotContain(npc.Result.Warnings, w => w.Contains("setProtected"));
    }

    [Fact] // formList field map present
    public void TheFormListFieldMapIsPresent() => Assert.NotNull(FieldMap.For("formList", "FormList"));

    static readonly FormKey X = new(new ModKey("HcItm", ModType.Plugin), 0xB01);
    static readonly FormKey Y = new(new ModKey("HcItm", ModType.Plugin), 0xB02);
    static readonly FormKey Z = new(new ModKey("HcItm", ModType.Plugin), 0xB03);

    static FormList ListOf(params FormKey[] items)
    {
        var fl = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE).FormLists.AddNew();
        foreach (var i in items) fl.Items.Add(i.ToLink<ISkyrimMajorRecordGetter>());
        return fl;
    }

    [Fact] // replaceForm re-points BOTH occurrences in place (X,Y,X -> Z,Y,Z)
    public void ReplaceFormRepointsEveryOccurrenceInPlace()
    {
        var fl = ListOf(X, Y, X);
        var r = Apply(fl, fl.FormKey, null, "formList", "FormList", new StubResolver(),
            Line("fl.ini", 1, $"filterByFormLists=HcSpOv.esp|{fl.FormKey.ID:X}:formsToReplace=HcItm.esp|B01=HcItm.esp|B03"));
        Assert.Equal(new[] { Z, Y, Z }, fl.Items.Select(i => i.FormKey));
        Assert.Contains(r.Applied, a => a.Op == "formsToReplace" && a.Note is { } n && n.Contains("replaced 2"));
    }

    [Fact] // clearList empties the list through the engine (2 -> 0, honest before-count)
    public void ClearEmptiesTheListAndReportsTheBeforeCount()
    {
        var fl = ListOf(X, Y);
        var r = Apply(fl, fl.FormKey, null, "formList", "FormList", new StubResolver(),
            Line("fl.ini", 1, $"filterByFormLists=HcSpOv.esp|{fl.FormKey.ID:X}:clear=true"));
        Assert.Empty(fl.Items);
        Assert.Contains(r.Applied, a => a.Op == "clear" && a.Before == "2 entr(ies)" && a.Note == "cleared");
    }

    static readonly SkyPatcherOverlay.OrderedLine[] LeveledLines =
    {
        Line("ll.ini", 1, "noFilterLLNPC=true:calcForLevel=true"),   // character lists only
        Line("ll.ini", 2, "noFilterLL=true:calcEachItem=true"),      // item lists only
    };

    [Fact] // LVLI: the LLNPC apply-all did NOT touch it; the LL one did
    public void AnItemListTakesOnlyTheItemListApplyAll()
    {
        var lvli = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE).LeveledItems.AddNew();
        Apply(lvli, lvli.FormKey, null, "leveledList", "LeveledItem", new StubResolver(), LeveledLines);
        Assert.False(lvli.Flags.HasFlag(LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer));
        Assert.True(lvli.Flags.HasFlag(LeveledItem.Flag.CalculateForEachItemInCount));
    }

    [Fact] // LVLN: the LL apply-all did NOT touch it; the LLNPC one did
    public void ACharacterListTakesOnlyTheCharacterListApplyAll()
    {
        var lvln = new SkyrimMod(new ModKey("HcSpOv", ModType.Plugin), SkyrimRelease.SkyrimSE).LeveledNpcs.AddNew();
        Apply(lvln, lvln.FormKey, null, "leveledList", "LeveledNpc", new StubResolver(), LeveledLines);
        Assert.True(lvln.Flags.HasFlag(LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer));
        Assert.False(lvln.Flags.HasFlag(LeveledNpc.Flag.CalculateForEachItemInCount));
    }
}
