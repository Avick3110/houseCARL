using System.Text;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A gather over cells and references on a plugin whose Cells group broke after the index build: the fault
/// that stops the shared-group read is named as itself, not as a reflection wrapper.</summary>
[Trait("tier", "integration")]
public sealed class SharedGroupFaultTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-shared-fault-");
    readonly ModKey _key = new("HcSharedFault", ModType.Master);
    readonly FormKey _cell, _ref;
    readonly string _path;

    public SharedGroupFaultTests()
    {
        _cell = new FormKey(_key, 0xC01); _ref = new FormKey(_key, 0xC02);
        var m = new SkyrimMod(_key, SkyrimRelease.SkyrimSE);
        var cell = new Cell(_cell, SkyrimRelease.SkyrimSE) { EditorID = "HcSxCell", Flags = Cell.Flag.IsInteriorCell };
        cell.Temporary.Add(new PlacedObject(_ref, SkyrimRelease.SkyrimSE) { EditorID = "HcSxRef" });
        var block = new CellBlock { BlockNumber = 1, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell); block.SubBlocks.Add(sub); m.Cells.Records.Add(block);
        _path = _mo2.InMod("SharedFault", _key);
        m.BeginWrite.ToPath(_path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(_key.FileName + "\r\n", "*" + _key.FileName + "\r\n", "+SharedFault\r\n");
    }

    public void Dispose() => _mo2.Delete();

    /// <summary>0xFF over the first block header, 24 bytes into the CELL group: reading the Cells group then overflows.</summary>
    void BreakFirstCellBlock()
    {
        var bytes = File.ReadAllBytes(_path);
        var cellGroup = Encoding.ASCII.GetBytes("CELL");
        int at = -1;
        for (int i = 0; i + 12 <= bytes.Length && at < 0; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("GRUP"u8) && bytes.AsSpan(i + 8, 4).SequenceEqual(cellGroup)) at = i;
        Assert.True(at >= 0, "the plugin has no CELL group");
        bytes.AsSpan(at + 24, 24).Fill(0xFF);
        File.WriteAllBytes(_path, bytes);
    }

    // Fails when the group getter is called through reflection: the cause then reads TargetInvocationException.
    [Fact]
    public void AFaultReadingTheSharedGroupIsNamedAsItself()
    {
        using var svc = _mo2.Open();
        var pin = svc.CapturePin();
        BreakFirstCellBlock();
        using var session = pin.Resolver.OpenSession();
        var fault = BodyGather.WalkOnce(pin.View, session, _key.FileName, new[] { _cell, _ref }, new Dictionary<FormKey, IMajorRecordGetter>());
        var scan = Assert.IsType<PluginUnscannableException>(fault);
        Assert.IsType<OverflowException>(scan.InnerException);
        Assert.DoesNotContain(nameof(System.Reflection.TargetInvocationException), scan.Message);
    }
}
