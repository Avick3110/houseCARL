using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The collect pass gathers every '->' target the scan judges, whatever the fold: a target it missed would be
/// read again one record at a time, and Top's Armors group would be walked more than once.</summary>
[Trait("tier", "integration")]
public sealed class DoorCollectTests : IClassFixture<DoorGroupReadWorld>
{
    readonly DoorGroupReadWorld _w;
    public DoorCollectTests(DoorGroupReadWorld w) => _w = w;

    [Theory]
    [InlineData("Items[*any]->editorid = HcDoorArmor4")]
    [InlineData("Items[*all]->editorid startswith HcDoorArmor")]
    [InlineData("Items[*none]->editorid = HcDoorNoSuchArmor")]
    public void EveryFoldReadsTheTargetGroupOnce(string where)
    {
        var before = new Dictionary<(string Plugin, string Group), int>(_w.Svc.Counters.GroupReads);
        var r = RecordsTools.Records(_w.Svc, types: new[] { "FormList" }, where: new[] { where });
        Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
        Assert.True(r.Contains("HcDoorList"), r);
        var d = _w.Svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                                          .ToDictionary(kv => kv.Key, kv => kv.Value - before.GetValueOrDefault(kv.Key));
        string B = _w.Base.FileName.String, T = _w.Top.FileName.String;
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "FormLists")] = 2,
            [(B, "Armors")] = 1, [(T, "Armors")] = 1,
        }, d);
    }
}
