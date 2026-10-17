using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A formid universe on a plugin whose Cells group broke after the index build: every key that plugin wins is
/// unscannable, whichever group the door walked first and in whatever order the keys were asked.</summary>
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
    public void AFaultedWinnersKeysAreAllUnscannable(bool armorFirst)
    {
        using var svc = _mo2.Open();
        var ids = new[] { ScratchMo2.Fid(_cell), ScratchMo2.Fid(_armor) };
        Assert.Contains("HcDfCell", RecordsTools.Records(svc, formids: ids));
        BreakCellGroup();
        if (armorFirst) Array.Reverse(ids);
        var r = RecordsTools.Records(svc, formids: ids, references: new[] { "!" + ScratchMo2.Fid(_armor) });
        Assert.True(r.Contains("scan: 0 matches") && r.Contains("2 record instance(s) could not be scanned"), r);
    }
}
