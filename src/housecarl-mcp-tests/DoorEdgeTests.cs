using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A formid universe on a plugin whose Cells group broke after the index build: only the cell is unscannable and
/// the armor still answers, in whatever order the keys were asked.</summary>
[Trait("tier", "integration")]
public sealed class DoorFaultedUniverseTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-door-fault-");
    readonly ModKey _key = new("HcDoorFault", ModType.Master);
    readonly FormKey _cell, _armor;
    readonly string _path;

    public DoorFaultedUniverseTests()
    {
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var armor = m.Armors.AddNew(); armor.EditorID = "HcDfArmor";
        var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDfCell", Flags = Cell.Flag.IsInteriorCell };
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        _cell = cell.FormKey; _armor = armor.FormKey;
        _path = _mo2.InMod("DoorFault", _key);
        m.BeginWrite.ToPath(_path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(_key.FileName + "\r\n", "*" + _key.FileName + "\r\n", "+DoorFault\r\n");
    }

    public void Dispose() => _mo2.Delete();

    /// <summary>0xFF over the first block header inside the CELL group, with the file's size and write time kept.</summary>
    void BreakCellGroup()
    {
        var mtime = File.GetLastWriteTimeUtc(_path);
        var bytes = File.ReadAllBytes(_path);
        int at = -1;
        for (int i = 0; i + 12 <= bytes.Length && at < 0; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(i + 8, 4).SequenceEqual("CELL"u8)) at = i;
        Assert.True(at >= 0, "the plugin has no CELL group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(_path, bytes);
        File.SetLastWriteTimeUtc(_path, mtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTheBrokenGroupsKeysAreUnscannable(bool armorFirst)
    {
        using var svc = _mo2.Open();
        var ids = new[] { ScratchMo2.Fid(_cell), ScratchMo2.Fid(_armor) };
        Assert.Contains("HcDfCell", RecordsTools.Records(svc, formids: ids));
        BreakCellGroup();
        if (armorFirst) Array.Reverse(ids);
        var r = RecordsTools.Records(svc, formids: ids, references: new[] { "!" + ScratchMo2.Fid(_armor) });
        Assert.True(r.Contains("scan: 1 match") && r.Contains("HcDfArmor") && r.Contains("1 record instance(s) could not be scanned"), r);
    }
}

/// <summary>A target-side '*parent' on targets of two types that no record contains: the note names the first
/// candidate's target type, though the door judged the other type last.</summary>
[Trait("tier", "integration")]
public sealed class DoorNoParentNoteTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-door-noparent-");

    public void Dispose() => _mo2.Delete();

    [Fact]
    public void TheNoteNamesTheFirstCandidatesTargetType()
    {
        var key = new ModKey("HcDoorNoParent", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var armor = m.Armors.AddNew(); armor.EditorID = "HcDnpArmor";
        var weapon = m.Weapons.AddNew(); weapon.EditorID = "HcDnpWeapon";
        var l1 = m.FormLists.AddNew(); l1.EditorID = "HcDnpList1"; l1.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(armor.FormKey));
        var l2 = m.FormLists.AddNew(); l2.EditorID = "HcDnpList2"; l2.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(weapon.FormKey));
        m.BeginWrite.ToPath(_mo2.InMod("DoorNoParent", key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(key.FileName + "\r\n", "*" + key.FileName + "\r\n", "+DoorNoParent\r\n");
        using var svc = _mo2.Open();
        var r = RecordsTools.Records(svc, types: new[] { "FormList" }, where: new[] { "Items[*any]->*parent.EditorID = HcDnpNone" });
        Assert.True(r.Contains("no record CONTAINS a Armor"), r);
    }
}

/// <summary>A formid universe whose first winner's CELL group faults: that plugin's armor keeps the page's one slot, the
/// next plugin's armor is counted, and only the cell is unscannable.</summary>
[Trait("tier", "integration")]
public sealed class DoorFaultedPageTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-door-page-");

    public void Dispose() => _mo2.Delete();

    [Fact]
    public void AFaultedPluginsHealthyRowKeepsThePageSlot()
    {
        var p = new ModKey("HcDoorPageP", ModType.Master);
        var q = new ModKey("HcDoorPageQ", ModType.Plugin);
        var pm = new SkyrimMod(p, SkyrimRelease.SkyrimSE);
        var armorP = pm.Armors.AddNew(); armorP.EditorID = "HcDpArmorP";
        var cell = new Cell(pm.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDpCell", Flags = Cell.Flag.IsInteriorCell };
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); pm.Cells.Records.Add(block);
        var pPath = _mo2.InMod("DoorPageP", p);
        pm.BeginWrite.ToPath(pPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var qm = new SkyrimMod(q, SkyrimRelease.SkyrimSE);
        var armorQ = qm.Armors.AddNew(); armorQ.EditorID = "HcDpArmorQ";
        qm.BeginWrite.ToPath(_mo2.InMod("DoorPageQ", q)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(p.FileName + "\r\n" + q.FileName + "\r\n", "*" + p.FileName + "\r\n*" + q.FileName + "\r\n", "+DoorPageQ\r\n+DoorPageP\r\n");
        using var svc = _mo2.Open();
        var ids = new[] { ScratchMo2.Fid(armorP.FormKey), ScratchMo2.Fid(cell.FormKey), ScratchMo2.Fid(armorQ.FormKey) };
        Assert.Contains("HcDpCell", RecordsTools.Records(svc, formids: ids));

        // 0xFF over the first block header inside P's CELL group, with the file's size and write time kept.
        var mtime = File.GetLastWriteTimeUtc(pPath);
        var bytes = File.ReadAllBytes(pPath);
        int at = -1;
        for (int i = 0; i + 12 <= bytes.Length && at < 0; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(i + 8, 4).SequenceEqual("CELL"u8)) at = i;
        Assert.True(at >= 0, "the plugin has no CELL group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(pPath, bytes);
        File.SetLastWriteTimeUtc(pPath, mtime);

        var r = RecordsTools.Records(svc, formids: ids, references: new[] { "!" + ScratchMo2.Fid(armorP.FormKey) }, limit: 1);
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        Assert.True(r.Contains("scan: 2 matches") && r.Contains("HcDpArmorP") && !r.Contains("HcDpArmorQ")
                    && r.Contains("1 record instance(s) could not be scanned"), r);
    }
}
