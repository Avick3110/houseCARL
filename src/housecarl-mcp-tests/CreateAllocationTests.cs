using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Where a create allocates its FormID (Stryker row T04): every allocation floors the patch's counter at 0x800 and past
/// every record the patch defines, never lowers a counter already past that, and refuses past the 24-bit ceiling; a
/// patch write persists the floored counter. World-free: the engine takes a mod in memory.
/// </summary>
[Trait("tier", "unit")]
public sealed class CreateFormIdFloorTests
{
    static SkyrimMod Patch(uint counter, params uint[] ownIds)
    {
        var mod = new SkyrimMod(new ModKey("HcFloorPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        foreach (var id in ownIds)
            mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE));
        mod.ModHeader.Stats.NextFormID = counter;
        return mod;
    }

    [Fact]
    public void AFlatCreateIntoAPatchWhoseCounterIsBelow0x800AllocatesFrom0x800()
    {
        var mod = Patch(0x10);
        var rec = WriteEngine.GenericAddNew(mod, "Keyword", "HcFloorNew");
        Assert.Equal(0x800u, rec.FormKey.ID);
    }

    /// <summary>A record sitting exactly on the floor is taken, so the next id is past it.</summary>
    [Fact]
    public void AFlatCreateSkipsARecordThePatchHoldsAt0x800()
    {
        var mod = Patch(0x10, 0x800);
        var rec = WriteEngine.GenericAddNew(mod, "Keyword", "HcFloorNew");
        Assert.Equal(0x801u, rec.FormKey.ID);
    }

    [Fact]
    public void AFlatCreateAllocatesPastTheHighestIdThePatchDefines()
    {
        var mod = Patch(0x850, 0x900);
        var rec = WriteEngine.GenericAddNew(mod, "Keyword", "HcFloorNew");
        Assert.Equal(0x901u, rec.FormKey.ID);
    }

    [Fact]
    public void ACounterAlreadyPastTheFloorIsNotLowered()
    {
        var mod = Patch(0x1000, 0x900);
        var rec = WriteEngine.GenericAddNew(mod, "Keyword", "HcFloorNew");
        Assert.Equal(0x1000u, rec.FormKey.ID);
    }

    [Fact]
    public void AFlatCreatePastTheObjectIdCeilingIsRefused()
    {
        var mod = Patch(0x1000000);
        var ex = Assert.ThrowsAny<Exception>(() => WriteEngine.GenericAddNew(mod, "Keyword", "HcFloorNew"));
        Assert.Contains("ceiling", ex.Message);
    }

    [Fact]
    public void AnInteriorCellCreateIntoAPatchWhoseCounterIsBelow0x800AllocatesFrom0x800()
    {
        var mod = Patch(0x10);
        var cell = WriteEngine.AddInteriorCell(mod, "HcFloorCell");
        Assert.Equal(0x800u, cell.FormKey.ID);
    }

    [Fact]
    public void AnInteriorCellCreatePastTheObjectIdCeilingIsRefused()
    {
        var mod = Patch(0x1000000);
        var ex = Assert.ThrowsAny<Exception>(() => WriteEngine.AddInteriorCell(mod, "HcFloorCell"));
        Assert.Contains("ceiling", ex.Message);
    }

    /// <summary>An extend that only overrides a master's record allocates nothing, and the written header counter is
    /// still floored.</summary>
    [Fact]
    public void AnOverrideOnlyPatchWriteRaisesAWrittenCounterBelow0x800()
    {
        var master = new SkyrimMod(new ModKey("HcFloorMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var kw = master.Keywords.AddNew("HcFloorMasterKw");
        var patch = Patch(0x10);
        patch.Keywords.GetOrAddAsOverride(kw);

        var dir = Path.Combine(Path.GetTempPath(), "hc-floor-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "HcFloorPatch.esp");
            WriteEngine.WritePatch(patch, new ISkyrimModGetter[] { master }, path);
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            Assert.True(back.ModHeader.Stats.NextFormID >= 0x800u, $"written counter 0x{back.ModHeader.Stats.NextFormID:X}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// Upsert on the extend lane (Stryker row T05): an editorid the patch holds on a record of a DIFFERENT type is a
/// collision, refused rather than replaced.
/// </summary>
[Trait("tier", "unit")]
public sealed class CreateUpsertTypeClashTests
{
    [Fact]
    public void UpsertOverAnEditorIdHeldByAnotherRecordTypeIsRefused()
    {
        var mod = new SkyrimMod(new ModKey("HcUpsertPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        mod.Keywords.AddNew("HcUpsertShared");

        var ex = Assert.Throws<InvalidOperationException>(() => WriteEngine.GenericUpsertNew(mod, "Weapon", "HcUpsertShared"));
        Assert.Contains("not a Weapon", ex.Message);
    }
}

/// <summary>
/// An interior cell create (Stryker row T07): the cell is flagged interior and filed in the block and sub-block its own
/// FormID's digits name, under the interior group types; editorids are unique per patch, and only a repeat refuses.
/// </summary>
[Trait("tier", "unit")]
public sealed class CreateInteriorCellTests
{
    /// <summary>0x853 is 2131: block 2131 % 10 = 1, sub-block (2131 / 10) % 10 = 3.</summary>
    static (SkyrimMod Mod, Cell Cell) CreateAt0x853()
    {
        var mod = new SkyrimMod(new ModKey("HcInteriorPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.NextFormID = 0x853;
        return (mod, WriteEngine.AddInteriorCell(mod, "HcInteriorA"));
    }

    [Fact]
    public void TheCreatedCellIsFlaggedInterior()
    {
        var (_, cell) = CreateAt0x853();
        Assert.True(cell.Flags.HasFlag(Cell.Flag.IsInteriorCell));
    }

    [Fact]
    public void TheCellIsFiledInTheBlockAndSubBlockItsIdNames()
    {
        var (mod, cell) = CreateAt0x853();
        Assert.Equal(0x853u, cell.FormKey.ID);

        var block = Assert.Single(mod.Cells.Records);
        Assert.Equal(1, block.BlockNumber);
        Assert.Equal(GroupTypeEnum.InteriorCellBlock, block.GroupType);
        var sub = Assert.Single(block.SubBlocks);
        Assert.Equal(3, sub.BlockNumber);
        Assert.Equal(GroupTypeEnum.InteriorCellSubBlock, sub.GroupType);
        Assert.Same(cell, Assert.Single(sub.Cells));
    }

    [Fact]
    public void ASecondInteriorCellWithAnotherEditorIdIsCreated()
    {
        var (mod, _) = CreateAt0x853();
        var second = WriteEngine.AddInteriorCell(mod, "HcInteriorB");
        Assert.Equal("HcInteriorB", second.EditorID);
    }

    [Fact]
    public void ASecondInteriorCellWithTheSameEditorIdIsRefused()
    {
        var (mod, _) = CreateAt0x853();
        var ex = Assert.Throws<InvalidOperationException>(() => WriteEngine.AddInteriorCell(mod, "HcInteriorA"));
        Assert.Contains("edit the existing record", ex.Message);
        Assert.Contains("different editorid", ex.Message);
    }
}
